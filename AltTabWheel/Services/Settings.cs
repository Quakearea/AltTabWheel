using System.IO;
using System.Text.Json;

namespace AltTabWheel.Services;

/// <summary>
/// 用户设置。JSON 持久化到 %AppData%\AltTabWheel\settings.json。
/// 所有开关默认关闭/保守值 —— 新行为必须显式开启，避免破坏既有体验。
/// </summary>
public sealed class AppSettings
{
    public bool MouseHoverEnabled { get; set; } = true;

    /// <summary>鼠标从键盘模式接管选中所需的累计移动距离（物理 px）。</summary>
    public double MouseTakeoverTravelPx { get; set; } = 24;

    /// <summary>鼠标命中选中的最小半径（DIP），中心区域内移动不触发。</summary>
    public double MouseMinRadiusDip { get; set; } = 70;

    /// <summary>合并任务栏：同进程窗口合并为一个扇区，选中后展开级联子轮盘。</summary>
    public bool MergeTaskbar { get; set; } = false;

    /// <summary>窗口实时预览：轮盘下方半椭圆轮播图（DWM 缩略图实时合成）。</summary>
    public bool ShowWindowPreview { get; set; } = true;

    /// <summary>固定应用（进程名或 exe 绝对路径），无窗口时也显示在轮盘上。</summary>
    public List<string> PinnedApps { get; set; } = new();

    // ---- 持久化 ----

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AltTabWheel", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            string path = FilePath;
            Log.Write($"settings path: {path}, exists={File.Exists(path)}");
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                Log.Write($"settings json: {json}");
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    Log.Write($"settings loaded: merge={settings.MergeTaskbar}, pinned={settings.PinnedApps.Count}");
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"settings load failed: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存失败静默（下次启动用旧值/默认值）。
        }
    }
}
