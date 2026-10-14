using System.IO;

namespace AltTabWheel.Services;

/// <summary>Minimal append-only trace file for v1 debugging (%TEMP%\alttabwheel.log).</summary>
public static class Log
{
    private static readonly string LogPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "alttabwheel.log");

    public static void Write(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the switcher.
        }
    }
}
