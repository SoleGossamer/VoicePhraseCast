using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Vosk;

namespace VoicePhraseCast
{
    /// <summary>Результат попытки начать диктовку (AudioProcessor.BeginDictation).</summary>
    public enum DictationStart
    {
        Started,
        Busy,            // предыдущая диктовка ещё завершается
        NoRecognizer,    // модель Vosk не загружена
        NotRunning       // мост не запущен
    }

    /// <summary>
    /// Аудиоядро: захват микрофона, проход голоса в виртуальный кабель, воспроизведение фраз, распознавание (Vosk).
    ///
    /// Тракт живого голоса: микрофон → LiveVoiceBuffer (подушка + защита от недобора) → микшер → ограничитель пиков → вывод в кабель (16 бит).
    /// Тракт фраз: кэш (float, моно; громкость измерена при декодировании) → CachedSoundProvider (выравнивание громкости под потолок пика,
    /// одинаковое для кабеля и наушников; мягкий гейт пауз у сильно усиленных фраз; плавные вход и выход) → тот же микшер.
    /// Поток записи Vosk не ждёт: чанк копируется в очередь (PcmPump), распознаёт отдельный поток.
    ///
    /// Эфир (LiveGateOpen) закрыт, пока идёт запись фразы (_listen), пока принимается решение «играть или нет» (_holdUntil),
    /// пока звучит фраза (_isPlaying) и во время диктовки с глушением (_dictMute). Закрытый эфир отсекается дважды: поток записи
    /// не кладёт микрофон в буфер, а поток вывода (LiveVoiceBuffer.Read) выбрасывает уже накопленное и отдаёт тишину —
    /// живой голос не может оказаться в одном блоке с фразой, а ограничитель пиков страхует остальные случаи (две фразы подряд).
    ///
    /// Правила потоков:
    ///  • блокировки _lifecycle, _playLock, _voskLock, _fileLock никогда не вкладываются друг в друга;
    ///  • события (OnPhraseRecognized, OnSoundFinished, DeviceFailed) вызываются только вне блокировок;
    ///  • устройства NAudio создаются в Task.Run: у потока пула нет SynchronizationContext, поэтому события приходят
    ///    из потоков NAudio, а синхронное ожидание на UI-потоке не может зависнуть;
    ///  • потоки записи и вывода регистрируются в MMCSS (AudioThread): игра, загрузившая все ядра, не должна их задерживать;
    ///  • Vosk.Model этому классу не принадлежит (владелец — BridgeController), класс освобождает только свой VoskRecognizer.
    /// Вызовы StartAsync/StopAsync/AttachModelAsync сериализуются снаружи (BridgeController).
    /// </summary>
    public sealed class AudioProcessor : IDisposable
    {
        /// <summary>Единая частота тракта: микрофон, кабель, наушники, кэш фраз и Vosk. Совпадает с форматом по умолчанию большинства устройств Windows.</summary>
        internal const int SampleRate = 48000;

        private const int CaptureBufferMs = 30;        // чанк записи
        private const int CaptureBuffers = 4;          // 4 × 30 мс запаса на случай, если поток записи задержится
        private const int OutputBuffers = 4;           // буферов вывода: запас при задержке потока вывода = (N-1)/N от DesiredLatency
        private const int MinOutputLatencyMs = 100;    // ниже MME-вывод на загруженной игрой машине даёт микропаузы
        private const int MaxOutputLatencyMs = 300;    // совпадает с верхней границей в MainWindow.ApplyAudioSettings
        private const int ListenTailMs = 100;          // добор хвоста речи после отпускания клавиши фраз
        private const int DictationTailMs = 100;       // то же для диктовки; микрофон в игре в это время ещё глушится
        private const int PartialIntervalMs = 150;     // partial-результаты не чаще ~6 раз в секунду
        private const int PlayDebounceMs = 1000;
        private const int PostListenHoldMs = 1000;     // потолок ожидания решения «играть / не играть» после отпускания клавиши фраз: эфир закрыт
        private const int StopWaitMs = 500;
        private const int MicSilenceTimeoutMs = 3000;  // тишина от драйвера дольше этого времени = устройство умерло
        private const int PrefetchMinScore = 92;
        private const int PostRollMs = 250;            // запас после конца фразы (поверх задержки вывода) до отпускания рации
        private static readonly TimeSpan FlushTimeout = TimeSpan.FromMilliseconds(400);

        // ---- настройки, которые можно менять на лету (читаются из потоков аудио) ----
        private volatile int _radioKeyVk = 0x47;       // 'G'
        private volatile bool _isEmulationEnabled = true;
        private volatile bool _isMonitoringEnabled;
        private volatile float _volumeMic = 1f;
        private volatile float _targetPeakDb = -3f;    // потолок пика фраз, dBFS (громкость фраз в кабеле и в наушниках выравнивается под него)
        private volatile bool _muteMicDuringStt = true;
        private int _outputLatencyMs = 120;

        // ---- устройства (_lifecycle) ----
        private readonly object _lifecycle = new();
        private volatile bool _running;
        private WaveInEvent? _micInput;
        private WaveOutEvent? _virtualOutput;
        private WaveOutEvent? _monitorOutput;
        private System.Threading.Timer? _watchdog;
        private volatile MixingSampleProvider? _cableMixer;
        private volatile MixingSampleProvider? _headMixer;
        private volatile LiveVoiceBuffer? _live;
        private volatile PcmPump? _pump;
        private volatile int _appliedLatencyMs = 120;
        private long _lastMicTick;
        private int _failureRaised;
        private LiveStats _lastStats;                  // только таймер watchdog

        // ---- воспроизведение (_playLock) ----
        private readonly object _playLock = new();
        private CancellationTokenSource? _playCts;
        private Task _playTask = Task.CompletedTask;
        private long _playGen;
        private int _pressedRadioVk = -1;              // какая клавиша рации сейчас нажата нами (-1 — никакая)
        private volatile bool _isPlaying;
        private volatile CachedSoundProvider? _curCable;
        private volatile CachedSoundProvider? _curHead;
        private long _lastPlayTick;
        private long _holdUntil;                       // Environment.TickCount64, до которого эфир закрыт после отпускания клавиши фраз (0 — не держим)
        private long _mutedBytes;                      // диагностика (только Debug): сколько микрофона шлюз удержал за текущую фразу, байт PCM
        private volatile int _mutedPeak;               // и его пик, 0…32768; пишет поток записи, сбрасывает поток фразы
        private readonly PhraseAudioCache _cache = new(SampleRate);

        // ---- распознавание фраз (_voskLock) ----
        private readonly object _voskLock = new();
        private VoskRecognizer? _recognizer;
        private readonly StringBuilder _utterance = new();
        private string _lastPartial = "";
        private long _listenGen;
        private volatile bool _listen;
        private long _lastPartialTick;                 // только поток насоса

        // ---- диктовка (STT) ----
        private const int DictIdle = 0, DictRecording = 1, DictFinishing = 2;
        private int _dictState;
        private volatile bool _sttFeed;                // чанки копируются в распознаватель диктовки
        private volatile bool _dictMute;               // снимок MuteMicDuringStt на начало диктовки: true — микрофон в кабель не идёт

        // ---- база фраз ----
        private volatile PhraseIndex _phrases = PhraseIndex.Empty;
        private string _lastLoadedFolderPath = string.Empty;
        private int _prefetchBusy;
        private string? _lastPrefetchKey;

        // ---- журнал промахов (_fileLock) ----
        private static readonly string MissingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoicePhraseCast", "missing_phrases.txt");
        private readonly object _fileLock = new();
        private readonly HashSet<string> _missingSeen = new(StringComparer.OrdinalIgnoreCase);
        private bool _missingLoaded;

        private int _disposed;

        // ================= публичный интерфейс =================

