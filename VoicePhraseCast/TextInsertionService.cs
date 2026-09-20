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

        private const ushort VK_RETURN = 0x0D;  // Enter
        private const ushort VK_SHIFT = 0x10;   // Shift
        private const ushort VK_CONTROL = 0x11; // Ctrl
        private const ushort VK_MENU = 0x12;    // Alt
        private const ushort VK_V = 0x56;       // V

        public static void SimulateKeyDown(ushort vkCode) => SendKeyEvent(vkCode, isKeyUp: false);
        public static void SimulateKeyUp(ushort vkCode) => SendKeyEvent(vkCode, isKeyUp: true);

        private static void ReleaseAllModifiers()
        {
            SimulateKeyUp(VK_SHIFT);
            SimulateKeyUp(VK_CONTROL);
            SimulateKeyUp(VK_MENU);
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
                keybd_event((byte)vkCode, (byte)scanCode, isKeyUp ? KEYEVENTF_KEYUP : 0, UIntPtr.Zero);
            }
        }

        public static async Task InsertViaClipboardAsync(string text, bool isAllChat = false, bool openChatFirst = true, bool autoSendEnter = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // 1. Помещаем текст в буфер обмена Windows в ApartmentThread (STA)
            Thread staThread = new Thread(() =>
            {
                try
                {
                    Clipboard.SetText(text);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Clipboard Error] {ex.Message}");
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            // 2. Отжимаем все модификаторы, которые пользователь мог зажать руками
            ReleaseAllModifiers();
            await Task.Delay(50);

            // 3. Если требуется — открываем чат игры (Enter или Shift+Enter)
            if (openChatFirst)
            {
                if (isAllChat)
                {
                    SimulateKeyDown(VK_SHIFT);
                    await Task.Delay(30);
                    SimulateKeyDown(VK_RETURN);
                    await Task.Delay(40);
                    SimulateKeyUp(VK_RETURN);
                    SimulateKeyUp(VK_SHIFT);
                }
                else
                {
                    SimulateKeyDown(VK_RETURN);
                    await Task.Delay(40);
                    SimulateKeyUp(VK_RETURN);
                }

                // Задержка на анимацию открытия окна чата в игре
                await Task.Delay(120);
            }

            // 4. Нажимаем Ctrl + V для вставки текста
            SimulateKeyDown(VK_CONTROL);
            await Task.Delay(30);
            SimulateKeyDown(VK_V);
            await Task.Delay(40);
            SimulateKeyUp(VK_V);
            SimulateKeyUp(VK_CONTROL);

            await Task.Delay(60);

            // 5. Нажимаем Enter для отправки сообщения (если включено)
            if (autoSendEnter)
            {
                SimulateKeyDown(VK_RETURN);
                await Task.Delay(40);
                SimulateKeyUp(VK_RETURN);
            }
        }
    }
}