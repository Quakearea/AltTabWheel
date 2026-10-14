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
    private Forms.NotifyIcon? _trayIcon;

    private IReadOnlyList<AppWindow> _windows = Array.Empty<AppWindow>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SetupTrayIcon();
        Log.Write("OnStartup");

        _hook.TabPressed += OnTabPressed;
        _hook.ArrowPressed += OnArrowPressed;
        _hook.AltReleased += OnAltReleased;
        _hook.EscapePressed += OnEscapePressed;
        _hook.WheelScrolled += OnWheelScrolled;
        _wheel.IsVisibleChanged += (_, _) => _hook.IsWheelOpen = _wheel.IsOpen;

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
                Log.Write($"selftest: {_windows.Count} windows");
                _wheel.Open(_windows, _windows.Count > 1 ? 1 : 0);
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
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void OnTabPressed(object? sender, NavigationDirection direction)
    {
        if (!_wheel.IsOpen)
        {
            _windows = _windowService.GetWindows();
            Log.Write($"OnTabPressed: {_windows.Count} windows, open={!_wheel.IsOpen}");
            if (_windows.Count == 0)
                return;
            // Match system Alt+Tab: first press jumps straight to the previous window.
            int start = _windows.Count > 1 ? 1 : 0;
            _hook.IsWheelOpen = true;
            _wheel.Open(_windows, start);
        }
        else
        {
            _wheel.SelectDelta(direction == NavigationDirection.Next ? 1 : -1);
        }
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
        var selected = _wheel.SelectedWindow;
        _hook.IsWheelOpen = false;
        _wheel.CloseWheel();
        Log.Write($"AltReleased: selected={(selected == null ? "null" : selected.ProcessName)}");
        if (selected != null)
            WindowActivator.Activate(selected.Handle);
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