        /// <summary>Распознанный текст (накопленные сегменты + текущий partial). Приходит из потока насоса: UI только через Dispatcher.</summary>
        public event Action<string>? OnPhraseRecognized;
        public event Action? OnSoundFinished;
        public event Action<string>? OnVoskStatusChanged;
        /// <summary>Микрофон или вывод перестали работать посреди сеанса (выдернули USB, перезапустился аудиосервис).</summary>
        public event Action<string>? DeviceFailed;

        /// <summary>Распознаватель диктовки. Его владелец — BridgeController; AudioProcessor только вызывает Begin/Feed/End/Cancel.</summary>
        public SpeechToTextService? Stt { get; set; }

        public int RadioKeyVk { get => _radioKeyVk; set => _radioKeyVk = value; }
        public bool IsEmulationEnabled { get => _isEmulationEnabled; set => _isEmulationEnabled = value; }

        /// <summary>Глушить микрофон в кабеле на время диктовки. Читается в момент начала диктовки: смена флажка посреди записи её не затрагивает.</summary>
        public bool MuteMicDuringStt { get => _muteMicDuringStt; set => _muteMicDuringStt = value; }

        public bool LogMissingPhrases { get; set; }
        public int RadioWarmupMs { get; set; } = 150;      // подбирается экспериментально под вашу игру

        /// <summary>
        /// Задержка вывода, мс (100–300; значение вне диапазона подтягивается к границе). Это глубина очереди MME: чем она больше, тем
        /// устойчивее звук под нагрузкой от игры и тем позже он слышен. Применяется при следующем запуске моста.
        /// </summary>
        public int OutputLatencyMs
        {
            get => _outputLatencyMs;
            set => _outputLatencyMs = Math.Clamp(value, MinOutputLatencyMs, MaxOutputLatencyMs);
        }

        public bool IsListenMode => _listen;
        public bool IsRunning => _running;
        public int PhraseCount => _phrases.Count;

        /// <summary>Идёт диктовка (запись или завершение).</summary>
        public bool IsDictating => Volatile.Read(ref _dictState) != DictIdle;

        /// <summary>В текущей диктовке микрофон в кабель не идёт.</summary>
        public bool IsDictationMuted => _dictMute;

        public bool IsMonitoringEnabled
        {
            get => _isMonitoringEnabled;
            set
            {
                _isMonitoringEnabled = value;
                try
                {
                    var head = _monitorOutput;
                    if (head != null) head.Volume = value ? 1f : 0f;   // уже играющая фраза в наушниках обрывается сразу
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Audio] Громкость мониторинга: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Громкость «для себя» (наушники), 0…1. Это подстройка относительно выровненного уровня (см. TargetPeakDb), а не громкость
        /// файла: 1 — фразы в наушниках звучат так же громко, как уходят в кабель, меньше — тише. Действует и на уже играющую фразу.
        /// </summary>
        public float VolumeMic
        {
            get => _volumeMic;
            set
            {
                _volumeMic = value;
                ApplyLevel(_curHead, _targetPeakDb, value);
            }
        }

        public const float MinTargetPeakDb = -24f;
        public const float MaxTargetPeakDb = 0f;

        /// <summary>
        /// Потолок пика фраз, dBFS: −24…0. Громкость фраз выравнивается по их активной части (без тишины и пауз, с учётом слуха;
        /// см. CachedSound.GainFor): тихий шёпот подтягивается, плотная музыка приглушается, а пик обычной речи ложится на этот
        /// уровень. Ни один отсчёт не выше потолка, перегруза нет (0 dBFS — максимум). Выравнивание одно на оба тракта: в кабель
        /// и в наушники («для себя») фразы идут с одним усилением, в наушниках сверху ещё VolumeMic.
        /// Действует и на уже играющую фразу.
        /// </summary>
        public float TargetPeakDb
        {
            get => _targetPeakDb;
            set
            {
                float db = Math.Clamp(value, MinTargetPeakDb, MaxTargetPeakDb);
                _targetPeakDb = db;
                ApplyLevel(_curCable, db, 1f);
                ApplyLevel(_curHead, db, _volumeMic);
            }
        }

        // Уровень играющей фразы при потолке ceilingDb: выровненное усиление (× trim — ползунок «для себя» для наушников) и сила гейта пауз.
        // Сила гейта зависит только от усиления выравнивания, не от trim: в кабеле и в наушниках пауза звучит одинаково.
        private static void ApplyLevel(CachedSoundProvider? provider, float ceilingDb, float trim)
        {
            if (provider == null) return;

            provider.Gain = provider.Sound.GainFor(ceilingDb) * trim;
            provider.GateStrength = provider.Sound.GateStrengthFor(ceilingDb);
        }

        // Settings.ReferenceCrestDb с защитой: сбой чтения настроек не должен мешать запуску моста, тогда берётся значение по умолчанию.
        private static float LoadReferenceCrestDb()
        {
            try
            {
                return CachedSound.ClampReferenceCrest(VoicePhraseCast.Properties.Settings.Default.ReferenceCrestDb);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] ReferenceCrestDb не прочитан, берётся {CachedSound.DefaultReferenceCrestDb:F0} дБ: {ex.Message}");
                return CachedSound.DefaultReferenceCrestDb;
            }
        }

        // ================= жизненный цикл устройств =================

        public Task StartAsync(int micIndex, int cableIndex, int monitorIndex) => Task.Run(() =>
        {
            lock (_lifecycle)
            {
                if (_running) return;

                WaveInEvent? mic = null;
                WaveOutEvent? cable = null, head = null;
                PcmPump? pump = null;
                try
                {
                    // Запас «пик над громкостью» из настроек: от него зависит подготовка фраз, при смене кэш очищается и фразы готовятся заново.
                    _cache.ReferenceCrestDb = LoadReferenceCrestDb();

                    int latency = _outputLatencyMs;
                    int chunkMs = (latency + OutputBuffers - 1) / OutputBuffers;   // столько звука вывод запрашивает за один раз

                    var pcm = new WaveFormat(SampleRate, 1);
                    var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

                    // Подушка живого голоса: размер запроса вывода + чанк записи + запас на неровность потоков.
                    // Шлюз эфира проверяется потоком вывода на каждом чтении: закрытый эфир отсекается жёстко, без отдельных сбросов подушки.
                    var live = new LiveVoiceBuffer(SampleRate, chunkMs + CaptureBufferMs + 10, LiveGateOpen);
                    var cableMixer = new MixingSampleProvider(floatFormat) { ReadFully = true };
                    cableMixer.AddMixerInput(live);
                    var headMixer = new MixingSampleProvider(floatFormat) { ReadFully = true };

                    // 16 бит вместо float32: самый совместимый формат для MME и виртуальных кабелей, вдвое меньше данных.
                    // Микшер NAudio просто складывает источники, а SampleToWaveProvider16 срезает всё выше ±1.0 («хрип»):
                    // ограничитель пиков между ними превращает редкую сумму двух фраз в короткое приглушение вместо среза.
                    cable = new WaveOutEvent { DeviceNumber = cableIndex, DesiredLatency = latency, NumberOfBuffers = OutputBuffers };
                    cable.Init(new SampleToWaveProvider16(new ThreadBoostProvider(new PeakLimiterProvider(cableMixer))));

                    head = new WaveOutEvent { DeviceNumber = monitorIndex, DesiredLatency = latency, NumberOfBuffers = OutputBuffers };
                    head.Init(new SampleToWaveProvider16(new ThreadBoostProvider(new PeakLimiterProvider(headMixer))));
                    head.Volume = _isMonitoringEnabled ? 1f : 0f;

                    pump = new PcmPump(PumpSink);

                    mic = new WaveInEvent
                    {
                        DeviceNumber = micIndex,
                        WaveFormat = pcm,
                        BufferMilliseconds = CaptureBufferMs,
                        NumberOfBuffers = CaptureBuffers
                    };
                    mic.DataAvailable += OnMicData;
                    mic.RecordingStopped += OnMicStopped;
                    cable.PlaybackStopped += OnOutputStopped;
                    head.PlaybackStopped += OnOutputStopped;

                    _lastMicTick = Environment.TickCount64;
                    Interlocked.Exchange(ref _failureRaised, 0);
                    _lastStats = default;
                    _appliedLatencyMs = latency;

                    // Всё, что читает OnMicData, публикуется до StartRecording.
                    _live = live;
                    _pump = pump;
                    _cableMixer = cableMixer;
                    _headMixer = headMixer;

                    cable.Play();
                    head.Play();
                    mic.StartRecording();

                    _micInput = mic;
                    _virtualOutput = cable;
                    _monitorOutput = head;
                    _running = true;
                    _watchdog = new System.Threading.Timer(WatchdogTick, null, 1000, 1000);

                    AudioThread.Release();   // страховка: этот поток пула приоритет получать не должен, даже если NAudio прочитал первый блок синхронно
                }
                catch
                {
                    // Откат: ни одно устройство не остаётся занятым (иначе CABLE занят до выхода из приложения).
                    AudioThread.Release();
                    TryDispose(mic);
                    TryDispose(cable);
                    TryDispose(head);
                    pump?.Dispose();
                    _live = null;
                    _pump = null;
                    _cableMixer = null;
                    _headMixer = null;
                    throw;
                }
            }
        });

        public async Task StopAsync()
        {
            WaveInEvent? mic;
            WaveOutEvent? cable, head;
            System.Threading.Timer? watchdog;
            PcmPump? pump;

            lock (_lifecycle)
            {
                if (!_running) return;
                _running = false;                       // с этого момента остановка устройств не считается сбоем

                (mic, cable, head, watchdog, pump) = (_micInput, _virtualOutput, _monitorOutput, _watchdog, _pump);
                (_micInput, _virtualOutput, _monitorOutput, _watchdog) = (null, null, null, null);
                _cableMixer = null;
                _headMixer = null;
                _live = null;
                _pump = null;
            }

            watchdog?.Dispose();

            // Прослушивание и диктовка прерываются: после остановки моста «хвосты» никому не нужны.
            CancelDictation();
            lock (_voskLock)
            {
                _listen = false;
                _utterance.Clear();
                _lastPartial = "";
            }

            // Рация должна быть отпущена до закрытия устройств: сначала отмена фразы, затем ограниченное ожидание.
            Task play;
            lock (_playLock)
            {
                _playCts?.Cancel();
                play = _playTask;
            }
            await Task.WhenAny(play, Task.Delay(StopWaitMs)).ConfigureAwait(false);
            lock (_playLock) ReleaseRadioKeyLocked();   // страховка, если задача не успела (например, шло декодирование)

            // Обработчики OnMicStopped/OnOutputStopped остаются подписанными: в них потоки NAudio возвращают себе обычный приоритет.
            // Сбоем остановка не считается: _running уже false.
            if (mic != null)
            {
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                mic.DataAvailable -= OnMicData;
                mic.RecordingStopped += (_, _) => stopped.TrySetResult();
                try { mic.StopRecording(); } catch (Exception ex) { Debug.WriteLine($"[Audio] StopRecording: {ex.Message}"); }
                await Task.WhenAny(stopped.Task, Task.Delay(StopWaitMs)).ConfigureAwait(false);   // ждём поток записи, но не вечно
                TryDispose(mic);
            }

            TryDispose(cable);
            TryDispose(head);
            pump?.Dispose();   // после остановки записи: новых чанков уже не будет
        }

        private static void TryDispose(IDisposable? device)
        {
            try { device?.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"[Audio] Dispose: {ex.Message}"); }
        }

