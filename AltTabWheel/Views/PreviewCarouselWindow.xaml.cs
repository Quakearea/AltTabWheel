using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AltTabWheel.Models;
using AltTabWheel.Services;

namespace AltTabWheel.Views;

/// <summary>
/// 窗口实时预览轮播（设计移植自 HalfEllipseCarousel，v0.3 起）。
///
/// 布局：竖直椭圆**左半边**弧（参考原版几何，cx = 0.72w，theta ∈ [-π/2, π/2]，
/// x = cx - a·cos(theta)，y = cy + b·sin(theta)，弧长均匀参数化）。
/// 选中卡左凸点在面板水平中部、垂直居中（与轮心同高水平线），窗口位置与轮盘对应。
/// z 层：中间卡最上层，±1 下一层，依次往下（WPF 罩层 Panel.ZIndex 按距离递降）。
/// 无限循环 Wrap（[-n/2,n/2] 最短路径）+ 指数阻尼动画（帧率无关，系数 7.5）。
///
/// 合成模型（实验证实，docs/d2_zorder.png）：
///  - 本窗口是非 layered 宿主（DWM 缩略图在 AllowsTransparency 窗口上不渲染）；
///  - DWM 缩略图渲染在客户区 WPF 内容**之下**，因此每张卡 = 下层的实时缩略图矩形
///    + 上层的 WPF 罩层（半透明黑 + 描边 + 圆角遮罩）；
///  - 规格表两个独立参数恰好分层映射：
///      卡片不透明度 → DWM_TNP_OPACITY（中心 100% → ±4 15% → ±5 0%）
///      黑色罩层     → 罩层 Border.Opacity（±1 20% → ±4 68%）
///  - 模糊半径（1.2~4.8px）：WPF BlurEffect 无法作用于下层缩略图，
///    需 CompositionBackdropBrush（v2 待办），v1 由罩层近似。
///  - 选中卡亮蓝高亮边框（ad<0.5，与轮盘选中扇区同色 #7AA5FF）。
///
/// 其他关键决策：
///  - 背景去黑块：Background=Transparent + DwmEnableBlurBehindWindow 毛玻璃；
///  - 停靠工作区左缘（面板右缘若伸到轮心会盖住左侧扇区，首版实测）；
///  - 缩略图矩形内缩 CardGap，罩层圆角才能完全遮住缩略图直角；
///  - 与合并任务栏兼容：轮播目标 = 当前激活目标窗口（含组内子窗口），
///    组/子轮盘导航都经 SelectionChanged → SyncPreview 按 HWND 映射回展平列表；
///  - 缩略图注册按 HWND 缓存、跨轮盘开关复用；窗口列表刷新走 diff；
///  - 窗口数 > 21（MaxThumbs）时超出项不参与轮播（v1 截断）。
/// </summary>
public partial class PreviewCarouselWindow : Window
{
    private const int MaxThumbs = 21;       // 缩略图注册上限
    private const double HalfSpan = 4.5;    // u ∈ [-1,1] 覆盖 9 个可见位，±5 为预留位

    // ---- 布局参数（DIP，宿主 500×760 ≈ 屏幕 1/3 宽，参考原版比例） ----
    private const double HostW = 500, HostH = 760;
    private const double Cx = HostW * 0.72;         // 360：椭圆圆心偏右
    private const double Cy = HostH * 0.50;         // 380：选中卡垂直居中
    private const double RadiusA = HostW * 0.26;    // 130：水平半径
    private const double RadiusB = HostH * 0.385;   // 293：垂直半径
    private static readonly double BaseCardW = Math.Min(HostH * 0.42, HostW * 0.86);  // 319
    private const double CardRatio = 0.62;          // 横向长方形
    private const double CardGap = 3.0;             // 缩略图内缩（罩层圆角遮住直角 + 卡片分隔）
    private const double CornerR = 16;              // 罩层圆角

    // 停靠参数：预览面板固定在轮盘工作区左缘、垂直居中，
    // 右半边弧顶（选中大卡）朝右指向轮盘，与左侧扇区间留 MarginFromEdge
    private const double MarginFromEdge = 40;

