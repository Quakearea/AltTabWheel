using System.Windows.Media;

namespace AltTabWheel.Models;

/// <summary>
/// One switchable top-level window as shown on the wheel.
/// Icons are extracted lazily by <see cref="Services.IconService"/> and cached
/// on the instance so refreshes reuse them (HWND-keyed reuse lives in WindowService).
/// </summary>
public sealed class AppWindow
{
    public required IntPtr Handle { get; init; }

    /// <summary>Window title shown on the wheel.</summary>
    public required string Title { get; set; }

    /// <summary>Short process name shown in the wheel center (e.g. "chrome").</summary>
    public required string ProcessName { get; init; }

    /// <summary>True when the window carries WS_EX_TOPMOST (rendered after MRU items).</summary>
    public required bool IsTopmost { get; init; }

    /// <summary>True when the window is minimized (needs SW_RESTORE on activation).</summary>
    public required bool IsIconic { get; init; }

    /// <summary>Icon image; null until IconService resolves it.</summary>
    public ImageSource? Icon { get; set; }

    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;
}
