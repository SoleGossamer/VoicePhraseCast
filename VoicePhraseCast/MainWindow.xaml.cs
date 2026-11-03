using NAudio.Wave;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VoicePhraseCast
{
    /// <summary>
    /// Тонкий слой интерфейса. Жизненный цикл (устройства, хук, модель Vosk) принадлежит BridgeController,
    /// окно только отражает его состояние (ApplyState) и передаёт команды.
    /// Файл разбит на три части: MainWindow.xaml.cs (окно, устройства, настройки, фразы),
    /// MainWindow.Hotkeys.cs (горячие клавиши, фразы по хоткею, диктовка) и MainWindow.Models.cs (модели Vosk).
    ///
    /// Правила потоков: события BridgeController, AudioProcessor и InputHook приходят из потоков пула и в интерфейс
    /// попадают только через PostUi (Dispatcher.InvokeAsync). Из потоков пула контролы не читаются: нужные значения
    /// лежат в полях (_keys, _dictation, _activationLabel, ...).
    /// </summary>
    public partial class MainWindow : Window
    {
        public class AudioDevice
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
        }

        private static VoicePhraseCast.Properties.Settings Cfg => VoicePhraseCast.Properties.Settings.Default;

        private static readonly System.Windows.Media.Brush RunningBrush = FrozenBrush(0x15, 0x80, 0x3D);
        private static readonly System.Windows.Media.Brush IdleStatusBrush = FrozenBrush(0x4B, 0x55, 0x63);

        private readonly BridgeController _controller = new();
        private readonly ModelManager _modelManager = new();
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private System.Drawing.Icon? _trayIcon;
        private System.Windows.Media.Brush? _startIdleBrush;

        private bool _balloonShown;
        private bool _isDataLoaded;
        private bool _isSettingsExpanded;
        private bool _shutdownStarted;
        private bool _shutdownComplete;

        private long _keepStatusUntil;                 // до этого момента (TickCount64) статус «Воспроизведение остановлено» не затирается
        private volatile string _activationLabel = ""; // подпись клавиши активации для статуса: читается из потоков пула
        private volatile string _heardText = "";
        private int _heardPosted;

        public MainWindow()
        {
            InitializeComponent();

            _startIdleBrush = BtnStart.Background;
            SettingsPanel.Height = 0;

            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveSettingsNow();
            };

            // Таймер создаётся здесь, а не лениво: ScheduleReindex вызывается из потоков наблюдателя, и два потока создали бы два таймера.
            _reindexTimer = new System.Threading.Timer(_ => { _ = ReindexAsync(); }, null, Timeout.Infinite, Timeout.Infinite);

            LoadDevices();
            LoadSettingsIntoUi();
            ConnectController();
            InitTrayIcon();

            System.Windows.Application.Current.SessionEnding += OnSessionEnding;
            Loaded += Window_Loaded;

            // Начиная с этого момента изменения контролов записываются в настройки.
            _isDataLoaded = true;
        }

        // ================= запуск программы =================

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Индексация фраз идёт в фоне параллельно с загрузкой модели.
                if (!string.IsNullOrEmpty(TxtPath.Text))
                    InitializeFolderWatcher(TxtPath.Text);

                await LoadInitialModelAsync();

                // Автостарт моста — только после того, как модель готова (раньше он стартовал вслепую).
                if (ChkAutoBridgeStart.IsChecked == true)
                    await StartBridgeAsync(interactive: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Init] {ex}");
                StatusText.Text = $"Ошибка инициализации: {ex.Message}";
            }
        }

        private void ConnectController()
        {
            _controller.StateChanged += state => Dispatcher.InvokeAsync(() => ApplyState(state));
            _controller.Faulted += message => Dispatcher.InvokeAsync(() => OnBridgeFaulted(message));

            var audio = _controller.Audio;
            audio.OnVoskStatusChanged += status => PostUi(() => StatusText.Text = $"Статус ИИ: {status}");
            _controller.Stt.OnStatusChanged += status => PostUi(() => StatusText.Text = $"Статус ИИ: {status}");   // раньше на это событие никто не подписывался (M3)
            audio.OnPhraseRecognized += ShowHeard;
            audio.OnSoundFinished += OnSoundFinished;

            _controller.Hook.KeyDown += OnHookKeyDown;
            _controller.Hook.KeyUp += OnHookKeyUp;
        }

        // ================= состояние моста =================

        private string IdleStatus => $"Статус: РАБОТАЕТ ({_activationLabel} active)";

        private void ApplyState(BridgeState state)
        {
            bool running = state == BridgeState.Running || (state == BridgeState.ReloadingModel && _controller.Audio.IsRunning);
            bool busy = state is BridgeState.Starting or BridgeState.Stopping or BridgeState.ReloadingModel;
            bool editable = !running && !busy;

            BtnStart.IsEnabled = !busy;   // во время работы остаётся включённой (зелёной): повторное нажатие игнорируется в StartBridgeAsync
            BtnStop.IsEnabled = running && !busy;
            CmbVoskModels.IsEnabled = !busy;
            MicComboBox.IsEnabled = editable;
            CableComboBox.IsEnabled = editable;
            MonitorComboBox.IsEnabled = editable;
            foreach (var box in HotkeyBoxes) box.IsEnabled = editable;

            BtnStart.Content = running ? "РАБОТАЕТ" : busy ? "…" : "ЗАПУСТИТЬ";
            BtnStart.Background = running ? RunningBrush : _startIdleBrush;
            BtnStart.Foreground = System.Windows.Media.Brushes.White;

            switch (state)
            {
                case BridgeState.Starting:
                    SetStatus("Статус: запуск…", IdleStatusBrush);
                    break;
                case BridgeState.Running:
                    SetStatus(IdleStatus, System.Windows.Media.Brushes.Green);
                    break;
                case BridgeState.Stopping:
                    SetStatus("Статус: остановка…", IdleStatusBrush);
                    break;
                case BridgeState.Stopped:
                    DisarmGuards();
                    SetStatus("Статус: Остановлено", System.Windows.Media.Brushes.Red);
                    RecognizedText.Text = "Слышу: ...";
                    break;
            }
        }

        private void SetStatus(string text, System.Windows.Media.Brush brush)
        {
            StatusText.Text = text;
            StatusText.Foreground = brush;
        }

        private void OnBridgeFaulted(string message)
        {
            StatusText.Text = $"Мост остановлен: {message}";
            _notifyIcon?.ShowBalloonTip(5000, "VoicePhraseCast: мост остановлен", message, System.Windows.Forms.ToolTipIcon.Warning);
        }

        private void PostUi(Action action) => Dispatcher.InvokeAsync(action);

        // Partial-результаты приходят ~6 раз в секунду: в очереди Dispatcher держим не больше одного обновления.
        private void ShowHeard(string text)
        {
            _heardText = text;
            if (Interlocked.Exchange(ref _heardPosted, 1) == 1) return;

            Dispatcher.InvokeAsync(() =>
            {
                Volatile.Write(ref _heardPosted, 0);
                RecognizedText.Text = $"Слышу: {_heardText}";
            }, DispatcherPriority.Background);
        }

        private void OnSoundFinished()
        {
            PostUi(() =>
            {
                RecognizedText.Text = "Слышу: ...";

                // Состояние хранится в полях, а не в тексте контролов: «остановлено» держится 1,5 с, потом возвращается рабочий статус.
                if (_controller.State == BridgeState.Running && Environment.TickCount64 >= Interlocked.Read(ref _keepStatusUntil))
                    StatusText.Text = IdleStatus;
            });
        }

        // ================= запуск и остановка по кнопкам =================

        private async void BtnStart_Click(object sender, RoutedEventArgs e) => await StartBridgeAsync(interactive: true);

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _controller.StopAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Stop] {ex}");
                StatusText.Text = $"Ошибка остановки: {ex.Message}";
            }
        }

        private async Task StartBridgeAsync(bool interactive)
        {
            if (_controller.State != BridgeState.Stopped) return;

            if (MicComboBox.SelectedItem is not AudioDevice mic ||
                CableComboBox.SelectedItem is not AudioDevice cable ||
                MonitorComboBox.SelectedItem is not AudioDevice monitor)
            {
                ReportStartProblem("Выберите микрофон, виртуальный кабель и наушники (вкладка «Настройки»).", interactive);
                return;
            }

            BridgeConfig config;
            try
            {
                config = BuildConfig(mic, cable, monitor);
                ApplyAudioSettings();
            }
            catch (Exception ex)
            {
                ReportStartProblem($"Не удалось прочитать настройки: {ex.Message}", interactive);
                return;
            }

            try
            {
                await _controller.StartAsync(config);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Start] {ex}");
                ReportStartProblem($"Ошибка при запуске: {ex.Message}\nПроверьте выбранные устройства и настройки клавиш.", interactive);
                return;
            }

            Cfg.MicDevice = mic.Name;
            Cfg.CableDevice = cable.Name;
            Cfg.MonitorDevice = monitor.Name;
            ScheduleSave();

            if (ChkMinimizeOnStart.IsChecked == true)
            {
                // Небольшая задержка, чтобы зелёный статус успел отрисоваться до сворачивания в трей.
                // Результат намеренно не ждём (приоритет Background, после отрисовки): «_ =» снимает предупреждение CS4014.
                _ = Dispatcher.InvokeAsync(() => { WindowState = WindowState.Minimized; }, DispatcherPriority.Background);
            }
        }

        private void ReportStartProblem(string message, bool interactive)
        {
            StatusText.Text = message.Split('\n')[0];

            if (interactive)
                System.Windows.MessageBox.Show(message, "Мост не запущен", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                _notifyIcon?.ShowBalloonTip(5000, "VoicePhraseCast: мост не запущен", message, System.Windows.Forms.ToolTipIcon.Warning);
        }

        // Настройки, которые AudioProcessor читает на лету. Клавиша рации задаётся в BuildConfig.
        private void ApplyAudioSettings()
        {
            var audio = _controller.Audio;
            audio.IsEmulationEnabled = ChkEmulationEnabled.IsChecked == true;
            audio.IsMonitoringEnabled = ChkMonitor.IsChecked == true;
            audio.VolumeMic = (float)SliderMic.Value;
            audio.TargetPeakDb = (float)SliderCable.Value;   // ползунок «в игру» теперь в dBFS
            audio.MuteMicDuringStt = _muteMicDuringStt;
            audio.LogMissingPhrases = Cfg.LogMissingPhrases;
            audio.RadioWarmupMs = Math.Clamp(Cfg.RadioWarmupMs, 0, 1000);
            audio.OutputLatencyMs = Math.Clamp(Cfg.OutputLatencyMs, 20, 300);
        }

        // ================= устройства =================

        private void LoadDevices()
        {
            MicComboBox.Items.Clear();
            CableComboBox.Items.Clear();
            MonitorComboBox.Items.Clear();

            MicComboBox.DisplayMemberPath = "Name";
            CableComboBox.DisplayMemberPath = "Name";
            MonitorComboBox.DisplayMemberPath = "Name";

            for (int i = 0; i < WaveIn.DeviceCount; i++)
                MicComboBox.Items.Add(new AudioDevice { Id = i, Name = WaveIn.GetCapabilities(i).ProductName });

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                string name = WaveOut.GetCapabilities(i).ProductName;
                CableComboBox.Items.Add(new AudioDevice { Id = i, Name = name });
                MonitorComboBox.Items.Add(new AudioDevice { Id = i, Name = name });
            }

            // Сначала сохранённое имя, затем подсказки по типичным названиям, затем первое подходящее устройство.
            RestoreSelection(MicComboBox, Cfg.MicDevice, false, "HyperX", "QuadCast", "Микрофон", "Microphone");
            RestoreSelection(CableComboBox, Cfg.CableDevice, false, "16ch", "CABLE Input", "CABLE", "Virtual");
            RestoreSelection(MonitorComboBox, Cfg.MonitorDevice, true, "High Definition", "Headphones", "Наушники", "Speakers", "Динамики");

            MicComboBox.SelectionChanged += Device_SelectionChanged;
            CableComboBox.SelectionChanged += Device_SelectionChanged;
            MonitorComboBox.SelectionChanged += Device_SelectionChanged;
            UpdateCableHint();
        }

        private static void RestoreSelection(System.Windows.Controls.ComboBox box, string saved, bool avoidCable, params string[] hints)
        {
            var list = box.Items.Cast<AudioDevice>().ToList();

            int index = string.IsNullOrEmpty(saved) ? -1 : list.FindIndex(d => d.Name == saved);
            for (int i = 0; index < 0 && i < hints.Length; i++)
                index = list.FindIndex(d => d.Name.Contains(hints[i], StringComparison.OrdinalIgnoreCase));

            if (index < 0 && avoidCable)   // наушники по умолчанию не должны оказаться самим виртуальным кабелем
                index = list.FindIndex(d => !d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase));

            box.SelectedIndex = index >= 0 ? index : (list.Count > 0 ? 0 : -1);
        }

        private void Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCableHint();
            if (!_isDataLoaded) return;

            if (MicComboBox.SelectedItem is AudioDevice mic) Cfg.MicDevice = mic.Name;
            if (CableComboBox.SelectedItem is AudioDevice cable) Cfg.CableDevice = cable.Name;
            if (MonitorComboBox.SelectedItem is AudioDevice monitor) Cfg.MonitorDevice = monitor.Name;
            ScheduleSave();
        }

        private void UpdateCableHint()
        {
            if (CableComboBox.SelectedItem is not AudioDevice cable)
            {
                HintText.Text = "⚠ Устройство вывода в игру не выбрано. Установите виртуальный кабель (VB-Audio Virtual Cable).";
                return;
            }

            bool looksLikeCable = cable.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
                               || cable.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

            if (!looksLikeCable)
            {
                HintText.Text = "⚠ Выбранное устройство не похоже на виртуальный кабель: фразы услышите только вы, а не игра.";
                return;
            }

            string gameMic = cable.Name.Contains("16ch", StringComparison.OrdinalIgnoreCase) ? "CABLE In 16ch Output" : "CABLE Output";
            HintText.Text = $"💡 Для работы в игре: зайдите в настройки звука игры и выберите микрофон: '{gameMic}'";
        }

        // ================= загрузка настроек в интерфейс =================

        private bool _muteMicDuringStt = true;

        private void LoadSettingsIntoUi()
        {
            var s = Cfg;
            var audio = _controller.Audio;

            if (!string.IsNullOrEmpty(s.LastPath) && Directory.Exists(s.LastPath))
                TxtPath.Text = s.LastPath;

            LoadHotkeysFromSettings();

            float volMic = Math.Clamp(s.VolumeMic, 0f, 1f);
            // Ползунок «в игру» — целевой пик в dBFS, шаг 1 дБ. Старая линейная VolumeCable не переносится:
            // это другая величина (множитель, а не уровень), новое значение по умолчанию задаёт Settings.Extra.cs (−3).
            double peakDb = Math.Clamp(Math.Round(s.TargetPeakDb), AudioProcessor.MinTargetPeakDb, AudioProcessor.MaxTargetPeakDb);
            SliderMic.Value = volMic;
            SliderCable.Value = peakDb;
            TxtMicVolumeValue.Text = $"{Math.Round(volMic * 100)}%";
            TxtCableVolumeValue.Text = FormatDb(peakDb);
            audio.VolumeMic = volMic;
            audio.TargetPeakDb = (float)peakDb;

            ChkMonitor.IsChecked = s.IsMonitorEnabled;
            audio.IsMonitoringEnabled = s.IsMonitorEnabled;

            ChkEmulationEnabled.IsChecked = s.IsEmulationEnabled;
            audio.IsEmulationEnabled = s.IsEmulationEnabled;

            ChkAutostartWindows.IsChecked = s.IsAutostartWithWindowsEnabled;
            if (s.IsAutostartWithWindowsEnabled)
                UpdateRegistryAutostart(false, true);   // обновит путь в реестре, если exe переехал

            ChkAutoBridgeStart.IsChecked = s.IsAutoBridgeStartEnabled;
            ChkMinimizeOnStart.IsChecked = s.IsMinimizeOnStartEnabled;

            ChkAutoPaste.IsChecked = s.SttAutoPaste;
            ChkOpenChatBeforePaste.IsChecked = s.SttOpenChat;
            ChkUseClipboard.IsChecked = s.SttUseClipboard;
            ChkSendEnterAfterText.IsChecked = s.SttSendEnter;
            ReadDictationOptions();
            foreach (var chk in new[] { ChkAutoPaste, ChkOpenChatBeforePaste, ChkUseClipboard, ChkSendEnterAfterText })
                chk.Click += SttOption_Click;

            // Флажок «глушить микрофон во время диктовки» необязателен в XAML: по умолчанию глушение включено всегда.
            _muteMicDuringStt = s.MuteMicDuringStt;
            audio.MuteMicDuringStt = _muteMicDuringStt;
            if (FindName("ChkMuteMicDuringStt") is System.Windows.Controls.CheckBox muteBox)
            {
                muteBox.IsChecked = _muteMicDuringStt;
                muteBox.Click += (_, _) =>
                {
                    _muteMicDuringStt = muteBox.IsChecked == true;
                    _controller.Audio.MuteMicDuringStt = _muteMicDuringStt;
                    Cfg.MuteMicDuringStt = _muteMicDuringStt;
                    ScheduleSave();
                };
            }

            audio.LogMissingPhrases = s.LogMissingPhrases;
        }

        // ================= сохранение настроек =================

        private void ScheduleSave()
        {
            _saveTimer.Stop();    // перезапуск таймера = debounce: пока тянут слайдер, файл не пишется
            _saveTimer.Start();
        }

        private void SaveSettingsNow()
        {
            try
            {
                Cfg.Save();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] {ex.Message}");
            }
        }

        // ================= чекбоксы и слайдеры =================

        private void ChkMonitor_Click(object sender, RoutedEventArgs e)
        {
            bool enabled = ChkMonitor.IsChecked == true;
            _controller.Audio.IsMonitoringEnabled = enabled;
            if (!_isDataLoaded) return;

            StatusText.Text = enabled ? "Мониторинг включен" : "Мониторинг выключен";
            Cfg.IsMonitorEnabled = enabled;
            ScheduleSave();
        }

        private void ChkEmulationEnabled_Click(object sender, RoutedEventArgs e)
        {
            bool enabled = ChkEmulationEnabled.IsChecked == true;
            _controller.Audio.IsEmulationEnabled = enabled;
            if (!_isDataLoaded) return;

            StatusText.Text = enabled ? "Эмуляция рации активна" : "Эмуляция рации отключена (Стелс-режим)";
            Cfg.IsEmulationEnabled = enabled;
            ScheduleSave();
        }

        private void ChkAutostartWindows_Click(object sender, RoutedEventArgs e)
        {
            if (!_isDataLoaded) return;

            bool enabled = ChkAutostartWindows.IsChecked == true;
            Cfg.IsAutostartWithWindowsEnabled = enabled;
            ScheduleSave();

            UpdateRegistryAutostart(true, enabled);
            StatusText.Text = enabled ? "Автозапуск с Windows включен" : "Автозапуск с Windows выключен";
        }

        private void ChkAutoBridgeStart_Click(object sender, RoutedEventArgs e)
        {
            if (!_isDataLoaded) return;

            bool enabled = ChkAutoBridgeStart.IsChecked == true;
            Cfg.IsAutoBridgeStartEnabled = enabled;
            ScheduleSave();
            StatusText.Text = enabled ? "Автостарт моста включен" : "Автостарт моста выключен";
        }

        private void ChkMinimizeOnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!_isDataLoaded) return;

            bool enabled = ChkMinimizeOnStart.IsChecked == true;
            Cfg.IsMinimizeOnStartEnabled = enabled;
            ScheduleSave();
            StatusText.Text = enabled ? "Режим скрытия при старте включен" : "Режим скрытия при старте выключен";
        }

        private void SliderMic_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            float volume = (float)e.NewValue;
            _controller.Audio.VolumeMic = volume;   // действует и на уже играющую фразу
            if (TxtMicVolumeValue != null) TxtMicVolumeValue.Text = $"{Math.Round(volume * 100)}%";
            if (!_isDataLoaded) return;

            Cfg.VolumeMic = volume;
            ScheduleSave();
        }

        // Ползунок «в игру»: целевой пиковый уровень фраз, dBFS (−24…0, шаг 1). Каждая фраза нормализуется до него по своему пику.
        private void SliderCable_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            double db = e.NewValue;
            _controller.Audio.TargetPeakDb = (float)db;   // действует и на уже играющую фразу
            if (TxtCableVolumeValue != null) TxtCableVolumeValue.Text = FormatDb(db);
            if (!_isDataLoaded) return;

            Cfg.TargetPeakDb = db;
            ScheduleSave();
        }

        // «-3 dB», «0 dB». Инвариантная культура: знак минус и разделитель не зависят от региональных настроек; «-0» не бывает.
        private static string FormatDb(double db) =>
            (db == 0 ? 0 : db).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " dB";

        // Автозапуск с Windows. showErrors = true только при ручном клике пользователя.
        private void UpdateRegistryAutostart(bool showErrors, bool enable)
        {
            string exePath = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrEmpty(exePath)) return;

            const string runKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            const string appName = "VoicePhraseCast";
            string appPath = $"\"{exePath}\"";

            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKeyPath, true);
                if (key == null) return;

                if (enable)
                {
                    if (key.GetValue(appName) as string != appPath)
                        key.SetValue(appName, appPath);
                }
                else
                {
                    key.DeleteValue(appName, false);
                }
            }
            catch (Exception ex)
            {
                if (showErrors)
                {
                    System.Windows.MessageBox.Show($"Не удалось изменить настройки автозагрузки в реестре: {ex.Message}",
                        "Ошибка реестра", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ================= база фраз =================

        private FileSystemWatcher? _folderWatcher;
        private string? _phrasesFolder;
        private CancellationTokenSource? _reindexCts;
        private System.Threading.Timer? _reindexTimer;
        private int _reindexRetries;                      // повторы после IOException ограничены: удалённая папка не должна давать вечный цикл

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            if (dialog.ShowDialog() != true) return;

            TxtPath.Text = dialog.FolderName;
            Cfg.LastPath = dialog.FolderName;
            ScheduleSave();
            InitializeFolderWatcher(dialog.FolderName);
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e) => _controller.Audio.OpenPhrasesFolder();

        private void InitializeFolderWatcher(string folder)
        {
            _folderWatcher?.Dispose();
            _folderWatcher = null;

            if (!Directory.Exists(folder)) return;
            _phrasesFolder = folder;

            try
            {
                _folderWatcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,   // индекс обходит подпапки, наблюдатель обязан тоже
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024  // 8 КБ по умолчанию переполняются на пачке файлов
                };
                _folderWatcher.Created += OnFolderEvent;
                _folderWatcher.Deleted += OnFolderEvent;
                _folderWatcher.Renamed += OnFolderEvent;
                _folderWatcher.Error += (_, _) => ScheduleReindex();   // переполнение буфера = события потеряны: пересобрать всё
                _folderWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Watcher] {ex.Message}");
            }

            ScheduleReindex();   // первичная индексация тоже в фоне
        }

        private void OnFolderEvent(object? sender, FileSystemEventArgs e)
        {
            // Не-аудио файлы (Thumbs.db, *.tmp) пересборку не запускают; папки и удаления запускают.
            if (e.ChangeType != WatcherChangeTypes.Deleted && !Directory.Exists(e.FullPath) && !PhraseIndex.IsAudio(e.FullPath))
                return;

            ScheduleReindex();
        }

        private void ScheduleReindex()
        {
            try
            {
                _reindexTimer?.Change(500, Timeout.Infinite);   // debounce: 300 скопированных файлов = один пересчёт
            }
            catch (ObjectDisposedException)
            {
                // окно закрывается
            }
        }

        private async Task ReindexAsync()
        {
            string? folder = _phrasesFolder;
            if (folder == null) return;

            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _reindexCts, cts)?.Cancel();   // устаревший пересчёт отменяется

            try
            {
                await _controller.Audio.ReloadPhrasesAsync(folder, cts.Token).ConfigureAwait(false);
                Volatile.Write(ref _reindexRetries, 0);

                int count = _controller.Audio.PhraseCount;
                PostUi(() => StatusText.Text = $"База фраз обновлена: {count} шт.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Папку переименовали или заблокировали посреди обхода: пробуем ещё не больше трёх раз.
                // Если папки больше нет, повторять бессмысленно (иначе пересборка крутилась бы бесконечно).
                if (Directory.Exists(folder) && Interlocked.Increment(ref _reindexRetries) <= 3)
                    ScheduleReindex();
                else
                    PostUi(() => StatusText.Text = $"Не удалось прочитать папку фраз: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Reindex] {ex}");
            }
        }

        private void StopFolderWatcher()
        {
            _folderWatcher?.Dispose();
            _folderWatcher = null;
            _reindexTimer?.Dispose();
            _reindexTimer = null;
            Interlocked.Exchange(ref _reindexCts, null)?.Cancel();
        }

        // ================= тестовый звук =================

        private void BtnTestSound_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_controller.Audio.IsRunning)
                {
                    StatusText.Text = "Запустите мост, чтобы проверить тестовый звук";
                    return;
                }

                _controller.Audio.PlaySound(EnsureTestSound());
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Не удалось воспроизвести встроенный тестовый звук: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Файл извлекается один раз: играющий файл не пересоздаётся (раньше второй клик давал IOException), поток ресурса закрывается.
        private static string EnsureTestSound()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       "VoicePhraseCast", "test_phrase.mpeg");
            if (File.Exists(path)) return path;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var info = System.Windows.Application.GetResourceStream(
                           new Uri("pack://application:,,,/Resources/Winwyv_levelup_04_ru.mp3.mpeg"))
                       ?? throw new FileNotFoundException("Встроенный тестовый звук не найден в сборке.");

            // Сначала во временный файл, затем переименование: оборванное копирование не оставит битый test_phrase.mpeg.
            string temp = path + ".tmp";
            using (info.Stream)
            using (var file = File.Create(temp))
                info.Stream.CopyTo(file);

            File.Move(temp, path, overwrite: true);
            return path;
        }

        // ================= окно, трей, панель настроек =================

        private void InitTrayIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();

            try
            {
                var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/VoicePhraseCast.ico"));
                if (info != null)
                {
                    using (info.Stream)
                        _trayIcon = new System.Drawing.Icon(info.Stream);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tray] Не удалось загрузить иконку из ресурсов: {ex.Message}");
            }

            if (_trayIcon == null)
            {
                try
                {
                    if (Environment.ProcessPath is { } exe)
                        _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Tray] Не удалось взять иконку exe: {ex.Message}");
                }
            }

            _notifyIcon.Icon = _trayIcon ?? System.Drawing.SystemIcons.Application;
            _notifyIcon.Text = "VoicePhraseCast вещает";

            _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Развернуть", null, (_, _) => RestoreFromTray());
            menu.Items.Add("Выход", null, (_, _) => Close());   // обычное закрытие: останавливает мост и отпускает рацию
            _notifyIcon.ContextMenuStrip = menu;

            _notifyIcon.Visible = true;
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();   // без этого окно может открыться под другими
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();   // убирает окно с панели задач

                // Баллун один раз за сеанс, а не при каждом сворачивании.
                if (_notifyIcon != null && !_balloonShown)
                {
                    _balloonShown = true;
                    _notifyIcon.ShowBalloonTip(2000, "Программа свёрнута", "Глобальный перехват фраз продолжает работать в фоне.",
                        System.Windows.Forms.ToolTipIcon.Info);
                }
            }

            base.OnStateChanged(e);
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_isSettingsExpanded)
            {
                // Фиксируем текущую высоту, снимаем прежние анимации и плавно сворачиваем панель.
                SettingsPanel.Height = SettingsPanel.ActualHeight;
                SettingsPanel.BeginAnimation(HeightProperty, null);

                var collapse = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromSeconds(0.25));
                SettingsPanel.BeginAnimation(HeightProperty, collapse);
                _isSettingsExpanded = false;
                return;
            }

            // Измеряем естественную высоту панели в режиме Auto, затем раскрываем от нуля.
            SettingsPanel.BeginAnimation(HeightProperty, null);
            SettingsPanel.Height = double.NaN;
            SettingsPanel.UpdateLayout();
            double targetHeight = SettingsPanel.DesiredSize.Height;
            SettingsPanel.Height = 0;

            var expand = new System.Windows.Media.Animation.DoubleAnimation(0, targetHeight, TimeSpan.FromSeconds(0.25));
            expand.Completed += (_, _) =>
            {
                SettingsPanel.BeginAnimation(HeightProperty, null);
                SettingsPanel.Height = double.NaN;   // после анимации высота снова подстраивается под содержимое
            };

            SettingsPanel.BeginAnimation(HeightProperty, expand);
            _isSettingsExpanded = true;
        }

        // ================= завершение работы =================

        // Закрытие асинхронное: сначала мост останавливается (рация отпускается, устройства закрываются, нативная память
        // освобождается в фоне), и только потом окно закрывается по-настоящему. UI-поток при этом не блокируется.
        protected override async void OnClosing(CancelEventArgs e)
        {
            if (_shutdownComplete)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
            if (_shutdownStarted) return;
            _shutdownStarted = true;
            IsEnabled = false;

            try
            {
                await Task.Yield();   // Hide()/Close() нельзя вызывать, пока идёт обработка Closing
                Hide();

                _saveTimer.Stop();
                SaveSettingsNow();
                StopFolderWatcher();
                DisarmGuards();

                await Task.WhenAny(_controller.ShutdownAsync(), Task.Delay(TimeSpan.FromSeconds(5)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] {ex}");
            }
            finally
            {
                _shutdownComplete = true;
            }

            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            System.Windows.Application.Current.SessionEnding -= OnSessionEnding;

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;   // иначе значок остаётся в трее до наведения мыши
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            _trayIcon?.Dispose();
            _trayIcon = null;

            base.OnClosed(e);
        }

        // Выход из Windows: ждать асинхронного закрытия некогда, останавливаем синхронно, но с ограничением по времени.
        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            try
            {
                SaveSettingsNow();
                _controller.ShutdownAsync().Wait(TimeSpan.FromSeconds(3));   // безопасно: внутри везде ConfigureAwait(false)
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionEnding] {ex.Message}");
            }
            finally
            {
                // WPF сразу после этого закрывает окна: асинхронный OnClosing не должен их удерживать.
                _shutdownStarted = true;
                _shutdownComplete = true;
            }
        }

        private static System.Windows.Media.Brush FrozenBrush(byte r, byte g, byte b)
        {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
