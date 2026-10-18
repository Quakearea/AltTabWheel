using System.Drawing;
using System.Windows;
using AltTabWheel.Models;
using AltTabWheel.Services;
using AltTabWheel.Views;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AltTabWheel;

/// <summary>
/// Composition root: owns the input hook, window enumeration, wheel overlay,
/// and the tray icon. Translates hook events into wheel commands.
/// </summary>
public partial class App : Application
{
    private readonly WindowService _windowService = new();
    private readonly GlobalInputHook _hook = new();
    private readonly WheelWindow _wheel = new();
    private readonly PreviewCarouselWindow _preview = new();
    private readonly AppSettings _settings;
    private bool _refreshing;
    private Forms.NotifyIcon? _trayIcon;

    private IReadOnlyList<AppWindow> _windows = Array.Empty<AppWindow>();
    private List<WheelItem> _items = new();
    /// <summary>预览轮播数据源：_items 按轮盘顺序展平（组内窗口原位展开），轮盘相邻 = 轮播相邻。</summary>
    private List<AppWindow> _previewWindows = new();
    // 后台刷新合并：枚举中再到的请求记一次尾随；距上次开始过近则防抖到一个定时器
    private bool _refreshQueued;
    private DateTime _lastRefreshStart = DateTime.MinValue;
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMilliseconds(900);
    private System.Windows.Threading.DispatcherTimer? _refreshDebounce;

