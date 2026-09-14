using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VoicePhraseCast
{
    public static class TextInsertionService
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public KEYBDINPUT ki;
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

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_KEYUP_FLAG = 0x0002;

        private const ushort VK_RETURN = 0x0D;  // Enter
        private const ushort VK_SHIFT = 0x10;   // Shift
        private const ushort VK_CONTROL = 0x11; // Ctrl
        private const ushort VK_V = 0x56;       // V

        public static void SimulateKeyDown(ushort vkCode) => SendKeyEvent(vkCode, isKeyUp: false);
        public static void SimulateKeyUp(ushort vkCode) => SendKeyEvent(vkCode, isKeyUp: true);

        public static async Task PressKeyAsync(ushort vkCode, bool withShift = false)
        {
            if (withShift)
            {
                SimulateKeyDown(VK_SHIFT);
                await Task.Delay(30);
            }

            SimulateKeyDown(vkCode);
            await Task.Delay(40);
            SimulateKeyUp(vkCode);
            await Task.Delay(20);

            if (withShift)
            {
                SimulateKeyUp(VK_SHIFT);
                await Task.Delay(20);
            }
        }

        private static void SendKeyEvent(ushort vkCode, bool isKeyUp)
        {
            uint scanCode = MapVirtualKey(vkCode, 0);

            INPUT[] inputs = new INPUT[1];
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wVk = vkCode,
                    wScan = (ushort)scanCode,
                    dwFlags = isKeyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            };

            uint result = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));

            if (result == 0)
            {
                uint flags = isKeyUp ? KEYEVENTF_KEYUP_FLAG : 0;
                keybd_event((byte)vkCode, (byte)scanCode, flags, UIntPtr.Zero);
            }
        }

        public static async Task InsertViaClipboardAsync(string text, bool isAllChat = false, bool openChatFirst = true, bool autoSendEnter = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // 0. Даем 150 мс, чтобы система окончательно зарегистрировала отпускание горячих клавиш пользователя
            await Task.Delay(150);

            // 1. Открытие чата (если задействована опция)
            if (openChatFirst)
            {
                await PressKeyAsync(VK_RETURN, withShift: isAllChat);
                await Task.Delay(100);
            }

            // 2. Буфер обмена
            string? previousText = GetClipboardTextSafe();
            SetClipboardTextSafe(text);
            await Task.Delay(60);

            // 3. Честная эмуляция Ctrl + V (Ctrl зажимается ДО V и отпускается ПОСЛЕ V)
            SimulateKeyDown(VK_CONTROL);
            await Task.Delay(40);

            SimulateKeyDown(VK_V);
            await Task.Delay(40);

            SimulateKeyUp(VK_V);
            await Task.Delay(40); // Гарантирует, что V поднялась раньше Ctrl

            SimulateKeyUp(VK_CONTROL);
            await Task.Delay(40);

            // 4. Отправка сообщения
            if (autoSendEnter)
            {
                await PressKeyAsync(VK_RETURN, withShift: false);
            }

            // 5. Восстановление буфера
            await Task.Delay(100);
            if (previousText != null)
            {
                SetClipboardTextSafe(previousText);
            }
        }

        private static string? GetClipboardTextSafe()
        {
            string? text = null;
            var thread = new Thread(() =>
            {
                try
                {
                    if (Clipboard.ContainsText()) text = Clipboard.GetText();
                }
                catch { }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return text;
        }

        private static void SetClipboardTextSafe(string text)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    Clipboard.SetText(text);
                }
                catch { }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
    }
}