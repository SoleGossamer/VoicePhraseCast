using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace VoicePhraseCast
{
    public class SpeechToTextService : IDisposable
    {
        private WhisperFactory? _whisperFactory;
        private WhisperProcessor? _processor;

        public event Action<string>? OnTextRecognized;
        public event Action<string>? OnStatusChanged;

        public bool IsInitialized => _processor != null;

        public void Initialize(string modelPath)
        {
            if (!File.Exists(modelPath))
            {
                OnStatusChanged?.Invoke("Ошибка: Файл модели Whisper не найден!");
                return;
            }

            try
            {
                _whisperFactory = WhisperFactory.FromPath(modelPath);

                _processor = _whisperFactory.CreateBuilder()
                    .WithLanguage("ru")
                    .WithThreads(4) // Оставляем лимит потоков для стабильности
                    .Build();

                OnStatusChanged?.Invoke("Whisper готов к работе (Vulkan GPU)");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Ошибка инициализации Whisper: {ex.Message}");
            }
        }

        /// <summary>
        /// Принимает сырые байты PCM 44.1 кГц / 16-bit / Mono из AudioProcessor,
        /// ресемплирует в 16 кГц и распознает с помощью Whisper.
        /// </summary>
        public async Task<string> RecognizeBytesAsync(byte[] pcm44100Bytes)
        {
            if (_processor == null || pcm44100Bytes == null || pcm44100Bytes.Length == 0)
                return string.Empty;

            try
            {
                OnStatusChanged?.Invoke("Распознавание речи...");

                // 1. Оборачиваем сырые байты 44.1 кГц в RawSourceWaveStream
                var waveFormat = new WaveFormat(44100, 16, 1);
                using var inputStream = new MemoryStream(pcm44100Bytes);
                using var rawProvider = new RawSourceWaveStream(inputStream, waveFormat);

                // 2. Ресемплируем до 16000 Гц с помощью WdlResamplingSampleProvider
                var resampler = new WdlResamplingSampleProvider(rawProvider.ToSampleProvider(), 16000);

                // 3. Считываем сэмплы в массив float (значения -1.0f .. 1.0f)
                var floatSamples = new List<float>();
                var buffer = new float[1600];
                int read;
                while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        floatSamples.Add(buffer[i]);
                    }
                }

                if (floatSamples.Count == 0)
                {
                    OnStatusChanged?.Invoke("Запись слишком короткая.");
                    return string.Empty;
                }

                // 4. Прогоняем массив float через WhisperProcessor
                var resultBuilder = new StringBuilder();

                await foreach (var segment in _processor.ProcessAsync(floatSamples.ToArray()))
                {
                    resultBuilder.Append(segment.Text);
                }

                string recognizedText = resultBuilder.ToString().Trim();

                if (!string.IsNullOrWhiteSpace(recognizedText))
                {
                    OnStatusChanged?.Invoke("Готово!");
                    OnTextRecognized?.Invoke(recognizedText);
                    return recognizedText;
                }
                else
                {
                    OnStatusChanged?.Invoke("Речь не распознана.");
                    return string.Empty;
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Ошибка распознавания: {ex.Message}");
                return string.Empty;
            }
        }

        public void Dispose()
        {
            _processor?.Dispose();
            _whisperFactory?.Dispose();
        }
    }
}