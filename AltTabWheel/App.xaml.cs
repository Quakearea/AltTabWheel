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
        _hook.MouseMoved += (s, pt) => Dispatcher.BeginInvoke(() => _wheel.HandleMouseMove(pt.X, pt.Y));
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
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
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
            return;
        _refreshing = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            var windows = _windowService.GetWindows();
            var items = _windowService.BuildItems(windows, _settings);
            Dispatcher.BeginInvoke(() =>
            {
                _refreshing = false;
                bool wasOpen = _wheel.IsOpen;
                IntPtr? selectedHwnd = null;
                int selectedChild = -1;
                if (wasOpen)
                {
                    selectedHwnd = _wheel.SelectedItem?.ActivateTarget(_wheel.SelectedChildIndex)?.Handle;
                    selectedChild = _wheel.SelectedChildIndex;
                }
                _windows = windows;
                _items = items;
                if (wasOpen)
                    RefreshOpenWheel(selectedHwnd, selectedChild);
            });
        });
    }

    private void RefreshOpenWheel(IntPtr? selectedHwnd, int selectedChild)
    {
        if (_items.Count == 0)
        {
            // 窗口全部关闭：收起轮盘
            _hook.IsWheelOpen = false;
            _wheel.CloseWheel();
            _preview.ClosePreview();
            return;
        }
        _wheel.RebuildItems(_items);
        int index = selectedHwnd.HasValue
            ? _items.FindIndex(it => it.Windows.Any(w => w.Handle == selectedHwnd.Value))
            : -1;
        if (index >= 0)
        {
            _wheel.Select(index);
            _wheel.RestoreChildSelection(selectedChild);
        }
        _preview.Refresh(_windows);
        SyncPreview();
    }

    /// <summary>把预览轮播同步到当前选中窗口（选中项→目标窗口→展平列表索引）。</summary>
    private void SyncPreview()
    {
        bool wantPreview = _settings.ShowWindowPreview && _wheel.IsOpen && _windows.Count > 0;
        var item = wantPreview ? _wheel.SelectedItem : null;
        var target = item?.ActivateTarget(_wheel.SelectedChildIndex) ?? item?.Primary;
        if (target == null)
        {
            _preview.ClosePreview();
            return;
        }
        int index = -1;
        for (int i = 0; i < _windows.Count; i++)
        {
            if (_windows[i].Handle == target.Handle)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            _preview.ClosePreview();
            return;
        }
        if (!_preview.IsOpen)
            _preview.Open(_windows, _wheel.WorkArea, _wheel.DpiScale);
        _preview.SetTarget(index);
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
