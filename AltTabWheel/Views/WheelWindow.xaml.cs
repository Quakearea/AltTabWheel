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
/// 几何约定（确定性，保持肌肉记忆）：顶层扇区从 -90°（正上方）开始顺时针均布，
/// 扇区 i 角度 = -90 + i * (360 / n)。半径随数量自适应：
///   R = clamp(max(150, (item+gap) / (2·sin(π/n))), 150, 330)。
/// 组（合并任务栏）选中后在外环展开级联子轮盘：子扇区以组角度为中心扇形展开，
/// 半径 R2 = R + 100，尺寸 48。鼠标按距离分带命中：内环带 → 顶层，外环带 → 子项。
/// </summary>
public partial class WheelWindow : Window
{
    private const double ItemSize = 64;
    private const double ItemGap = 26;
    private const double ChildSize = 48;
    private const double ChildRadiusOffset = 100;
    private const double SelectedScale = 1.3;
    private const double SelectAnimMs = 110;

    private static readonly Brush SectorBackground = new SolidColorBrush(Color.FromArgb(0xE6, 0x2A, 0x2A, 0x2E));
    private static readonly Brush SectorBorder = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SelectedBackground = new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0x5B, 0xE6));
    private static readonly Brush SelectedBorder = new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0xA5, 0xFF));
    private static readonly Brush PinnedBackground = new SolidColorBrush(Color.FromArgb(0x80, 0x2A, 0x2A, 0x2E));
    private static readonly Brush TrackBrush = new SolidColorBrush(Color.FromArgb(0x21, 0xFF, 0xFF, 0xFF));
    private static readonly Brush GroupBadgeBackground = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x4A, 0x52));

    /// <summary>扇区共享的冻结阴影（每扇区 new 一个 DropShadowEffect = 每个都占一份 GPU 资源）。</summary>
    private static readonly System.Windows.Media.Effects.DropShadowEffect SectorShadow = CreateSectorShadow();

    private static System.Windows.Media.Effects.DropShadowEffect CreateSectorShadow()
    {
        var effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 12,
            ShadowDepth = 0,
            Opacity = 0.3,
            Color = Colors.Black,
        };
        effect.Freeze();
        return effect;
    }

    // 顶层扇区
    private readonly List<Grid> _sectors = new();
    private readonly List<ScaleTransform> _scales = new();
    private readonly List<Border> _borders = new();

    // 级联子轮盘（当前展开的组）
    private readonly List<Grid> _childSectors = new();
    private readonly List<Border> _childBorders = new();
    private readonly List<ScaleTransform> _childScales = new();

    private IReadOnlyList<WheelItem> _items = Array.Empty<WheelItem>();
    private NativeMethods.RECT _workArea = new() { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    private int _selectedIndex = -1;   // 顶层选中
    private int _expandedIndex = -1;   // 展开的组（-1 = 无）
    private int _childIndex = -1;      // 子轮盘选中
    private double _ringRadius = 150;

    public bool IsOpen { get; private set; }

    /// <summary>选中项（含子轮盘选中）变化通知，用于同步预览轮播。</summary>
    public event Action? SelectionChanged;

    /// <summary>鼠标指向选中开关（由设置注入）。</summary>
    public bool MouseHoverEnabled { get; set; } = true;
    public double MouseTakeoverTravel { get; set; } = 24;
    public double MouseMinRadius { get; set; } = 70;

    private enum InputMode { Keyboard, Mouse }
    private InputMode _inputMode = InputMode.Keyboard;
    private double _mouseTravel;
    private Point _lastMouseDip;

    private double _dpiScale = 1.0;

    /// <summary>窗口所在显示器的缩放（打开轮盘后有效）。</summary>
    public double DpiScale => _dpiScale;

    /// <summary>轮盘覆盖的工作区（物理 px，打开轮盘后有效），预览宿主据此停靠左侧。</summary>
    public NativeMethods.RECT WorkArea => _workArea;

    /// <summary>当前选中索引（顶层，-1 = 无）。刷新后选中窗口消失时用于位置保持。</summary>
    public int SelectedIndex => _selectedIndex;

    /// <summary>当前选中项（顶层）。</summary>
    public WheelItem? SelectedItem =>
        _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;

    /// <summary>子轮盘选中索引（未展开为 -1）。</summary>
    public int SelectedChildIndex => _expandedIndex >= 0 ? _childIndex : -1;

    public WheelWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 被动覆盖层：不抢焦点、不进 Alt+Tab 列表
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    /// <summary>显示轮盘（鼠标所在显示器），预选 startIndex。</summary>
    public void Open(IReadOnlyList<WheelItem> items, int startIndex)
    {
        if (items.Count == 0)
            return;
        _items = items;
        PositionOnMouseMonitor();

        // 鼠标模式基线：以呼出时的鼠标位置为参考点，避免开场跳动
        _inputMode = InputMode.Keyboard;
        _mouseTravel = 0;
        if (NativeMethods.GetCursorPos(out var pt0))
            _lastMouseDip = new Point(pt0.X / _dpiScale, pt0.Y / _dpiScale);

        BuildWheel();

        // IsOpen 必须先于 Show()：IsVisibleChanged 同步会读它，
        // 顺序错了会把 hook 状态 stomp 回 false。
        IsOpen = true;
        Opacity = 0;
        Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));

        Select(Math.Clamp(startIndex, 0, items.Count - 1));
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

    /// <summary>覆盖鼠标所在显示器工作区。WPF 尺寸是 DIP，显示器 API 是物理像素。</summary>
    private void PositionOnMouseMonitor()
    {
        NativeMethods.GetCursorPos(out var pt);
        IntPtr monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(monitor, ref info))
            rect = info.rcWork;
        _workArea = rect;

        double scaleX = 1.0, scaleY = 1.0;
        if (monitor != IntPtr.Zero
            && NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0
            && dpiX > 0 && dpiY > 0)
        {
            scaleX = dpiX / 96.0;
            scaleY = dpiY / 96.0;
        }

        Left = rect.Left / scaleX;
        Top = rect.Top / scaleX;
        Width = rect.Width / scaleX;
        Height = rect.Height / scaleY;
        _dpiScale = scaleX;
    }

    private void ClearWheel()
    {
        WheelCanvas.Children.Clear();
        _sectors.Clear();
        _scales.Clear();
        _borders.Clear();
        _childSectors.Clear();
        _childBorders.Clear();
        _childScales.Clear();
        _items = Array.Empty<WheelItem>();
        _selectedIndex = -1;
        _expandedIndex = -1;
        _childIndex = -1;
    }

    private void BuildWheel()
    {
        // 只清容器 —— _items 由 Open 刚赋值，必须保留
        WheelCanvas.Children.Clear();
        _sectors.Clear();
        _scales.Clear();
        _borders.Clear();
        _childSectors.Clear();
        _childBorders.Clear();
        _childScales.Clear();
        _selectedIndex = -1;
        _expandedIndex = -1;
        _childIndex = -1;

        int n = _items.Count;
        double radius = 150;
        if (n >= 2)
        {
            double needed = (ItemSize + ItemGap) / (2 * Math.Sin(Math.PI / n));
            radius = Math.Clamp(Math.Max(150, needed), 150, 330);
        }
        _ringRadius = radius;

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
            AddSector(_items[i], i, n, radius, ItemSize, isChild: false);
    }

    private void AddSector(WheelItem item, int index, int count, double radius, double size, bool isChild, int childIndex = -1)
    {
        var border = new Border
        {
            Background = item.IsPinned ? PinnedBackground : SectorBackground,
            BorderBrush = SectorBorder,
            BorderThickness = new Thickness(1.5),
            Width = size,
            Height = size,
            Opacity = item.IsPinned ? 0.62 : 1.0,
            Effect = SectorShadow,
        };

        var image = new Image
        {
            Source = item.Primary?.Icon ?? item.Icon,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(9),
        };
        // Fant 采样：128px 源 → 显示尺寸的锐利下采样（默认 Linear 发虚）
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);

        // 角标容器：组窗口数量（右下）
        var overlays = new List<UIElement>();
        if (!isChild && item.IsGroup)
        {
            overlays.Add(new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = GroupBadgeBackground,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, -4, -4),
                Child = new TextBlock
                {
                    Text = item.Windows.Count.ToString(),
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }

        if (overlays.Count > 0)
        {
            var container = new Grid();
            container.Children.Add(image);
            foreach (var overlay in overlays)
                container.Children.Add(overlay);
            border.Child = container;
        }
        else
        {
            border.Child = image;
        }

        var scale = new ScaleTransform(1, 1);
        var sector = new Grid
        {
            Width = size,
            Height = size,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
            Children = { border },
            Tag = isChild ? childIndex : index,
        };

        double angle = isChild
            ? SectorAngle(_expandedIndex, _items.Count) + ChildSectorAngle(index, count, _ringRadius + ChildRadiusOffset)
            : SectorAngle(index, count);
        double r = isChild ? _ringRadius + ChildRadiusOffset : radius;
        double x = WheelCanvas.Width / 2 + Math.Cos(angle) * r;
        double y = WheelCanvas.Height / 2 + Math.Sin(angle) * r;
        Canvas.SetLeft(sector, x - size / 2);
        Canvas.SetTop(sector, y - size / 2);

        WheelCanvas.Children.Add(sector);
        if (isChild)
        {
            _childSectors.Add(sector);
            _childScales.Add(scale);
            _childBorders.Add(border);
        }
        else
        {
            _sectors.Add(sector);
            _scales.Add(scale);
            _borders.Add(border);
        }
    }

    /// <summary>顶层扇区角度（弧度）：0=右，-π/2=顶，屏幕坐标 y 向下顺时针。</summary>
    private static double SectorAngle(int index, int count) =>
        (-90.0 + index * (360.0 / count)) * Math.PI / 180.0;

    /// <summary>子扇区角度：以组角度为中心，按数量扇形展开（最大角距 42°）。</summary>
    private static double ChildSectorAngle(int childIndex, int childCount, double radius)
    {
        if (childCount <= 1) return 0;
        double stepDeg = Math.Min(42, 150.0 / (childCount - 1));
        double startDeg = -stepDeg * (childCount - 1) / 2.0;
        return (startDeg + childIndex * stepDeg) * Math.PI / 180.0;
    }

    // ---------- 导航 ----------

    public void SelectDelta(int delta)
    {
        if (_items.Count == 0)
            return;
        NotifyKeyboardNavigation();

        if (_expandedIndex >= 0)
        {
            var group = _items[_expandedIndex];
            int next = _childIndex + delta;
            if (next < 0 || next >= group.Windows.Count)
            {
                // 子轮盘越界：收起并移到顶层相邻项
                int from = _expandedIndex;
                CollapseChildWheel();
                Select(WrapIndex(from + delta, _items.Count));
            }
            else
            {
                SelectChild(next);
            }
            return;
        }

        // 顶层移动（选中组时 Select 会自动展开子轮盘）
        Select(WrapIndex(_selectedIndex + delta, _items.Count));
    }

    private static int WrapIndex(int i, int count)
    {
        i %= count;
        return i < 0 ? i + count : i;
    }

    public void SelectByDirection(double directionRadians)
    {
        if (_items.Count < 2)
            return;
        NotifyKeyboardNavigation();
        // 键盘方向导航保持顶层；若有子轮盘先收起
        if (_expandedIndex >= 0)
            CollapseChildWheel();
        int? hit = FindSectorByAngle(directionRadians, _items.Count, _ringRadius);
        if (hit.HasValue && hit.Value != _selectedIndex)
            Select(hit.Value);
    }

    /// <summary>角度最近扇区。</summary>
    private int? FindSectorByAngle(double angle, int count, double radius)
    {
        if (count < 2)
            return count == 1 ? 0 : null;
        int best = -1;
        double bestDot = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            double a = SectorAngle(i, count);
            double dot = Math.Cos(a) * Math.Cos(angle) + Math.Sin(a) * Math.Sin(angle);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = i;
            }
        }
        return best >= 0 ? best : null;
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _items.Count || index == _selectedIndex)
            return;

        // 顶层切换时收起子轮盘
        if (_expandedIndex >= 0 && index != _expandedIndex)
            CollapseChildWheel();

        if (_selectedIndex >= 0 && _selectedIndex < _borders.Count)
        {
            AnimateScale(_scales[_selectedIndex], 1.0);
            RestoreBorder(_borders[_selectedIndex], _items[_selectedIndex]);
            Panel.SetZIndex(_sectors[_selectedIndex], 0);
        }

        _selectedIndex = index;
        AnimateScale(_scales[index], SelectedScale);
        HighlightBorder(_borders[index]);
        Panel.SetZIndex(_sectors[index], 10);

        UpdateCenterBadge(_items[index], child: null);

        // 选中合并组立即展开子轮盘（无需再按一次 Tab），首个子项直接选中
        if (_items[index].IsGroup && _expandedIndex != index)
            ExpandChildWheel(index, 0);

        SelectionChanged?.Invoke();
    }

    /// <summary>后台刷新重建后恢复子轮盘选中位置（组已展开时）。</summary>
    public void RestoreChildSelection(int childIndex)
    {
        if (_expandedIndex >= 0 && childIndex > 0)
            SelectChild(childIndex);
    }

    /// <summary>轮盘打开期间静默重建项（后台窗口列表刷新后调用，选中由调用方恢复）。</summary>
    public void RebuildItems(IReadOnlyList<WheelItem> items)
    {
        if (!IsOpen || items.Count == 0)
            return;
        _items = items;
        BuildWheel();
    }

    // ---------- 级联子轮盘 ----------

    private void ExpandChildWheel(int groupIndex, int startChild)
    {
        if (groupIndex < 0 || groupIndex >= _items.Count || !_items[groupIndex].IsGroup)
            return;
        if (_expandedIndex == groupIndex)
            return;
        CollapseChildWheel();
        _expandedIndex = groupIndex;
        _childIndex = -1;

        var group = _items[groupIndex];
        int m = group.Windows.Count;
        for (int j = 0; j < m; j++)
        {
            var childItem = new WheelItem { Windows = new List<AppWindow> { group.Windows[j] } };
            AddSector(childItem, j, m, _ringRadius + ChildRadiusOffset, ChildSize, isChild: true, childIndex: j);
        }

        // 组扇区保持高亮，视觉中心移到子轮盘
        SelectChild(Math.Clamp(startChild, 0, m - 1));
    }

    private void SelectChild(int childIndex)
    {
        if (_expandedIndex < 0 || _expandedIndex >= _items.Count)
            return;
        var group = _items[_expandedIndex];
        if (childIndex < 0 || childIndex >= group.Windows.Count)
            return;
        if (childIndex == _childIndex)
            return;

        if (_childIndex >= 0 && _childIndex < _childBorders.Count)
        {
            AnimateScale(_childScales[_childIndex], 1.0);
            _childBorders[_childIndex].Background = SectorBackground;
            _childBorders[_childIndex].BorderBrush = SectorBorder;
            Panel.SetZIndex(_childSectors[_childIndex], 0);
        }

        _childIndex = childIndex;
        AnimateScale(_childScales[childIndex], SelectedScale);
        HighlightBorder(_childBorders[childIndex]);
        Panel.SetZIndex(_childSectors[childIndex], 20);

        UpdateCenterBadge(group, child: group.Windows[childIndex]);
        SelectionChanged?.Invoke();
    }

    private void CollapseChildWheel()
    {
        if (_expandedIndex < 0)
            return;
        foreach (var sector in _childSectors)
            WheelCanvas.Children.Remove(sector);
        _childSectors.Clear();
        _childBorders.Clear();
        _childScales.Clear();
        _expandedIndex = -1;
        _childIndex = -1;
    }

    // ---------- 鼠标 ----------

    private void NotifyKeyboardNavigation()
    {
        _inputMode = InputMode.Keyboard;
        _mouseTravel = 0;
    }

    public void HandleMouseMove(int physicalX, int physicalY)
    {
        if (!IsOpen || !MouseHoverEnabled || _items.Count == 0)
            return;

        double x = physicalX / _dpiScale;
        double y = physicalY / _dpiScale;

        double dx = x - _lastMouseDip.X;
        double dy = y - _lastMouseDip.Y;
        _mouseTravel += Math.Sqrt(dx * dx + dy * dy);
        _lastMouseDip = new Point(x, y);

        if (_inputMode == InputMode.Keyboard && _mouseTravel < MouseTakeoverTravel)
            return;
        _inputMode = InputMode.Mouse;

        double cx = Left + Width / 2;
        double cy = Top + Height / 2;
        double rx = x - cx;
        double ry = y - cy;
        double distance = Math.Sqrt(rx * rx + ry * ry);
        if (distance < MouseMinRadius)
            return;

        double angle = Math.Atan2(ry, rx);

        // 分带命中：外环带 → 已展开组的子项；其余 → 顶层
        if (_expandedIndex >= 0 && Math.Abs(distance - (_ringRadius + ChildRadiusOffset)) <= ChildSize)
        {
            var group = _items[_expandedIndex];
            int m = group.Windows.Count;
            // 子扇区角度是相对组角度的偏移，命中时还原绝对角度比较
            double groupAngle = SectorAngle(_expandedIndex, _items.Count);
            int best = -1;
            double bestDot = double.MinValue;
            for (int j = 0; j < m; j++)
            {
                double a = groupAngle + ChildSectorAngle(j, m, _ringRadius + ChildRadiusOffset);
                double dot = Math.Cos(a) * Math.Cos(angle) + Math.Sin(a) * Math.Sin(angle);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = j;
                }
            }
            if (best >= 0)
                SelectChild(best);
            return;
        }

        int? hit = FindSectorByAngle(angle, _items.Count, _ringRadius);
        if (hit.HasValue)
            Select(hit.Value);
    }

    // ---------- 视觉 ----------

    private void UpdateCenterBadge(WheelItem item, AppWindow? child)
    {
        CenterProcess.Text = item.ProcessName;
        CenterTitle.Text = child != null
            ? child.DisplayTitle
            : item.IsGroup ? $"{item.Windows.Count} 个窗口" : item.DisplayTitle;
    }

    private static void HighlightBorder(Border border)
    {
        border.Background = SelectedBackground;
        border.BorderBrush = SelectedBorder;
    }

    private static void RestoreBorder(Border border, WheelItem item)
    {
        border.Background = item.IsPinned ? PinnedBackground : SectorBackground;
        border.BorderBrush = SectorBorder;
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