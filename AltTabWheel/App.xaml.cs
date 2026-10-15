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
    private readonly AppSettings _settings;
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
        ApplySettingsToWheel();

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

        // --selftest: skip the hook and open the wheel directly after 2s (UI pipeline check).
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _windows = _windowService.GetWindows();
                _items = _windowService.BuildItems(_windows, _settings);
                Log.Write($"selftest: {_windows.Count} windows -> {_items.Count} items");
                _hook.IsWheelOpen = true;
                ApplySettingsToWheel();
                _wheel.Open(_items, _items.Count > 1 ? 1 : 0);
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

    /// <summary>枚举窗口并按设置构建轮盘项，呼出轮盘。</summary>
    private void OpenWheel()
    {
        _windows = _windowService.GetWindows();
        _items = _windowService.BuildItems(_windows, _settings);
        Log.Write($"OpenWheel: {_windows.Count} windows -> {_items.Count} items");
        if (_items.Count == 0)
            return;
        // Match system Alt+Tab: first press jumps straight to the previous window.
        int start = _items.Count > 1 ? 1 : 0;
        _hook.IsWheelOpen = true;
        ApplySettingsToWheel();
        _wheel.Open(_items, start);
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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        base.OnExit(e);
    }
}