        private void OnMicStopped(object? sender, StoppedEventArgs e)
        {
            AudioThread.Release();   // событие приходит из самого потока записи, который сейчас завершается
            if (!ReferenceEquals(sender, _micInput) || !_running) return;   // остановка по нашей просьбе или устаревшее устройство
            RaiseDeviceFailed(e.Exception != null
                ? $"Микрофон остановлен: {e.Exception.Message}"
                : "Запись с микрофона остановлена системой");
        }

        private void OnOutputStopped(object? sender, StoppedEventArgs e)
        {
            AudioThread.Release();   // то же для потока вывода
            if (!_running || (!ReferenceEquals(sender, _virtualOutput) && !ReferenceEquals(sender, _monitorOutput))) return;
            RaiseDeviceFailed(e.Exception != null
                ? $"Вывод звука остановлен: {e.Exception.Message}"
                : "Вывод звука остановлен системой");
        }

        // Некоторые драйверы умирают без единого события: watchdog следит за потоком данных от микрофона.
        private void WatchdogTick(object? state)
        {
            if (!_running) return;
            if (Environment.TickCount64 - Interlocked.Read(ref _lastMicTick) > MicSilenceTimeoutMs)
                RaiseDeviceFailed("Микрофон перестал передавать данные");

            LogStats();
        }

        // Если заикания останутся, эти строки в окне «Вывод» Visual Studio покажут, где рвётся звук: в нашей подушке или дальше (драйвер, кабель, игра).
        private void LogStats()
        {
            var live = _live;
            if (live == null) return;

            LiveStats s = live.Snapshot();
            long dropped = _pump?.Dropped ?? 0;
            if (s.Underruns != _lastStats.Underruns || s.Trims != _lastStats.Trims || s.Pads != _lastStats.Pads ||
                s.Overflows != _lastStats.Overflows || dropped != _lastStats.Dropped)
            {
                Debug.WriteLine($"[Live] недобор={s.Underruns} срезов={s.Trims} вставок тишины={s.Pads} переполнений={s.Overflows} " +
                                $"отброшено для Vosk={dropped} подушка={s.BufferedMs} мс (выученный запас {s.ExtraMs} мс)");
            }
            _lastStats = s with { Dropped = dropped };
        }

        private void RaiseDeviceFailed(string message)
        {
            if (Interlocked.Exchange(ref _failureRaised, 1) == 1) return;   // один отчёт на сеанс
            DeviceFailed?.Invoke(message);
        }

        // ================= горячий путь захвата =================

        private void OnMicData(object? sender, WaveInEventArgs e)
        {
            // Исключение отсюда NAudio превращает в тихую остановку записи навсегда, поэтому ловим всё.
            try
            {
                AudioThread.Boost();   // один раз на поток: дальше это проверка булева поля
                Interlocked.Exchange(ref _lastMicTick, Environment.TickCount64);

                byte[] data = e.Buffer;
                int count = e.BytesRecorded;
                if (count <= 0) return;

                // 1. Распознавание. Чанк копируется в очередь и обрабатывается другим потоком: Vosk может думать дольше 30 мс,
                //    а поток записи, который задержался, теряет звук (у драйвера всего 4 × 30 мс запаса).
                var targets = RecognizeTargets.None;
                long gen = 0;
                if (_sttFeed) targets |= RecognizeTargets.Dictation;
                if (_listen)
                {
                    targets |= RecognizeTargets.Phrase;
                    gen = Interlocked.Read(ref _listenGen);
                }
                if (targets != RecognizeTargets.None) _pump?.TryPost(data, count, targets, gen);

                // 2. Живой голос в кабель — только пока эфир открыт. Закрытый эфир сюда не пишется вообще, а то, что успело
                //    накопиться до закрытия, поток вывода выбросит сам (LiveVoiceBuffer.Read): отдельного «сброса» не нужно.
                var live = _live;
                if (live != null)
                {
                    if (LiveGateOpen()) live.Write(data, 0, count);
                    else if (_isPlaying) NoteMuted(data, count);   // только Debug: считаем, сколько микрофона не пущено за время фразы
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Mic] {ex}");
            }
        }

