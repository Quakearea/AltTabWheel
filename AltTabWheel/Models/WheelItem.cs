using System.Windows.Media;

namespace AltTabWheel.Models;

/// <summary>
/// 轮盘顶层扇区项。三种形态：
///  - 单窗口（IsGroup=false, Windows.Count=1）
///  - 进程组（IsGroup=true，选中后展开级联子轮盘；参考"合并任务栏"）
///  - 固定项（IsPinned，无窗口时显示为半透明，确认时启动；参考"固定任务栏"）
/// </summary>
public sealed class WheelItem
{
    /// <summary>组内窗口（单窗口组即自身）。</summary>
    public required IReadOnlyList<AppWindow> Windows { get; init; }

    /// <summary>固定项：无可窗口时的启动路径（exe 绝对路径或进程名）。</summary>
    public string? LaunchPath { get; init; }

    public bool IsGroup => Windows.Count > 1;

    public bool IsPinned { get; init; }

    public AppWindow? Primary => Windows.Count > 0 ? Windows[0] : null;

    public string ProcessName =>
        Primary?.ProcessName
        ?? (LaunchPath != null ? System.IO.Path.GetFileNameWithoutExtension(LaunchPath) : "unknown");

    public string DisplayTitle =>
        Primary?.DisplayTitle
        ?? (IsPinned ? "未运行（点击启动）" : ProcessName);

    /// <summary>图标：组取首个窗口图标；固定项由调用方注入。</summary>
    public ImageSource? Icon { get; set; }

    /// <summary>激活目标：展开时由子索引决定；否则 Primary。</summary>
    public AppWindow? ActivateTarget(int childIndex) =>
        IsGroup && childIndex >= 0 && childIndex < Windows.Count
            ? Windows[childIndex]
            : Primary;
}
