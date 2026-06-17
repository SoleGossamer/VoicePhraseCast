using DotaVoiceAssistant;
using NAudio.Wave;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;



namespace VoicePhraseCast
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private KeyboardHook _hook = new KeyboardHook();
        private bool _isKeyAlreadyPressed = false;
        private System.Windows.Forms.NotifyIcon _notifyIcon = null!;
        private bool _isDataLoaded = false;
        private bool _isSettingsExpanded = false;
        private bool _isBridgeRunning = false;
        public MainWindow()
        {
            InitializeComponent();
            LoadDevices();

            _processor.OnVoskStatusChanged += (status) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatusText.Text = $"Статус ИИ: {status}";
                }));
            };
            _processor.OnSoundFinished += () => {
                Dispatcher.BeginInvoke(new Action(() => {
                    RecognizedText.Text = "Слышу: ...";

                    // Переписываем статус на "РАБОТАЕТ" только если там не горит сообщение об остановке
                    if (StatusText.Text != "Воспроизведение остановлено" && StatusText.Text != "Воспроизведение остановлено пользователем")
                    {
                        StatusText.Text = "Статус: РАБОТАЕТ";
                    }
                }));
            };

            // Подписка на промежуточные результаты распознавания (вывод текста на экран "на лету")
            _processor.OnPhraseRecognized += (text) => {
                this.Dispatcher.BeginInvoke(new Action(() => {
                    RecognizedText.Text = $"Слышу: {text}";
                }));
            };

            Task.Run(async () => await _processor.InitializeVoskAsync());

            // Загрузка путей и кнопок
            string savedPath = VoicePhraseCast.Properties.Settings.Default.LastPath;
            if (!string.IsNullOrEmpty(savedPath))
            {
                TxtPath.Text = savedPath;
                _processor.LoadPhrases(savedPath);
            }

            string actKey = VoicePhraseCast.Properties.Settings.Default.ActivationKey;
            string emuKey = VoicePhraseCast.Properties.Settings.Default.EmulationKey;
            string stopKey = VoicePhraseCast.Properties.Settings.Default.StopKey;
            if (!string.IsNullOrEmpty(actKey)) TxtActivationKey.Text = actKey;
            if (!string.IsNullOrEmpty(emuKey)) TxtEmulationKey.Text = emuKey;
            if (!string.IsNullOrEmpty(stopKey)) TxtStopKey.Text = stopKey;

            // --- ЗАГРУЖАЕМ ГРОМКОСТЬ ИЗ НАСТРОЕК (в таблице Settings верни дефолт 1) ---
            float savedVolumeMic = VoicePhraseCast.Properties.Settings.Default.VolumeMic;
            float savedVolumeCable = VoicePhraseCast.Properties.Settings.Default.VolumeCable;

            // Выставляем слайдеры. События сработают, но наш флаг _isDataLoaded их заблокирует!
            SliderMic.Value = savedVolumeMic;
            SliderCable.Value = savedVolumeCable;

            if (TxtMicVolumeValue != null) TxtMicVolumeValue.Text = $"{Math.Round(savedVolumeMic * 100)}%";
            if (TxtCableVolumeValue != null) TxtCableVolumeValue.Text = $"{Math.Round(savedVolumeCable * 100)}%";

            if (_processor != null)
            {
                _processor.VolumeMic = savedVolumeMic;
                _processor.VolumeCable = savedVolumeCable;
            }

            // Загружаем сохраненный статус мониторинга
            bool savedMonitorState = VoicePhraseCast.Properties.Settings.Default.IsMonitorEnabled;

            // Выставляем галочку в интерфейсе
            ChkMonitor.IsChecked = savedMonitorState;

            // Сразу передаем её значение в процессор
            if (_processor != null)
            {
                _processor.IsMonitoringEnabled = savedMonitorState;
            }

            InitTrayIcon();

            // Загружаем статус эмуляции рации
            bool savedEmulationState = VoicePhraseCast.Properties.Settings.Default.IsEmulationEnabled;
            ChkEmulationEnabled.IsChecked = savedEmulationState;

            if (_processor != null)
            {
                _processor.IsEmulationEnabled = savedEmulationState;
            }

            SettingsPanel.Height = 0;

            // ВСЁ ГОТОВО: Включаем зеленый свет для сохранения!
            _isDataLoaded = true;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Отключаем хук перед выходом
            _hook.Unhook();

            // Останавливаем аудио-процессор
            _processor.Stop();

            base.OnClosing(e);
        }

        private AudioProcessor _processor = new AudioProcessor();

        private void InitTrayIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();

            try
            {
                // Загружаем твоего пиксельного мага напрямую из встроенных ресурсов WPF
                System.Windows.Resources.StreamResourceInfo sri = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/Resources/VoicePhraseCast.ico")
                );

                if (sri != null)
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(sri.Stream);
                }
            }
            catch (Exception ex)
            {
                // Если забыл настроить Build Action: Resource или папка называется иначе, 
                // приложение не упадет, а аккуратно подставит дефолтную иконку сборки
                System.Windows.MessageBox.Show($"Не удалось загрузить мага в трей: {ex.Message}\nБудет использована стандартная иконка.");
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
            }

            // Текст при наведении на мага в трее
            _notifyIcon.Text = "VoicePhraseCast вещает";

            // При двойном клике по иконке в трее — разворачиваем окно обратно
            _notifyIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
            };

            // Создаем контекстное меню для трея (по правому клику)
            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("Развернуть", null, (s, e) => {
                this.Show();
                this.WindowState = WindowState.Normal;
            });
            contextMenu.Items.Add("Выход", null, (s, e) => {
                _notifyIcon.Dispose();
                System.Windows.Application.Current.Shutdown();
            });

            _notifyIcon.ContextMenuStrip = contextMenu;

            // Важно: делаем иконку видимой сразу при старте, если это необходимо, 
            // либо оставляем её включение только при сворачивании (у тебя логика включения ниже в OnStateChanged)
            _notifyIcon.Visible = true;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.Hide(); // Прячем окно с панели задач
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = true; // Показываем иконку в трее

                    // Показываем красивое всплывающее уведомление Windows при первом сворачивании
                    _notifyIcon.ShowBalloonTip(2000, "Программа свернута", "Глобальный перехват фраз продолжает работать в фоне.", System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            base.OnStateChanged(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            // Обязательно уничтожаем иконку при закрытии, иначе она "зависнет" в трее до перезагрузки проводника
            if (_notifyIcon != null)
            {
                _notifyIcon.Dispose();
            }
            base.OnClosed(e);
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_isSettingsExpanded)
            {
                // 1. Фиксируем текущую реальную высоту в пикселях перед закрытием
                SettingsPanel.Height = SettingsPanel.ActualHeight;

                // 2. Отключаем удерживание свойства анимацией (сбрасываем старый хук)
                SettingsPanel.BeginAnimation(HeightProperty, null);

                // 3. Плавно сворачиваем панель до 0
                System.Windows.Media.Animation.DoubleAnimation anim = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromSeconds(0.25));
                SettingsPanel.BeginAnimation(HeightProperty, anim);
                _isSettingsExpanded = false;
            }
            else
            {
                // 1. СБРОС АНИМАЦИИ: Освобождаем свойство Height от предыдущих запусков
                SettingsPanel.BeginAnimation(HeightProperty, null);

                // 2. Временно сбрасываем высоту в Auto для проведения точных расчетов
                SettingsPanel.Height = double.NaN;

                // 3. Принудительно обновляем дерево элементов, чтобы StackPanel внутри развернулся на максимум
                SettingsPanel.UpdateLayout();

                // 4. Замеряем, сколько честных пикселей занимает весь контент настроек
                double targetHeight = SettingsPanel.DesiredSize.Height;

                // 5. Возвращаем высоту в 0 непосредственно перед стартом, чтобы избежать визуального скачка
                SettingsPanel.Height = 0;

                // 6. Конфигурируем и запускаем анимацию раскрытия
                System.Windows.Media.Animation.DoubleAnimation anim = new System.Windows.Media.Animation.DoubleAnimation(0, targetHeight, TimeSpan.FromSeconds(0.25));

                // 7. Как только панель полностью раскрылась, сбрасываем анимацию в null и ставим Auto (double.NaN),
                // чтобы окно оставалось гибким и динамически адаптировалось под контент в будущем
                anim.Completed += (s, args) =>
                {
                    SettingsPanel.BeginAnimation(HeightProperty, null);
                    SettingsPanel.Height = double.NaN;
                };

                SettingsPanel.BeginAnimation(HeightProperty, anim);
                _isSettingsExpanded = true;
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            var indices = _processor.GetDeviceIndices();

            if (indices.cableId == -1)
            {
                System.Windows.MessageBox.Show(
                    "Внимание! В системе не найден VB-Audio Virtual Cable.\n\n" +
                    "Программа будет воспроизводить звук только в ваши наушники. " +
                    "Для того чтобы фразы работали в голосовом чате игры, пожалуйста, установите виртуальный аудиокабель и перезапустите программу.",
                    "Предупреждение: Кабель не найден",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
            else
            {
                // Если кабель найден, выводим подсказку, какой именно кабель выбрала программа
                // Чтобы пользователь точно знал, что выбрать в настройках игры!
                string cableName = indices.cableId == 5 ? "CABLE In 16ch Output" : "CABLE Output";

                HintText.Text = $"💡 Для работы в игре: зайдите в настройки звука игры и выберите микрофон: '{cableName}'";
            }
            // Берем выбранные устройства напрямую из интерфейса
            if (MicComboBox.SelectedItem is AudioDevice mic &&
                CableComboBox.SelectedItem is AudioDevice cable &&
                MonitorComboBox.SelectedItem is AudioDevice monitor)
            {
                // Передаем их ID в метод Start
                _processor.Start(mic.Id, cable.Id, monitor.Id);

                // Обновляем статус (для красоты)
                StatusText.Text = "Статус: РАБОТАЕТ";
                StatusText.Foreground = System.Windows.Media.Brushes.Green;
            }
            else
            {
                System.Windows.MessageBox.Show("Ошибка: Выберите все три устройства (Микрофон, Кабель и Наушники).");
            }
        }

        private void BtnOpenFolder_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _processor.OpenPhrasesFolder();
        }

        public class AudioDevice
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
        }

        private void LoadDevices()
        {
            // Очистим на всякий случай
            MicComboBox.Items.Clear();
            CableComboBox.Items.Clear();
            MonitorComboBox.Items.Clear();

            // Явно говорим, что показывать Имя
            MicComboBox.DisplayMemberPath = "Name";
            CableComboBox.DisplayMemberPath = "Name";
            MonitorComboBox.DisplayMemberPath = "Name";
            // Заполняем микрофоны
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                MicComboBox.Items.Add(new AudioDevice { Id = i, Name = WaveIn.GetCapabilities(i).ProductName });
            }

            // Заполняем выходы (динамики/кабели)
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                CableComboBox.Items.Add(new AudioDevice { Id = i, Name = WaveOut.GetCapabilities(i).ProductName });
            }

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                MonitorComboBox.Items.Add(new AudioDevice { Id = i, Name = WaveOut.GetCapabilities(i).ProductName });
            }

            // Пытаемся выбрать твои девайсы автоматически
            MicComboBox.SelectedIndex = MicComboBox.Items.Cast<AudioDevice>()
                .ToList().FindIndex(d => d.Name.Contains("HyperX"));
            CableComboBox.SelectedIndex = CableComboBox.Items.Cast<AudioDevice>()
                .ToList().FindIndex(d => d.Name.Contains("CABLE Input"));
            MonitorComboBox.SelectedIndex = MonitorComboBox.Items.Cast<AudioDevice>()
                .ToList().FindIndex(d => d.Name.Contains("High Definition"));
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            // Используем WinForms диалог (нужно добавить ссылку на System.Windows.Forms или Microsoft.Win32)
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            if (dialog.ShowDialog() == true)
            {
                TxtPath.Text = dialog.FolderName;
                _processor.LoadPhrases(dialog.FolderName);

                // Сохраняем путь навсегда
                VoicePhraseCast.Properties.Settings.Default.LastPath = dialog.FolderName;
                VoicePhraseCast.Properties.Settings.Default.Save();
            }
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_isBridgeRunning) return;

            if (MicComboBox.SelectedItem is AudioDevice mic &&
                CableComboBox.SelectedItem is AudioDevice cable &&
                MonitorComboBox.SelectedItem is AudioDevice monitor)
            {
                try
                {
                    // 1. Получаем актуальные коды клавиш из текстовых полей
                    int activationVkCode = KeyInterop.VirtualKeyFromKey((Key)Enum.Parse(typeof(Key), TxtActivationKey.Text, true));
                    byte emulationVkCode = (byte)KeyInterop.VirtualKeyFromKey((Key)Enum.Parse(typeof(Key), TxtEmulationKey.Text, true));
                    int stopVkCode = KeyInterop.VirtualKeyFromKey((Key)Enum.Parse(typeof(Key), TxtStopKey.Text, true));

                    // Передаем выбранную клавишу для эмуляции в процессор
                    _processor.CurrentEmulationKey = emulationVkCode;

                    // Очищаем старые подписки, чтобы избежать дублирования
                    _hook.ClearSubscribers();

                    // 2. Подписка на зажатие (активация режима прослушивания ИИ)
                    _hook.OnKeyDown += (vkCode) => {
                        if (vkCode == activationVkCode)
                        {
                            if (_isKeyAlreadyPressed) return; // Игнорируем автоповтор зажатой клавиши
                            _isKeyAlreadyPressed = true;

                            Dispatcher.BeginInvoke(new Action(() => {
                                _processor.ToggleVosk(true);
                                StatusText.Text = "СЛУШАЮ...";
                            }));
                        }
                        if (vkCode == stopVkCode)
                        {
                            _processor.StopCurrentSound();
                            Dispatcher.BeginInvoke(new Action(() => {
                                StatusText.Text = "Воспроизведение остановлено";
                            }));
                        }
                    };

                    // 3. Подписка на отпускание кнопки (завершение записи и сопоставление фразы)
                    // 3. Подписка на отпускание кнопки
                    _hook.OnKeyUp += (vkCode) => {
                        if (vkCode == activationVkCode)
                        {
                            _isKeyAlreadyPressed = false;

                            Dispatcher.BeginInvoke(new Action(() => {
                                _processor.ToggleVosk(false);
                                StatusText.Text = $"Статус: РАБОТАЕТ ({TxtActivationKey.Text} активна)";

                                // 1. Проверяем чистый буфер Vosk
                                string finalSpeech = _processor.GetFinalText()?.Trim() ?? "";

                                // 2. Если в буфере пусто, берем текст с экрана
                                if (string.IsNullOrEmpty(finalSpeech))
                                {
                                    // ОЧИЩАЕМ И ОТ "Слышу: ", И ОТ "Финально: ", чтобы текст не дублировался циклично!
                                    finalSpeech = RecognizedText.Text
                                        .Replace("Слышу: ", "")
                                        .Replace("Финально: ", "")
                                        .Trim();
                                }

                                // 3. Запускаем обработку
                                if (!string.IsNullOrEmpty(finalSpeech))
                                {
                                    RecognizedText.Text = $"Финально: {finalSpeech}";
                                    _processor.ProcessFinalPhrase(finalSpeech);
                                }
                            }));
                        }
                    };

                    // Синхронизируем состояние мониторинга звука в наушниках
                    _processor.IsMonitoringEnabled = ChkMonitor.IsChecked ?? false;

                    // 4. Инициализация модели Vosk
                    string modelPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model");
                    _processor.InitVosk(modelPath);



                    // 5. РЕШЕНИЕ ПРОБЛЕМЫ ИНДЕКСОВ:
                    // Просим процессор автоматически найти настоящие, железные индексы устройств Windows
                    var realIndices = _processor.GetDeviceIndices();

                    // Если они нашлись (-1 означает, что не найдено) — используем их, иначе страхуемся комбобоксом
                    int finalMicId = realIndices.micId != -1 ? realIndices.micId : mic.Id;
                    int finalCableId = realIndices.cableId != -1 ? realIndices.cableId : cable.Id;
                    int finalMonitorId = monitor.Id;

                    // Запускаем аудио-потоки на проверенных ID и ставим глобальный хук клавиш
                    _processor.Start(finalMicId, finalCableId, finalMonitorId);
                    _hook.SetHook();

                    // Фиксируем, что мост запущен
                    _isBridgeRunning = true;

                    // Обновление интерфейса приложения
                    StatusText.Text = $"Статус: РАБОТАЕТ ({TxtActivationKey.Text} активна)";
                    StatusText.Foreground = System.Windows.Media.Brushes.Green;

                    // Вместо BtnStart.IsEnabled = false делаем визуальное переключение:
                    BtnStart.Content = "РАБОТАЕТ";
                    var greenColor = (Color)System.Windows.Media.ColorConverter.ConvertFromString("#15803D");
                    BtnStart.Background = new SolidColorBrush(greenColor);
                    BtnStart.Foreground = System.Windows.Media.Brushes.White;

                    BtnStop.IsEnabled = true;

                    // Визуально блокируем поля ввода, чтобы они стали серыми и недоступными для фокуса
                    TxtActivationKey.IsEnabled = false;
                    TxtEmulationKey.IsEnabled = false;
                    TxtStopKey.IsEnabled = false;
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Ошибка при запуске: {ex.Message}\nПроверьте настройки клавиш.");
                }
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _hook.Unhook();
            _processor.Stop();

            // Фиксируем, что мост остановлен
            _isBridgeRunning = false;

            // ОБНОВЛЕНИЕ ИНТЕРФЕЙСА ПРИ ОСТАНОВКЕ
            StatusText.Text = "Статус: Остановлено";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;

            BtnStop.IsEnabled = false;

            // ВОЗВРАЩАЕМ ИСХОДНЫЙ ВИД КНОПКЕ СТАРТ
            BtnStart.Content = "ЗАПУСТИТЬ МОСТ";

            // Сбрасываем цвета к тем, которые прописаны у тебя в XAML по умолчанию
            var defaultColor = (Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E2937");
            BtnStart.Background = new SolidColorBrush(defaultColor);
            BtnStart.Foreground = System.Windows.Media.Brushes.White;

            // Возвращаем доступность полям ввода клавиш
            TxtActivationKey.IsEnabled = true;
            TxtEmulationKey.IsEnabled = true;
            TxtStopKey.IsEnabled = true;
        }

        private void BtnTestSound_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. Формируем путь во временной папке Windows (например, AppData\Local\Temp\test_phrase.wav)
                string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_phrase.mpeg");

                // 2. Вытаскиваем файл из встроенных ресурсов сборки (папка Resources внутри проекта)
                Uri resourceUri = new Uri("pack://application:,,,/Resources/Winwyv_levelup_04_ru.mp3.mpeg");
                var resourceInfo = System.Windows.Application.GetResourceStream(resourceUri);

                if (resourceInfo != null)
                {
                    // 3. Копируем байты из exe-файла во временный файл на диске
                    using (var fileStream = System.IO.File.Create(tempFile))
                    {
                        resourceInfo.Stream.CopyTo(fileStream);
                    }

                    // 4. Скармливаем готовый временный путь в твой неизмененный метод PlaySound
                    if (System.IO.File.Exists(tempFile))
                    {
                        _processor.PlaySound(tempFile);
                    }
                }
                else
                {
                    System.Windows.MessageBox.Show("Встроенный тестовый звуковой ресурс не найден в сборке.", "Ошибка ресурсов", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Не удалось воспроизвести встроенный тестовый звук: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ChkMonitor_Click(object sender, RoutedEventArgs e)
        {
            if (_processor != null)
            {
                bool isEnabled = ChkMonitor.IsChecked ?? false;

                // Меняем состояние в процессоре на лету
                _processor.IsMonitoringEnabled = isEnabled;
                _processor.SetMonitoring(isEnabled);

                // Обновляем статус-бар для наглядности
                StatusText.Text = isEnabled ? "Мониторинг включен" : "Мониторинг выключен";

                // Если данные еще загружаются при старте приложения — не перезаписываем файл
                if (!_isDataLoaded) return;

                // Сохраняем в конфигурацию .NET
                VoicePhraseCast.Properties.Settings.Default.IsMonitorEnabled = isEnabled;
                VoicePhraseCast.Properties.Settings.Default.Save();
            }
        }

        private void ChkEmulationEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (_processor != null)
            {
                bool isEnabled = ChkEmulationEnabled.IsChecked ?? true;

                // Меняем флаг в процессоре на лету
                _processor.IsEmulationEnabled = isEnabled;

                // Обновляем статус для наглядности
                StatusText.Text = isEnabled ? "Эмуляция рации активна" : "Эмуляция рации отключена (Стелс-режим)";

                if (!_isDataLoaded) return;

                // Сохраняем в настройки .NET
                VoicePhraseCast.Properties.Settings.Default.IsEmulationEnabled = isEnabled;
                VoicePhraseCast.Properties.Settings.Default.Save();
            }
        }

        private void KeyField_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // ЕСЛИ ПРОГРАММА УЖЕ ЗАПУЩЕНА (Кнопка "Старт" отключена) — БЛОКИРУЕМ ИЗМЕНЕНИЯ
            if (!BtnStart.IsEnabled)
            {
                // Не даем WPF обработать нажатие и просто выходим, 
                // чтобы пользователь не мог случайно сбросить клавишу во время игры
                return;
            }

            e.Handled = true;

            // Игнорируем нажатия системных клавиш
            if (e.Key == Key.Tab || e.Key == Key.Capital || e.Key == Key.LeftShift || e.Key == Key.RightShift) return;

            var currentTextBox = sender as System.Windows.Controls.TextBox;
            if (currentTextBox == null) return;

            // Определяем нажатую клавишу
            Key pressedKey = (e.Key == Key.System) ? e.SystemKey : e.Key;
            string pressedKeyName = pressedKey.ToString();

            // ПРОВЕРКА НА ДУБЛИКАТ
            // Проверяем, не занята ли эта клавиша в другом поле
            if (currentTextBox == TxtActivationKey && (pressedKeyName == TxtEmulationKey.Text || pressedKeyName == TxtStopKey.Text) ||
          currentTextBox == TxtEmulationKey && (pressedKeyName == TxtActivationKey.Text || pressedKeyName == TxtStopKey.Text) ||
          currentTextBox == TxtStopKey && (pressedKeyName == TxtActivationKey.Text || pressedKeyName == TxtEmulationKey.Text))
            {
                System.Windows.MessageBox.Show("Клавиши не могут быть одинаковыми!",
                                "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Если проверка прошла — обновляем текст
            currentTextBox.Text = pressedKeyName;

            // Снимаем фокус
            Keyboard.ClearFocus();

            // Сохраняем настройки
            SaveKeySettings();
        }

        private void SaveKeySettings()
        {
            VoicePhraseCast.Properties.Settings.Default.ActivationKey = TxtActivationKey.Text;
            VoicePhraseCast.Properties.Settings.Default.EmulationKey = TxtEmulationKey.Text;
            VoicePhraseCast.Properties.Settings.Default.StopKey = TxtStopKey.Text;
            VoicePhraseCast.Properties.Settings.Default.Save();

            // Сразу обновляем клавишу эмуляции в процессоре, если он запущен
            if (_processor != null)
            {
                try
                {
                    _processor.CurrentEmulationKey = (byte)KeyInterop.VirtualKeyFromKey(
                        (Key)Enum.Parse(typeof(Key), TxtEmulationKey.Text));
                }
                catch { /* На случай пустых полей */ }
            }
        }

        private void SliderMic_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            float volume = (float)e.NewValue;

            if (_processor != null)
            {
                _processor.VolumeMic = volume;
            }

            if (TxtMicVolumeValue != null)
            {
                TxtMicVolumeValue.Text = $"{Math.Round(volume * 100)}%";
            }

            // ЕСЛИ ОКНО ЕЩЕ НЕ ИНИЦИАЛИЗИРОВАЛОСЬ — В ФАЙЛ НЕ ПИШЕМ!
            if (!_isDataLoaded) return;

            VoicePhraseCast.Properties.Settings.Default.VolumeMic = volume;
            VoicePhraseCast.Properties.Settings.Default.Save();
        }

        private void SliderCable_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            float volume = (float)e.NewValue;

            if (_processor != null)
            {
                _processor.VolumeCable = volume;
            }

            if (TxtCableVolumeValue != null)
            {
                TxtCableVolumeValue.Text = $"{Math.Round(volume * 100)}%";
            }

            // ЕСЛИ ОКНО ЕЩЕ НЕ ИНИЦИАЛИЗИРОВАЛОСЬ — В ФАЙЛ НЕ ПИШЕМ!
            if (!_isDataLoaded) return;

            VoicePhraseCast.Properties.Settings.Default.VolumeCable = volume;
            VoicePhraseCast.Properties.Settings.Default.Save();
        }
    }
}