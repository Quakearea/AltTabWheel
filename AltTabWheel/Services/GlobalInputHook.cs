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
    private NativeMethods.POINT _pendingMousePt;   // 免分配投递：钩子侧写字段，UI 侧回调读

    /// <summary>
    /// 鼠标移动节流：LL 鼠标钩子按像素级频率到达，超过阈值距离才投递 UI 线程，
    /// 避免按住 Alt 期间不必要的 Dispatcher 压力。
    /// </summary>
    private const int MovePostThresholdPx = 6;

    // Alt/Shift 状态由钩子消息流自行跟踪（所有键盘消息必经本回调），
    // 热路径不再调 GetAsyncKeyState（每次按键 3-4 次 P/Invoke 纯属浪费）。
    // 初值在 Install（冷路径）用 GetAsyncKeyState 同步一次。
    private bool _altHeld;
    private bool _shiftHeld;

    private bool _isWheelOpen;

    /// <summary>
    /// Set by the app: only swallow keys when the wheel is being shown.
    /// 鼠标钩子按需安装——轮盘关闭时卸载，WH_MOUSE_LL 常驻会让全系统每个
    /// 鼠标消息都调度进本进程消息循环，而轮盘关闭后它毫无用处。
    /// </summary>
    public bool IsWheelOpen
    {
        get => _isWheelOpen;
        set
        {
            if (_isWheelOpen == value)
                return;
            _isWheelOpen = value;
            if (value)
                InstallMouseHook();
            else
                UninstallMouseHook();
        }
    }

    private IntPtr _keyboardHookId = IntPtr.Zero;
    private IntPtr _mouseHookId = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private NativeMethods.LowLevelMouseProc? _mouseProc;

    // 预分配的无闭包委托：钩子热路径投递零分配（只读字段引用）。
    private Action? _raiseTabNext;
    private Action? _raiseTabPrev;
    private Action? _raiseWheelNext;
    private Action? _raiseWheelPrev;
    private Action? _raiseAltReleased;
    private Action? _raiseEscape;
    private Action? _raiseMouseMove;
    private Action[]? _raiseArrows;   // 按 WheelDirection 枚举序索引

    public bool Install()
    {
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
        _keyboardHookId = NativeMethods.InstallKeyboardHook(_keyboardProc);
        // 冷路径一次性同步修饰键初值（之后完全由消息流维护）
        _altHeld = QueryAltHeld();
        _shiftHeld = QueryShiftHeld();
        InitRaiseDelegates();
        return _keyboardHookId != IntPtr.Zero;
    }

    private void InitRaiseDelegates()
    {
        _raiseTabNext = () => TabPressed?.Invoke(this, NavigationDirection.Next);
        _raiseTabPrev = () => TabPressed?.Invoke(this, NavigationDirection.Previous);
        _raiseWheelNext = () => WheelScrolled?.Invoke(this, NavigationDirection.Next);
        _raiseWheelPrev = () => WheelScrolled?.Invoke(this, NavigationDirection.Previous);
        _raiseAltReleased = () => AltReleased?.Invoke(this, EventArgs.Empty);
        _raiseEscape = () => EscapePressed?.Invoke(this, EventArgs.Empty);
        _raiseMouseMove = () => MouseMoved?.Invoke(this, _pendingMousePt);
        _raiseArrows = new Action[]
        {
            () => ArrowPressed?.Invoke(this, WheelDirection.Left),
            () => ArrowPressed?.Invoke(this, WheelDirection.Up),
            () => ArrowPressed?.Invoke(this, WheelDirection.Right),
            () => ArrowPressed?.Invoke(this, WheelDirection.Down),
        };
    }

    private void InstallMouseHook()
    {
        if (_mouseHookId == IntPtr.Zero && _mouseProc != null)
        {
            _mouseHookId = NativeMethods.InstallMouseHook(_mouseProc);
            Log.Write($"mouse hook install: {_mouseHookId != IntPtr.Zero}");
        }
    }

    private void UninstallMouseHook()
    {
        if (_mouseHookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
            Log.Write("mouse hook uninstall");
        }
    }

    private static bool QueryAltHeld() =>
        (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_MENU) & 0x8000) != 0 ||
        (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_LMENU) & 0x8000) != 0 ||
        (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_RMENU) & 0x8000) != 0;

    private static bool QueryShiftHeld() =>
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

        // 修饰键状态跟踪：所有键盘消息必经本回调，比 GetAsyncKeyState 更省且不会竞态。
        switch (vkCode)
        {
            case NativeMethods.VK_MENU:
            case NativeMethods.VK_LMENU:
            case NativeMethods.VK_RMENU:
                _altHeld = isKeyDown;
                break;
            case NativeMethods.VK_SHIFT:
            case NativeMethods.VK_LSHIFT:
            case NativeMethods.VK_RSHIFT:
                _shiftHeld = isKeyDown;
                break;
        }

        // Alt release confirms the selection whenever the wheel was open.
        if (isKeyUp && (vkCode == NativeMethods.VK_MENU || vkCode == NativeMethods.VK_LMENU || vkCode == NativeMethods.VK_RMENU))
        {
            if (_isWheelOpen && _raiseAltReleased != null)
                Post(_raiseAltReleased);
            return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        if (!isKeyDown)
            return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

        // Escape cancels the open wheel (covers both Esc and Alt+Esc).
        if (vkCode == NativeMethods.VK_ESCAPE && _isWheelOpen)
        {
            if (_raiseEscape != null)
                Post(_raiseEscape);
            return (IntPtr)1;
        }

        // Everything below requires Alt held.
        if (!_altHeld)
            return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

        if (vkCode == NativeMethods.VK_TAB)
        {
            var raise = _shiftHeld ? _raiseTabPrev : _raiseTabNext;
            if (raise != null)
                Post(raise);
            return (IntPtr)1;
        }

        if (_isWheelOpen && _raiseArrows != null)
        {
            int arrowIndex = vkCode switch
            {
                NativeMethods.VK_LEFT => (int)WheelDirection.Left,
                NativeMethods.VK_UP => (int)WheelDirection.Up,
                NativeMethods.VK_RIGHT => (int)WheelDirection.Right,
                NativeMethods.VK_DOWN => (int)WheelDirection.Down,
                _ => -1,
            };
            if (arrowIndex >= 0)
            {
                Post(_raiseArrows[arrowIndex]);
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
        if (msg == NativeMethods.WM_MOUSEMOVE && _isWheelOpen)
        {
            // 只观察不拦截：鼠标始终归下层窗口/系统使用（IsHitTestVisible=False 同理）。
            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            int dx = data.pt.X - _lastMovePosted.X;
            int dy = data.pt.Y - _lastMovePosted.Y;
            if (dx * dx + dy * dy >= MovePostThresholdPx * MovePostThresholdPx)
            {
                _lastMovePosted = data.pt;
                _pendingMousePt = data.pt;
                if (_raiseMouseMove != null)
                    Post(_raiseMouseMove);
            }
            return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        if (msg != NativeMethods.WM_MOUSEWHEEL || !_isWheelOpen)
            return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);

        // MSLLHOOKSTRUCT.mouseData high word = wheel delta (WHEEL_DELTA = 120).
        var wheelData = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
        int delta = (short)((wheelData.mouseData >> 16) & 0xFFFF);
        var raiseWheel = delta < 0 ? _raiseWheelNext : _raiseWheelPrev;
        if (raiseWheel != null)
            Post(raiseWheel);
        return (IntPtr)1;
    }

    public void Dispose()
    {
        _isWheelOpen = false;   // 防止 IsWheelOpen 语义在卸载后残留
        if (_keyboardHookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }
        UninstallMouseHook();
        // Keep delegates referenced until after unhook (callback may be in flight).
        _keyboardProc = null;
        _mouseProc = null;
    }
}