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
    public class AudioProcessor
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
        private bool _isPlaying = false;
        private readonly object _voskLock = new object();

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private string _lastLoadedFolderPath = string.Empty;

        public byte CurrentEmulationKey { get; set; } = 0x47; // По умолчанию клавиша 'G'
        public bool IsMonitoringEnabled { get; set; } = false;
        public bool IsListenMode { get; private set; } = false;
        public bool IsEmulationEnabled { get; set; } = true;
        // Громкость для наушников (мониторинг) и для кабеля (чат игры)
        public float VolumeMic { get; set; } = 1.0f;
        public float VolumeCable { get; set; } = 1.0f;

        private Dictionary<string, List<string>> _phraseFiles = new Dictionary<string, List<string>>();
        public Action<string>? OnPhraseRecognized;

        private readonly string _modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model");
        private const string VoskModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip";
        private CancellationTokenSource? _soundCancelTokenSource;
        public event Action? OnSoundFinished;

        // Событие, чтобы передавать статус загрузки в MainWindow (на UI)
        public event Action<string>? OnVoskStatusChanged;
        private DateTime _lastPlayTime = DateTime.MinValue;

        public async Task InitializeVoskAsync()
        {
            try
            {
                if (!Directory.Exists(_modelPath))
                {
                    OnVoskStatusChanged?.Invoke("Модель ИИ отсутствует. Скачивание (около 50 МБ)...");
                    await DownloadAndExtractModelAsync();
                }

                OnVoskStatusChanged?.Invoke("Загрузка модели ИИ в память...");

                // Запускаем тяжелую инициализацию Vosk в отдельном потоке, чтобы UI не зависал
                await Task.Run(() =>
                {
                    Vosk.Vosk.SetLogLevel(-1); // Отключаем лишний спам Vosk в консоль
                    _voskModel = new Vosk.Model(_modelPath);
                    _recognizer = new Vosk.VoskRecognizer(_voskModel, 44100.0f);
                });

                OnVoskStatusChanged?.Invoke("Модель ИИ успешно загружена и готова.");
            }
            catch (Exception ex)
            {
                OnVoskStatusChanged?.Invoke($"Ошибка ИИ: {ex.Message}");
                Debug.WriteLine($"[Vosk Init Error] {ex}");
            }
        }

        private async Task DownloadAndExtractModelAsync()
        {
            string zipPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vosk_model.zip");
            string tempExtractPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp_model");

            using (var client = new HttpClient())
            {
                // Скачиваем архив во временный файл
                var response = await client.GetAsync(VoskModelUrl);
                response.EnsureSuccessStatusCode();
                using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs);
                }
            }

            OnVoskStatusChanged?.Invoke("Распаковка модели ИИ...");

            // Распаковываем во временную папку
            if (Directory.Exists(tempExtractPath)) Directory.Delete(tempExtractPath, true);
            ZipFile.ExtractToDirectory(zipPath, tempExtractPath);

            // Vosk в архиве хранит папку внутри папки. Нам нужно вытащить внутренности и назвать папку просто "model"
            string innerFolder = Directory.GetDirectories(tempExtractPath)[0];
            Directory.Move(innerFolder, _modelPath);

            // Чистим мусор за собой
            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (Directory.Exists(tempExtractPath)) Directory.Delete(tempExtractPath, true);
        }

        public void InitVosk(string modelPath)
        {
            if (_voskModel != null) return;

            try
            {
                _voskModel = new Model(modelPath);
                // Задаем частоту 44100Гц, так как микрофон будет писать в ней
                _recognizer = new VoskRecognizer(_voskModel, 44100.0f);
                Debug.WriteLine("[Vosk] Модель успешно загружена.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Vosk Error] Ошибка загрузки модели: {ex.Message}");
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
            // Запоминаем путь к папке, чтобы потом открыть её в проводнике
            _lastLoadedFolderPath = folderPath;

            _phraseFiles.Clear();
            if (!Directory.Exists(folderPath)) return;

            // Хеш-сет для быстрой проверки расширений (.ToLower() для безопасности)
            var extensions = new HashSet<string> { ".mp3", ".wav", ".ogg", ".flac", ".mpeg" };
            int totalFilesCounter = 0;

            try
            {
                // 1. Сканируем папку ОДИН раз на всю глубину (SearchOption.AllDirectories)
                // И берем вообще все файлы (*.*)
                var allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);

                // 2. Фильтруем файлы по расширению и заполняем словарь
                foreach (var file in allFiles)
                {
                    string ext = Path.GetExtension(file).ToLower();

                    // Если расширение файла есть в нашем списке — обрабатываем его
                    if (extensions.Contains(ext))
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file).ToLower().Trim();

                        // РЕГУЛЯРКА: Очищаем имя от любых приписок в круглых скобках
                        // 1. Сначала очищаем круглые скобки, если они есть: "нет 1 (старая)" -> "нет 1"
                        string cleanKey = System.Text.RegularExpressions.Regex.Replace(fileName, @"\s*\(.*?\)", "").Trim();

                        // 2. РЕГУЛЯРКА ДЛЯ ЦИФР: Очищаем пробелы и цифры на конце строки: "нет 1" -> "нет", "нет 05" -> "нет"
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

        // Исправленный метод открытия папки
        public void OpenPhrasesFolder()
        {
            try
            {
                // Если папка еще не была загружена или путь пустой, берем дефолтный путь в корне программы
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

            // 1. Ищем микрофон среди устройств ЗАПИСИ (WaveIn)
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

            // 2. Ищем виртуальный кабель среди устройств ВОСПРОИЗВЕДЕНИЯ (WaveOut)
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                string name = WaveOut.GetCapabilities(i).ProductName;
                Debug.WriteLine($"[Audio Scan] Устройство вывода [{i}]: {name}");

                // Ищем конкретно 16-канальный кабель, так как он гарантированно рабочий в системе
                if (name.Contains("16ch", StringComparison.OrdinalIgnoreCase))
                {
                    cableId = i;
                    break; // Нашли наш рабочий 16-канальный кабель — останавливаем поиск!
                }
            }

            // На случай, если вдруг 16ch не нашелся (страховка), сделаем запасной поиск обычного кабеля
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

            // Если звуковое устройство наушников уже запущено — на лету меняем ему громкость
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

            // 1. Настройка микшера для Виртуального Кабеля
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1)) { ReadFully = true };
            _cableBuffer = new BufferedWaveProvider(format) { DiscardOnBufferOverflow = true };
            _mixer.AddMixerInput(_cableBuffer.ToSampleProvider());

            _virtualOutput = new WaveOutEvent { DeviceNumber = cableIndex, DesiredLatency = 100 };
            _virtualOutput.Init(_mixer);

            // 2. Настройка микшера для Наушников (Мониторинг)
            _monitorMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1)) { ReadFully = true };
            _monitorBuffer = new BufferedWaveProvider(format) { DiscardOnBufferOverflow = true };
            _monitorMixer.AddMixerInput(_monitorBuffer.ToSampleProvider());

            _monitorOutput = new WaveOutEvent { DeviceNumber = monitorIndex, DesiredLatency = 100 };
            _monitorOutput.Init(_monitorMixer);
            _monitorOutput.Volume = IsMonitoringEnabled ? 1.0f : 0.0f;

            // 3. Настройка микрофона
            _micInput = new WaveInEvent
            {
                DeviceNumber = micIndex,
                WaveFormat = format,
                BufferMilliseconds = 50
            };

            _micInput.DataAvailable += (s, e) =>
            {
                // ЕСЛИ СЕЙЧАС ИГРАЕТ ФРАЗА — полностью игнорируем микрофон для кабеля!
                if (!_isPlaying && !IsListenMode)
                {
                    _cableBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                }
                else
                {
                    // Пока играет фраза, мы можем очищать буфер микрофона, чтобы звук не копился,
                    // но благодаря '_isPlaying' твой живой голос в кабель физически не пролезет.
                }

                // При этом ИИ Vosk может продолжать слушать (если нужно), 
                // но так как кнопка активации Vosk в MainWindow уже отпущена, этот блок сработает штатно.
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

            // МЕХАНИЗМ ОСТАНОВКИ: Если что-то уже играет — жестко прерываем перед запуском новой фразы
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
                    // Твой стабильный код с AudioFileReader
                    reader = new AudioFileReader(path);
                    var resampler = new WdlResamplingSampleProvider(reader, cableMixer.WaveFormat.SampleRate);
                    volumeCable = new VolumeSampleProvider(resampler.ToMono()) { Volume = VolumeCable };

                    if (headMixer != null)
                    {
                        reader2 = new AudioFileReader(path);
                        var resampler2 = new WdlResamplingSampleProvider(reader2, headMixer.WaveFormat.SampleRate);
                        volumeHead = new VolumeSampleProvider(resampler2.ToMono()) { Volume = VolumeMic };
                    }

                    // ИЗМЕНЕНИЕ 1: Эмулируем зажатие клавиши в Доте только если эмуляция ВКЛЮЧЕНА
                    if (IsEmulationEnabled)
                    {
                        keybd_event(CurrentEmulationKey, 0, 0, 0);
                    }
                    Thread.Sleep(150);

                    // Подмешиваем звук в тракты одновременно
                    cableMixer.AddMixerInput(volumeCable);
                    if (volumeHead != null && headMixer != null)
                    {
                        headMixer.AddMixerInput(volumeHead);
                    }

                    // БЕЗОПАСНОЕ НЕБЛОКИРУЮЩЕЕ ОЖИДАНИЕ (с возможностью прерывания)
                    int duration = (int)reader.TotalTime.TotalMilliseconds + 400;
                    int elapsed = 0;
                    int step = 50; // Проверяем флаг отмены каждые 50 мс

                    while (elapsed < duration)
                    {
                        if (token.IsCancellationRequested)
                        {
                            Debug.WriteLine("[PlaySound] Воспроизведение прервано пользователем.");
                            break;
                        }
                        Thread.Sleep(step);
                        elapsed += step;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PlaySound Error] Ошибка: {ex.Message}");
                }
                finally
                {
                    // ГАРАНТИРОВАННЫЙ СБРОС (вызовется всегда, даже при экстренной остановке)

                    // ИЗМЕНЕНИЕ 2: Отжимаем кнопку микрофона в Доте только если эмуляция ВКЛЮЧЕНА
                    if (IsEmulationEnabled)
                    {
                        keybd_event(CurrentEmulationKey, 0, KEYEVENTF_KEYUP, 0);
                    }

                    // Удаляем аудио из микшеров
                    if (volumeCable != null) cableMixer.RemoveMixerInput(volumeCable);
                    if (volumeHead != null && headMixer != null)
                    {
                        headMixer.RemoveMixerInput(volumeHead);
                    }

                    // Освобождаем файлы
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
            // Быстрая очистка строки
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

                    // =========================================================================
                    // ШАГ 1: ТОЧНОЕ ИЛИ МАКСИМАЛЬНО БЛИЗКОЕ СОВПАДЕНИЕ (RatioScorer)
                    // =========================================================================
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

                    // =========================================================================
                    // ШАГ 2: СИСТЕМНЫЙ ПЕРЕХВАТ ДЛЯ ДЛИННЫХ ФРАЗ (БЕЗУПРЕЧНЫЙ СИМВОЛЬНЫЙ ПОИСК)
                    // =========================================================================
                    if (bestMatchKey == null && cleanText.Length > 5)
                    {
                        // Схлопываем ввод в одну сплошную строку букв
                        string flatCleanText = cleanText.Replace(" ", "");

                        // Ищем в базе фразу, которая длиннее ввода, но содержит в себе 
                        // практически весь наш набор букв в правильном порядке
                        var betterLongKey = _phraseFiles.Keys
                            .FirstOrDefault(k =>
                            {
                                if (k.Length <= cleanText.Length) return false;

                                // Схлопываем ключ из базы в строку букв
                                string flatKey = k.Replace(" ", "");

                                // Проверяем, входит ли плоский ввод как неразрывная подстрока
                                bool isSubstring = flatKey.Contains(flatCleanText);

                                // Если это не чистая подстрока, проверяем нечетко (для изменений окончаний вроде "будет/будут")
                                if (!isSubstring)
                                {
                                    // Используем PartialRatio на плоских строках, чтобы прощать опечатки движка
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

                    // =========================================================================
                    // ШАГ 3: СТАНДАРТНЫЙ ПОИСК ПО СЛОВАМ (TokenSetScorer С ДВУХСТОРОННЕЙ ЗАЩИТОЙ)
                    // =========================================================================
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
                            // 1. ЗАЩИТА СВЕРХУ: Ввод длинный ("да но будет"), а кандидат короткий ("да")
                            bool isCandidateTooShort = cleanText.Length > tokenResult.Value.Length + 4;

                            // 2. ЗАЩИТА СНИЗУ: Ввод короткий ("что"), а кандидат огромный ("с давних времен...")
                            // Если ввод короткий (<= 5 символов), то кандидат не должен быть длиннее ввода более чем на 4 символа
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

                    // =========================================================================
                    // ШАГ 4: ОТКАТ НА ЧАСТИЧНЫЙ ПОИСК ДЛЯ ОБРУБКОВ (С ДВУХСТОРОННЕЙ ЗАЩИТОЙ ПО ДЛИНЕ)
                    // =========================================================================
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

                            // 1. ЗАЩИТА ПО ДЛИНЕ (Аналогично Шагу 3)
                            // Если ввод длинный ("да ну будет"), кандидат не должен быть слишком коротким ("да")
                            if (cleanText.Length > res.Value.Length + 4) return false;

                            // Если ввод короткий ("что"), кандидат не должен быть слишком длинным ("я вот что скажу...")
                            if (cleanText.Length <= 5 && res.Value.Length > cleanText.Length + 5) return false;


                            // 2. ПОСЛОВНАЯ ПРОВЕРИФИКАЦИЯ
                            // Если ключ сверхкороткий (типа "мур", "да"), он обязан быть во вводе как отдельное слово
                            if (res.Value.Length <= 4)
                            {
                                return inputWords.Contains(res.Value);
                            }

                            // Для остальных случаев: хотя бы одно слово из ввода должно быть полноценным словом в ключе базы,
                            // либо плоские строки должны иметь пересечение (на случай разрезов Vosk-а)
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

                    // =========================================================================
                    // ФИНАЛЬНЫЙ ЗАПУСК ЗВУКА
                    // =========================================================================
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
    }
}