    private sealed class ThumbEntry
    {
        public required IntPtr Handle;          // 源窗口 HWND
        public IntPtr ThumbnailId;              // DWM 缩略图 ID（Zero = 注册失败）
        public NativeMethods.SIZE SourceSize;   // 源窗口尺寸（rcSource 裁剪用）
    }

    private readonly List<ThumbEntry> _entries = new();
    private readonly Dictionary<IntPtr, ThumbEntry> _byHandle = new();
    private readonly List<Border> _dims = new();    // 罩层池，与 _entries 一一对应

    private double _current;    // 连续位置（动画中小数）
    private double _target;
    private double _dpi = 1.0;
    private DateTime _lastFrame = DateTime.MinValue;

    // ---- 渲染循环按需启停（收敛后预览静止期间零渲染成本） ----
    private bool _rendering;        // 是否已订阅 CompositionTarget.Rendering
    private bool _needsFrame;       // 有未应用的布局变化（目标/列表/注册变化）
    private bool _settleLogged;     // 每次 Open 记一次 settled 日志（调试用）

    // ---- 每帧 brush 零分配：alpha 量化成 256 级共享冻结画刷 ----
    private static readonly System.Windows.Media.SolidColorBrush[] DimBrushes = BuildAlphaBrushes(0x00, 0x00, 0x00);
    private static readonly System.Windows.Media.SolidColorBrush[] WhiteBorderBrushes = BuildAlphaBrushes(0xFF, 0xFF, 0xFF);
    private static readonly System.Windows.Media.SolidColorBrush SelectedBorderBrush = MakeFrozenBrush(0xFF, 0x7A, 0xA5, 0xFF);

    private static System.Windows.Media.SolidColorBrush MakeFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.SolidColorBrush[] BuildAlphaBrushes(byte r, byte g, byte b)
    {
        var brushes = new System.Windows.Media.SolidColorBrush[256];
        for (int a = 0; a < 256; a++)
            brushes[a] = MakeFrozenBrush((byte)a, r, g, b);
        return brushes;
    }

    // ---- 每帧对象池（UpdateLayoutFrame 零分配） ----
    private FrameLayout[] _frames = Array.Empty<FrameLayout>();
    private int[] _order = Array.Empty<int>();          // 帧内按 Ad 升序的帧索引
    private double[] _orderKeys = Array.Empty<double>(); // Array.Sort 键（= Ad）
    private readonly List<FrameLayout> _accepted = new();
    private readonly List<(double X, double Y, double W, double H)> _visibleCards = new();

    public bool IsOpen { get; private set; }

    /// <summary>请求至少一帧布局更新；渲染循环未运行则重新订阅。</summary>
    private void EnsureRendering()
    {
        _needsFrame = true;
        if (!_rendering)
        {
            CompositionTarget.Rendering += OnRender;
            _rendering = true;
        }
    }

