using System.IO;
using System.Text;

namespace AltTabWheel.Services;

/// <summary>
/// Minimal append-only trace file for v1 debugging (%TEMP%\alttabwheel.log).
/// 常驻 StreamWriter + AutoFlush：AppendAllText 每行都要 open/ACL/write/close 一次文件，
/// 事件密集时（鼠标钩子装拆、导航）纯浪费。锁内写，任意线程可调用。
/// </summary>
public static class Log
{
    private static readonly string LogPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "alttabwheel.log");

    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                _writer ??= new StreamWriter(
                    new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                    Encoding.UTF8)
                { AutoFlush = true };
                _writer.Write($"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the switcher.
        }
    }
}
