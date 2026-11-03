using System.Diagnostics;
using System.Text.Json;
using Vosk;

namespace VoicePhraseCast
{
    /// <summary>Разбор JSON-ответов Vosk. Заменяет IndexOf по строке и JObject.Parse на горячем пути.</summary>
    internal static class VoskJson
    {
        public static string Get(string? json, string field)
        {
            if (string.IsNullOrEmpty(json)) return string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Потоковый распознаватель для диктовки в чат: Begin → Feed → End. Работает на частоте тракта (AudioProcessor.SampleRate),
    /// Vosk ресемплирует сам; после отпускания клавиши остаётся только FinalResult().
    ///
    /// Класс отвечает только за распознавание. Когда начинать, когда заканчивать, добирать ли хвост и глушить ли микрофон в игре,
    /// решает AudioProcessor (BeginDictation / EndDictationAsync): там же живут флаги глушения, и по IsActive этого больше не выводится.
    /// Feed вызывается из потока очереди распознавания (PcmPump), а не из потока записи.
    /// Vosk.Model принадлежит владельцу (BridgeController): здесь она не освобождается.
    /// </summary>
    public sealed class SpeechToTextService : IDisposable
    {
        private const float SampleRate = AudioProcessor.SampleRate;   // одна константа на весь тракт

        private readonly object _lock = new();
        private readonly TextFormattingService _formatter = new();
        private VoskRecognizer? _recognizer;
        private volatile bool _active;

        public event Action<string>? OnStatusChanged;

        /// <summary>Распознаватель принимает звук (между Begin и End/Cancel).</summary>
        public bool IsActive => _active;

        /// <summary>
        /// Создаёт распознаватель для модели и подменяет прежний (новый создаётся первым, старый освобождается вне блокировки).
        /// Вызывать из Task.Run: создание занимает заметное время. Бросает исключение, если распознаватель создать не удалось.
        /// </summary>
        public void Initialize(Model model)
        {
            ArgumentNullException.ThrowIfNull(model);

            var recognizer = new VoskRecognizer(model, SampleRate);
            try
            {
                recognizer.SetMaxAlternatives(0);
                recognizer.SetWords(false);
            }
            catch
            {
                recognizer.Dispose();
                throw;
            }

            VoskRecognizer? old;
            lock (_lock)
            {
                old = _recognizer;
                _recognizer = recognizer;
                _active = false;
            }
            old?.Dispose();

            OnStatusChanged?.Invoke("STT готов к работе");
        }

        /// <summary>Сбрасывает распознаватель и начинает приём звука. false — модель ещё не загружена (приём не начат).</summary>
        public bool Begin()
        {
            lock (_lock)
            {
                _recognizer?.Reset();
                _active = _recognizer != null;
                return _active;
            }
        }

        /// <summary>Вызывается на каждый чанк из потока очереди распознавания. Буфер переиспользуется: ссылку не сохранять.</summary>
        public void Feed(byte[] buffer, int count)
        {
            if (!_active) return;

            lock (_lock)
            {
                if (_active && _recognizer != null)
                    _recognizer.AcceptWaveform(buffer, count);
            }
        }

        /// <summary>
        /// Завершает приём и возвращает отформатированный текст. Повторный вызов возвращает пустую строку.
        /// Хвост речи и ожидание очереди (Flush) обеспечивает AudioProcessor до этого вызова.
        /// </summary>
        public string End()
        {
            string raw;
            lock (_lock)
            {
                if (!_active) return string.Empty;
                _active = false;
                raw = VoskJson.Get(_recognizer?.FinalResult(), "text");
            }

            try
            {
                return _formatter.Format(raw);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STT Format] {ex.Message}");
                return raw;
            }
        }

        /// <summary>Отбрасывает текущую диктовку без результата (остановка моста, потеря устройства).</summary>
        public void Cancel()
        {
            lock (_lock)
            {
                if (!_active) return;
                _active = false;
                _recognizer?.Reset();
            }
        }

        public void Dispose()
        {
            VoskRecognizer? recognizer;
            lock (_lock)
            {
                recognizer = _recognizer;
                _recognizer = null;
                _active = false;
            }
            recognizer?.Dispose();
        }
    }
}
