using System.Runtime.InteropServices;
using Poppy.Core;
using static Poppy.Native;

namespace Poppy;

/// <summary>
/// Global low-level mouse and keyboard hooks feeding <see cref="DragGesture"/>.
/// Hook callbacks must return fast (Windows drops slow hooks), so all UI work is
/// posted to the dispatcher.
/// </summary>
sealed class InputHooks : IDisposable
{
    readonly DragGesture gesture;
    readonly HookProc mouseProc, keyProc; // kept alive: the GC must not collect hook delegates
    IntPtr mouseHook, keyHook;

    public bool Paused { get; set; }

    public InputHooks(DragGesture gesture)
    {
        this.gesture = gesture;
        mouseProc = OnMouse;
        keyProc = OnKey;
    }

    public void Start()
    {
        var mod = GetModuleHandle(null);
        mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, mod, 0);
        keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyProc, mod, 0);
        if (mouseHook == IntPtr.Zero || keyHook == IntPtr.Zero)
            throw new InvalidOperationException("Could not install input hooks: " + Marshal.GetLastWin32Error());
    }

    IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !Paused)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (info.dwExtraInfo != PoppyInputTag)
            {
                switch ((int)wParam)
                {
                    case WM_LBUTTONDOWN: gesture.MouseDown(info.pt.X, info.pt.Y); break;
                    case WM_MOUSEMOVE: gesture.MouseMove(info.pt.X, info.pt.Y); break;
                    case WM_LBUTTONUP: gesture.MouseUp(); break;
                }
            }
        }
        return CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !Paused)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vk = (int)info.vkCode;
            var msg = (int)wParam;
            bool swallow = msg is WM_KEYDOWN or WM_SYSKEYDOWN ? gesture.KeyDown(vk)
                : msg is WM_KEYUP or WM_SYSKEYUP && gesture.KeyUp(vk);
            if (swallow) return 1;
        }
        return CallNextHookEx(keyHook, code, wParam, lParam);
    }

    /// <summary>Explorer, the desktop and most shell-aware apps show this window while dragging files.</summary>
    public static bool ShellDragImageVisible()
    {
        var h = FindWindow("SysDragImage", null);
        return h != IntPtr.Zero && IsWindowVisible(h);
    }

    public void Dispose()
    {
        if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
        if (keyHook != IntPtr.Zero) UnhookWindowsHookEx(keyHook);
        mouseHook = keyHook = IntPtr.Zero;
    }
}