        /// <summary>
        /// Пускать ли микрофон в кабель прямо сейчас. Единственное место, где это решается: его спрашивают и поток записи (что класть
        /// в буфер), и поток вывода (что отдавать в кабель). Закрыт, если идёт запись фразы, не истёк срок ожидания решения после неё,
        /// звучит фраза или идёт диктовка с глушением.
        /// </summary>
        private bool LiveGateOpen() =>
            !_listen && !_isPlaying && !_dictMute && Environment.TickCount64 >= Volatile.Read(ref _holdUntil);

        // Отпускает эфир, не дожидаясь срока PostListenHoldMs: фраза не пойдёт (нет совпадения, дребезг, ошибка).
        private void ReleaseHold() => Volatile.Write(ref _holdUntil, 0);

        // Диагностика строки [Gate]: что именно шлюз не пустил в кабель, пока звучала фраза. Вызовы вырезаются в Release.
        [Conditional("DEBUG")]
        private void NoteMuted(byte[] data, int count)
        {
            int peak = 0;
            for (int i = 0; i + 1 < count; i += 2)
            {
                int s = (short)(data[i] | (data[i + 1] << 8));
                if (s < 0) s = -s;
                if (s > peak) peak = s;
            }

            Interlocked.Add(ref _mutedBytes, count);
            if (peak > _mutedPeak) _mutedPeak = peak;
        }

        // Строка [Gate] в окне «Вывод» после фразы. Если здесь пик громкий (микрофон был, и программа его удержала), а в игре фраза всё
        // равно искажается, причина вне программы: Windows «Прослушивать с данного устройства», ducking связи, шумодав или АРУ игры/Discord.
        [Conditional("DEBUG")]
        private void ReportGate()
        {
            long bytes = Interlocked.Exchange(ref _mutedBytes, 0);
            int peak = _mutedPeak;
            _mutedPeak = 0;

            double ms = bytes / 2.0 * 1000.0 / SampleRate;
            string level = peak > 0 ? $"{20.0 * Math.Log10(peak / 32768.0):F1} dBFS" : "тишина";
            Debug.WriteLine($"[Gate] за время фразы микрофон не пущен в кабель: {ms:F0} мс звука, пик {level}");
        }

        // Поток насоса. Всё, что связано с Vosk, выполняется здесь, а не в потоке записи.
        private void PumpSink(byte[] data, int count, RecognizeTargets targets, long gen)
        {
            if ((targets & RecognizeTargets.Dictation) != 0)
                Stt?.Feed(data, count);

            if ((targets & RecognizeTargets.Phrase) != 0)
            {
                string? shown = FeedRecognizer(data, count, gen);
                if (shown != null)
                {
                    OnPhraseRecognized?.Invoke(shown);   // вне блокировки
                    Prefetch(shown);
                }
            }
        }

        // ================= распознавание фраз =================

        /// <summary>Загружает распознаватель для модели: новый создаётся первым, старый освобождается после подмены. Модель не захватывается.</summary>
        public async Task AttachModelAsync(Model model)
        {
            ArgumentNullException.ThrowIfNull(model);

            var recognizer = await Task.Run(() => new VoskRecognizer(model, (float)SampleRate)).ConfigureAwait(false);

            VoskRecognizer? old;
            lock (_voskLock)
            {
                old = _recognizer;
                _recognizer = recognizer;
                _utterance.Clear();
                _lastPartial = "";
            }
            old?.Dispose();

            OnVoskStatusChanged?.Invoke("Модель ИИ успешно подключена.");
        }

        public void BeginListening()
        {
            lock (_voskLock)
            {
                Interlocked.Increment(ref _listenGen);
                _utterance.Clear();
                _lastPartial = "";
                _recognizer?.Reset();
                _listen = true;
            }
        }

        /// <summary>Завершает прослушивание и возвращает фразу целиком. Идемпотентна: страховка и настоящий key-up не конфликтуют.</summary>
        public async Task<string> EndListeningAsync()
        {
            long gen;
            lock (_voskLock)
            {
                if (!_listen) return string.Empty;
                gen = _listenGen;
            }

            // Хвост добирается, пока _listen == true: звук в это время не уходит в кабель, но уходит в распознавание.
            await Task.Delay(ListenTailMs).ConfigureAwait(false);

            // Всё, что записано до этой точки, должно дойти до Vosk раньше FinalResult (очередь насоса может отставать).
            var pump = _pump;
            if (pump != null) await pump.FlushAsync(FlushTimeout).ConfigureAwait(false);

            lock (_voskLock)
            {
                if (!_listen || gen != _listenGen) return string.Empty;   // уже завершено или начато новое прослушивание

                // Между «слушаю» и «играю» проходит FinalResult, поиск и декодирование: десятки и сотни миллисекунд. Эфир на это время
                // остаётся закрытым (срок ставится ДО сброса _listen, щели нет): иначе голос уходил бы в кабель прямо перед фразой
                // и звучал поверх её начала. Если играть нечего, эфир открывается сразу (ниже и в ProcessFinalPhrase).
                Volatile.Write(ref _holdUntil, Environment.TickCount64 + PostListenHoldMs);
                _listen = false;

                if (_recognizer != null) Append(VoskJson.Get(_recognizer.FinalResult(), "text"));
                string phrase = _utterance.ToString().Trim();
                _utterance.Clear();
                _lastPartial = "";

                // FinalResult мог занять сколько угодно под нагрузкой: срок отсчитывается заново от его конца.
                if (phrase.Length == 0) ReleaseHold();
                else Volatile.Write(ref _holdUntil, Environment.TickCount64 + PostListenHoldMs);
                return phrase;
            }
        }

        // Вызывается из потока насоса; возвращает текст для UI или null, если показывать нечего.
        private string? FeedRecognizer(byte[] buffer, int count, long gen)
        {
            lock (_voskLock)
            {
                var recognizer = _recognizer;
                if (recognizer == null || !_listen || gen != _listenGen) return null;   // чанк устаревшего прослушивания отбрасывается

                if (recognizer.AcceptWaveform(buffer, count))
                {
                    Append(VoskJson.Get(recognizer.Result(), "text"));   // сегмент закрыт паузой: копим, а не теряем
                    _lastPartial = "";
                    return _utterance.ToString();
                }

                long now = Environment.TickCount64;
                if (now - _lastPartialTick < PartialIntervalMs) return null;
                _lastPartialTick = now;

                string partial = VoskJson.Get(recognizer.PartialResult(), "partial");
                if (partial == _lastPartial) return null;
                _lastPartial = partial;
                return _utterance.Length == 0 ? partial : $"{_utterance} {partial}";
            }
        }

        private void Append(string text)
        {
            if (text.Length == 0) return;
            if (_utterance.Length > 0) _utterance.Append(' ');
            _utterance.Append(text);
        }

        // ================= диктовка (STT) =================

        /// <summary>
        /// Начинает диктовку. Порядок важен: сначала глушение, потом запуск распознавателя, потом подача звука.
        /// Если MuteMicDuringStt включён, с этого момента ни один новый отсчёт микрофона не попадает в кабель,
        /// а накопленное в подушке поток вывода выбросит при ближайшем чтении.
        /// </summary>
        public DictationStart BeginDictation()
        {
            var stt = Stt;
            if (stt == null || !_running) return DictationStart.NotRunning;
            if (Interlocked.CompareExchange(ref _dictState, DictRecording, DictIdle) != DictIdle) return DictationStart.Busy;

            bool mute = _muteMicDuringStt;
            _dictMute = mute;   // очередь самого драйвера (до OutputLatencyMs) отозвать нельзя, всё остальное отсечёт шлюз

            stt.Begin();
            if (!stt.IsActive)
            {
                _dictMute = false;
                Interlocked.CompareExchange(ref _dictState, DictIdle, DictRecording);
                return DictationStart.NoRecognizer;
            }

            _sttFeed = true;
            Debug.WriteLine($"[Dictation] старт, микрофон в кабеле: {(mute ? "ЗАГЛУШЁН" : "открыт")}");
            return DictationStart.Started;
        }

