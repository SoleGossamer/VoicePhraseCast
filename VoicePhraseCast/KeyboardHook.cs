using System;
using System.Runtime.InteropServices;

public class KeyboardHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104; // Для системных клавиш (Alt и т.д.)
    private const int WM_SYSKEYUP = 0x0105;

    // Теперь события передают int (vkCode)
    public event Action<int>? OnKeyDown;
    public event Action<int>? OnKeyUp;

    public void ClearSubscribers()
    {
        OnKeyDown = null;
        OnKeyUp = null;
    }

    private LowLevelKeyboardProc _proc;
    private IntPtr _hookID = IntPtr.Zero;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void SetHook()
    {
        // IntPtr.Zero — это «родной» способ сказать WinAPI: 
        // «используй модуль текущего процесса»
        _hookID = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
    }

    public void Unhook() => UnhookWindowsHookEx(_hookID);

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);

            // Просто передаем код любой нажатой клавиши в события
            if (wParam == (IntPtr)WM_KEYDOWN) OnKeyDown?.Invoke(vkCode);
            if (wParam == (IntPtr)WM_KEYUP) OnKeyUp?.Invoke(vkCode);
        }
        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string lpModuleName);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
}