using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AltTabWheel.Services;

/// <summary>
/// Best-effort window icon extraction. Order:
///   1. WM_GETICON (ICON_BIG) — what Explorer shows in Alt+Tab.
///   2. WM_GETICON (ICON_SMALL2) — fallback for apps that only set a small icon.
///   3. First large icon of the owning executable via ExtractIconEx.
/// Handles returned by WM_GETICON are borrowed (never destroyed). Icons created
/// by ExtractIconEx are owned and destroyed after conversion.
/// </summary>
public static class IconService
{
    private const uint IconLoadTimeoutMs = 200;

    public static ImageSource? GetWindowIcon(IntPtr hwnd, uint processId)
    {
        try
        {
            IntPtr hIcon = QueryIcon(hwnd, NativeMethods.ICON_BIG);
            if (hIcon == IntPtr.Zero)
                hIcon = QueryIcon(hwnd, NativeMethods.ICON_SMALL2);

            if (hIcon != IntPtr.Zero)
                return Convert(hIcon, ownsHandle: false);

            string? exePath = GetProcessPath(processId);
            if (exePath != null)
            {
                int extracted = NativeMethods.ExtractIconExW(exePath, 0, out IntPtr big, out _, 1);
                if (extracted > 0 && big != IntPtr.Zero)
                    return Convert(big, ownsHandle: true);
            }
        }
        catch
        {
            // Icon extraction must never break the switcher.
        }
        return null;
    }

    private static IntPtr QueryIcon(IntPtr hwnd, int iconKind)
    {
        IntPtr result = NativeMethods.SendMessageTimeoutW(
            hwnd, NativeMethods.WM_GETICON, new IntPtr(iconKind), IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, IconLoadTimeoutMs, out IntPtr hIcon)
            ? hIcon
            : IntPtr.Zero;
        return result;
    }

    private static string? GetProcessPath(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.MainModule?.FileName;
        }
        catch
        {
            // 32/64-bit boundary or exited process — no icon available.
            return null;
        }
    }

    private static ImageSource Convert(IntPtr hIcon, bool ownsHandle)
    {
        // Clone through the bitmap so the source is not tied to the native handle's lifetime.
        using var icon = ownsHandle
            ? Icon.FromHandle(hIcon)
            : (Icon)Icon.FromHandle(hIcon).Clone();
        BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        if (ownsHandle)
            NativeMethods.DestroyIcon(hIcon);
        return source;
    }


}
