using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AltTabWheel.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Image = System.Windows.Controls.Image;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;

namespace AltTabWheel.Views;

/// <summary>
/// Radial Alt+Tab switcher overlay.
///
/// Geometry contract (kept deterministic so muscle memory survives refactor):
/// sectors start at -90° (screen top) and run clockwise; sector i sits at
/// angle = -90 + i * (360 / n), placed on a ring of radius R around the
/// wheel center. R grows with sector count so items never overlap:
///   R = clamp(max(150, (item + gap) / (2·sin(π/n))), 150, 330).
/// The selected sector scales up, brightens, and drives the center badge text.
/// </summary>
public partial class WheelWindow : Window
{
    private const double ItemSize = 64;
    private const double ItemGap = 26;
    private const double SelectedScale = 1.3;
    private const double SelectAnimMs = 110;

    private static readonly Brush SectorBackground = new SolidColorBrush(Color.FromArgb(0xE6, 0x2A, 0x2A, 0x2E));
    private static readonly Brush SectorBorder = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SelectedBackground = new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0x5B, 0xE6));
    private static readonly Brush SelectedBorder = new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0xA5, 0xFF));
    private static readonly Brush TrackBrush = new SolidColorBrush(Color.FromArgb(0x21, 0xFF, 0xFF, 0xFF));

    private readonly List<Grid> _sectors = new();
    private readonly List<ScaleTransform> _scales = new();
    private readonly List<Border> _borders = new();

    private IReadOnlyList<AppWindow> _windows = Array.Empty<AppWindow>();
    private int _selectedIndex = -1;
    private double _ringRadius = 150;

    public bool IsOpen { get; private set; }

    public AppWindow? SelectedWindow =>
        _selectedIndex >= 0 && _selectedIndex < _windows.Count
            ? _windows[_selectedIndex]
            : null;

    public WheelWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Stay a passive overlay: never steal activation, never appear in Alt+Tab.
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    /// <summary>Show the wheel on the monitor under the mouse, preselecting startIndex.</summary>
    public void Open(IReadOnlyList<AppWindow> windows, int startIndex)
    {
        if (windows.Count == 0)
            return;
        _windows = windows;
        PositionOnMouseMonitor();

        BuildWheel();

        // IsOpen must precede Show(): the IsVisibleChanged sync reads it and
        // would otherwise stomp _hook.IsWheelOpen back to false.
        IsOpen = true;
        Opacity = 0;
        Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));

        Select(Math.Clamp(startIndex, 0, windows.Count - 1));
    }

    public void CloseWheel()
    {
        if (!IsOpen)
            return;
        IsOpen = false;

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90));
        fade.Completed += (_, _) =>
        {
            Hide();
            ClearWheel();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void PositionOnMouseMonitor()
    {
        NativeMethods.GetCursorPos(out var pt);
        IntPtr monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(monitor, ref info))
            rect = info.rcMonitor;

        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;
    }

    private void ClearWheel()
    {
        WheelCanvas.Children.Clear();
        _sectors.Clear();
        _scales.Clear();
        _borders.Clear();
        _windows = Array.Empty<AppWindow>();
        _selectedIndex = -1;
    }

    private void BuildWheel()
    {
        // Clear containers only — _windows was just assigned by Open() and must survive.
        WheelCanvas.Children.Clear();
        _sectors.Clear();
        _scales.Clear();
        _borders.Clear();
        _selectedIndex = -1;

        int n = _windows.Count;

        // Ring radius: keep adjacent items separated; clamp for very few / many windows.
        double radius = 150;
        if (n >= 2)
        {
            double needed = (ItemSize + ItemGap) / (2 * Math.Sin(Math.PI / n));
            radius = Math.Clamp(Math.Max(150, needed), 150, 330);
        }
        _ringRadius = radius;

        // Decorative track ring under the sectors.
        var track = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = TrackBrush,
            StrokeThickness = 1,
        };
        Canvas.SetLeft(track, WheelCanvas.Width / 2 - radius);
        Canvas.SetTop(track, WheelCanvas.Height / 2 - radius);
        WheelCanvas.Children.Add(track);

        for (int i = 0; i < n; i++)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = SectorBackground,
                BorderBrush = SectorBorder,
                BorderThickness = new Thickness(1.5),
                Width = ItemSize,
                Height = ItemSize,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 12,
                    ShadowDepth = 0,
                    Opacity = 0.3,
                    Color = Colors.Black,
                },
            };

            var image = new Image
            {
                Source = _windows[i].Icon,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(10),
            };
            border.Child = image;

            var scale = new ScaleTransform(1, 1);
            var sector = new Grid
            {
                Width = ItemSize,
                Height = ItemSize,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
                Children = { border },
                Tag = i,
            };

            double angle = SectorAngle(i, n);
            double x = WheelCanvas.Width / 2 + Math.Cos(angle) * radius;
            double y = WheelCanvas.Height / 2 + Math.Sin(angle) * radius;
            Canvas.SetLeft(sector, x - ItemSize / 2);
            Canvas.SetTop(sector, y - ItemSize / 2);

            WheelCanvas.Children.Add(sector);
            _sectors.Add(sector);
            _scales.Add(scale);
            _borders.Add(border);
        }
    }

    /// <summary>Sector angle in radians; 0 = right, -π/2 = top, clockwise on screen (y down).</summary>
    private static double SectorAngle(int index, int count) =>
        (-90.0 + index * (360.0 / count)) * Math.PI / 180.0;

    public void SelectDelta(int delta)
    {
        if (_windows.Count == 0)
            return;
        int next = (_selectedIndex + delta) % _windows.Count;
        if (next < 0)
            next += _windows.Count;
        Select(next);
    }

    /// <summary>Select the sector geometrically closest to the given direction.</summary>
    public void SelectByDirection(double directionRadians)
    {
        if (_windows.Count < 2)
            return;

        double dirX = Math.Cos(directionRadians);
        double dirY = Math.Sin(directionRadians);

        int best = -1;
        double bestDot = double.MinValue;
        for (int i = 0; i < _windows.Count; i++)
        {
            if (i == _selectedIndex)
                continue;
            double a = SectorAngle(i, _windows.Count);
            double dot = Math.Cos(a) * dirX + Math.Sin(a) * dirY;
            if (dot > bestDot)
            {
                bestDot = dot;
                best = i;
            }
        }
        if (best >= 0)
            Select(best);
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _windows.Count || index == _selectedIndex)
            return;

        // Deselect previous.
        if (_selectedIndex >= 0 && _selectedIndex < _borders.Count)
        {
            AnimateScale(_scales[_selectedIndex], 1.0);
            _borders[_selectedIndex].Background = SectorBackground;
            _borders[_selectedIndex].BorderBrush = SectorBorder;
            Panel.SetZIndex(_sectors[_selectedIndex], 0);
        }

        _selectedIndex = index;

        AnimateScale(_scales[index], SelectedScale);
        _borders[index].Background = SelectedBackground;
        _borders[index].BorderBrush = SelectedBorder;
        Panel.SetZIndex(_sectors[index], 10);

        var selected = _windows[index];
        CenterProcess.Text = selected.ProcessName;
        CenterTitle.Text = selected.DisplayTitle;
    }

    private static void AnimateScale(ScaleTransform transform, double to)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(SelectAnimMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}
