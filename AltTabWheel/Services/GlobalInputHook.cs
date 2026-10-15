using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace AltTabWheel.Services;

public enum NavigationDirection { Next, Previous }

public enum WheelDirection { Left, Up, Right, Down }

/// <summary>
/// Low-level keyboard + mouse hooks that swallow the system Alt+Tab and turn it
/// into wheel navigation events.
///
/// Hot-path discipline (mirrors FlipSwitcher's approach): the hook callback only
/// reads the vkCode via an unsafe pointer, checks a few bools, and either swallows
/// (return 1) or forwards. All UI work is posted to the Dispatcher.
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    /// <summary>Alt+Tab pressed. Fires on first Tab (open wheel) and subsequent Tabs (navigate).</summary>
    public event EventHandler<NavigationDirection>? TabPressed;

    /// <summary>Alt+arrow key pressed while the wheel is open.</summary>
    public event EventHandler<WheelDirection>? ArrowPressed;

    /// <summary>Alt released — confirm selection.</summary>
    public event EventHandler? AltReleased;

    /// <summary>Escape while the wheel is open — cancel.</summary>
    public event EventHandler? EscapePressed;

    /// <summary>Mouse wheel rotated while Alt is held and the wheel is open.</summary>
    public event EventHandler<NavigationDirection>? WheelScrolled;

    /// <summary>Mouse moved while the wheel is open (screen physical coordinates).</summary>
    public event EventHandler<NativeMethods.POINT>? MouseMoved;



    private NativeMethods.POINT _lastMovePosted;

    /// <summary>
    /// 鼠标移动节流：LL 鼠标钩子按像素级频率到达，超过阈值距离才投递 UI 线程，
    /// 避免按住 Alt 期间不必要的 Dispatcher 压力。
    /// </summary>
    private const int MovePostThresholdPx = 6;

    /// <summary>Set by the app: only swallow keys when the wheel is being shown.</summary>
    public bool IsWheelOpen { get; set; }

    private IntPtr _keyboardHookId = IntPtr.Zero;
    private IntPtr _mouseHookId = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private NativeMethods.LowLevelMouseProc? _mouseProc;

    public bool Install()
    {
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
        _keyboardHookId = NativeMethods.InstallKeyboardHook(_keyboardProc);
        _mouseHookId = NativeMethods.InstallMouseHook(_mouseProc);
        bool ok = _keyboardHookId != IntPtr.Zero;
        if (!ok && _mouseHookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }
        return ok;
    }

    private static bool IsAltHeld() =>
        (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_MENU) & 0x8000) != 0 ||
        (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_LMENU) & 0x8000) != 0 ||
        (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_RMENU) & 0x8000) != 0;

    private static bool IsShiftHeld() =>
        (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_SHIFT) & 0x8000) != 0;

    private void Post(Action action) =>
        Application.Current?.Dispatcher.BeginInvoke(action, DispatcherPriority.Input);

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

        long msg = wParam.ToInt64();
        bool isKeyDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
        bool isKeyUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;
        if (!isKeyDown && !isKeyUp)
            return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

        // KBDLLHOOKSTRUCT: vkCode is the first UInt32 — read it without marshalling.
        uint vkCode;
        unsafe
        {
            vkCode = *(uint*)lParam;
        }

        // Alt release confirms the selection whenever the wheel was open.
        if (isKeyUp && (vkCode == NativeMethods.VK_MENU || vkCode == NativeMethods.VK_LMENU || vkCode == NativeMethods.VK_RMENU))
        {
            if (IsWheelOpen)
                Post(() => AltReleased?.Invoke(this, EventArgs.Empty));
            return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        if (!isKeyDown)
            return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

        // Escape cancels the open wheel (covers both Esc and Alt+Esc).
        if (vkCode == NativeMethods.VK_ESCAPE && IsWheelOpen)
        {
            Post(() => EscapePressed?.Invoke(this, EventArgs.Empty));
            return (IntPtr)1;
        }

        // Everything below requires Alt held.
        if (!IsAltHeld())
            return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

        if (vkCode == NativeMethods.VK_TAB)
        {
            var direction = IsShiftHeld() ? NavigationDirection.Previous : NavigationDirection.Next;
            Post(() => TabPressed?.Invoke(this, direction));
            return (IntPtr)1;
        }

        if (IsWheelOpen)
        {
            switch (vkCode)
            {
                case NativeMethods.VK_LEFT:
                    Post(() => ArrowPressed?.Invoke(this, WheelDirection.Left));
                    return (IntPtr)1;
                case NativeMethods.VK_UP:
                    Post(() => ArrowPressed?.Invoke(this, WheelDirection.Up));
                    return (IntPtr)1;
                case NativeMethods.VK_RIGHT:
                    Post(() => ArrowPressed?.Invoke(this, WheelDirection.Right));
                    return (IntPtr)1;
                case NativeMethods.VK_DOWN:
                    Post(() => ArrowPressed?.Invoke(this, WheelDirection.Down));
                    return (IntPtr)1;
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);

        long msg = wParam.ToInt64();
        if (msg == NativeMethods.WM_MOUSEMOVE && IsWheelOpen)
        {
            // 只观察不拦截：鼠标始终归下层窗口/系统使用（IsHitTestVisible=False 同理）。
            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            int dx = data.pt.X - _lastMovePosted.X;
            int dy = data.pt.Y - _lastMovePosted.Y;
            if (dx * dx + dy * dy >= MovePostThresholdPx * MovePostThresholdPx)
            {
                _lastMovePosted = data.pt;
                NativeMethods.POINT pt = data.pt;
                Post(() => MouseMoved?.Invoke(this, pt));
            }
            return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        if (msg != NativeMethods.WM_MOUSEWHEEL || !IsWheelOpen)
            return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);

        // MSLLHOOKSTRUCT.mouseData high word = wheel delta (WHEEL_DELTA = 120).
        var wheelData = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
        int delta = (short)((wheelData.mouseData >> 16) & 0xFFFF);
        var direction = delta < 0 ? NavigationDirection.Next : NavigationDirection.Previous;
        Post(() => WheelScrolled?.Invoke(this, direction));
        return (IntPtr)1;
    }

    public void Dispose()
    {
        if (_keyboardHookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }
        if (_mouseHookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }
        // Keep delegates referenced until after unhook (callback may be in flight).
        _keyboardProc = null;
        _mouseProc = null;
    }
}