        /// <summary>
        /// Завершает диктовку и возвращает текст. null — диктовки нет или её уже завершает другой вызов (key-up и страховка HoldGuard
        /// не конфликтуют); пустая строка — речь не разобрана.
        /// </summary>
        public async Task<string?> EndDictationAsync()
        {
            if (Interlocked.CompareExchange(ref _dictState, DictFinishing, DictRecording) != DictRecording) return null;

            try
            {
                // Хвост: последние десятки миллисекунд ещё лежат в буфере драйвера. Глушение и подача звука идут до конца паузы.
                await Task.Delay(DictationTailMs).ConfigureAwait(false);
                _sttFeed = false;
                _dictMute = false;

                // Чанки, уже отправленные насосу, должны дойти до Vosk раньше FinalResult.
                var pump = _pump;
                if (pump != null) await pump.FlushAsync(FlushTimeout).ConfigureAwait(false);

                Debug.WriteLine("[Dictation] конец, микрофон в кабеле снова открыт");
                return Stt?.End() ?? string.Empty;
            }
            finally
            {
                // CompareExchange: если мост остановили и диктовку успели начать заново, чужое состояние не трогаем.
                Interlocked.CompareExchange(ref _dictState, DictIdle, DictFinishing);
            }
        }

        /// <summary>Прерывает диктовку без результата (остановка моста, потеря устройства). Микрофон открывается сразу.</summary>
        public void CancelDictation()
        {
            _sttFeed = false;
            _dictMute = false;
            Volatile.Write(ref _dictState, DictIdle);
            Stt?.Cancel();
        }

        // ================= база фраз и поиск =================

        public async Task ReloadPhrasesAsync(string folder, CancellationToken ct = default)
        {
            _lastLoadedFolderPath = folder;
            _phrases = await Task.Run(() => PhraseIndex.Build(folder, ct), ct).ConfigureAwait(false);   // индекс собран целиком → одна атомарная запись ссылки
            Debug.WriteLine($"[Phrases] Уникальных ключей: {_phrases.Count}");
        }

        public void OpenPhrasesFolder()
        {
            try
            {
                string target = string.IsNullOrEmpty(_lastLoadedFolderPath)
                    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Phrases")
                    : _lastLoadedFolderPath;

                Directory.CreateDirectory(target);
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Folder Error] Не удалось открыть папку: {ex.Message}");
            }
        }

