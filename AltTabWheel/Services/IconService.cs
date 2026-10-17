using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using IShellItemImageFactory = AltTabWheel.NativeMethods.IShellItemImageFactory;

namespace AltTabWheel.Services;

/// <summary>
/// 窗口图标提取（高清版）。
///
/// 清晰度关键：旧版 CreateBitmapSourceFromHIcon 只取系统默认 32×32 图标，
/// 显示在 ~46px 区域（高 DPI 下更大）必然发虚。本版统一以 128×128 产出（2× 余量）：
///  1. 窗口自身图标 WM_GETICON → DrawIconEx 高质量双三次光栅化到 64×64；
///  2. 窗口类图标 GCLP_HICON → 同上；
///  3. exe 文件图标 → IShellItemImageFactory.GetImage(128, RESIZETOSTRETCH)，
///     由 Shell 提取 exe 内最大图标（可达 256px）平滑缩放，最清晰；
///  4. SHGetFileInfo / ExtractIconEx 最后兜底。
/// 图标来源：优先应用运行时设置的窗口图标，失败才用文件图标。
/// </summary>
public static class IconService
{
    private const uint IconLoadTimeoutMs = 200;
    // 2× 渲染：显示约 46-83 DIP（选中放大），高 DPI 屏下物理像素更高；
    // 64px 源会被上采样发虚，128px 源走 Fant 下采样后锐利
    private const int IconRasterSize = 128;

    public static ImageSource? GetWindowIcon(IntPtr hwnd, uint processId)
    {
        try
        {
            // 1) 窗口自身设置的大图标（应用运行时图标，与系统 Alt+Tab 一致）
            IntPtr hIcon = QueryIcon(hwnd, NativeMethods.ICON_BIG);
            if (hIcon == IntPtr.Zero)
                hIcon = QueryIcon(hwnd, NativeMethods.ICON_SMALL2);
            if (hIcon != IntPtr.Zero)
                return RasterizeIcon(hIcon, IconRasterSize);

            // 2) 窗口类注册表里的默认图标
            hIcon = NativeMethods.GetClassIcon(hwnd);
            if (hIcon != IntPtr.Zero)
                return RasterizeIcon(hIcon, IconRasterSize);

            // 3) exe 文件图标：Shell 高清渲染（256px 源平滑缩放到 128）
            string? exePath = GetProcessPath(processId);
            if (exePath != null)
            {
                var shellIcon = GetShellFileIcon(exePath, IconRasterSize);
                if (shellIcon != null)
                    return shellIcon;

                // 4) 经典兜底
                return FallbackFileIcon(exePath);
            }
        }
        catch
        {
            // 图标提取绝不能打断切换器
        }
        return null;
    }

    /// <summary>从文件路径提取图标（固定项/兜底用），同样走 Shell 高清渲染。</summary>
    public static ImageSource? GetFileIcon(string path)
    {
        try
        {
            string fullPath = path;
            if (!System.IO.Path.IsPathRooted(fullPath))
            {
                // 进程名形式：先找运行中的进程，再按常见目录解析 exe
                fullPath = System.Diagnostics.Process.GetProcessesByName(
                        System.IO.Path.GetFileNameWithoutExtension(path))
                    .Select(p => { try { return p.MainModule?.FileName; } catch { return null; } })
                    .FirstOrDefault(f => f != null)
                    ?? ResolveFromSystemDirs(path)
                    ?? path;
            }

            return GetShellFileIcon(fullPath, IconRasterSize)
                ?? FallbackFileIcon(fullPath);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? FallbackFileIcon(string fullPath)
    {
        try
        {
            var shfi = new NativeMethods.SHFILEINFO();
            if (NativeMethods.SHGetFileInfoW(fullPath, 0, ref shfi,
                    (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                    NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON) != IntPtr.Zero
                && shfi.hIcon != IntPtr.Zero)
                return RasterizeIcon(shfi.hIcon, IconRasterSize);

            int extracted = NativeMethods.ExtractIconExW(fullPath, 0, out IntPtr big, out _, 1);
            if (extracted > 0 && big != IntPtr.Zero)
                return RasterizeIcon(big, IconRasterSize);
        }
        catch
        {
        }
        return null;
    }

    /// <summary>按进程名在系统目录中定位 exe（notepad → C:\Windows\notepad.exe）。</summary>
    private static string? ResolveFromSystemDirs(string name)
    {
        string file = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        var candidates = new[]
        {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), file),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), file),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), file),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), file),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static IntPtr QueryIcon(IntPtr hwnd, int iconKind)
    {
        return NativeMethods.SendMessageTimeoutW(
            hwnd, NativeMethods.WM_GETICON, new IntPtr(iconKind), IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, IconLoadTimeoutMs, out IntPtr hIcon)
            ? hIcon
            : IntPtr.Zero;
    }

    private static string? GetProcessPath(uint processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.MainModule?.FileName;
        }
        catch
        {
            // 32/64 边界或进程已退出
            return null;
        }
    }

    // ---- 高清光栅化 ----

    /// <summary>
    /// DrawIconEx 高质量光栅化 HICON 到指定位图（双三次插值）。
    /// 不销毁传入的 HICON（WM_GETICON/GCLP_HICON 均为借用句柄）。
    /// </summary>
    private static ImageSource? RasterizeIcon(IntPtr hIcon, int size)
    {
        try
        {
            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                IntPtr hdc = graphics.GetHdc();
                NativeMethods.DrawIconEx(hdc, 0, 0, hIcon, size, size, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
                graphics.ReleaseHdc();
            }
            return BitmapToSource(bitmap);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// IShellItemImageFactory：Shell 提取 exe 内最大图标（最高 256px）并平滑缩放到目标尺寸。
    /// </summary>
    private static ImageSource? GetShellFileIcon(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            if (NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object obj) != 0)
                return null;
            factory = (IShellItemImageFactory)obj;
            var sz = new NativeMethods.SIZE { cx = size, cy = size };
            uint flags = NativeMethods.SIIGBF_RESIZETOSTRETCH | NativeMethods.SIIGBF_BIGGERSIZEOK;
            if (factory.GetImage(sz, flags, out IntPtr hBitmap) != 0 || hBitmap == IntPtr.Zero)
                return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                NativeMethods.DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (factory != null)
                Marshal.ReleaseComObject(factory);
        }
    }

    private static ImageSource BitmapToSource(Bitmap bitmap)
    {
        IntPtr hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }
    }
}