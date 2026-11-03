using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoicePhraseCast
{
    /// <summary>
    /// Единая точка эмуляции ввода (клавиши, боковые кнопки мыши, Unicode-символы) через SendInput.
    /// Все события помечаются InputHook.OwnInputTag, чтобы наш же хук их игнорировал.
    /// Используется и вставкой текста, и клавишей рации в AudioProcessor.
    /// </summary>
    internal static class InputSender
    {
        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;

        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        private const uint MOUSEEVENTF_XDOWN = 0x0080;
        private const uint MOUSEEVENTF_XUP = 0x0100;

        private const uint MAPVK_VK_TO_VSC = 0;

        // Windows ждёт INPUT = { type; union { MOUSEINPUT; KEYBDINPUT; HARDWAREINPUT } }.
        // Размер: 40 байт на x64 и 28 на x86. Структура без union (32 байта на x64) всегда отвергается через ERROR_INVALID_PARAMETER.
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        /// <summary>Нажатие или отпускание клавиши. Коды 0x05 и 0x06 (VK_XBUTTON1/2) отправляются как боковые кнопки мыши.</summary>
        public static bool SendKey(int vk, bool up)
        {
            if (vk == InputHook.VK_XBUTTON1) return SendXButton(1, up);
            if (vk == InputHook.VK_XBUTTON2) return SendXButton(2, up);

            uint flags = (up ? KEYEVENTF_KEYUP : 0u) | (IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0u);
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        wScan = (ushort)MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC),
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = InputHook.OwnInputTag
                    }
                }
            };
            return Send(input);
        }

        /// <summary>Печатает фрагмент текста Unicode-событиями (без буфера обмена). Каждая UTF-16 единица отправляется парой «вниз/вверх».</summary>
        public static bool SendUnicode(string text, int start, int length)
        {
            if (length <= 0) return true;

            var inputs = new INPUT[length * 2];
            for (int i = 0; i < length; i++)
            {
                inputs[2 * i] = UnicodeInput(text[start + i], up: false);
                inputs[2 * i + 1] = UnicodeInput(text[start + i], up: true);
            }
            return Send(inputs);
        }

        private static bool SendXButton(uint which, bool up)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        mouseData = which,                              // 1 = XBUTTON1, 2 = XBUTTON2
                        dwFlags = up ? MOUSEEVENTF_XUP : MOUSEEVENTF_XDOWN,
                        dwExtraInfo = InputHook.OwnInputTag
                    }
                }
            };
            return Send(input);
        }

        private static INPUT UnicodeInput(char c, bool up) => new()
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = c,
                    dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0u),
                    dwExtraInfo = InputHook.OwnInputTag
                }
            }
        };

        private static bool Send(params INPUT[] inputs)
        {
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent == inputs.Length) return true;

            // 0 обычно означает блокировку UIPI (целевое окно запущено от администратора) или неверный размер структуры.
            Debug.WriteLine($"[SendInput] отправлено {sent} из {inputs.Length}, ошибка {Marshal.GetLastWin32Error()}");
            return false;
        }

        private static bool IsExtendedKey(int vk) => vk switch
        {
            0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 => true,   // PgUp, PgDn, End, Home, стрелки
            0x2C or 0x2D or 0x2E => true,                                            // PrintScreen, Insert, Delete
            0x5B or 0x5C or 0x5D => true,                                            // Win, Win, Apps
            0x6F or 0x90 => true,                                                    // Num /, NumLock
            0xA3 or 0xA5 => true,                                                    // правые Ctrl и Alt
            _ => false
        };
    }

    /// <summary>
    /// Вставка распознанного текста в активное окно (игровой чат).
    /// Две вставки подряд сериализуются внутри сервиса и не перемешивают нажатия и буфер обмена.
    /// </summary>
    public static class TextInsertionService
    {
        private const ushort VK_RETURN = 0x0D;
        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_V = 0x56;

        private const int UnicodeChunk = 24;    // символов за один вызов SendInput

        private static readonly SemaphoreSlim Gate = new(1, 1);

        public static void SimulateKeyDown(ushort vkCode) => InputSender.SendKey(vkCode, up: false);
        public static void SimulateKeyUp(ushort vkCode) => InputSender.SendKey(vkCode, up: true);

        /// <summary>Вставляет текст выбранным способом: Ctrl+V или прямая печать Unicode. Возвращает false, если текст вставить не удалось.</summary>
        public static Task<bool> InsertTextAsync(string text, bool useClipboard, bool isAllChat, bool openChatFirst, bool autoSendEnter)
        {
            return useClipboard
                ? InsertViaClipboardAsync(text, isAllChat, openChatFirst, autoSendEnter)
                : TypeTextAsync(text, isAllChat, openChatFirst, autoSendEnter);
        }

        public static async Task<bool> InsertViaClipboardAsync(string text, bool isAllChat = false, bool openChatFirst = true,
                                                               bool autoSendEnter = true, bool restoreClipboard = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                string? previous = restoreClipboard ? await TryGetClipboardTextAsync().ConfigureAwait(false) : null;

                // Нет текста в буфере: нет и Ctrl+V с Enter, иначе в чат уйдёт то, что лежало в буфере раньше (например, пароль).
                if (!await TrySetClipboardAsync(text).ConfigureAwait(false)) return false;

                ReleaseAllModifiers();
                await Task.Delay(50).ConfigureAwait(false);

                if (openChatFirst && !await OpenChatAsync(isAllChat).ConfigureAwait(false)) return false;

                if (!await ChordAsync(VK_CONTROL, VK_V).ConfigureAwait(false)) return false;
                await Task.Delay(60).ConfigureAwait(false);

                if (autoSendEnter && !await TapAsync(VK_RETURN).ConfigureAwait(false)) return false;

                if (previous != null) await RestoreClipboardAsync(text, previous).ConfigureAwait(false);
                return true;
            }
            finally
            {
                Gate.Release();
            }
        }

        public static async Task<bool> TypeTextAsync(string text, bool isAllChat = false, bool openChatFirst = true, bool autoSendEnter = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ReleaseAllModifiers();
                await Task.Delay(50).ConfigureAwait(false);

                if (openChatFirst && !await OpenChatAsync(isAllChat).ConfigureAwait(false)) return false;

                for (int i = 0; i < text.Length; i += UnicodeChunk)
                {
                    int len = Math.Min(UnicodeChunk, text.Length - i);
                    if (!InputSender.SendUnicode(text, i, len)) return false;
                    await Task.Delay(8).ConfigureAwait(false);
                }
                await Task.Delay(60).ConfigureAwait(false);

                if (autoSendEnter && !await TapAsync(VK_RETURN).ConfigureAwait(false)) return false;
                return true;
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<bool> OpenChatAsync(bool isAllChat)
        {
            bool ok = isAllChat
                ? await ChordAsync(VK_SHIFT, VK_RETURN).ConfigureAwait(false)    // общий чат: Shift+Enter
                : await TapAsync(VK_RETURN).ConfigureAwait(false);               // союзный чат: Enter

            if (ok) await Task.Delay(120).ConfigureAwait(false);                 // анимация открытия окна чата в игре
            return ok;
        }

        private static void ReleaseAllModifiers()
        {
            InputSender.SendKey(VK_SHIFT, up: true);
            InputSender.SendKey(VK_CONTROL, up: true);
            InputSender.SendKey(VK_MENU, up: true);
        }

        private static async Task<bool> TapAsync(ushort vk, int holdMs = 40)
        {
            if (!InputSender.SendKey(vk, up: false)) return false;
            try
            {
                await Task.Delay(holdMs).ConfigureAwait(false);
            }
            finally
            {
                InputSender.SendKey(vk, up: true);   // клавиша не должна остаться зажатой при любом исходе
            }
            return true;
        }

        private static async Task<bool> ChordAsync(ushort modifier, ushort key)
        {
            if (!InputSender.SendKey(modifier, up: false)) return false;
            try
            {
                await Task.Delay(30).ConfigureAwait(false);
                return await TapAsync(key).ConfigureAwait(false);
            }
            finally
            {
                InputSender.SendKey(modifier, up: true);
            }
        }

        private static async Task RestoreClipboardAsync(string inserted, string previous)
        {
            await Task.Delay(300).ConfigureAwait(false);    // раньше нельзя: игра успеет вставить уже старое содержимое

            string? current = await TryGetClipboardTextAsync().ConfigureAwait(false);
            if (current == inserted)                         // пользователь успел скопировать своё: не затираем
                await TrySetClipboardAsync(previous).ConfigureAwait(false);
        }

        private static Task<bool> TrySetClipboardAsync(string text) =>
            RunStaAsync(() =>
            {
                // SetDataObject с повторами: буфер может быть кратковременно занят другим процессом.
                System.Windows.Forms.Clipboard.SetDataObject(text, true, 10, 30);
                return true;
            }, false);

        private static Task<string?> TryGetClipboardTextAsync() =>
            RunStaAsync<string?>(() =>
                System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null, null);

        /// <summary>Выполняет функцию в отдельном STA-потоке (для Clipboard) и не блокирует вызывающий поток. При ошибке возвращает fallback.</summary>
        private static Task<T> RunStaAsync<T>(Func<T> func, T fallback)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Clipboard] {ex.Message}");
                    tcs.SetResult(fallback);
                }
            })
            { IsBackground = true, Name = "ClipboardSta" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }
    }
}
