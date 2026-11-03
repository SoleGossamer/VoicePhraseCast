using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace VoicePhraseCast
{
    /// <summary>
    /// Горячие клавиши: назначение в полях, перевод в коды, обработка событий InputHook (фразы по хоткею, диктовка в чат).
    /// Обработчики хука исполняются в потоках пула: контролы не читаются, нужные значения лежат в полях-снимках.
    /// </summary>
    public partial class MainWindow
    {
        private sealed record HotkeyMap(int Activation, int Stop, int SttTeam, int SttAll);

        private sealed record DictationOptions(bool AutoPaste, bool OpenChat, bool UseClipboard, bool SendEnter);

        private const int MaxHoldMs = 20_000;   // дольше клавиша «зажатой» не считается: запись принудительно завершается

        // Порядок: активация, рация, стоп звука, STT союзникам, STT всем. Совпадает с порядком в HotkeyBoxes.
        private static readonly string[] DefaultHotkeyIds = { "F1", "G", "Scroll", "D7", "D8" };

        // Источник истины — идентификатор клавиши (имя Key или MouseXButtonN). Текст в поле — только подпись:
        // она зависит от раскладки, и разбирать её обратно нельзя (на русской раскладке «[» превращается в «Х»).
        private readonly Dictionary<System.Windows.Controls.TextBox, string> _hotkeyIds = new();

        private volatile HotkeyMap _keys = new(0, 0, 0, 0);
        private volatile DictationOptions _dictation = new(true, true, true, true);
        private volatile bool _dictationToAllChat;
        private HoldGuard? _phraseGuard;
        private HoldGuard? _sttGuard;

        private AudioProcessor Audio => _controller.Audio;

        private IEnumerable<System.Windows.Controls.TextBox> HotkeyBoxes =>
            new[] { TxtActivationKey, TxtEmulationKey, TxtStopKey, TxtSttTeamHotkey, TxtSttAllHotkey };

        // ================= загрузка и сохранение назначений =================

        private void LoadHotkeysFromSettings()
        {
            EnsureMuteMicCheckBox();   // до LoadSettingsIntoUi: оно ищет флажок через FindName и вешает на него обработчик

            var s = Cfg;
            var boxes = HotkeyBoxes.ToArray();
            string?[] saved = { s.ActivationKey, s.EmulationKey, s.StopKey, s.SttTeamHotkey, s.SttAllHotkey };
            var ids = new string[boxes.Length];

            for (int i = 0; i < boxes.Length; i++)
            {
                // Битое значение (например, «Х» из старой версии) заменяется значением по умолчанию, а не роняет запуск моста.
                if (!TryNormalizeId(saved[i], out string id))
                    id = DefaultHotkeyIds[i];
                ids[i] = id;
            }

            // Две одинаковые клавиши делают раскладку неработоспособной: в этом случае возвращаем набор по умолчанию целиком.
            if (ids.Distinct().Count() != ids.Length)
                ids = (string[])DefaultHotkeyIds.Clone();

            for (int i = 0; i < boxes.Length; i++)
            {
                _hotkeyIds[boxes[i]] = ids[i];
                boxes[i].Text = LabelOfId(ids[i]);
                boxes[i].PreviewMouseDown += KeyField_PreviewMouseDown;
            }

            _activationLabel = TxtActivationKey.Text;
        }

        private void SaveKeySettings()
        {
            var s = Cfg;
            s.ActivationKey = _hotkeyIds[TxtActivationKey];
            s.EmulationKey = _hotkeyIds[TxtEmulationKey];
            s.StopKey = _hotkeyIds[TxtStopKey];
            s.SttTeamHotkey = _hotkeyIds[TxtSttTeamHotkey];
            s.SttAllHotkey = _hotkeyIds[TxtSttAllHotkey];
            ScheduleSave();
        }

        private bool TryAssign(System.Windows.Controls.TextBox box, string id, string label)
        {
            if (_controller.State != BridgeState.Stopped) return false;   // при работающем мосте хук слушает старые коды

            foreach (var pair in _hotkeyIds)
            {
                // Сравнение по id среди ВСЕХ пяти полей, включая «общий чат».
                if (pair.Key != box && pair.Value == id)
                {
                    System.Windows.MessageBox.Show("Клавиши не могут быть одинаковыми!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            _hotkeyIds[box] = id;
            box.Text = label;
            if (box == TxtActivationKey) _activationLabel = label;

            SaveKeySettings();
            return true;
        }

        private void KeyField_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Tab) return;   // Tab остаётся обычной навигацией
            e.Handled = true;

            if (_controller.State != BridgeState.Stopped) return;
            if (sender is not System.Windows.Controls.TextBox box) return;

            Key pressed = e.Key == Key.System ? e.SystemKey : e.Key;
            if (pressed is Key.None or Key.Capital or Key.LeftShift or Key.RightShift or Key.ImeProcessed or Key.DeadCharProcessed)
                return;

            if (TryAssign(box, pressed.ToString(), KeyLabel(pressed)))
                Keyboard.ClearFocus();
        }

        private void KeyField_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.XButton1 && e.ChangedButton != MouseButton.XButton2) return;
            e.Handled = true;

            if (_controller.State != BridgeState.Stopped) return;
            if (sender is not System.Windows.Controls.TextBox box) return;

            bool first = e.ChangedButton == MouseButton.XButton1;
            if (TryAssign(box, first ? "MouseXButton1" : "MouseXButton2", first ? "Mouse 4" : "Mouse 5"))
                Keyboard.ClearFocus();
        }

        // ================= идентификаторы, подписи, коды =================

        // Принимает и новые идентификаторы (имя Key), и значения старых версий ("7", "Y", "-").
        private static bool TryNormalizeId(string? text, out string id)
        {
            id = "";
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();

            if (text is "MouseXButton1" or "MouseXButton2")
            {
                id = text;
                return true;
            }

            if (text.Length == 1)
            {
                char c = text[0];
                if (char.IsDigit(c)) text = "D" + c;                    // иначе Enum.TryParse прочтёт "7" как число 7 = Key.Pause
                else if (char.IsLetter(c)) text = char.ToUpperInvariant(c).ToString();
                else text = LegacyOemName(c) ?? text;
            }
            else if (text.All(char.IsDigit))
            {
                return false;   // "12" Enum.TryParse тоже прочёл бы как число
            }

            if (!Enum.TryParse(text, true, out Key key) || !Enum.IsDefined(key) || key == Key.None)
                return false;

            id = key.ToString();
            return true;
        }

        // Таблица старых версий: символ из настроек → имя Oem-клавиши (раскладка US).
        private static string? LegacyOemName(char c) => c switch
        {
            '`' or '~' => "Oem3",
            '-' => "OemMinus",
            '+' or '=' => "OemPlus",
            '[' => "OemOpenBrackets",
            ']' => "OemCloseBrackets",
            '\'' => "OemQuotes",
            ';' => "OemSemicolon",
            ',' => "OemComma",
            '.' => "OemPeriod",
            '/' => "OemQuestion",
            '\\' => "OemPipe",
            _ => null
        };

        private static string LabelOfId(string id) => id switch
        {
            "MouseXButton1" => "Mouse 4",
            "MouseXButton2" => "Mouse 5",
            _ => Enum.TryParse(id, true, out Key key) ? KeyLabel(key) : id
        };

        // Только для подписи в поле, назад не разбирается.
        private static string KeyLabel(Key key)
        {
            string name = key.ToString();
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) return name[1].ToString();
            if (!name.StartsWith("Oem", StringComparison.Ordinal)) return name;

            uint ch = MapVirtualKey((uint)KeyInterop.VirtualKeyFromKey(key), 2) & 0x7FFFFFFF;   // 2 = MAPVK_VK_TO_CHAR
            return ch != 0 ? char.ToUpperInvariant((char)ch).ToString() : name;
        }

        private static int VkFromId(string id)
        {
            if (id == "MouseXButton1") return InputHook.VK_XBUTTON1;
            if (id == "MouseXButton2") return InputHook.VK_XBUTTON2;
            return Enum.TryParse(id, true, out Key key) ? KeyInterop.VirtualKeyFromKey(key) : 0;
        }

        private int VkOf(System.Windows.Controls.TextBox box)
        {
            int vk = VkFromId(_hotkeyIds[box]);
            if (vk == 0) throw new InvalidOperationException($"Для клавиши «{box.Text}» не найден код виртуальной клавиши.");
            return vk;
        }

        // Снимок назначений для потоков пула и параметры запуска моста.
        private BridgeConfig BuildConfig(AudioDevice mic, AudioDevice cable, AudioDevice monitor)
        {
            int activation = VkOf(TxtActivationKey);
            int emulation = VkOf(TxtEmulationKey);
            int stop = VkOf(TxtStopKey);
            int sttTeam = VkOf(TxtSttTeamHotkey);
            int sttAll = VkOf(TxtSttAllHotkey);

            _keys = new HotkeyMap(activation, stop, sttTeam, sttAll);
            _activationLabel = TxtActivationKey.Text;
            Audio.RadioKeyVk = emulation;   // клавиша рации только эмулируется, хук её не слушает

            return new BridgeConfig(mic.Id, cable.Id, monitor.Id, new[] { activation, stop, sttTeam, sttAll });
        }

        // ================= события хука (потоки пула) =================

        private void OnHookKeyDown(int vk)
        {
            var keys = _keys;
            if (vk == keys.Activation) BeginPhraseCapture(vk);
            else if (vk == keys.Stop) StopPlayback();
            else if (vk == keys.SttTeam || vk == keys.SttAll) BeginDictation(vk, vk == keys.SttAll);
        }

        private void OnHookKeyUp(int vk)
        {
            var keys = _keys;
            if (vk == keys.Activation)
            {
                Interlocked.Exchange(ref _phraseGuard, null)?.Dispose();
                _ = FinishPhraseCaptureAsync();
            }
            else if (vk == keys.SttTeam || vk == keys.SttAll)
            {
                Interlocked.Exchange(ref _sttGuard, null)?.Dispose();
                _ = FinishDictationAsync();
            }
        }

        // ---- фразы по хоткею ----

        private void BeginPhraseCapture(int vk)
        {
            Audio.BeginListening();
            ArmGuard(ref _phraseGuard, vk);
            PostUi(() => StatusText.Text = "СЛУШАЮ...");
        }

        private async Task FinishPhraseCaptureAsync()
        {
            try
            {
                LatencyTrace.Start();   // точка отсчёта: «отпустил клавишу»
                string phrase = await Audio.EndListeningAsync().ConfigureAwait(false);   // идемпотентно: guard и настоящий key-up не конфликтуют
                LatencyTrace.Mark("final text");

                PostUi(() =>
                {
                    if (_controller.State == BridgeState.Running) StatusText.Text = IdleStatus;
                    if (phrase.Length > 0) RecognizedText.Text = $"Финально: {phrase}";
                });

                // Пустая запись (нажали и отпустили без речи) в поиск не уходит, поэтому «...» больше не попадает в missing_phrases.txt.
                if (phrase.Length > 0) Audio.ProcessFinalPhrase(phrase);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Phrase] {ex}");
            }
        }

        private void StopPlayback()
        {
            Audio.StopCurrentSound();
            Interlocked.Exchange(ref _keepStatusUntil, Environment.TickCount64 + 1500);
            PostUi(() => StatusText.Text = "Воспроизведение остановлено");
        }

        // ---- диктовка в чат (микрофон в игре в это время глушится внутри AudioProcessor) ----

        private void BeginDictation(int vk, bool allChat)
        {
            // Глушение, запуск распознавателя и подачу звука в правильном порядке делает AudioProcessor.
            switch (Audio.BeginDictation())
            {
                case DictationStart.Started:
                    {
                        _dictationToAllChat = allChat;   // только после успешного старта: пока предыдущая диктовка завершается, её адресата менять нельзя
                        ArmGuard(ref _sttGuard, vk);

                        // Состояние микрофона показываем прямо в статусе: сразу видно, глушит ли приложение звук в игре.
                        bool muted = Audio.IsDictationMuted;
                        PostUi(() => StatusText.Text = muted
                            ? "Запись речи (STT) • микрофон в игре заглушён"
                            : "Запись речи (STT) • микрофон в игре ОТКРЫТ (глушение выключено)");
                        break;
                    }

                case DictationStart.NoRecognizer:
                    PostUi(() => StatusText.Text = "STT недоступен: модель Vosk ещё не загружена");
                    break;

                    // Busy: прошлая диктовка ещё завершается; NotRunning: мост остановлен. В обоих случаях нажатие молча игнорируется.
            }
        }

        private async Task FinishDictationAsync()
        {
            try
            {
                if (!Audio.IsDictating) return;

                bool allChat = _dictationToAllChat;
                PostUi(() => StatusText.Text = "Распознавание (Vosk STT)...");

                // Хвост речи добирается, пока микрофон в игре ещё глушится; финализация идёт не в UI-потоке.
                // null — диктовку уже завершает другой вызов (key-up и страховка HoldGuard не конфликтуют).
                string? text = await Audio.EndDictationAsync().ConfigureAwait(false);
                if (text == null) return;

                if (string.IsNullOrWhiteSpace(text))
                {
                    PostUi(() => StatusText.Text = "Готово (речь не разобрана)");
                    return;
                }

                PostUi(() => StatusText.Text = $"Распознано: {text}");

                var options = _dictation;   // снимок: флажки в интерфейсе из потока пула не читаются
                if (!options.AutoPaste) return;

                bool inserted = await TextInsertionService
                    .InsertTextAsync(text, options.UseClipboard, allChat, options.OpenChat, options.SendEnter)
                    .ConfigureAwait(false);

                if (!inserted)
                    PostUi(() => StatusText.Text = "Текст не вставлен: буфер обмена занят. Повторите диктовку.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Dictation] {ex}");
                PostUi(() => StatusText.Text = $"Ошибка диктовки: {ex.Message}");
            }
        }

        // В исходном MainWindow.xaml флажка «глушить микрофон» нет, а без него настройку нельзя ни выключить, ни проверить:
        // глушение оставалось включённым всегда. Если флажок не добавлен вручную (x:Name="ChkMuteMicDuringStt"),
        // создаём его под остальными настройками ввода текста. Состояние и обработчик подключает LoadSettingsIntoUi через FindName.
        private void EnsureMuteMicCheckBox()
        {
            if (FindName("ChkMuteMicDuringStt") != null) return;
            if (ChkSendEnterAfterText.Parent is not System.Windows.Controls.Panel panel) return;

            var box = new System.Windows.Controls.CheckBox
            {
                Name = "ChkMuteMicDuringStt",
                Content = "Глушить микрофон в игре на время записи",
                ToolTip = "Пока удерживается клавиша диктовки, голос не уходит в виртуальный кабель, и союзники его не слышат.",
                Foreground = ChkSendEnterAfterText.Foreground,
                FontSize = ChkSendEnterAfterText.FontSize,
                Margin = ChkSendEnterAfterText.Margin,
                IsChecked = true
            };

            RegisterName(box.Name, box);
            panel.Children.Add(box);
        }

        private void ReadDictationOptions() =>
            _dictation = new DictationOptions(
                ChkAutoPaste.IsChecked == true,
                ChkOpenChatBeforePaste.IsChecked == true,
                ChkUseClipboard.IsChecked == true,
                ChkSendEnterAfterText.IsChecked == true);

        private void SttOption_Click(object sender, RoutedEventArgs e)
        {
            ReadDictationOptions();
            if (!_isDataLoaded) return;

            var s = Cfg;
            s.SttAutoPaste = ChkAutoPaste.IsChecked == true;
            s.SttOpenChat = ChkOpenChatBeforePaste.IsChecked == true;
            s.SttUseClipboard = ChkUseClipboard.IsChecked == true;
            s.SttSendEnter = ChkSendEnterAfterText.IsChecked == true;
            ScheduleSave();
        }

        // ================= страховка от потерянного key-up =================

        // Alt+Tab, UAC, экран блокировки или снятый системой хук оставляют режим прослушивания включённым, а пока он включён,
        // микрофон не уходит в кабель. Таймер сверяет состояние клавиши с системой: отпущена — завершаем запись сами.
        private void ArmGuard(ref HoldGuard? slot, int vk) =>
            Interlocked.Exchange(ref slot, new HoldGuard(vk, OnHoldLost))?.Dispose();

        private void DisarmGuards()
        {
            Interlocked.Exchange(ref _phraseGuard, null)?.Dispose();
            Interlocked.Exchange(ref _sttGuard, null)?.Dispose();
        }

        private void OnHoldLost(int vk, bool stillDown)
        {
            Debug.WriteLine($"[Hotkey] Клавиша {vk}: key-up потерян или удержание дольше {MaxHoldMs / 1000} с");

            // Отпущена, а события нет: InputHook доставит недостающий key-up и сбросит отсечение автоповтора для этой клавиши.
            // Если клавишу действительно держат слишком долго, состояние хука не трогаем, иначе автоповтор тут же начал бы новую запись.
            if (!stillDown) _controller.Hook.ReleaseStuckKey(vk);

            // Завершение идемпотентно: настоящий key-up и страховка не конфликтуют, текст получит только один из них.
            var keys = _keys;
            if (vk == keys.Activation) _ = FinishPhraseCaptureAsync();
            else if (vk == keys.SttTeam || vk == keys.SttAll) _ = FinishDictationAsync();
        }

        private sealed class HoldGuard : IDisposable
        {
            private readonly System.Threading.Timer _timer;
            private readonly long _started = Environment.TickCount64;
            private int _fired;

            public HoldGuard(int vk, Action<int, bool> onLost)
            {
                _timer = new System.Threading.Timer(_ =>
                {
                    bool down = IsPhysicallyDown(vk);
                    if (down && Environment.TickCount64 - _started < MaxHoldMs) return;   // зажата по-настоящему и не дольше лимита
                    if (Interlocked.Exchange(ref _fired, 1) == 1) return;                  // сработать нужно один раз
                    onLost(vk, down);
                }, null, 200, 100);
            }

            public void Dispose() => _timer.Dispose();
        }

        private static bool IsPhysicallyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;   // работает и для 0x05/0x06 (боковые кнопки мыши)

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    }
}
