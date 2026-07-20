using System;
using System.Runtime.InteropServices;

public class KeyboardHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14; // Добавляем поддержку мышиного хука для боковых кнопок

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    // Мышиные события для боковых кнопок
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;

    public event Action<int>? OnKeyDown;
    public event Action<int>? OnKeyUp;

    public void ClearSubscribers()
    {
        OnKeyDown = null;
        OnKeyUp = null;
    }

    private LowLevelKeyboardProc _keyboardProc;
    private LowLevelMouseProc _mouseProc;
    private IntPtr _keyboardHookID = IntPtr.Zero;
    private IntPtr _mouseHookID = IntPtr.Zero;

    public KeyboardHook()
    {
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public void SetHook()
    {
        _keyboardHookID = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, IntPtr.Zero, 0);
        _mouseHookID = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
    }

    public void Unhook()
    {
        UnhookWindowsHookEx(_keyboardHookID);
        UnhookWindowsHookEx(_mouseHookID);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN) OnKeyDown?.Invoke(vkCode);
            if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP) OnKeyUp?.Invoke(vkCode);
        }
        return CallNextHookEx(_keyboardHookID, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
            {
                // Извлекаем структуру MSLLHOOKSTRUCT, чтобы понять, какая именно боковая кнопка нажата
                int mouseData = Marshal.ReadInt32(lParam + 8); // +8 байт — это смещение к mouseData в x64
                int xButton = (mouseData >> 16) & 0xFFFF;

                // 1 — это XBUTTON1 (назад), 2 — это XBUTTON2 (вперед)
                // Назначаем им свободные виртуальные коды Windows: 0x05 и 0x06
                int vkCode = (xButton == 1) ? 0x05 : 0x06;

                if (msg == WM_XBUTTONDOWN) OnKeyDown?.Invoke(vkCode);
                if (msg == WM_XBUTTONUP) OnKeyUp?.Invoke(vkCode);
            }
        }
        return CallNextHookEx(_mouseHookID, nCode, wParam, lParam);
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, Delegate lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
}