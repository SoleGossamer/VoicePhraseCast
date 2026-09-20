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
        private Model? _voskModel;
        private VoskRecognizer? _recognizer;
        private readonly object _lock = new();

        public event Action<string>? OnStatusChanged;

        /// <summary>
        /// Инициализация модели Vosk для STT
        /// </summary>
        public void Initialize(string modelPath)
        {
            if (!Directory.Exists(modelPath))
            {
                OnStatusChanged?.Invoke("Ошибка STT: Папка с моделью Vosk не найдена!");
                return;
            }

            try
            {
                lock (_lock)
                {
                    Vosk.Vosk.SetLogLevel(-1);
                    _voskModel = new Model(modelPath);
                    _recognizer = new VoskRecognizer(_voskModel, 16000.0f);
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

        /// <summary>
        /// Принимает сырые байты PCM (44.1 кГц, 16 бит, Mono), ресемплирует до 16 кГц и распознает текст.
        /// </summary>
        public Task<string> RecognizeBytesAsync(byte[] pcm44100Bytes)
        {
            if (pcm44100Bytes == null || pcm44100Bytes.Length == 0)
                return Task.FromResult(string.Empty);

            if (_recognizer == null)
            {
                // Резервная инициализация, если модель лежит в папке приложения по умолчанию
                string defaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model");
                if (Directory.Exists(defaultPath))
                {
                    Initialize(defaultPath);
                }

                if (_recognizer == null)
                {
                    OnStatusChanged?.Invoke("Ошибка: Модель STT не инициализирована!");
                    return Task.FromResult(string.Empty);
                }
            }

            return Task.Run(() =>
            {
                try
                {
                    OnStatusChanged?.Invoke("Распознавание речи...");

                    // 1. Делаем сырой PCM 44.1 кГц
                    var waveFormat = new WaveFormat(44100, 16, 1);
                    using var inputStream = new MemoryStream(pcm44100Bytes);
                    using var rawProvider = new RawSourceWaveStream(inputStream, waveFormat);

                    // 2. Ресемплируем в 16000 Гц
                    var resampler = new WdlResamplingSampleProvider(rawProvider.ToSampleProvider(), 16000);
                    var sampleProvider16 = resampler.ToWaveProvider16();

                    byte[] buffer = new byte[4096];
                    int bytesRead;

                    lock (_lock)
                    {
                        _recognizer.Reset();

                        while ((bytesRead = sampleProvider16.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            _recognizer.AcceptWaveform(buffer, bytesRead);
                        }

                        string jsonResult = _recognizer.FinalResult();
                        string text = ExtractTextFromJson(jsonResult);

                        return text;
                    }
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
                _voskModel?.Dispose();
            }
        }
    }
}