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

                    // Сброс статусных сообщений до исходного рабочего состояния
                    if (StatusText.Text != "Воспроизведение остановлено" && StatusText.Text != "Воспроизведение остановлено пользователем")
                    {
                        StatusText.Text = "Статус: РАБОТАЕТ";
                    }
                }));
            };

            // Подписка на промежуточные результаты распознавания для вывода текста в реальном времени
            _processor.OnPhraseRecognized += (text) => {
                this.Dispatcher.BeginInvoke(new Action(() => {
                    RecognizedText.Text = $"Слышу: {text}";
                }));
            };

            Task.Run(async () => await _processor.InitializeVoskAsync());

            // Загрузка сохраненных путей к директориям и конфигурации горячих клавиш
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

            // Загрузка параметров громкости из пользовательских настроек
            float savedVolumeMic = VoicePhraseCast.Properties.Settings.Default.VolumeMic;
            float savedVolumeCable = VoicePhraseCast.Properties.Settings.Default.VolumeCable;

            // Инициализация значений слайдеров (события ValueChanged блокируются флагом _isDataLoaded)
            SliderMic.Value = savedVolumeMic;
            SliderCable.Value = savedVolumeCable;

            if (TxtMicVolumeValue != null) TxtMicVolumeValue.Text = $"{Math.Round(savedVolumeMic * 100)}%";
            if (TxtCableVolumeValue != null) TxtCableVolumeValue.Text = $"{Math.Round(savedVolumeCable * 100)}%";

            if (_processor != null)
            {
                _processor.VolumeMic = savedVolumeMic;
                _processor.VolumeCable = savedVolumeCable;
            }

            // Загрузка и инициализация состояния аппаратного мониторинга звука
            bool savedMonitorState = VoicePhraseCast.Properties.Settings.Default.IsMonitorEnabled;

            // Активация состояния в интерфейсе
            ChkMonitor.IsChecked = savedMonitorState;

            // Передача его значение в процессор
            if (_processor != null)
            {
                _processor.IsMonitoringEnabled = savedMonitorState;
            }

            InitTrayIcon();

            // Загрузка и инициализация статуса программной эмуляции радиостанции
            bool savedEmulationState = VoicePhraseCast.Properties.Settings.Default.IsEmulationEnabled;
            ChkEmulationEnabled.IsChecked = savedEmulationState;

            if (_processor != null)
            {
                _processor.IsEmulationEnabled = savedEmulationState;
            }

            SettingsPanel.Height = 0;

            // Инициализация данных завершена, разрешение на перезапись конфигурационных файлов активировано
            _isDataLoaded = true;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Освобождение системного хука клавиатуры перед завершением процесса
            _hook.Unhook();

            // Остановка всех потоков аудиопроцессора
            _processor.Stop();

            base.OnClosing(e);
        }

        private AudioProcessor _processor = new AudioProcessor();

        private void InitTrayIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();

            try
            {
                // Загрузка графического ресурса иконки приложения из встроенных ресурсов сборки
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
                // Резервный перехват: извлечение стандартной иконки сборки при сбое загрузки кастомного ресурса
                System.Windows.MessageBox.Show($"Не удалось загрузить мага в трей: {ex.Message}\nБудет использована стандартная иконка.");
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
            }

            // Текст при наведении на иконку в трее
            _notifyIcon.Text = "VoicePhraseCast вещает";

            // Восстановление нормального состояния окна при двойном клике по иконке трея
            _notifyIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
            };

            // Инициализация контекстного меню системного трея (ПКМ по иконке)
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

            // Инициализация видимости иконки трея при старте приложения (основное переключение состояния происходит в OnStateChanged)
            _notifyIcon.Visible = true;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.Hide(); // Удаление окна с панели задач при сворачивании
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = true; // Отображение иконки в трее

                    // Вывод системного уведомления ОС об активности фонового процесса
                    _notifyIcon.ShowBalloonTip(2000, "Программа свёрнута", "Глобальный перехват фраз продолжает работать в фоне.", System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            base.OnStateChanged(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            // Принудительное уничтожение дескриптора иконки трея во избежание зависания процесса в проводнике Windows
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
                // Фиксация текущей реальной высоты компонента перед запуском анимации закрытия
                SettingsPanel.Height = SettingsPanel.ActualHeight;

                // Удаление текущих анимационных привязок свойства Height
                SettingsPanel.BeginAnimation(HeightProperty, null);

                // Плавное свёртывание панели до нулевой высоты
                System.Windows.Media.Animation.DoubleAnimation anim = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromSeconds(0.25));
                SettingsPanel.BeginAnimation(HeightProperty, anim);
                _isSettingsExpanded = false;
            }
            else
            {
                // Сброс старых анимаций для освобождения свойства Height
                SettingsPanel.BeginAnimation(HeightProperty, null);

                // Временный перевод логики расчета высоты в режим Auto
                SettingsPanel.Height = double.NaN;

                // Принудительный пересчет дерева элементов графического интерфейса для определения размеров контента
                SettingsPanel.UpdateLayout();

                // Измерение фактической целевой высоты развернутого StackPanel
                double targetHeight = SettingsPanel.DesiredSize.Height;

                // Предотвращение визуального скачка интерфейса непосредственным обнулением высоты перед стартом
                SettingsPanel.Height = 0;

                // Настройка и инициализация анимации плавного раскрытия компонента
                System.Windows.Media.Animation.DoubleAnimation anim = new System.Windows.Media.Animation.DoubleAnimation(0, targetHeight, TimeSpan.FromSeconds(0.25));

                // По завершении анимации свойство освобождается и переводится в Auto для обеспечения динамической гибкости
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
                // Формирование строкового имени выбранного аудиокабеля для отображения в интерфейсе пользователя
                string cableName = indices.cableId == 5 ? "CABLE In 16ch Output" : "CABLE Output";

                HintText.Text = $"💡 Для работы в игре: зайдите в настройки звука игры и выберите микрофон: '{cableName}'";
            }
            // Валидация выбора аудиоустройств в комбобоксах UI-интерфейса
            if (MicComboBox.SelectedItem is AudioDevice mic &&
                CableComboBox.SelectedItem is AudioDevice cable &&
                MonitorComboBox.SelectedItem is AudioDevice monitor)
            {
                // Передача ID аудиоустройств в метод Start
                _processor.Start(mic.Id, cable.Id, monitor.Id);

                // Обновляем статус (для наглядности)
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
            // Очистка полей комбобоксов перед загрузкой новых данных
            MicComboBox.Items.Clear();
            CableComboBox.Items.Clear();
            MonitorComboBox.Items.Clear();

            // Явно указание свойства отображения для комбобоксов, чтобы они показывали имена устройств
            MicComboBox.DisplayMemberPath = "Name";
            CableComboBox.DisplayMemberPath = "Name";
            MonitorComboBox.DisplayMemberPath = "Name";

            // Перечисление и заполнение доступных физических устройств записи звука
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                MicComboBox.Items.Add(new AudioDevice { Id = i, Name = WaveIn.GetCapabilities(i).ProductName });
            }

            // Перечисление и заполнение доступных аудиовыходов (микшеры, кабели)
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                CableComboBox.Items.Add(new AudioDevice { Id = i, Name = WaveOut.GetCapabilities(i).ProductName });
            }

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                MonitorComboBox.Items.Add(new AudioDevice { Id = i, Name = WaveOut.GetCapabilities(i).ProductName });
            }

            // Автоматическое позиционирование индексов комбобоксов под аппаратные аудиоустройства по умолчанию
            MicComboBox.SelectedIndex = MicComboBox.Items.Cast<AudioDevice>()
                .ToList().FindIndex(d => d.Name.Contains("HyperX"));
            CableComboBox.SelectedIndex = CableComboBox.Items.Cast<AudioDevice>()
                .ToList().FindIndex(d => d.Name.Contains("CABLE Input"));
            MonitorComboBox.SelectedIndex = MonitorComboBox.Items.Cast<AudioDevice>()
                .ToList().FindIndex(d => d.Name.Contains("High Definition"));
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            if (dialog.ShowDialog() == true)
            {
                TxtPath.Text = dialog.FolderName;
                _processor.LoadPhrases(dialog.FolderName);

                // Сохранение выбранного пути в постоянную конфигурацию .NET
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
                    // Извлечение и парсинг актуальных кодов виртуальных клавиш из текстовых полей интерфейса
                    int activationVkCode = KeyInterop.VirtualKeyFromKey((Key)Enum.Parse(typeof(Key), TxtActivationKey.Text, true));
                    byte emulationVkCode = (byte)KeyInterop.VirtualKeyFromKey((Key)Enum.Parse(typeof(Key), TxtEmulationKey.Text, true));
                    int stopVkCode = KeyInterop.VirtualKeyFromKey((Key)Enum.Parse(typeof(Key), TxtStopKey.Text, true));

                    // Передаем выбранную клавишу для эмуляции в процессор
                    _processor.CurrentEmulationKey = emulationVkCode;

                    // Очищаем старые подписки, чтобы избежать дублирования
                    _hook.ClearSubscribers();

                    // Подписка на событие зажатия горячей клавиши (активация записи потока и прослушивания ИИ)
                    _hook.OnKeyDown += (vkCode) => {
                        if (vkCode == activationVkCode)
                        {
                            if (_isKeyAlreadyPressed) return; // Игнорирование аппаратного автоповтора зажатой клавиши ОС
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

                    // Подписка на событие отпускания горячей клавиши (остановка записи, извлечение буфера и фильтрация фразы)
                    _hook.OnKeyUp += (vkCode) => {
                        if (vkCode == activationVkCode)
                        {
                            _isKeyAlreadyPressed = false;

                            Dispatcher.BeginInvoke(new Action(() => {
                                _processor.ToggleVosk(false);
                                StatusText.Text = $"Статус: РАБОТАЕТ ({TxtActivationKey.Text} активна)";

                                // Извлечение финального текста из внутреннего буфера Vosk
                                string finalSpeech = _processor.GetFinalText()?.Trim() ?? "";

                                // Резервный перехват промежуточного экранного текста при пустом итоговом буфере
                                if (string.IsNullOrEmpty(finalSpeech))
                                {
                                    // Очистка служебных префиксов интерфейса во избежание циклического дублирования строк
                                    finalSpeech = RecognizedText.Text
                                        .Replace("Слышу: ", "")
                                        .Replace("Финально: ", "")
                                        .Trim();
                                }

                                // Инициализация конвейера нечеткого сопоставления при наличии валидного текста
                                if (!string.IsNullOrEmpty(finalSpeech))
                                {
                                    RecognizedText.Text = $"Финально: {finalSpeech}";
                                    _processor.ProcessFinalPhrase(finalSpeech);
                                }
                            }));
                        }
                    };

                    // Синхронизация состояния мониторинга звука в наушниках
                    _processor.IsMonitoringEnabled = ChkMonitor.IsChecked ?? false;

                    // Загрузка и инициализация языковых библиотек Vosk
                    string modelPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model");
                    _processor.InitVosk(modelPath);

                    // Автоматическое определение реальных аппаратных индексов аудиоустройств ввода-вывода Windows
                    var realIndices = _processor.GetDeviceIndices();

                    // Если аппартные индексы нашлись (-1 означает, что не найдено), тогда используются они, иначе - комбобоксы
                    int finalMicId = realIndices.micId != -1 ? realIndices.micId : mic.Id;
                    int finalCableId = realIndices.cableId != -1 ? realIndices.cableId : cable.Id;
                    int finalMonitorId = monitor.Id;

                    // Активация аудиопотоков и установка глобального низкоуровневого хука клавиатуры
                    _processor.Start(finalMicId, finalCableId, finalMonitorId);
                    _hook.SetHook();

                    // Фиксация состояния работы моста для блокировки повторного запуска
                    _isBridgeRunning = true;

                    // Визуальное обновление элементов управления интерфейса
                    StatusText.Text = $"Статус: РАБОТАЕТ ({TxtActivationKey.Text} активна)";
                    StatusText.Foreground = System.Windows.Media.Brushes.Green;

                    // Визуальное обновление кнопки "Старт" для индикации активного состояния
                    BtnStart.Content = "РАБОТАЕТ";
                    var greenColor = (Color)System.Windows.Media.ColorConverter.ConvertFromString("#15803D");
                    BtnStart.Background = new SolidColorBrush(greenColor);
                    BtnStart.Foreground = System.Windows.Media.Brushes.White;

                    BtnStop.IsEnabled = true;

                    // Деактивация полей ввода для предотвращения случайного сброса клавиш во время работы моста
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

            // Фиксация состояния остановки моста для разрешения повторного запуска
            _isBridgeRunning = false;

            // Обновление статуса интерфейса для наглядности
            StatusText.Text = "Статус: Остановлено";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;

            BtnStop.IsEnabled = false;

            // Возврат кнопки "Старт" в исходное состояние для повторного запуска
            BtnStart.Content = "ЗАПУСТИТЬ МОСТ";

            // Возврат цветовой схемы кнопки Старт к исходным XAML-значениям по умолчанию
            var defaultColor = (Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E2937");
            BtnStart.Background = new SolidColorBrush(defaultColor);
            BtnStart.Foreground = System.Windows.Media.Brushes.White;

            // Возврат полей ввода горячих клавиш в активное состояние для редактирования
            TxtActivationKey.IsEnabled = true;
            TxtEmulationKey.IsEnabled = true;
            TxtStopKey.IsEnabled = true;
        }

        private void BtnTestSound_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Формирование пути к файлу во временной директории операционной системы
                string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_phrase.mpeg");

                // Извлечение встроенного аудиоресурса из манифеста исполняемого файла сборки
                Uri resourceUri = new Uri("pack://application:,,,/Resources/Winwyv_levelup_04_ru.mp3.mpeg");
                var resourceInfo = System.Windows.Application.GetResourceStream(resourceUri);

                if (resourceInfo != null)
                {
                    // Побайтовое копирование аудиоданных из ресурсов во временный файл файловой системы
                    using (var fileStream = System.IO.File.Create(tempFile))
                    {
                        resourceInfo.Stream.CopyTo(fileStream);
                    }

                    // Передача временного файла в метод воспроизведения звука аудиопроцессора
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

                // Смена флага мониторинга в аудиопроцессоре в реальном времени
                _processor.IsMonitoringEnabled = isEnabled;
                _processor.SetMonitoring(isEnabled);

                // Обновление статусного текста для наглядности
                StatusText.Text = isEnabled ? "Мониторинг включен" : "Мониторинг выключен";

                // Отмена сохранения в конфигурационный файл, если данные еще не были загружены (чтобы избежать перезаписи при инициализации)
                if (!_isDataLoaded) return;

                // Сохранение состояния мониторинга в конфигурационный файл .NET
                VoicePhraseCast.Properties.Settings.Default.IsMonitorEnabled = isEnabled;
                VoicePhraseCast.Properties.Settings.Default.Save();
            }
        }

        private void ChkEmulationEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (_processor != null)
            {
                bool isEnabled = ChkEmulationEnabled.IsChecked ?? true;

                // Смена флага эмуляции в аудиопроцессоре в реальном времени
                _processor.IsEmulationEnabled = isEnabled;

                // Обновление статусного текста для наглядности
                StatusText.Text = isEnabled ? "Эмуляция рации активна" : "Эмуляция рации отключена (Стелс-режим)";

                if (!_isDataLoaded) return;

                // Сохранение состояния эмуляции в конфигурационный файл .NET
                VoicePhraseCast.Properties.Settings.Default.IsEmulationEnabled = isEnabled;
                VoicePhraseCast.Properties.Settings.Default.Save();
            }
        }

        private void KeyField_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Блокировка перехвата клавиатурного ввода, если мост находится в активном состоянии
            if (!BtnStart.IsEnabled)
            {
                // Предотвращение WPF обработать нажатие, чтобы пользователь не мог случайно сбросить клавишу во время работы
                return;
            }

            e.Handled = true;

            // Игнорирование служебных навигационных клавиш и модификаторов ОС
            if (e.Key == Key.Tab || e.Key == Key.Capital || e.Key == Key.LeftShift || e.Key == Key.RightShift) return;

            var currentTextBox = sender as System.Windows.Controls.TextBox;
            if (currentTextBox == null) return;

            // Определение фактической клавиши, учитывая системные клавиши (например, Alt)
            Key pressedKey = (e.Key == Key.System) ? e.SystemKey : e.Key;
            string pressedKeyName = pressedKey.ToString();

            // Валидация горячих клавиш на предмет пересечения и дублирования значений
            if (currentTextBox == TxtActivationKey && (pressedKeyName == TxtEmulationKey.Text || pressedKeyName == TxtStopKey.Text) ||
          currentTextBox == TxtEmulationKey && (pressedKeyName == TxtActivationKey.Text || pressedKeyName == TxtStopKey.Text) ||
          currentTextBox == TxtStopKey && (pressedKeyName == TxtActivationKey.Text || pressedKeyName == TxtEmulationKey.Text))
            {
                System.Windows.MessageBox.Show("Клавиши не могут быть одинаковыми!",
                                "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // При успешной валидации обновляем текстовое поле с именем нажатой клавиши
            currentTextBox.Text = pressedKeyName;

            // Снятие фокуса с текстового поля, чтобы предотвратить дальнейший ввод и зафиксировать выбранную клавишу
            Keyboard.ClearFocus();

            // Сохранение настроек
            SaveKeySettings();
        }

        private void SaveKeySettings()
        {
            VoicePhraseCast.Properties.Settings.Default.ActivationKey = TxtActivationKey.Text;
            VoicePhraseCast.Properties.Settings.Default.EmulationKey = TxtEmulationKey.Text;
            VoicePhraseCast.Properties.Settings.Default.StopKey = TxtStopKey.Text;
            VoicePhraseCast.Properties.Settings.Default.Save();

            // Обновление текущей клавиши эмуляции в аудиопроцессоре, если он уже инициализирован
            if (_processor != null)
            {
                try
                {
                    _processor.CurrentEmulationKey = (byte)KeyInterop.VirtualKeyFromKey(
                        (Key)Enum.Parse(typeof(Key), TxtEmulationKey.Text));
                }
                catch { /* Перехват исключения при обработке незаполненных значений */ }
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

            // В случае, если окно еще не завершило инициализацию, в конфигурационный файл значения не сохраняются, чтобы избежать перезаписи при старте
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

            // В случае, если окно еще не завершило инициализацию, в конфигурационный файл значения не сохраняются, чтобы избежать перезаписи при старте
            if (!_isDataLoaded) return;

            VoicePhraseCast.Properties.Settings.Default.VolumeCable = volume;
            VoicePhraseCast.Properties.Settings.Default.Save();
        }
    }
}