    public App()
    {
        _settings = AppSettings.Load();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"UNHANDLED: {args.Exception}");
            args.Handled = true;
        };

        SetupTrayIcon();
        Log.Write("OnStartup");

        _hook.TabPressed += OnTabPressed;
        _hook.ArrowPressed += OnArrowPressed;
        _hook.AltReleased += OnAltReleased;
        _hook.EscapePressed += OnEscapePressed;
        _hook.WheelScrolled += OnWheelScrolled;
        // 钩子侧已 BeginInvoke 到 UI 线程，这里直接调用即可（原来再包一层是双重调度 + 闭包分配）
        _hook.MouseMoved += (s, pt) => _wheel.HandleMouseMove(pt.X, pt.Y);
        _wheel.IsVisibleChanged += (_, _) => _hook.IsWheelOpen = _wheel.IsOpen;
        _wheel.SelectionChanged += () => SyncPreview();
        ApplySettingsToWheel();

        // 预热：启动 2.5s 后后台枚举一次，填充标题/图标/进程名缓存。
        // UIA 标题读取偶发极慢（SO 报告可达 30s），绝不能在 Alt+Tab 按下的 UI 线程上做；
        // 预热后 _items 非空，首次呼出走快照路径，枚举永远发生在后台。
        var warmup = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        warmup.Tick += (_, _) =>
        {
            warmup.Stop();
            RequestBackgroundRefresh();
        };
        warmup.Start();

        // --settings：直接打开设置窗口（便于托盘之外的方式进入）
        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            ShowSettings();

        bool hookOk = _hook.Install();
        Log.Write($"hook install: {hookOk}");
        if (!hookOk)
        {
            MessageBox.Show(
                "Failed to install the global keyboard hook. AltTabWheel cannot start.",
                "AltTabWheel", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // --selftest: skip the hook and open the wheel directly after 2s (full pipeline check,
        // including the preview carousel — OpenWheel enumerates synchronously on first call).
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase)
            || e.Args.Contains("--selftest-reorder", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _hook.IsWheelOpen = true;
                OpenWheel();
            };
            timer.Start();
        }

        // --selftest-reorder: selftest 基础上验证"选中窗口与轮盘对应"——
        // t+5s 程序化激活一个非选中窗口（改变 z-order），t+6s 触发后台刷新（预览条目重排），
        // t+7s 移动一次选择；截图应始终显示预览高亮卡 == 轮盘选中扇区对应窗口。
        if (e.Args.Contains("--selftest-reorder", StringComparer.OrdinalIgnoreCase))
        {
            var activate = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            activate.Tick += (_, _) =>
            {
                activate.Stop();
                var selected = _wheel.SelectedItem?.ActivateTarget(_wheel.SelectedChildIndex);
                var other = _previewWindows.FirstOrDefault(w => w.Handle != selected?.Handle);
                if (other != null)
                {
                    Log.Write($"selftest-reorder: activate {other.ProcessName} (0x{other.Handle.ToInt64():X}), selected stays {selected?.ProcessName}");
                    WindowActivator.Activate(other.Handle);
                }
            };
            activate.Start();
            var refresh = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            refresh.Tick += (_, _) =>
            {
                refresh.Stop();
                Log.Write("selftest-reorder: background refresh (entries reorder expected)");
                RequestBackgroundRefresh();
            };
            refresh.Start();
            var nav = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            nav.Tick += (_, _) =>
            {
                nav.Stop();
                _wheel.SelectDelta(1);
                Log.Write($"selftest-reorder: navigated -> {_wheel.SelectedItem?.ProcessName}");
            };
            nav.Start();
        }
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "AltTabWheel (right-click to exit)",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add("-");
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void ApplySettingsToWheel()
    {
        _wheel.MouseHoverEnabled = _settings.MouseHoverEnabled;
        _wheel.MouseTakeoverTravel = _settings.MouseTakeoverTravelPx / (_wheel.DpiScale > 0 ? _wheel.DpiScale : 1);
        _wheel.MouseMinRadius = _settings.MouseMinRadiusDip;
    }

    private void ShowSettings()
    {
        var window = new Views.SettingsWindow(_settings, ApplySettingsToWheel);
        window.Show();
    }

    private void OnTabPressed(object? sender, NavigationDirection direction)
    {
        if (!_wheel.IsOpen)
        {
            OpenWheel();
            return;
        }
        _wheel.SelectDelta(direction == NavigationDirection.Next ? 1 : -1);
    }

    /// <summary>
    /// 呼出轮盘。快照策略：优先用已有缓存（上次枚举结果）立即显示，窗口实况在
    /// 后台线程重新枚举（UIA 标题偶发秒级卡顿，绝不能阻塞呼出），完成后静默刷新。
    /// </summary>
    private void OpenWheel()
    {
        if (_items.Count == 0)
        {
            // 首次呼出且无预热结果：同步枚举兜底线
            _windows = _windowService.GetWindows();
            _items = _windowService.BuildItems(_windows, _settings);
            _previewWindows = FlattenForPreview(_items);
        }
        Log.Write($"OpenWheel: {_windows.Count} windows -> {_items.Count} items");
        if (_items.Count == 0)
            return;
        // Match system Alt+Tab: first press jumps straight to the previous window.
        int start = _items.Count > 1 ? 1 : 0;
        _hook.IsWheelOpen = true;
        ApplySettingsToWheel();
        _wheel.Open(_items, start);
        RequestBackgroundRefresh();
    }

    /// <summary>后台重新枚举窗口实况；轮盘仍开着则按 HWND 保持选中静默刷新。</summary>
    private void RequestBackgroundRefresh()
    {
        if (_refreshing)
        {
            // 枚举中再到的请求合并成一次尾随和刷新
            _refreshQueued = true;
            return;
        }
        if (DateTime.UtcNow - _lastRefreshStart < MinRefreshInterval)
        {
            // 防抖：快速开关轮盘不必每次都付全量 UIA 枚举
            _refreshDebounce ??= CreateRefreshDebounce();
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
            return;
        }
        StartBackgroundRefresh();
    }

    private System.Windows.Threading.DispatcherTimer CreateRefreshDebounce()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = MinRefreshInterval };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RequestBackgroundRefresh();
        };
        return timer;
    }

    private void StartBackgroundRefresh()
    {
        _refreshing = true;
        _lastRefreshStart = DateTime.UtcNow;
        System.Threading.Tasks.Task.Run(() =>
        {
            var windows = _windowService.GetWindows();
            var items = _windowService.BuildItems(windows, _settings);
            Dispatcher.BeginInvoke(() =>
            {
                _refreshing = false;
                bool wasOpen = _wheel.IsOpen;
                IntPtr? selectedHwnd = null;
                int selectedIndex = -1;
                int selectedChild = -1;
                if (wasOpen)
                {
                    selectedHwnd = _wheel.SelectedItem?.ActivateTarget(_wheel.SelectedChildIndex)?.Handle;
                    selectedIndex = _wheel.SelectedIndex;
                    selectedChild = _wheel.SelectedChildIndex;
                }
                bool structureChanged = !ItemsStructurallyEqual(_items, items);
                _windows = windows;
                _items = items;
                _previewWindows = FlattenForPreview(items);
                if (wasOpen)
                    RefreshOpenWheel(selectedHwnd, selectedIndex, selectedChild, structureChanged);
                if (_refreshQueued)
                {
                    _refreshQueued = false;
                    RequestBackgroundRefresh();
                }
            });
        });
    }

    private void RefreshOpenWheel(IntPtr? selectedHwnd, int selectedIndex, int selectedChild, bool structureChanged)
    {
        if (_items.Count == 0)
        {
            // 窗口全部关闭：收起轮盘
            _hook.IsWheelOpen = false;
            _wheel.CloseWheel();
            _preview.ClosePreview();
            return;
        }
        if (structureChanged)
        {
            _wheel.RebuildItems(_items);
            int index = selectedHwnd.HasValue
                ? _items.FindIndex(it => it.Windows.Any(w => w.Handle == selectedHwnd.Value))
                : -1;
            if (index < 0 && selectedHwnd.HasValue)
            {
                // 选中窗口在刷新期间关闭：落到原位置最近的存活项（对齐系统 Alt+Tab 的位置保持）
                index = Math.Clamp(selectedIndex, 0, _items.Count - 1);
                Log.Write($"refresh: selected window gone, fallback to index {index}");
            }
            if (index >= 0)
            {
                _wheel.Select(index);
                _wheel.RestoreChildSelection(selectedChild);
            }
        }
        // 结构没变：轮盘 DOM/选中原样保留（零重建零闪烁）；
        // 预览侧 SyncEntries 自行 diff，顺序没变就是一次空转
        _preview.Refresh(_previewWindows);
        SyncPreview();
    }

    /// <summary>把预览轮播同步到当前选中窗口（选中项→目标窗口→按 HWND 定位轮播条目）。</summary>
    private void SyncPreview()
    {
        bool wantPreview = _settings.ShowWindowPreview && _wheel.IsOpen && _previewWindows.Count > 0;
        var item = wantPreview ? _wheel.SelectedItem : null;
        var target = item?.ActivateTarget(_wheel.SelectedChildIndex) ?? item?.Primary;
        if (target == null)
        {
            _preview.ClosePreview();
            return;
        }
        if (!_preview.IsOpen)
            _preview.Open(_previewWindows, _wheel.WorkArea, _wheel.DpiScale);
        if (!_preview.SetTarget(target.Handle))
        {
            // 超 MaxThumbs 截断的窗口没有轮播条目，任何卡片高亮都会误导 → 收掉
            Log.Write($"preview target miss: {target.ProcessName} hwnd=0x{target.Handle.ToInt64():X}");
            _preview.ClosePreview();
        }
    }

    /// <summary>轮盘项按遍历顺序展平成窗口列表（组内窗口原位展开；无窗口的固定项不产生条目）。</summary>
    private static List<AppWindow> FlattenForPreview(IReadOnlyList<WheelItem> items)
    {
        var list = new List<AppWindow>();
        foreach (var item in items)
            foreach (var window in item.Windows)
                list.Add(window);
        return list;
    }

    /// <summary>结构比较：项数、固定标记、组内窗口句柄序列全同 → 后台刷新可跳过轮盘重建。</summary>
    private static bool ItemsStructurallyEqual(IReadOnlyList<WheelItem> a, IReadOnlyList<WheelItem> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].IsPinned != b[i].IsPinned || a[i].Windows.Count != b[i].Windows.Count)
                return false;
            for (int j = 0; j < a[i].Windows.Count; j++)
            {
                if (a[i].Windows[j].Handle != b[i].Windows[j].Handle)
                    return false;
            }
        }
        return true;
    }

    private void OnWheelScrolled(object? sender, NavigationDirection direction) =>
        _wheel.SelectDelta(direction == NavigationDirection.Next ? 1 : -1);

    private void OnArrowPressed(object? sender, WheelDirection direction)
    {
        double radians = direction switch
        {
            WheelDirection.Right => 0,
            WheelDirection.Down => Math.PI / 2,
            WheelDirection.Left => Math.PI,
            WheelDirection.Up => -Math.PI / 2,
            _ => 0,
        };
        _wheel.SelectByDirection(radians);
    }

    private void OnAltReleased(object? sender, EventArgs e)
    {
        if (!_wheel.IsOpen)
            return;
        var item = _wheel.SelectedItem;
        var childIndex = _wheel.SelectedChildIndex;
        _hook.IsWheelOpen = false;
        _wheel.CloseWheel();
        _preview.ClosePreview();
        Log.Write($"AltReleased: item={(item == null ? "null" : item.ProcessName)}, child={childIndex}");
        if (item == null)
            return;

        var target = item.ActivateTarget(childIndex);
        if (target != null)
            WindowActivator.Activate(target.Handle);
        else if (item.IsPinned)
            LaunchPinned(item);
    }

    private static void LaunchPinned(WheelItem item)
    {
        try
        {
            if (item.LaunchPath == null)
                return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.LaunchPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Write($"LaunchPinned failed: {ex.Message}");
        }
    }

    private void OnEscapePressed(object? sender, EventArgs e)
    {
        if (!_wheel.IsOpen)
            return;
        _hook.IsWheelOpen = false;
        _wheel.CloseWheel();
        _preview.ClosePreview();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _preview.DisposeAllThumbnails();
        _hook.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        base.OnExit(e);
    }
}
