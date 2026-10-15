using System.IO;
using System.Text;
using AltTabWheel.Models;

namespace AltTabWheel.Services;

/// <summary>
/// Enumerates Alt+Tab-eligible windows in Z-order (topmost-of-screen first),
/// which approximates the system's MRU switching order.
///
/// Filtering follows Raymond Chen's IsAltTabWindow rules plus DWM cloaking for
/// UWP/immersive windows that are suspended or on other virtual desktops:
///   - must be visible, uncloaked, and carry a non-empty title
///   - WS_EX_TOOLWINDOW excluded unless WS_EX_APPWINDOW is also set
///   - owned windows excluded (only root owners are switchable)
///   - topmost windows are collected in a side bucket and appended last, so
///     always-on-top utilities (PiP, notifications) never displace MRU windows
///
/// The self process and known shell windows are excluded explicitly.
/// </summary>
public sealed class WindowService
{
    private static readonly HashSet<string> ExcludedClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "Button",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "DV2ControlHost",
        "SysShadow",
    };

    private readonly Dictionary<IntPtr, AppWindow> _instanceCache = new();
    private readonly uint _selfProcessId;

    public WindowService()
    {
        _selfProcessId = (uint)Environment.ProcessId;
    }

    public IReadOnlyList<AppWindow> GetWindows()
    {
        var windows = new List<AppWindow>();
        var topmost = new List<AppWindow>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (TryCreateWindow(hWnd, out AppWindow? window) && window is not null)
                (window.IsTopmost ? topmost : windows).Add(window);
            return true;
        }, IntPtr.Zero);

        // Drop stale cache entries for closed windows.
        var alive = new HashSet<IntPtr>(windows.Select(w => w.Handle));
        alive.UnionWith(topmost.Select(w => w.Handle));
        foreach (var key in _instanceCache.Keys.Where(k => !alive.Contains(k)).ToList())
            _instanceCache.Remove(key);

        windows.AddRange(topmost);
        return windows;
    }

    private bool TryCreateWindow(IntPtr hWnd, out AppWindow? window)
    {
        window = null;

        if (!NativeMethods.IsWindowVisible(hWnd))
            return false;

        // DWM cloaked: UWP suspended / other virtual desktop / start menu area.
        if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0)
            return false;

        var exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();

        bool toolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        bool appWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
        if (toolWindow && !appWindow)
            return false;

        // Only root owners are Alt+Tab candidates.
        if (NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
            return false;
        if (NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOTOWNER) != hWnd)
            return false;

        uint processId = 0;
        NativeMethods.GetWindowThreadProcessId(hWnd, out processId);
        if (processId == 0 || processId == _selfProcessId)
            return false;

        var title = ReadTitle(hWnd);
        if (title.Length == 0)
            return false;

        var className = ReadClassName(hWnd);
        if (className.Length > 0 && ExcludedClassNames.Contains(className))
            return false;

        bool isTopmost = (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;
        string processName = QueryProcessName(processId);

        // Reuse the cached instance (and its extracted icon) while the HWND stays alive.
        // Iconic state is re-snapshotted on activation, so staleness here is harmless.
        if (_instanceCache.TryGetValue(hWnd, out AppWindow? cached))
        {
            cached.Title = title;
            window = cached;
            return true;
        }

        var created = new AppWindow
        {
            Handle = hWnd,
            Title = title,
            ProcessName = processName,
            IsTopmost = isTopmost,
            IsIconic = NativeMethods.IsIconic(hWnd),
        };
        created.Icon ??= IconService.GetWindowIcon(hWnd, processId);
        _instanceCache[hWnd] = created;
        window = created;
        return true;
    }

    private static string ReadTitle(IntPtr hWnd)
    {
        // GetWindowTextW with nMaxCount=0 always returns 0 — query length first.
        // 首选 UIA 名称：由窗口的 UIA provider 自报，绕过本环境 win32k 标题层失真
        // （GetWindowTextW 对 Edge/微信等自绘窗口返回损坏或空数据），
        // 同时覆盖标题为空的窗口（显示真实窗口名而非进程名）。
        try
        {
            string? uiaName = System.Windows.Automation.AutomationElement.FromHandle(hWnd)?.Current.Name;
            if (!string.IsNullOrWhiteSpace(uiaName))
                return TitleCodec.Decode(uiaName.Trim());
        }
        catch
        {
            // UIA 不可用时走 Win32 兜底
        }

        int length = NativeMethods.GetWindowTextLengthW(hWnd);
        if (length <= 0)
            return string.Empty;
        // 缓冲给足（length 按 WCHAR 计，环境失真时实际字符可能更短，从宽不截断）
        var buffer = new char[Math.Min(length * 2 + 4, 1024)];
        int written = NativeMethods.GetWindowTextW(hWnd, buffer, buffer.Length);
        if (written <= 0)
            return string.Empty;
        // 环境层标题编码失真修复（交错零折叠 + 控制符清理）
        return TitleCodec.Decode(new string(buffer, 0, written));
    }

    private static string ReadClassName(IntPtr hWnd)
    {
        var builder = new StringBuilder(256);
        return NativeMethods.GetClassNameW(hWnd, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    /// <summary>
    /// 把窗口列表转换为轮盘项。开启合并任务栏时按进程名分组（保持 MRU 首个出现顺序）；
    /// 固定应用追加在末尾（已有窗口的固定项合并进对应组并打标）。
    /// </summary>
    public List<WheelItem> BuildItems(IReadOnlyList<AppWindow> windows, AppSettings settings)
    {
        var items = new List<WheelItem>();
        var byProcess = new Dictionary<string, WheelItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var window in windows)
        {
            if (settings.MergeTaskbar && byProcess.TryGetValue(window.ProcessName, out var existing))
            {
                ((List<AppWindow>)existing.Windows).Add(window);
                continue;
            }

            var item = new WheelItem
            {
                Windows = new List<AppWindow> { window },
                Icon = window.Icon,
            };
            items.Add(item);
            byProcess[window.ProcessName] = item;
        }

        // 固定项：窗口已存在的进程跳过（已在组内）；否则追加启动项
        foreach (var pinned in settings.PinnedApps)
        {
            string name = Path.GetFileNameWithoutExtension(pinned);
            if (byProcess.ContainsKey(name))
                continue;
            if (items.Any(i => string.Equals(i.ProcessName, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var pinnedWindow = FindPinnedWindow(windows, pinned);
            items.Add(new WheelItem
            {
                Windows = pinnedWindow != null ? new List<AppWindow> { pinnedWindow } : new List<AppWindow>(),
                IsPinned = true,
                LaunchPath = pinned,
                Icon = pinnedWindow?.Icon ?? IconService.GetFileIcon(pinned),
            });
        }

        return items;
    }

    private static AppWindow? FindPinnedWindow(IReadOnlyList<AppWindow> windows, string pinned)
    {
        // 支持两种写法：进程名 或 exe 绝对路径
        string name = Path.GetFileNameWithoutExtension(pinned);
        var match = windows.FirstOrDefault(w =>
            string.Equals(w.ProcessName, name, StringComparison.OrdinalIgnoreCase));
        return match;
    }

    private readonly Dictionary<uint, string> _processNameCache = new();

    private string QueryProcessName(uint processId)
    {
        if (_processNameCache.TryGetValue(processId, out string? cached))
            return cached;
        string name = "unknown";
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            name = process.ProcessName;
        }
        catch
        {
            // Process may exit mid-enumeration or be inaccessible.
        }
        _processNameCache[processId] = name;
        return name;
    }
}
