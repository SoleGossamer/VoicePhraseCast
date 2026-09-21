using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Vosk;

namespace VoicePhraseCast
{
    public class SpeechToTextService : IDisposable
    {
        private VoskRecognizer? _recognizer;
        private readonly object _lock = new();
        private readonly TextFormattingService _formatter = new();

        public event Action<string>? OnStatusChanged;

        /// <summary>
        /// Инициализация распознавателя STT с использованием единого централизованного экземпляра Vosk.Model.
        /// </summary>
        public void Initialize(Model sharedModel)
        {
            if (sharedModel == null)
            {
                OnStatusChanged?.Invoke("Ошибка STT: Передана пустая ссылка на Vosk.Model!");
                return;
            }

            try
            {
                lock (_lock)
                {
                    _recognizer?.Dispose();
                    // Создаем отдельный recognizer для STT с частотой 16 кГц
                    _recognizer = new VoskRecognizer(sharedModel, 16000.0f);
                    _recognizer.SetMaxAlternatives(0);
                    _recognizer.SetWords(false);
                }

                OnStatusChanged?.Invoke("STT готов к работе");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Ошибка инициализации STT: {ex.Message}");
            }
        }

        public Task<string> RecognizeBytesAsync(byte[] pcm44100Bytes)
        {
            if (pcm44100Bytes == null || pcm44100Bytes.Length == 0)
                return Task.FromResult(string.Empty);

            if (_recognizer == null)
            {
                OnStatusChanged?.Invoke("Ошибка: Модель STT не инициализирована!");
                return Task.FromResult(string.Empty);
            }

            return Task.Run(() =>
            {
                try
                {
                    OnStatusChanged?.Invoke("Распознавание речи...");

                    var waveFormat = new WaveFormat(44100, 16, 1);
                    using var inputStream = new MemoryStream(pcm44100Bytes);
                    using var rawProvider = new RawSourceWaveStream(inputStream, waveFormat);

                    var resampler = new WdlResamplingSampleProvider(rawProvider.ToSampleProvider(), 16000);
                    var sampleProvider16 = resampler.ToWaveProvider16();

                    byte[] buffer = new byte[4096];
                    int bytesRead;

                    string rawText = string.Empty;

                    lock (_lock)
                    {
                        _recognizer.Reset();

                        while ((bytesRead = sampleProvider16.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            _recognizer.AcceptWaveform(buffer, bytesRead);
                        }

                        string jsonResult = _recognizer.FinalResult();
                        rawText = ExtractTextFromJson(jsonResult);
                    }

                    string formattedText = _formatter.Format(rawText);
                    return formattedText;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[STT Error] {ex.Message}");
                    return string.Empty;
                }
            });
        }

        private string ExtractTextFromJson(string json)
        {
            int textIndex = json.IndexOf("\"text\" : \"");
            if (textIndex != -1)
            {
                int start = textIndex + 10;
                int end = json.IndexOf("\"", start);
                if (end != -1)
                {
                    return json.Substring(start, end - start).Trim();
                }
            }
            return string.Empty;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _recognizer?.Dispose();
                _recognizer = null;
                // Vosk.Model НЕ освобождаем здесь, так как она принадлежит главному контейнеру/окну
            }
        }
    }
}