    private void StopRendering()
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRender;
            _rendering = false;
        }
    }

    public PreviewCarouselWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // 被动覆盖层：不抢焦点、不进 Alt+Tab 列表
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
        // Win11 圆角 + 深色框（非透明窗口的圆角只能由 DWM 提供）
        int one = 1, round = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref one, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        // 去黑背景的正解不是窗口透明（WPF 非 layered 交换链不保留 alpha，
        // BlurBehind/Acrylic/Opacity 全部实测无效），而是 SetWindowRgn 把窗口
        // 裁剪成卡片轮廓集合 —— 间隙与空白处没有窗口，直接露出轮盘遮罩。
    }

    /// <summary>显示轮播宿主（工作区左缘、垂直居中）并对窗口列表注册缩略图。</summary>
    public void Open(IReadOnlyList<AppWindow> windows, NativeMethods.RECT workArea, double dpiScale)
    {
        _dpi = dpiScale > 0 ? dpiScale : 1.0;
        Width = HostW;
        Height = HostH;
        // 停靠工作区左缘；垂直居中（DIP = 物理 px / dpi）
        double workW = workArea.Width / _dpi;
        double workH = workArea.Height / _dpi;
        Left = workArea.Left / _dpi + Math.Min(MarginFromEdge, Math.Max(0, workW / 2 - HostW - 200));
        Top = workArea.Top / _dpi + Math.Max(0, (workH - HostH) / 2);

        EnsureArcTable();
        Log.Write($"preview open: host=({Left:F0},{Top:F0}) {HostW}x{HostH} dpi={_dpi:F2} wins={windows.Count}");
        IsOpen = true;
        if (!IsVisible)
        {
            Show();
            var hwnd = new WindowInteropHelper(this).Handle;
            SyncEntries(windows, hwnd);
        }

        _settleLogged = false;
        EnsureRendering();
    }

    /// <summary>关闭预览（窗口隐藏、缩略图隐藏，注册保留供下次复用）。</summary>
    public void ClosePreview()
    {
        if (!IsOpen)
            return;
        IsOpen = false;
        StopRendering();
        SetAllInvisible();
        NativeMethods.SetWindowRgn(new WindowInteropHelper(this).Handle, IntPtr.Zero, false);
        Hide();
    }

    /// <summary>窗口列表刷新后的差异同步（新增注册、消失注销、罩层池重建）。</summary>
    public void Refresh(IReadOnlyList<AppWindow> windows)
    {
        if (!IsVisible)
            return;
        var hwnd = new WindowInteropHelper(this).Handle;
        SyncEntries(windows, hwnd);
    }

    /// <summary>
    /// 选中项变化：按 HWND 定位条目（调用方不再关心轮播内部顺序），
    /// 轮播目标移到该窗口（无限环绕取最短路径）。
    /// 返回 false = 该窗口没有条目（超 MaxThumbs 截断），调用方决定兜底。
    /// </summary>
    public bool SetTarget(IntPtr handle)
    {
        if (!_byHandle.TryGetValue(handle, out var entry))
            return false;
        int index = _entries.IndexOf(entry);
        _target = _current + Wrap(index - _current);
        if (_lastFrame == DateTime.MinValue)
        {
            // 首次定位：从下方滑入的入场效果（参考设计 +4.2）
            _current = _target + 4.2;
            _lastFrame = DateTime.UtcNow;
        }
        EnsureRendering();
        return true;
    }

    // ---------- 缩略图注册 ----------

    private void SyncEntries(IReadOnlyList<AppWindow> windows, IntPtr hostHwnd)
    {
        // 关键不变量：_entries 顺序 == 传入列表顺序（即轮盘展平序）。
        // 旧实现只追加不重排，注册缓存跨呼出复用后顺序滞留在首次注册序，
        // 而调用方按当前列表序定位 → z-order 一变就高亮错卡。
        // 重排只动条目对象引用，DWM 缩略图注册（按 HWND）原样保留。
        var alive = new HashSet<IntPtr>();
        var next = new List<ThumbEntry>(Math.Min(windows.Count, MaxThumbs));
        foreach (var window in windows)
        {
            alive.Add(window.Handle);
            if (next.Count >= MaxThumbs)
                continue;   // 超上限截断（v1）
            if (_byHandle.TryGetValue(window.Handle, out var cached))
            {
                next.Add(cached);
                continue;
            }
            var entry = new ThumbEntry { Handle = window.Handle };
            if (NativeMethods.DwmRegisterThumbnail(hostHwnd, window.Handle, out IntPtr thumbId) == 0
                && thumbId != IntPtr.Zero)
            {
                entry.ThumbnailId = thumbId;
                entry.SourceSize = QuerySourceSize(thumbId);
            }
            next.Add(entry);
            _byHandle[window.Handle] = entry;
        }

        // 注销已关闭窗口（从旧列表里摘除并释放缩略图）
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (alive.Contains(_entries[i].Handle))
                continue;
            Unregister(_entries[i]);
            _byHandle.Remove(_entries[i].Handle);
            _entries.RemoveAt(i);
        }

        // 序列比较：增/删/换位都算变化；完全不变则连罩层池重建都跳过
        bool changed = next.Count != _entries.Count;
        if (!changed)
        {
            for (int i = 0; i < next.Count; i++)
            {
                if (!ReferenceEquals(next[i], _entries[i]))
                {
                    changed = true;
                    break;
                }
            }
        }
        if (!changed)
            return;

        _entries.Clear();
        _entries.AddRange(next);
        RebuildOverlay();
        if (IsOpen)
            EnsureRendering();
    }

    /// <summary>罩层池与 _entries 对齐（数量变化时全量重建，成本 n ≤ 21 可忽略）。</summary>
    private void RebuildOverlay()
    {
        Overlay.Children.Clear();
        _dims.Clear();
        for (int i = 0; i < _entries.Count; i++)
        {
            var dim = new Border
            {
                CornerRadius = new CornerRadius(CornerR),
                Background = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00)),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
            };
            _dims.Add(dim);
            Overlay.Children.Add(dim);
            Panel.SetZIndex(dim, 10000 - i);   // 初始按索引，每帧再按距离精调
        }
    }

    private static NativeMethods.SIZE QuerySourceSize(IntPtr thumbId)
    {
        var size = new NativeMethods.SIZE { cx = 0, cy = 0 };
        IntPtr buffer = Marshal.AllocCoTaskMem(16);
        try
        {
            if (NativeMethods.DwmQueryThumbnailSourceSize(thumbId, buffer) == 0)
            {
                // 部分平台按 64 位写入，小端下低 32 位即值
                size.cx = Marshal.ReadInt32(buffer);
                size.cy = Marshal.ReadInt32(buffer, 4);
            }
        }
        catch
        {
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
        return size;
    }

    private void Unregister(ThumbEntry entry)
    {
        if (entry.ThumbnailId != IntPtr.Zero)
        {
            try { NativeMethods.DwmUnregisterThumbnail(entry.ThumbnailId); } catch { }
            entry.ThumbnailId = IntPtr.Zero;
        }
    }

    private void SetAllInvisible()
    {
        var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = NativeMethods.DWM_TNP_VISIBLE | NativeMethods.DWM_TNP_OPACITY,
            fVisible = false,
            opacity = 0,
        };
        foreach (var entry in _entries)
        {
            if (entry.ThumbnailId != IntPtr.Zero)
                NativeMethods.DwmUpdateThumbnailProperties(entry.ThumbnailId, ref props);
        }
    }

    public void DisposeAllThumbnails()
    {
        StopRendering();
        foreach (var entry in _entries)
            Unregister(entry);
        _entries.Clear();
        _byHandle.Clear();
    }

    // ---------- 动画 ----------

    private void OnRender(object? sender, EventArgs e)
    {
        if (!IsOpen)
        {
            StopRendering();
            return;
        }
        var now = DateTime.UtcNow;
        if (_lastFrame == DateTime.MinValue) _lastFrame = now;
        double dt = Math.Min(0.05, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;

        // 指数阻尼（帧率无关）逼近目标
        if (_current != _target)
        {
            _current = _target + (_current - _target) * Math.Exp(-dt * 7.5);
            if (Math.Abs(_current - _target) < 0.0005) _current = _target;
        }

        bool settled = _current == _target;
        if (settled && !_needsFrame)
        {
            // 收敛即停：预览静止期间不再每帧全量计算 + 逐卡 DwmUpdateThumbnailProperties。
            // DWM 缩略图内容由 DWM 实时自绘，与我们的渲染循环无关。
            StopRendering();
            if (!_settleLogged)
            {
                _settleLogged = true;
                Log.Write("preview render settled (loop stopped)");
            }
            return;
        }

        _needsFrame = false;
        // 收敛帧强制刷 SetWindowRgn（动画期间 80ms 节流，最后一帧必须拿到精确轮廓）
        UpdateLayoutFrame(forceRegion: settled);
    }

    private DateTime _lastRgnUpdate = DateTime.MinValue;

    /// <summary>
    /// 把窗口区域裁剪为可见卡的圆角矩形并集（物理 px，窗口坐标）。
    /// 节流 ~80ms：SetWindowRgn 会触发 DWM 重裁剪，导航动画期间不必每帧都付这个成本。
    /// </summary>
    private void UpdateWindowRegion(List<(double X, double Y, double W, double H)> cards, bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastRgnUpdate).TotalMilliseconds < 80)
            return;
        _lastRgnUpdate = now;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (cards.Count == 0)
        {
            NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, false);
            return;
        }
        IntPtr union = NativeMethods.CreateRoundRectRgn(0, 0, 0, 0, 1, 1);
        foreach (var (x, y, w, h) in cards)
        {
            int pad = (int)Math.Ceiling(2 * _dpi) + 1;   // 罩层 2 DIP 高亮边框须在窗口内
            IntPtr card = NativeMethods.CreateRoundRectRgn(
                (int)Math.Floor(x * _dpi) - pad, (int)Math.Floor(y * _dpi) - pad,
                (int)Math.Ceiling((x + w) * _dpi) + pad, (int)Math.Ceiling((y + h) * _dpi) + pad,
                (int)(CornerR * 2 * _dpi), (int)(CornerR * 2 * _dpi));
            NativeMethods.CombineRgn(union, union, card, NativeMethods.RGN_OR);
            NativeMethods.DeleteObject(card);
        }
        NativeMethods.SetWindowRgn(hwnd, union, false);
    }

    private sealed class FrameLayout
    {
        // 池化复用（每帧重写全部字段），故不用 required/init。
        public int Index;
        public ThumbEntry Entry = null!;
        public Border Dim = null!;
        public double D, Ad, U, Au, Scale, Cw, Ch, X, Y, Opacity, DimOpacity;
        public bool Visible;
        // 确定性 z 层：缩略图矩形裁掉与更近卡的垂直重叠带（被遮挡的视觉效果，
        // 不依赖 DWM 缩略图叠放顺序——实测其既非更新序也非注册序）
        public double ThumbTop, ThumbBottom;
        public bool ThumbVisible = true;
    }

    private void UpdateLayoutFrame(bool forceRegion = false)
    {
        int n = _entries.Count;
        if (n == 0)
            return;

        // 池扩容 + 逐槽就位（全帧零分配：帧对象/排序数组/列表全部复用）
        if (_frames.Length < n)
        {
            _frames = new FrameLayout[n];
            _order = new int[n];
            _orderKeys = new double[n];
        }

        // 第一遍：纯几何计算（无副作用）
        for (int i = 0; i < n; i++)
        {
            var frame = _frames[i] ??= new FrameLayout();
            frame.Index = i;
            frame.Entry = _entries[i];
            frame.Dim = _dims[i];

            double d = Wrap(i - _current);
            double ad = Math.Abs(d);
            double u = d / HalfSpan;
            double au = Math.Abs(u);

            // ---- 距离分层规格（见类注释表） ----
            double opacity = ad <= 1
                ? 1 - 0.4 * ad
                : 0.6 * Math.Max(0, (5 - ad) / 4);
            double dimOpacity = ad <= 1
                ? 0.2 * ad
                : Math.Min(0.85, 0.2 + 0.16 * (ad - 1));
            // 模糊半径 min(8, 1.2·ad)：v1 无手段作用于下层缩略图（待 CompositionBackdropBrush）

            frame.D = d; frame.Ad = ad; frame.U = u; frame.Au = au;
            frame.Opacity = opacity;
            frame.DimOpacity = dimOpacity;
            frame.Visible = opacity > 0.02;
            _order[i] = i;
            _orderKeys[i] = ad;

            if (!frame.Visible)
                continue;

            double theta = ThetaForU(u);

            // 尺度：中心 1，边缘约 0.55，预留位继续收缩
            double shrink = Math.Pow(Math.Min(au, 1.0), 1.25);
            double scale = 1 - 0.45 * shrink;
            if (au > 1) scale = Math.Max(0.30, scale - (au - 1) * 1.2);

            double cw = BaseCardW * scale;
            double ch = cw * CardRatio;

            // 竖直椭圆左半边（参考原版）：左凸点（theta=0）为选中卡，
            // 位于面板水平中部、垂直居中 —— 与轮心同高水平线，位置与轮盘对应
            double x = Cx - RadiusA * Math.Cos(theta) - cw / 2;
            double y = Cy + RadiusB * Math.Sin(theta) - ch / 2;

            frame.Scale = scale; frame.Cw = cw; frame.Ch = ch; frame.X = x; frame.Y = y;
        }

        // 距离排序：升序键 + 索引数组一次排序，接受 pass 正序、应用 pass 逆序复用。
        // （Array.Sort 对相同 Ad 不保稳定序，但等距卡互为上下对称、互不遮挡，无影响）
        Array.Sort(_orderKeys, _order, 0, n);

        // 接受 pass：按距离从近到远，远卡缩略图裁掉与已接受近卡的垂直重叠带。
        // 重叠区于是只显示近卡内容 —— "中间最上，±1 下一层，依次往下" 由几何保证，
        // 与 DWM 缩略图之间的叠放顺序无关（实测该顺序既非更新序也非注册序）。
        _accepted.Clear();
        for (int k = 0; k < n; k++)
        {
            var frame = _frames[_order[k]];
            if (!frame.Visible)
                continue;
            double top = frame.Y + CardGap;
            double bottom = frame.Y + frame.Ch - CardGap;
            double cx = frame.X + frame.Cw / 2;
            foreach (var near in _accepted)
            {
                // 水平相交才有垂直遮挡问题
                double nx = near.X + near.Cw / 2;
                if (Math.Abs(cx - nx) >= (frame.Cw + near.Cw) / 2 * 0.75)
                    continue;
                if (near.Y + near.Ch / 2 < frame.Y + frame.Ch / 2)
                    top = Math.Max(top, near.Y + near.Ch - CardGap);     // 近卡在上方：裁远卡顶部
                else
                    bottom = Math.Min(bottom, near.Y + CardGap);          // 近卡在下方：裁远卡底部
            }
            frame.ThumbTop = top;
            frame.ThumbBottom = bottom;
            frame.ThumbVisible = bottom - top >= (frame.Ch - 2 * CardGap) * 0.15;
            _accepted.Add(frame);
        }

        // 应用 pass：按距离从远到近（中心卡最后更新，WPF 罩层 ZIndex 同步递降）
        _visibleCards.Clear();
        double dpi = _dpi;

        for (int k = n - 1; k >= 0; k--)
        {
            var frame = _frames[_order[k]];
            if (!frame.Visible)
            {
                HideCard(frame.Entry, frame.Dim);
                continue;
            }

            var entry = frame.Entry;
            var dim = frame.Dim;
            double cw = frame.Cw, ch = frame.Ch, x = frame.X, y = frame.Y;
            double ad = frame.Ad, au = frame.Au;

            // ---- DWM 缩略图矩形（物理 px，已按近卡裁剪重叠带） ----
            if (entry.ThumbnailId != IntPtr.Zero)
            {
                double left = x + CardGap, top = frame.ThumbTop;
                double right = x + cw - CardGap, bottom = frame.ThumbBottom;
                if (!frame.ThumbVisible)
                    top = bottom;   // 退化矩形 = 隐藏
                var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION
                            | NativeMethods.DWM_TNP_VISIBLE
                            | NativeMethods.DWM_TNP_OPACITY
                            | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
                    fSourceClientAreaOnly = false,
                    fVisible = true,
                    rcDestination = new NativeMethods.RECT
                    {
                        Left = (int)Math.Round(left * dpi),
                        Top = (int)Math.Round(top * dpi),
                        Right = (int)Math.Round(right * dpi),
                        Bottom = (int)Math.Round(bottom * dpi),
                    },
                    rcSource = CropSource(entry.SourceSize, right - left, bottom - top),
                    opacity = (byte)Math.Round(255 * frame.Opacity),
                };
                NativeMethods.DwmUpdateThumbnailProperties(entry.ThumbnailId, ref props);
            }

            _visibleCards.Add((x, y, cw, ch));

            // ---- WPF 罩层（DIP，盖在缩略图之上） ----
            // 透明度只作用于 Background（alpha），Border.Opacity 恒 1 ——
            // 否则选中卡 dim≈0 会把亮蓝边框一起透明掉
            dim.Visibility = Visibility.Visible;
            dim.Width = cw;
            dim.Height = ch;
            Canvas.SetLeft(dim, x);
            Canvas.SetTop(dim, y);
            dim.Opacity = 1;
            // 共享冻结画刷（alpha 量化 256 级）：替代每帧 new SolidColorBrush ×2
            dim.Background = DimBrushes[(byte)Math.Round(255 * frame.DimOpacity)];
            if (ad < 0.5)
            {
                // 选中卡：亮蓝高亮边框（与轮盘选中扇区同色），罩层最上层
                dim.BorderBrush = SelectedBorderBrush;
                dim.BorderThickness = new Thickness(2);
                Panel.SetZIndex(dim, 20000);
            }
            else
            {
                // 远处卡：白色描边随距离渐淡；z 层按距离递降 ——
                // 中间卡最上层，±1 在下一层，依次往下（罩层间互相遮盖形成层次）
                byte borderAlpha = (byte)Math.Round(70 * Math.Max(0, 1 - au));
                dim.BorderBrush = WhiteBorderBrushes[borderAlpha];
                dim.BorderThickness = new Thickness(1);
                Panel.SetZIndex(dim, 10000 - (int)(ad * 1000));
            }
        }

        UpdateWindowRegion(_visibleCards, forceRegion);
    }

    private static void HideCard(ThumbEntry entry, Border dim)
    {
        if (entry.ThumbnailId != IntPtr.Zero)
        {
            var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_VISIBLE | NativeMethods.DWM_TNP_OPACITY,
                fVisible = false,
                opacity = 0,
            };
            NativeMethods.DwmUpdateThumbnailProperties(entry.ThumbnailId, ref props);
        }
        dim.Visibility = Visibility.Collapsed;
    }

    /// <summary>源矩形按目标宽高比居中裁剪（UniformToFill），保持窗口比例不变形。</summary>
    private static NativeMethods.RECT CropSource(NativeMethods.SIZE src, double destW, double destH)
    {
        if (src.cx <= 0 || src.cy <= 0 || destW <= 0 || destH <= 0)
            return new NativeMethods.RECT { Left = 0, Top = 0, Right = src.cx, Bottom = src.cy };

        double srcAspect = (double)src.cx / src.cy;
        double destAspect = destW / destH;
        int x0 = 0, y0 = 0, w = src.cx, h = src.cy;
        if (srcAspect > destAspect)
        {
            w = (int)Math.Round(src.cy * destAspect);
            x0 = (src.cx - w) / 2;
        }
        else
        {
            h = (int)Math.Round(src.cx / destAspect);
            y0 = (src.cy - h) / 2;
        }
        return new NativeMethods.RECT { Left = x0, Top = y0, Right = x0 + w, Bottom = y0 + h };
    }

    // ---------- 几何工具 ----------

    /// <summary>把索引差环绕到 [-n/2, n/2] 取最短路径（无限循环轮播）。</summary>
    private double Wrap(double d)
    {
        int n = _entries.Count;
        if (n <= 0) return d;
        d %= n;
        if (d > n / 2.0) d -= n;
        if (d < -n / 2.0) d += n;
        return d;
    }

    // ---- 弧长均匀参数化（竖直椭圆左半边，theta ∈ [-π/2, π/2]） ----
    // 直接按 theta 均匀分布会让卡片在弧顶/弧底堆叠，
    // 按弧长均匀取样使相邻卡片沿弧线的视觉间距一致。
    private const int ArcSamples = 360;
    private double[] _arcCum = Array.Empty<double>();

    private void EnsureArcTable()
    {
        if (_arcCum.Length == ArcSamples + 1)
            return;
        var cum = new double[ArcSamples + 1];
        double prev = 0;
        for (int i = 1; i <= ArcSamples; i++)
        {
            double t = -Math.PI / 2 + Math.PI * i / ArcSamples;
            double speed = Math.Sqrt(RadiusA * RadiusA * Math.Sin(t) * Math.Sin(t)
                                   + RadiusB * RadiusB * Math.Cos(t) * Math.Cos(t));
            cum[i] = cum[i - 1] + (speed + prev) / 2;
            prev = speed;
        }
        _arcCum = cum;
    }

    private double ThetaForU(double u)
    {
        double target = (Math.Clamp(u, -1, 1) + 1) / 2 * _arcCum[ArcSamples];
        int lo = 0, hi = ArcSamples;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_arcCum[mid] < target) lo = mid + 1; else hi = mid;
        }
        int i = Math.Max(1, lo);
        double seg = _arcCum[i] - _arcCum[i - 1];
        double f = seg > 0 ? (target - _arcCum[i - 1]) / seg : 0;
        double t = (i - 1 + f) / ArcSamples;
        // 超出 [-1,1] 的预留位按斜率外推（参考设计同款）
        if (u < -1) t += (u + 1) / ArcSamples * 40;
        if (u > 1) t += (u - 1) / ArcSamples * 40;
        return -Math.PI / 2 + Math.PI * t;
    }
}
