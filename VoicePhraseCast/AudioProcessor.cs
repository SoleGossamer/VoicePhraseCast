using FuzzySharp;
using FuzzySharp.SimilarityRatio.Scorer;
using FuzzySharp.SimilarityRatio.Scorer.Composite;
using FuzzySharp.SimilarityRatio.Scorer.Generic;
using FuzzySharp.SimilarityRatio.Scorer.StrategySensitive;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vosk;

namespace DotaVoiceAssistant
{
    public class AudioProcessor : IDisposable
    {
        private WaveInEvent? _micInput;
        private WaveOutEvent? _virtualOutput;
        private WaveOutEvent? _monitorOutput;

        private MixingSampleProvider? _mixer;
        private MixingSampleProvider? _monitorMixer;

        private BufferedWaveProvider? _cableBuffer;
        private BufferedWaveProvider? _monitorBuffer;

        private VoskRecognizer? _recognizer;
        private Model? _voskModel;
        private bool _isModelExternal = false;
        private bool _isPlaying = false;
        private readonly object _voskLock = new object();

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private string _lastLoadedFolderPath = string.Empty;

        public byte CurrentEmulationKey { get; set; } = 0x47; // По умолчанию 'G'
        public bool IsMonitoringEnabled { get; set; } = false;
        public bool IsListenMode { get; private set; } = false;
        public bool IsEmulationEnabled { get; set; } = true;

        public float VolumeMic { get; set; } = 1.0f;
        public float VolumeCable { get; set; } = 1.0f;

        private Dictionary<string, List<string>> _phraseFiles = new Dictionary<string, List<string>>();
        public Action<string>? OnPhraseRecognized;
        private CancellationTokenSource? _soundCancelTokenSource;
        public event Action? OnSoundFinished;

        public event Action<string>? OnVoskStatusChanged;
        private DateTime _lastPlayTime = DateTime.MinValue;

        private MemoryStream? _sttAudioBuffer;
        private readonly object _sttLock = new();
        public bool IsSttRecording { get; private set; }

        /// <summary>
        /// Загружает или подключает существующую модель Vosk, инициализирует recognizer (44.1 кГц) 
        /// и возвращает ссылку на загруженную Vosk.Model.
        /// </summary>
        public async Task<Model?> InitializeVoskAsync(Model sharedModel)
        {
            try
            {
                if (sharedModel == null) return null;

                _voskModel = sharedModel;
                _isModelExternal = true;

                await Task.Run(() =>
                {
                    _recognizer = new VoskRecognizer(_voskModel, 44100.0f);
                });

                OnVoskStatusChanged?.Invoke("Модель ИИ успешно подключена.");
                return _voskModel;
            }
            catch (Exception ex)
            {
                OnVoskStatusChanged?.Invoke($"Ошибка ИИ: {ex.Message}");
                Debug.WriteLine($"[Vosk Init Error] {ex}");
                return null;
            }
        }

        public void ToggleVosk(bool enable)
        {
            IsListenMode = enable;
            Debug.WriteLine($"[Vosk] Режим прослушивания: {IsListenMode}");
        }

        public string GetFinalText()
        {
            if (_recognizer == null) return "";
            lock (_voskLock)
            {
                var json = _recognizer.FinalResult();
                return JObject.Parse(json)["text"]?.ToString() ?? "";
            }
        }

        public void LoadPhrases(string folderPath)
        {
            _lastLoadedFolderPath = folderPath;
            _phraseFiles.Clear();
            if (!Directory.Exists(folderPath)) return;

            var extensions = new HashSet<string> { ".mp3", ".wav", ".ogg", ".flac", ".mpeg" };
            int totalFilesCounter = 0;

            try
            {
                var allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);

                foreach (var file in allFiles)
                {
                    string ext = Path.GetExtension(file).ToLower();

                    if (extensions.Contains(ext))
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file).ToLower().Trim();
                        string cleanKey = System.Text.RegularExpressions.Regex.Replace(fileName, @"\s*\(.*?\)", "").Trim();
                        cleanKey = System.Text.RegularExpressions.Regex.Replace(cleanKey, @"\s+\d+$", "").Trim();

                        if (!_phraseFiles.ContainsKey(cleanKey))
                        {
                            _phraseFiles[cleanKey] = new List<string>();
                        }

                        _phraseFiles[cleanKey].Add(file);
                        totalFilesCounter++;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Phrases Error] Ошибка при сканировании подпапок: {ex.Message}");
            }