        public void ProcessFinalPhrase(string recognizedText)
        {
            var index = _phrases;   // снимок на всё время обработки
            if (index.Count == 0 || string.IsNullOrWhiteSpace(recognizedText) || !recognizedText.Any(char.IsLetterOrDigit))
            {
                ReleaseHold();   // играть нечего
                return;
            }

            _ = Task.Run(() =>
            {
                bool started = false;
                try
                {
                    PhraseMatch? match = index.Find(recognizedText);
                    LatencyTrace.Mark("match");

                    if (match == null)
                    {
                        if (LogMissingPhrases) SaveMissingPhrase(PhraseIndex.Normalize(recognizedText));
                        return;
                    }

                    if (!TryClaimPlayback()) return;

                    string file = match.Files.Length == 1 ? match.Files[0] : match.Files[Random.Shared.Next(match.Files.Length)];
                    Debug.WriteLine($"[Phrase] {match.Step} {match.Score}: {match.Key}");
                    started = PlaySound(file);   // при успехе эфир уже держит _isPlaying
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Phrase] {ex}");
                }
                finally
                {
                    if (!started) ReleaseHold();   // фраза не пойдёт: микрофон открывается сразу, а не по истечении срока
                }
            });
        }

        // Антидребезг 1 с: атомарное «проверить и занять» вместо «проверил, потом записал».
        private bool TryClaimPlayback()
        {
            long now = Environment.TickCount64;
            long prev = Interlocked.Read(ref _lastPlayTick);
            return now - prev >= PlayDebounceMs && Interlocked.CompareExchange(ref _lastPlayTick, now, prev) == prev;
        }

        // Пока клавиша зажата, partial уже указывает на вероятную фразу: прогреваем её файлы в кэше.
        private void Prefetch(string partial)
        {
            if (Interlocked.Exchange(ref _prefetchBusy, 1) == 1) return;

            var index = _phrases;
            _ = Task.Run(() =>
            {
                try
                {
                    PhraseMatch? match = index.Find(partial);
                    if (match is { Score: >= PrefetchMinScore } && match.Key == _lastPrefetchKey)   // два partial подряд указали на одну фразу
                    {
                        foreach (string file in match.Files.Take(3)) _cache.Prefetch(file);
                    }
                    _lastPrefetchKey = match?.Key;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Prefetch] {ex.Message}");
                }
                finally
                {
                    Volatile.Write(ref _prefetchBusy, 0);
                }
            });
        }

        private void SaveMissingPhrase(string phrase)
        {
            try
            {
                lock (_fileLock)
                {
                    if (!_missingLoaded)   // файл читается один раз, а не на каждую промашку
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(MissingPath)!);
                        if (File.Exists(MissingPath))
                            foreach (string line in File.ReadLines(MissingPath)) _missingSeen.Add(line);
                        _missingLoaded = true;
                    }

                    if (_missingSeen.Add(phrase))
                        File.AppendAllText(MissingPath, phrase + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Missing] {ex.Message}");
            }
        }

        // ================= воспроизведение =================

        /// <summary>Запускает фразу. Предыдущая прерывается. Возвращает false, если мост не запущен или файла нет.</summary>
        public bool PlaySound(string path)
        {
            var cableMixer = _cableMixer;
            if (cableMixer == null || string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            var headMixer = _isMonitoringEnabled ? _headMixer : null;   // самопрослушивание выключено: во второй микшер не пишем
            bool emulate = _isEmulationEnabled;                          // снимок настроек: чекбокс посреди фразы
            int radioVk = _radioKeyVk;                                   // не должен оставить клавишу зажатой

            lock (_playLock)
            {
                _playCts?.Cancel();   // Dispose не нужен: у CTS без таймера нет неуправляемых ресурсов
                var cts = _playCts = new CancellationTokenSource();
                long gen = ++_playGen;
                Interlocked.Exchange(ref _mutedBytes, 0);   // счёт диагностики [Gate] начинается заново для каждой фразы
                _mutedPeak = 0;
                _isPlaying = true;    // синхронно, ДО старта задачи: микрофон не просочится поверх фразы
                ReleaseHold();        // с этого момента эфир держит _isPlaying, а срок ожидания решения больше не нужен
                _playTask = Task.Run(() => PlayCoreAsync(path, cableMixer, headMixer, emulate, radioVk, gen, cts.Token));
            }
            return true;
        }

        public void StopCurrentSound()
        {
            lock (_playLock) _playCts?.Cancel();
        }

        private async Task PlayCoreAsync(string path, MixingSampleProvider cableMixer, MixingSampleProvider? headMixer,
                                         bool emulate, int radioVk, long gen, CancellationToken ct)
        {
            CachedSoundProvider? cableIn = null, headIn = null;
            try
            {
                CachedSound sound = await _cache.GetAsync(path).ConfigureAwait(false);   // из ОЗУ: на горячем пути ни диска, ни декодера
                ct.ThrowIfCancellationRequested();

                bool warmUp = false;
                if (emulate)
                {
                    lock (_playLock)
                    {
                        if (_pressedRadioVk < 0 && gen == _playGen)   // нажимаем, только если мы всё ещё последняя фраза
                        {
                            InputSender.SendKey(radioVk, up: false);
                            _pressedRadioVk = radioVk;
                            warmUp = true;
                        }
                    }
                }
                LatencyTrace.Mark("radio key");

                if (warmUp) await Task.Delay(RadioWarmupMs, ct).ConfigureAwait(false);   // «разогрев» только если клавиша ещё не зажата
                ct.ThrowIfCancellationRequested();

                // Уровни читаются здесь, после «разогрева» рации: ползунок, сдвинутый за это время, не потеряется.
                // Выравнивание одно на оба тракта: в кабель — усиление под потолок пика с ползунка, в наушники — то же усиление ×
                // ползунок «для себя» (1 — звучит как в кабеле). Сила гейта пауз зависит только от усиления выравнивания, поэтому
                // в наушниках пауза приглушается так же, как в кабеле.
                float targetDb = _targetPeakDb;
                float headTrim = _volumeMic;
                float gain = sound.GainFor(targetDb);
                float gate = sound.GateStrengthFor(targetDb);
                Debug.WriteLine($"[Phrase] громкость {sound.LoudnessDb:F1} дБ, пик {sound.PeakDb:F1} dBFS" +
                                (sound.LimitedDb > 0.05f ? $" (выброс срезан на {sound.LimitedDb:F1} дБ)" : "") +
                                $", в расчёте {sound.ActiveFraction:P0}, запас {sound.ReferenceCrestDb:F0} дБ → потолок {targetDb:F0} dBFS, " +
                                $"усиление {20.0 * Math.Log10(gain):+0.0;-0.0;0.0} дБ" +
                                (sound.GateEnvelope == null ? ", гейт пауз: нет"
                                    : gate > 0f ? $", гейт пауз: {gate:P0} (до −{sound.GateDepthDb:F0} дБ, закрыт {sound.GateClosedFraction:P0})"
                                                : ", гейт пауз: выключен") +
                                (headMixer != null ? $", наушники: ×{headTrim:F2} от кабеля" : ""));

                var cableSrc = new CachedSoundProvider(sound, SampleRate, gain, gate);
                CachedSoundProvider? headSrc = headMixer == null ? null : new CachedSoundProvider(sound, SampleRate, gain * headTrim, gate);

                _curCable = cableSrc;
                _curHead = headSrc;
                cableMixer.AddMixerInput(cableSrc);
                cableIn = cableSrc;
                if (headSrc != null)
                {
                    headMixer!.AddMixerInput(headSrc);
                    headIn = headSrc;
                }
                LatencyTrace.Mark("audio in mixer");

                // Рацию отпускаем не раньше, чем хвост фразы дойдёт до кабеля: очередь вывода держит до OutputLatencyMs звука.
                await Task.Delay(sound.Duration + TimeSpan.FromMilliseconds(PostRollMs + _appliedLatencyMs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // штатно: прервали следующей фразой, кнопкой «стоп» или остановкой моста
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PlaySound] {ex.Message}");
            }
            finally
            {
                // Прерванная фраза не обрывается на полуслове (это щелчок в эфире): она затухает за ~15 мс и сама уходит из микшера.
                // У фразы, доигравшей до конца, источник давно снят микшером, и Stop ничего не меняет.
                cableIn?.Stop();
                headIn?.Stop();

                bool last;
                lock (_playLock)
                {
                    last = gen == _playGen;   // только ПОСЛЕДНЯЯ сессия отпускает рацию и открывает микрофон
                    if (last)
                    {
                        ReleaseRadioKeyLocked();
                        _isPlaying = false;
                    }
                }

                if (last)
                {
                    _curCable = null;
                    _curHead = null;
                    ReportGate();
                    OnSoundFinished?.Invoke();   // событие вне блокировки
                }
            }
        }

        // Только под _playLock. Отпускает ту клавишу, которую нажали мы, независимо от текущих настроек.
        private void ReleaseRadioKeyLocked()
        {
            if (_pressedRadioVk < 0) return;
            InputSender.SendKey(_pressedRadioVk, up: true);
            _pressedRadioVk = -1;
        }

        // ================= освобождение =================

        /// <summary>
        /// Освобождает распознаватель и, если мост не остановлен, останавливает его (с ограничением по времени).
        /// Vosk.Model не трогает. Вызывать не из UI-потока (BridgeController делает это в фоне).
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            if (_running)
            {
                try { Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(2)); }
                catch (Exception ex) { Debug.WriteLine($"[Audio] Dispose/Stop: {ex.Message}"); }
            }

            StopCurrentSound();
            lock (_playLock) ReleaseRadioKeyLocked();

            VoskRecognizer? recognizer;
            lock (_voskLock)
            {
                recognizer = _recognizer;
                _recognizer = null;
                _listen = false;
            }
            recognizer?.Dispose();

            _cache.Clear();
        }
    }

    // =====================================================================================================
    // Вспомогательные типы аудиотракта
    // =====================================================================================================

    /// <summary>Какие распознаватели должны получить чанк. Решается в потоке записи в момент копирования, а не в потоке насоса.</summary>
    [Flags]
    internal enum RecognizeTargets : byte
    {
        None = 0,
        Dictation = 1,
        Phrase = 2
    }

    /// <summary>Счётчики живого тракта (диагностика, строки [Live] в окне «Вывод»).</summary>
    internal readonly record struct LiveStats(long Underruns, long Trims, long Pads, long Overflows, int BufferedMs, long Dropped = 0, int ExtraMs = 0);

    /// <summary>
    /// Буфер живого голоса между микрофоном и выводом в кабель. Два независимых тактовых генератора (микрофон и виртуальный кабель),
    /// потоки пула и игра, занявшая процессор, неизбежно дают неровность. Голый BufferedWaveProvider при нехватке данных отдаёт
    /// часть блока и добивает остаток нулями при КАЖДОМ запросе — это и есть «микропаузы и треск».
    ///
    /// Здесь вместо этого (поведение проверено симуляцией: джиттер, дрейф часов ±400 ppm, редкие провалы потоков):
    ///  • вывод стартует, только когда накоплена подушка (prefill + extra): не меньше запроса вывода, одного чанка записи и запаса;
    ///  • при реальном недоборе блок затухает, после него идёт тишина, подушка копится заново ОДНИМ куском, а запас extra
    ///    растёт (на этой машине потокам нужна подушка побольше) и затем медленно, по шагу за минуту покоя, уменьшается;
    ///  • подушка НЕ подстраивается под каждое колебание (это и портило звук): раз в окно (8 с) берётся минимум подушки за окно,
    ///    и только если он стабильно выше цели, лишнее срезается — исключительно там, где вся срезаемая область тихая;
    ///  • если подушка у самого края (дрейф часов), в паузе речи вставляется тишина: в паузе этого не слышно;
    ///  • шлюз: на каждом чтении поток вывода спрашивает владельца, открыт ли эфир. Закрыт — всё накопленное выбрасывается
    ///    (последние 2 мс затухают, чтобы не щёлкнуло), отдаётся тишина, а когда эфир откроется, подушка копится заново.
    ///    Это и есть «жёсткая отсечка»: ни один отсчёт, записанный до закрытия, не доживёт до следующего блока;
    ///  • блокировок нет: поток записи пишет _write, поток вывода двигает _read (однопроизводительная кольцевая очередь).
    /// </summary>
    internal sealed class LiveVoiceBuffer : ISampleProvider
    {
        private const int Capacity = 1 << 16;      // 65 536 отсчётов ≈ 1,36 с при 48 кГц (степень двойки: индекс — маска)
        private const int Mask = Capacity - 1;
        private const float QuietPeak = 0.01f;     // ≈ −40 dBFS: пауза в речи, в которой подстройку не слышно
        private const int CalmWindowsPerDecay = 8; // столько спокойных окон (≈ минута) нужно, чтобы запас extra уменьшился на шаг

        private readonly float[] _ring = new float[Capacity];
        private readonly int _rate;
        private readonly int _prefill;             // подушка по умолчанию: запрос вывода + чанк записи + запас
        private readonly int _margin;              // запас сверх размера запроса вывода
        private readonly int _fade;                // длина плавного входа и выхода
        private readonly int _padGuard;            // подушка ближе этого к одному запросу: в паузе речи вставляется тишина
        private readonly int _shrinkMin;           // меньший излишек подушки не срезается
        private readonly int _window;              // окно наблюдения за минимумом подушки, отсчётов
        private readonly int _hardCeiling;         // превышение цели, при котором срез делается немедленно (даже по речи)
        private readonly int _maxExtra;            // предел роста запаса extra
        private readonly int _decayStep;           // шаг уменьшения extra

        private readonly Func<bool> _isOpen;       // шлюз эфира: false — микрофону в кабель нельзя

        private long _write;                       // двигает только поток записи
        private long _read;                        // двигает только поток вывода

        // Всё ниже трогает только поток вывода (кроме чтения _extra в Snapshot).
        private bool _priming = true;
        private bool _fadeInPending;
        private int _extra;                        // выученный запас подушки, отсчётов
        private long _winMin = long.MaxValue;      // минимум подушки за текущее окно
        private long _winSamples;                  // сколько отсчётов отдано в текущем окне
        private int _calmWindows;                  // подряд закрытых окон без недобора
        private long _underruns, _trims, _pads, _overflows;

        public WaveFormat WaveFormat { get; }

        public LiveVoiceBuffer(int sampleRate, int prefillMs, Func<bool> isOpen)
        {
            _isOpen = isOpen;
            _rate = sampleRate;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

            int perMs = sampleRate / 1000;
            _prefill = prefillMs * perMs;
            _margin = 10 * perMs;
            _fade = 2 * perMs;
            _padGuard = 10 * perMs;
            _shrinkMin = 20 * perMs;
            _window = 8 * sampleRate;
            _hardCeiling = 250 * perMs;
            _maxExtra = 150 * perMs;
            _decayStep = 10 * perMs;
        }

        /// <summary>Поток записи: 16-битный моно PCM little-endian. Переполнение возможно, только если вывод стоит дольше ~1,3 с.</summary>
        public void Write(byte[] data, int offset, int byteCount)
        {
            int n = byteCount >> 1;
            if (n <= 0) return;

            long w = _write;
            if (w - Volatile.Read(ref _read) + n > Capacity)
            {
                Interlocked.Increment(ref _overflows);   // читатель стоит: новое отбрасываем, непрочитанное не трогаем
                return;
            }

            for (int i = 0; i < n; i++)
            {
                int p = offset + (i << 1);
                short s = (short)(data[p] | (data[p + 1] << 8));
                _ring[(int)((w + i) & Mask)] = s * (1f / 32768f);
            }
            Volatile.Write(ref _write, w + n);
        }

        public LiveStats Snapshot() => new(
            Interlocked.Read(ref _underruns),
            Interlocked.Read(ref _trims),
            Interlocked.Read(ref _pads),
            Interlocked.Read(ref _overflows),
            (int)((Volatile.Read(ref _write) - Volatile.Read(ref _read)) * 1000L / _rate),
            0,
            (int)(Volatile.Read(ref _extra) * 1000L / _rate));

        /// <summary>Поток вывода. Всегда возвращает ровно count отсчётов: микшер иначе выбросил бы источник.</summary>
        public int Read(float[] buffer, int offset, int count)
        {
            long r = _read;

            // Сначала решение шлюза, потом _write: всё, что писатель успел записать до закрытия эфира, уже видно и будет выброшено.
            // Кусок, дописанный в гонке с закрытием, виден позже, но пока эфир закрыт, следующее чтение выбросит и его.
            bool open = _isOpen();
            long w = Volatile.Read(ref _write);

            if (!open)
            {
                // Если голос только что шёл, обрываем его без щелчка: самые старые из ещё не сыгранных отсчётов затухают за _fade.
                int have = 0;
                if (!_priming)
                {
                    have = (int)Math.Min(w - r, _fade);
                    if (have > 0)
                    {
                        CopyOut(r, buffer, offset, have);
                        FadeOut(buffer, offset, have);
                    }
                    _priming = true;
                }

                Array.Clear(buffer, offset + have, count - have);
                Volatile.Write(ref _read, w);   // только читатель двигает _read, поэтому выбрасывает он
                return count;
            }

            long avail = w - r;
            long target = Math.Max(_prefill, count + _margin) + _extra;   // подушка не меньше запроса вывода плюс запас плюс выученное

            if (_priming)
            {
                if (avail < target)
                {
                    Array.Clear(buffer, offset, count);
                    Volatile.Write(ref _read, r);
                    return count;
                }
                _priming = false;
                _fadeInPending = true;
                ResetWindow();
            }

            _winMin = Math.Min(_winMin, avail);
            _winSamples += count;
            bool trimmed = false;

            if (avail > target + _hardCeiling)
            {
                // Запись стояла и отдала всё разом: задержка не должна расти без предела, срезаем самое старое.
                r += avail - target;
                avail = target;
                _fadeInPending = true;
                Interlocked.Increment(ref _trims);
                trimmed = true;
                ResetWindow();
            }
            else if (_winSamples >= _window)
            {
                // Конец окна: подушка за всё окно ни разу не опускалась ниже target + surplus, значит surplus можно отдать.
                // Срезаем только по тихому участку; если сейчас речь, окно остаётся открытым и проверка повторится на следующем блоке.
                long surplus = _winMin - target;
                if (surplus > _shrinkMin && IsQuietSpan(r, surplus + count))
                {
                    r += surplus;
                    avail -= surplus;
                    _fadeInPending = true;
                    Interlocked.Increment(ref _trims);
                    trimmed = true;
                }

                if (trimmed || surplus <= _shrinkMin)
                {
                    // Окно закрыто спокойно: раз в CalmWindowsPerDecay окон выученный запас уменьшается на шаг.
                    if (_extra > 0 && ++_calmWindows >= CalmWindowsPerDecay)
                    {
                        _extra = Math.Max(0, _extra - _decayStep);
                        _calmWindows = 0;
                    }
                    ResetWindow();
                }
            }

            if (!trimmed && avail < count + _padGuard && avail >= count && IsQuietSpan(r, count))
            {
                // Подушка у самого края (вывод чуть быстрее записи): в паузе речи отдаём тишину, ничего не потребляя.
                Array.Clear(buffer, offset, count);
                Interlocked.Increment(ref _pads);
                Volatile.Write(ref _read, r);
                return count;
            }

            if (avail >= count)
            {
                CopyOut(r, buffer, offset, count);
                r += count;
            }
            else
            {
                // Недобор: доигрываем остаток с затуханием, добиваем тишиной, копим подушку заново и запоминаем, что нужна побольше.
                int have = (int)Math.Max(avail, 0);
                CopyOut(r, buffer, offset, have);
                FadeOut(buffer, offset, have);
                Array.Clear(buffer, offset + have, count - have);
                r += have;
                _priming = true;
                _extra = (int)Math.Min((long)_extra + 2L * count, _maxExtra);
                _calmWindows = 0;
                Interlocked.Increment(ref _underruns);
            }

            if (_fadeInPending)
            {
                FadeIn(buffer, offset, count);
                _fadeInPending = false;
            }

            Volatile.Write(ref _read, r);
            return count;
        }

        private void ResetWindow()
        {
            _winMin = long.MaxValue;
            _winSamples = 0;
        }

        private void CopyOut(long r, float[] dst, int dstOffset, int n)
        {
            if (n <= 0) return;
            int start = (int)(r & Mask);
            int first = Math.Min(n, Capacity - start);
            Array.Copy(_ring, start, dst, dstOffset, first);
            if (first < n) Array.Copy(_ring, 0, dst, dstOffset + first, n - first);
        }

        // Тихие ли ближайшие n отсчётов (только уже записанные). Выходит при первом громком отсчёте.
        private bool IsQuietSpan(long r, long n)
        {
            long limit = Math.Min(n, Volatile.Read(ref _write) - r);
            int start = (int)(r & Mask);
            for (long i = 0; i < limit; i++)
            {
                float s = _ring[(int)((start + i) & Mask)];
                if (s > QuietPeak || s < -QuietPeak) return false;
            }
            return true;
        }

        private void FadeIn(float[] buf, int offset, int count)
        {
            int n = Math.Min(_fade, count);
            for (int i = 0; i < n; i++) buf[offset + i] *= (float)i / n;
        }

        private void FadeOut(float[] buf, int offset, int have)
        {
            int n = Math.Min(_fade, have);
            for (int i = 0; i < n; i++) buf[offset + have - 1 - i] *= (float)i / n;
        }
    }

    /// <summary>
    /// Очередь чанков PCM для распознавателей и один рабочий поток. Поток записи только копирует чанк (TryPost) и возвращается,
    /// поэтому долгая работа Vosk (большая модель, FinalResult, нагруженный процессор) не может заставить драйвер потерять звук.
    /// FlushAsync возвращает управление, когда всё поставленное в очередь ДО вызова обработано: на этом держится «хвост» речи.
    /// </summary>
    internal sealed class PcmPump : IDisposable
    {
        private const int MaxPending = 100;     // ≈ 3 с звука: дальше чанки отбрасываются, а не копятся до нехватки памяти

        private readonly record struct Item(byte[]? Data, int Count, RecognizeTargets Targets, long Gen, TaskCompletionSource? Marker);

        private readonly BlockingCollection<Item> _queue = new();
        private readonly Action<byte[], int, RecognizeTargets, long> _sink;
        private readonly Thread _thread;
        private int _pending;
        private long _dropped;
        private volatile bool _disposed;

        public PcmPump(Action<byte[], int, RecognizeTargets, long> sink)
        {
            _sink = sink;
            _thread = new Thread(Run) { IsBackground = true, Name = "VoicePhraseCast.Recognition" };
            _thread.Start();
        }

        public long Dropped => Interlocked.Read(ref _dropped);

        /// <summary>Поток записи. Копирует чанк (буфер NAudio переиспользуется) и ставит в очередь; блокировок и ожиданий нет.</summary>
        public bool TryPost(byte[] data, int count, RecognizeTargets targets, long gen)
        {
            if (_disposed) return false;
            if (Volatile.Read(ref _pending) >= MaxPending)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            byte[] copy = ArrayPool<byte>.Shared.Rent(count);
            Buffer.BlockCopy(data, 0, copy, 0, count);
            Interlocked.Increment(ref _pending);
            try
            {
                _queue.Add(new Item(copy, count, targets, gen, null));
                return true;
            }
            catch (InvalidOperationException)   // CompleteAdding уже вызван: насос закрывается
            {
                Interlocked.Decrement(ref _pending);
                ArrayPool<byte>.Shared.Return(copy);
                return false;
            }
        }

        /// <summary>Ждёт, пока рабочий поток обработает всё, что было в очереди на момент вызова. false — не дождались за timeout.</summary>
        public async Task<bool> FlushAsync(TimeSpan timeout)
        {
            if (_disposed) return true;

            var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(new Item(null, 0, RecognizeTargets.None, 0, marker));
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            try
            {
                await marker.Task.WaitAsync(timeout).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        private void Run()
        {
            try
            {
                foreach (Item item in _queue.GetConsumingEnumerable())
                {
                    if (item.Marker != null)
                    {
                        item.Marker.TrySetResult();
                        continue;
                    }

                    try
                    {
                        _sink(item.Data!, item.Count, item.Targets, item.Gen);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Pump] {ex.Message}");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(item.Data!);
                        Interlocked.Decrement(ref _pending);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Pump] {ex}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            if (_thread.Join(500)) _queue.Dispose();   // если Vosk завис, поток фоновый и выходу не мешает
        }
    }

    /// <summary>
    /// Приоритет потоков аудио. NAudio крутит циклы записи и вывода на обычных потоках пула; когда игра занимает все ядра,
    /// такой поток может не получить процессор десятки миллисекунд — а запас вывода 4 × 30 мс. MMCSS ("Pro Audio") —
    /// штатный для Windows способ сказать планировщику, что поток обслуживает звук (так делают WASAPI-клиенты).
    /// Вызывать Boost из самого потока; Release — оттуда же, когда он заканчивает цикл (поток пула потом вернётся к другим задачам).
    /// </summary>
    internal static class AudioThread
    {
        [ThreadStatic] private static IntPtr _task;
        [ThreadStatic] private static bool _attempted;

        [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW", SetLastError = true)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", EntryPoint = "AvRevertMmThreadCharacteristics", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        public static void Boost()
        {
            if (_attempted) return;
            _attempted = true;   // одна попытка на поток, пока не вызван Release

            try
            {
                foreach (string name in new[] { "Pro Audio", "Audio" })
                {
                    uint index = 0;
                    IntPtr handle = AvSetMmThreadCharacteristics(name, ref index);
                    if (handle != IntPtr.Zero)
                    {
                        _task = handle;
                        return;
                    }
                }

                Thread.CurrentThread.Priority = ThreadPriority.Highest;   // MMCSS недоступен: хотя бы относительный приоритет
            }
            catch (Exception ex)   // нет avrt.dll или точки входа
            {
                Debug.WriteLine($"[AudioThread] {ex.Message}");
            }
        }

        public static void Release()
        {
            if (!_attempted) return;
            _attempted = false;

            IntPtr handle = _task;
            _task = IntPtr.Zero;
            try
            {
                if (handle != IntPtr.Zero) AvRevertMmThreadCharacteristics(handle);
                else Thread.CurrentThread.Priority = ThreadPriority.Normal;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioThread] {ex.Message}");
            }
        }
    }

    /// <summary>Оборачивает источник вывода: первое же чтение из потока вывода NAudio регистрирует этот поток в MMCSS.</summary>
    internal sealed class ThreadBoostProvider : ISampleProvider
    {
        private readonly ISampleProvider _inner;

        public ThreadBoostProvider(ISampleProvider inner) => _inner = inner;

        public WaveFormat WaveFormat => _inner.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            AudioThread.Boost();
            return _inner.Read(buffer, offset, count);
        }
    }

    /// <summary>
    /// Страховочный ограничитель пиков перед 16-битным выводом. Микшер NAudio складывает источники без защиты, а
    /// SampleToWaveProvider16 жёстко срезает всё выше ±1.0 — плоские вершины и есть «хрип». Здесь сумма, вылезшая за потолок,
    /// мгновенно приглушается ровно настолько, чтобы в него уложиться, и за ≈100 мс плавно возвращается к норме.
    /// Один источник (нормализованная фраза с пиком ≤ 0 dBFS или живой голос) потолка не достигает: для него усиление ровно 1, звук не меняется.
    /// Срабатывает только при сложении (две фразы при прерывании одной другой, живой голос в гонке с началом фразы).
    /// </summary>
    internal sealed class PeakLimiterProvider : ISampleProvider
    {
        private const float Ceiling = 1f;

        private readonly ISampleProvider _inner;
        private readonly float _release;     // доля пути к единице за один отсчёт
        private float _gain = 1f;            // только поток вывода

        public PeakLimiterProvider(ISampleProvider inner)
        {
            _inner = inner;
            _release = 1f - MathF.Exp(-1f / (0.1f * inner.WaveFormat.SampleRate));   // постоянная времени 100 мс
        }

        public WaveFormat WaveFormat => _inner.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            float g = _gain;

            for (int i = offset, end = offset + n; i < end; i++)
            {
                float x = buffer[i];
                float a = x < 0f ? -x : x;

                g += (1f - g) * _release;                  // отпускание
                if (1f - g < 1e-5f) g = 1f;                // иначе float застрянет в 0.99999994 и «прозрачность» не будет точной
                if (a * g > Ceiling) g = Ceiling / a;      // атака мгновенная: ни один отсчёт не выйдет за потолок
                buffer[i] = x * g;
            }

            _gain = g;
            return n;
        }
    }
}
