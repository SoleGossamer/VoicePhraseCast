using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace VoicePhraseCast
{
    /// <summary>
    /// Глобальный перехват клавиш и боковых кнопок мыши (замена KeyboardHook).
    /// Хуки живут в собственном потоке со своим циклом сообщений, а подписчики вызываются из пула потоков через Channel.
    /// Поэтому занятость UI-потока не может ни потерять событие, ни привести к снятию хука по таймауту системы.
    /// События KeyDown/KeyUp приходят из потока пула, UI напрямую трогать нельзя (только Dispatcher.InvokeAsync).
    /// Подписки сохраняются между Start/Stop, отписываться должен владелец.
    /// </summary>
    public sealed class InputHook : IDisposable
    {
        public const int VK_XBUTTON1 = 0x05;
        public const int VK_XBUTTON2 = 0x06;

        /// <summary>
        /// Метка наших собственных эмуляций в dwExtraInfo. Хук такие события игнорирует,
        /// поэтому эмулированная рация или Ctrl+V не могут запустить хоткей (петля «фраза → рация → запись»).
        /// Все чужие инъецированные события (G HUB, Synapse) при этом доставляются как обычные.
        /// </summary>
        public static readonly IntPtr OwnInputTag = (IntPtr)0x56504331;

        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;
        private const uint WM_QUIT = 0x0012;

        public event Action<int>? KeyDown;
        public event Action<int>? KeyUp;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        // Делегаты хранятся в полях: иначе GC соберёт их, и хук молча умрёт.
        private readonly HookProc _keyboardProc;
        private readonly HookProc _mouseProc;

        private readonly object _lifecycleLock = new();     // Start/Stop друг с другом
        private readonly object _stateLock = new();         // _pressed и порядок записи в очередь
        private readonly HashSet<int> _pressed = new();
        private volatile HashSet<int> _watched = new();     // неизменяемый снимок, подменяется целиком

        private volatile Channel<(int Vk, bool Down)>? _queue;
        private CancellationTokenSource? _cts;
        private Thread? _thread;
        private uint _threadId;
        private IntPtr _keyboardHook = IntPtr.Zero;
        private IntPtr _mouseHook = IntPtr.Zero;

        public InputHook()
        {
            _keyboardProc = OnKeyboard;
            _mouseProc = OnMouse;
        }

        public bool IsRunning => _thread != null;

        /// <summary>
        /// Устанавливает хук на выделенном потоке. Мышиный хук ставится только если среди клавиш есть боковая кнопка,
        /// иначе каждое движение мыши в системе шло бы через наш процесс.
        /// Бросает Win32Exception, если система отказала в установке хука.
        /// </summary>
        public void Start(IEnumerable<int> watchedKeys)
        {
            ArgumentNullException.ThrowIfNull(watchedKeys);

            lock (_lifecycleLock)
            {
                if (_thread != null) return;

                var watched = new HashSet<int>(watchedKeys);
                bool needMouse = watched.Contains(VK_XBUTTON1) || watched.Contains(VK_XBUTTON2);

                lock (_stateLock) _pressed.Clear();
                _watched = watched;

                var queue = Channel.CreateUnbounded<(int Vk, bool Down)>(
                    new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
                var cts = new CancellationTokenSource();
                var ready = new ManualResetEventSlim(false);
                var errorBox = new int[1];

                _queue = queue;

                var thread = new Thread(() => HookThreadMain(needMouse, ready, errorBox))
                {
                    IsBackground = true,
                    Name = "InputHook",
                    Priority = ThreadPriority.AboveNormal
                };
                thread.Start();
                ready.Wait();

                if (errorBox[0] != 0)
                {
                    _queue = null;
                    queue.Writer.TryComplete();
                    throw new Win32Exception(errorBox[0], "Не удалось установить глобальный хук клавиатуры или мыши");
                }

                _thread = thread;
                _cts = cts;
                _ = Task.Run(() => PumpAsync(queue.Reader, cts.Token));
            }
        }

        public void Stop()
        {
            Thread? thread;
            CancellationTokenSource? cts;
            Channel<(int Vk, bool Down)>? queue;

            lock (_lifecycleLock)
            {
                thread = _thread;
                cts = _cts;
                queue = _queue;
                _thread = null;
                _cts = null;
                _queue = null;
            }

            if (thread == null) return;

            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (!thread.Join(1000))
                Debug.WriteLine("[Hook] Поток хука не завершился за 1 с");

            queue?.Writer.TryComplete();
            cts?.Cancel();
        }

        public void Dispose() => Stop();

        /// <summary>
        /// Страховка от потерянного key-up (Alt+Tab, UAC, экран блокировки): доставляет подписчикам KeyUp,
        /// если система считала клавишу зажатой, и сбрасывает отсечение автоповтора для неё.
        /// Повторный вызов и вызов после настоящего key-up безопасны: второе событие не будет доставлено.
        /// </summary>
        public void ReleaseStuckKey(int vk)
        {
            var queue = _queue;
            if (queue == null) return;

            lock (_stateLock)
            {
                if (!_pressed.Remove(vk)) return;
                queue.Writer.TryWrite((vk, false));
            }
        }

        private void HookThreadMain(bool needMouse, ManualResetEventSlim ready, int[] errorBox)
        {
            bool installed = false;
            try
            {
                _threadId = GetCurrentThreadId();
                PeekMessage(out _, IntPtr.Zero, 0, 0, 0);   // создаёт очередь сообщений потока: без неё PostThreadMessage(WM_QUIT) не дойдёт

                IntPtr module = GetModuleHandle(null);

                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
                if (_keyboardHook == IntPtr.Zero)
                {
                    errorBox[0] = Marshal.GetLastWin32Error();
                }
                else if (needMouse)
                {
                    _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
                    if (_mouseHook == IntPtr.Zero)
                        errorBox[0] = Marshal.GetLastWin32Error();
                }

                installed = errorBox[0] == 0;
            }
            catch
            {
                if (errorBox[0] == 0) errorBox[0] = 1;
            }
            finally
            {
                ready.Set();
            }

            if (installed)
            {
                // LL-колбэки исполняются внутри GetMessage этого потока.
                while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            }

            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = IntPtr.Zero;
            _mouseHook = IntPtr.Zero;

            lock (_stateLock) _pressed.Clear();
        }

        private async Task PumpAsync(ChannelReader<(int Vk, bool Down)> reader, CancellationToken ct)
        {
            try
            {
                await foreach (var (vk, down) in reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        if (down) KeyDown?.Invoke(vk);
                        else KeyUp?.Invoke(vk);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Hook] Ошибка обработчика: {ex}");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    int vk = Marshal.ReadInt32(lParam);                                           // KBDLLHOOKSTRUCT.vkCode
                    if (_watched.Contains(vk) && Marshal.ReadIntPtr(lParam, 16) != OwnInputTag)   // dwExtraInfo
                    {
                        int msg = (int)wParam;
                        Enqueue(vk, msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                    }
                }
                catch
                {
                    // Исключение, ушедшее из колбэка в нативный код, роняет процесс.
                }
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)   // WM_MOUSEMOVE отсекается первой же проверкой
                {
                    try
                    {
                        // MSLLHOOKSTRUCT.dwExtraInfo: смещение 24 на x64 и 20 на x86
                        if (Marshal.ReadIntPtr(lParam, IntPtr.Size == 8 ? 24 : 20) != OwnInputTag)
                        {
                            int button = (Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF;   // старшее слово mouseData: 1 = XBUTTON1, 2 = XBUTTON2
                            int vk = button == 1 ? VK_XBUTTON1 : VK_XBUTTON2;
                            if (_watched.Contains(vk)) Enqueue(vk, msg == WM_XBUTTONDOWN);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private void Enqueue(int vk, bool down)
        {
            var queue = _queue;
            if (queue == null) return;

            lock (_stateLock)
            {
                // Автоповтор зажатой клавиши отсекается здесь, сразу для всех хоткеев.
                if (down) { if (!_pressed.Add(vk)) return; }
                // «Отпускание» без «нажатия» (клавиша была зажата до старта хука) не доставляем.
                else if (!_pressed.Remove(vk)) return;

                queue.Writer.TryWrite((vk, down));
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x;
            public int y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