            Debug.WriteLine($"[Phrases] Сгруппировано уникальных основ: {_phraseFiles.Count}. Всего файлов загружено из всех подпапок: {totalFilesCounter}");
        }

        public void OpenPhrasesFolder()
        {
            try
            {
                string targetPath = string.IsNullOrEmpty(_lastLoadedFolderPath)
                    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Phrases")
                    : _lastLoadedFolderPath;

                if (!Directory.Exists(targetPath))
                {
                    Directory.CreateDirectory(targetPath);
                }

                System.Diagnostics.Process.Start("explorer.exe", targetPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Folder Error] Не удалось открыть папку: {ex.Message}");
            }
        }

        public (int micId, int cableId) GetDeviceIndices()
        {
            int micId = -1;
            int cableId = -1;

            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                string name = WaveIn.GetCapabilities(i).ProductName;
                Debug.WriteLine($"[Audio Scan] Устройство записи [{i}]: {name}");

                if (name.Contains("HyperX", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("QuadCast", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Микрофон", StringComparison.OrdinalIgnoreCase))
                {
                    micId = i;
                }
            }

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                string name = WaveOut.GetCapabilities(i).ProductName;
                Debug.WriteLine($"[Audio Scan] Устройство вывода [{i}]: {name}");

                if (name.Contains("16ch", StringComparison.OrdinalIgnoreCase))
                {
                    cableId = i;
                    break;
                }
            }

            if (cableId == -1)
            {
                for (int i = 0; i < WaveOut.DeviceCount; i++)
                {
                    string name = WaveOut.GetCapabilities(i).ProductName;
                    if (name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                    {
                        cableId = i;
                        break;
                    }
                }
            }

            Debug.WriteLine($"[Audio Scan] Итог автоматического поиска -> Реал. Mic ID: {micId}, Реал. Cable ID: {cableId}");
            return (micId, cableId);
        }

        public void SetMonitoring(bool enable)
        {
            IsMonitoringEnabled = enable;

            if (_monitorOutput != null)
            {
                _monitorOutput.Volume = enable ? 1.0f : 0.0f;
            }
            Debug.WriteLine($"[Audio] Мониторинг изменен: {enable}");
        }

        public void Start(int micIndex, int cableIndex, int monitorIndex)
        {
            int sampleRate = 44100;
            var format = new WaveFormat(sampleRate, 1);

            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1)) { ReadFully = true };
            _cableBuffer = new BufferedWaveProvider(format) { DiscardOnBufferOverflow = true };
            _mixer.AddMixerInput(_cableBuffer.ToSampleProvider());

            _virtualOutput = new WaveOutEvent { DeviceNumber = cableIndex, DesiredLatency = 100 };
            _virtualOutput.Init(_mixer);

            _monitorMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1)) { ReadFully = true };
            _monitorBuffer = new BufferedWaveProvider(format) { DiscardOnBufferOverflow = true };
            _monitorMixer.AddMixerInput(_monitorBuffer.ToSampleProvider());

            _monitorOutput = new WaveOutEvent { DeviceNumber = monitorIndex, DesiredLatency = 100 };
            _monitorOutput.Init(_monitorMixer);
            _monitorOutput.Volume = IsMonitoringEnabled ? 1.0f : 0.0f;

            _micInput = new WaveInEvent
            {
                DeviceNumber = micIndex,
                WaveFormat = format,
                BufferMilliseconds = 50
            };

            _micInput.DataAvailable += (s, e) =>
            {
                if (IsSttRecording)
                {
                    lock (_sttLock)
                    {
                        _sttAudioBuffer?.Write(e.Buffer, 0, e.BytesRecorded);
                    }
                }

                if (!_isPlaying && !IsListenMode)
                {
                    _cableBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                }

                if (IsListenMode && _recognizer != null)
                {
                    lock (_voskLock)
                    {
                        if (_recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
                        {
                            var json = _recognizer.Result();
                            var text = JObject.Parse(json)["text"]?.ToString();
                            if (!string.IsNullOrEmpty(text)) OnPhraseRecognized?.Invoke(text);
                        }
                        else
                        {
                            var partialJson = _recognizer.PartialResult();
                            var partialText = JObject.Parse(partialJson)["partial"]?.ToString();
                            if (!string.IsNullOrEmpty(partialText)) OnPhraseRecognized?.Invoke(partialText);
                        }
                    }
                }
            };
            _virtualOutput.Play();
            _monitorOutput.Play();
            _micInput.StartRecording();
        }

        public void PlaySound(string path)
        {
            var cableMixer = _mixer;
            var headMixer = _monitorMixer;

            if (cableMixer == null || !File.Exists(path)) return;

            StopCurrentSound();

            _soundCancelTokenSource = new CancellationTokenSource();
            var token = _soundCancelTokenSource.Token;

            Task.Run(() =>
            {
                _isPlaying = true;
                AudioFileReader? reader = null;
                AudioFileReader? reader2 = null;
                VolumeSampleProvider? volumeCable = null;
                VolumeSampleProvider? volumeHead = null;

                try
                {
                    reader = new AudioFileReader(path);
                    var resampler = new WdlResamplingSampleProvider(reader, cableMixer.WaveFormat.SampleRate);
                    volumeCable = new VolumeSampleProvider(resampler.ToMono()) { Volume = VolumeCable };

                    if (headMixer != null)
                    {
                        reader2 = new AudioFileReader(path);
                        var resampler2 = new WdlResamplingSampleProvider(reader2, headMixer.WaveFormat.SampleRate);
                        volumeHead = new VolumeSampleProvider(resampler2.ToMono()) { Volume = VolumeMic };
                    }

                    if (IsEmulationEnabled)
                    {
                        keybd_event(CurrentEmulationKey, 0, 0, 0);
                    }
                    Thread.Sleep(150);

                    cableMixer.AddMixerInput(volumeCable);
                    if (volumeHead != null && headMixer != null)
                    {
                        headMixer.AddMixerInput(volumeHead);
                    }

                    int durationMs = (int)reader.TotalTime.TotalMilliseconds + 300;
                    var stopwatch = Stopwatch.StartNew();

                    while (stopwatch.ElapsedMilliseconds < durationMs)
                    {
                        if (token.IsCancellationRequested)
                        {
                            Debug.WriteLine("[PlaySound] Воспроизведение прервано пользователем.");
                            break;
                        }

                        Thread.Sleep(50);
                    }
                    stopwatch.Stop();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PlaySound Error] Ошибка: {ex.Message}");
                }
                finally
                {
                    if (IsEmulationEnabled)
                    {
                        keybd_event(CurrentEmulationKey, 0, KEYEVENTF_KEYUP, 0);
                    }

                    if (volumeCable != null) cableMixer.RemoveMixerInput(volumeCable);
                    if (volumeHead != null && headMixer != null)
                    {
                        headMixer.RemoveMixerInput(volumeHead);
                    }

                    reader?.Dispose();
                    reader2?.Dispose();

                    _isPlaying = false;
                    OnSoundFinished?.Invoke();
                }
            }, token);
        }

        public void StopCurrentSound()
        {
            if (_soundCancelTokenSource != null)
            {
                _soundCancelTokenSource.Cancel();
                _soundCancelTokenSource.Dispose();
                _soundCancelTokenSource = null;
            }
        }

        public void ProcessFinalPhrase(string recognizedText)
        {
            string cleanText = recognizedText.ToLower()
                                             .Replace("финально:", "")
                                             .Replace("слышу:", "")
                                             .Trim();

            Debug.WriteLine($"[Vosk Final] Пришло на обработку: '{cleanText}'");

            if (string.IsNullOrWhiteSpace(cleanText) || _phraseFiles.Count == 0) return;

            Task.Run(() =>
            {
                try
                {
                    string? bestMatchKey = null;
                    int bestScore = 0;
                    var inputWords = cleanText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    var strictResult = FuzzySharp.Process.ExtractOne(
                        cleanText,
                        _phraseFiles.Keys,
                        (s) => s,
                        FuzzySharp.SimilarityRatio.ScorerCache.Get<DefaultRatioScorer>()
                    );

                    if (strictResult != null && strictResult.Score > 85)
                    {
                        bestMatchKey = strictResult.Value;
                        bestScore = strictResult.Score;
                        Debug.WriteLine($"[Шаг 1 - Strict] Найдено: '{bestMatchKey}' со Score: {bestScore}");
                    }

                    if (bestMatchKey == null && cleanText.Length > 5)
                    {
                        string flatCleanText = cleanText.Replace(" ", "");

                        var betterLongKey = _phraseFiles.Keys
                            .FirstOrDefault(k =>
                            {
                                if (k.Length <= cleanText.Length) return false;

                                string flatKey = k.Replace(" ", "");
                                bool isSubstring = flatKey.Contains(flatCleanText);

                                if (!isSubstring)
                                {
                                    var partialResult = FuzzySharp.Process.ExtractOne(
                                        flatCleanText,
                                        new[] { flatKey },
                                        scorer: FuzzySharp.SimilarityRatio.ScorerCache.Get<PartialRatioScorer>()
                                    );

                                    return partialResult != null && partialResult.Score >= 90;
                                }

                                return isSubstring;
                            });

                        if (betterLongKey != null)
                        {
                            bestMatchKey = betterLongKey;
                            bestScore = 100;
                            Debug.WriteLine($"[Шаг 2 - Символьный маркер] Системный перехват на фразу: '{bestMatchKey}'");
                        }
                    }

                    if (bestMatchKey == null)
                    {
                        var tokenResult = FuzzySharp.Process.ExtractOne(
                            cleanText,
                            _phraseFiles.Keys,
                            (s) => s,
                            FuzzySharp.SimilarityRatio.ScorerCache.Get<TokenSetScorer>()
                        );

                        if (tokenResult != null && tokenResult.Score > 85)
                        {
                            bool isCandidateTooShort = cleanText.Length > tokenResult.Value.Length + 4;
                            bool isCandidateTooLong = cleanText.Length <= 5 && tokenResult.Value.Length > cleanText.Length + 4;

                            if (isCandidateTooShort)
                            {
                                Debug.WriteLine($"[Шаг 3 - Защита] Отклонен слишком короткий кандидат '{tokenResult.Value}' для длинного ввода '{cleanText}'");
                            }
                            else if (isCandidateTooLong)
                            {
                                Debug.WriteLine($"[Шаг 3 - Защита] Отклонен слишком длинный кандидат '{tokenResult.Value}' для короткого ввода '{cleanText}'");
                            }
                            else
                            {
                                bestMatchKey = tokenResult.Value;
                                bestScore = tokenResult.Score;
                                Debug.WriteLine($"[Шаг 3 - TokenSet] Найдено: '{bestMatchKey}' со Score: {bestScore}");
                            }
                        }
                    }

                    if (bestMatchKey == null)
                    {
                        var topPartialResults = FuzzySharp.Process.ExtractTop(
                            cleanText,
                            _phraseFiles.Keys,
                            (s) => s,
                            FuzzySharp.SimilarityRatio.ScorerCache.Get<PartialRatioScorer>(),
                            limit: 5
                        );

                        var validResults = topPartialResults.Where(res =>
                        {
                            if (res.Score <= 75) return false;

                            if (cleanText.Length > res.Value.Length + 4) return false;
                            if (cleanText.Length <= 5 && res.Value.Length > cleanText.Length + 5) return false;

                            if (res.Value.Length <= 4)
                            {
                                return inputWords.Contains(res.Value);
                            }

                            var wordsInKey = res.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            bool hasWholeWordMatch = inputWords.Any(w => wordsInKey.Contains(w));

                            if (!hasWholeWordMatch)
                            {
                                string flatKey = res.Value.Replace(" ", "");
                                string flatClean = cleanText.Replace(" ", "");
                                return flatKey.Contains(flatClean) || flatClean.Contains(flatKey);
                            }

                            return true;
                        }).ToList();

                        if (validResults.Any())
                        {
                            var bestPartial = validResults.OrderByDescending(r => r.Score).First();
                            bestMatchKey = bestPartial.Value;
                            bestScore = bestPartial.Score;
                            Debug.WriteLine($"[Шаг 4 - PartialTop] Найдено: '{bestMatchKey}' со Score: {bestScore}");
                        }
                    }

                    if (bestMatchKey != null && bestScore > 75)
                    {
                        if ((DateTime.Now - _lastPlayTime).TotalMilliseconds < 1000) return;

                        List<string> availableFiles = _phraseFiles[bestMatchKey];
                        string finalFilePath = availableFiles.Count > 1
                            ? availableFiles[Random.Shared.Next(availableFiles.Count)]
                            : availableFiles[0];

                        _lastPlayTime = DateTime.Now;
                        Debug.WriteLine($"[FuzzySharp] УСПЕХ! Запуск: {Path.GetFileName(finalFilePath)}");
                        PlaySound(finalFilePath);
                    }
                    else
                    {
                        Debug.WriteLine("[FuzzySharp] Отмена: Ничего не подошло.");
                        SaveMissingPhrase(cleanText);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FuzzySharp Error] Ошибка: {ex.Message}");
                }
            });
        }

        public void Stop()
        {
            _micInput?.StopRecording();
            _virtualOutput?.Stop();
            _monitorOutput?.Stop();

            _micInput?.Dispose();
            _virtualOutput?.Dispose();
            _monitorOutput?.Dispose();

            _micInput = null;
            _virtualOutput = null;
            _monitorOutput = null;
            _mixer = null;
            _monitorMixer = null;
            _cableBuffer = null;
            _monitorBuffer = null;
        }

        private readonly object _fileLock = new object();

        private void SaveMissingPhrase(string phrase)
        {
            try
            {
                string filePath = @"F:\missing_phrases.txt";

                lock (_fileLock)
                {
                    if (File.Exists(filePath))
                    {
                        var existingLines = File.ReadAllLines(filePath, Encoding.UTF8);
                        if (Array.Exists(existingLines, line => line.Equals(phrase, StringComparison.OrdinalIgnoreCase)))
                        {
                            return;
                        }
                    }

                    File.AppendAllText(filePath, phrase + Environment.NewLine, Encoding.UTF8);
                    Debug.WriteLine($"[Missing Phrases] Фраза '{phrase}' сохранена в {filePath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Missing Phrases Error] Не удалось записать в файл: {ex.Message}");
            }
        }

        public void StartSttRecording()
        {
            lock (_sttLock)
            {
                _sttAudioBuffer?.Dispose();
                _sttAudioBuffer = new MemoryStream();
                IsSttRecording = true;
            }
        }

        public byte[] StopSttRecording()
        {
            lock (_sttLock)
            {
                IsSttRecording = false;
                if (_sttAudioBuffer == null) return Array.Empty<byte>();

                byte[] audioData = _sttAudioBuffer.ToArray();
                _sttAudioBuffer.Dispose();
                _sttAudioBuffer = null;
                return audioData;
            }
        }

        public void Dispose()
        {
            Stop();
            StopCurrentSound();

            lock (_voskLock)
            {
                _recognizer?.Dispose();
                _recognizer = null;

                if (!_isModelExternal)
                {
                    _voskModel?.Dispose();
                    _voskModel = null;
                }
            }

            lock (_sttLock)
            {
                _sttAudioBuffer?.Dispose();
                _sttAudioBuffer = null;
            }
        }
    }
}