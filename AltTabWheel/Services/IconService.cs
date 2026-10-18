using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using IShellItemImageFactory = AltTabWheel.NativeMethods.IShellItemImageFactory;

namespace AltTabWheel.Services;

/// <summary>
/// 窗口图标提取（高清版 v2）。
///
/// 清晰度关键：显示尺寸 ~46-83 DIP（选中放大），高 DPI 屏下物理像素更高。
/// 统一以 256×256 产出（下采样锐利，上采样发糊）：
///  1. 窗口自身图标 WM_GETICON / 窗口类图标 GCLP_HICON —— 但先经 GetIconInfo 查
///     原生尺寸：大量应用的 ICON_BIG 只有 32×32，上采样到 256 必糊，此类窗口
///     跳过 HICON，直接走 exe 文件图标（Shell 提取 exe 内最大 256px 源平滑缩放）；
///  2. exe 文件图标 → IShellItemImageFactory.GetImage(256, RESIZETOSTRETCH)；
///  3. 小尺寸 HICON 兜底光栅化（聊胜于无）；
///  4. SHGetFileInfo / ExtractIconEx 最后兜底。
///
/// 黑边修复（v0.5）：两条路径产出的都是**预乘 alpha** 像素，而 HBITMAP→WPF
/// 互操作（CreateBitmapSourceFromHBitmap）无法声明 Pbgra32，半透明边缘被按
/// 直通 alpha 二次衰减 → 薄黑边。现在统一直读像素字节、显式按 Pbgra32 构造。
/// </summary>
public static class IconService
{
    private const uint IconLoadTimeoutMs = 200;
    // 显示约 46-83 DIP（选中放大），150%/200% DPI 下物理像素可达 ~166px；
    // 统一 256px 产出：大源走 Fant 下采样锐利，小源上采样必糊（故下方有原生尺寸门槛）
    private const int IconRasterSize = 256;
    // 窗口 HICON 原生尺寸低于该值时放弃它，优先 exe 文件图标（32px 上采样到 256 是主要糊源）
    private const int MinWindowIconSize = 128;

    public static ImageSource? GetWindowIcon(IntPtr hwnd, uint processId)
    {
        try
        {
            // 1) 窗口/窗口类 HICON（应用运行时图标，与系统 Alt+Tab 一致）——
            //    仅当原生尺寸足够大才用：32px 图标放大到 256 是模糊主因
            IntPtr hIcon = QueryIcon(hwnd, NativeMethods.ICON_BIG);
            if (hIcon == IntPtr.Zero)
                hIcon = QueryIcon(hwnd, NativeMethods.ICON_SMALL2);
            if (hIcon == IntPtr.Zero)
                hIcon = NativeMethods.GetClassIcon(hwnd);
            if (hIcon != IntPtr.Zero && GetIconNativeSize(hIcon) >= MinWindowIconSize)
                return RasterizeIcon(hIcon, IconRasterSize);

            // 2) exe 文件图标：Shell 高清渲染（256px 源）
            string? exePath = GetProcessPath(processId);
            if (exePath != null)
            {
                var shellIcon = GetShellFileIcon(exePath, IconRasterSize);
                if (shellIcon != null)
                    return shellIcon;
            }

            // 3) 小尺寸窗口 HICON 兜底（比没有强）
            if (hIcon != IntPtr.Zero)
                return RasterizeIcon(hIcon, IconRasterSize);

            // 4) 经典兜底
            if (exePath != null)
                return FallbackFileIcon(exePath);
        }
        catch
        {
            // 图标提取绝不能打断切换器
        }
        return null;
    }

    /// <summary>HICON 原生边长（GetIconInfo 取位图尺寸；查询失败返回 0）。</summary>
    private static int GetIconNativeSize(IntPtr hIcon)
    {
        if (!NativeMethods.GetIconInfo(hIcon, out var info))
            return 0;
        try
        {
            IntPtr hb = info.hbmColor != IntPtr.Zero ? info.hbmColor : info.hbmMask;
            if (hb != IntPtr.Zero
                && NativeMethods.GetObjectW(hb, Marshal.SizeOf<NativeMethods.BITMAP>(), out var bm) != 0)
                return Math.Min(bm.bmWidth, bm.bmHeight);
            return 0;
        }
        finally
        {
            // GetIconInfo 返回的位图归调用方所有，必须销毁
            if (info.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmMask);
        }
    }

    // 固定项文件图标缓存：BuildItems 每次刷新都会为未运行的固定项提图标，
    // Shell 提取是其中最贵的一步（IShellItemImageFactory COM + 位图缩放）。
    // 产出均为 Freeze 过的 ImageSource，跨线程共享安全。
    private static readonly Dictionary<string, ImageSource?> FileIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FileIconGate = new();

    /// <summary>从文件路径提取图标（固定项/兜底用），同样走 Shell 高清渲染。结果按路径缓存。</summary>
    public static ImageSource? GetFileIcon(string path)
    {
        lock (FileIconGate)
        {
            if (FileIconCache.TryGetValue(path, out var cached))
                return cached;
        }
        var icon = LoadFileIcon(path);
        lock (FileIconGate)
            FileIconCache[path] = icon;
        return icon;
    }

    private static ImageSource? LoadFileIcon(string path)
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
    /// HICON 光栅化到指定位图（v0.5 重写）。
    /// DrawIconEx 画 32bpp 图标走 AND/XOR 位操作而非真 alpha 混合，半透明边缘
    /// 产生脏色（薄黑边来源之一）；Icon.ToBitmap() 的 alpha 处理才是正确的，
    /// 再由 GDI+ 双三次缩放到目标尺寸。不销毁传入的 HICON（借用句柄）。
    /// </summary>
    private static ImageSource? RasterizeIcon(IntPtr hIcon, int size)
    {
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(hIcon);   // 包装不持有句柄，Dispose 安全
            using var srcBitmap = icon.ToBitmap();                     // 32bppArgb，alpha 正确
            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImage(srcBitmap, new Rectangle(0, 0, size, size));
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
                return HBitmapToSource(hBitmap);
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

    /// <summary>
    /// GDI+ 位图 → WPF。像素是 Format32bppPArgb（预乘 alpha），
    /// 必须显式按 Pbgra32 构造——走 GetHbitmap + CreateBitmapSourceFromHBitmap
    /// 会丢 alpha 语义，半透明边缘被二次衰减成黑边。
    /// </summary>
    private static ImageSource BitmapToSource(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            int len = data.Stride * bitmap.Height;
            var pixels = new byte[len];
            Marshal.Copy(data.Scan0, pixels, 0, len);
            NormalizeToPremultiplied(pixels);
            var source = BitmapSource.Create(
                bitmap.Width, bitmap.Height, 96, 96,
                PixelFormats.Pbgra32, null,
                pixels, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// Shell HBITMAP → WPF。IShellItemImageFactory 产出预乘 alpha 的 32bpp 位图，
    /// GetDIBits 直读像素显式按 Pbgra32 构造（同上，绕开互操作的黑边）。
    /// </summary>
    private static ImageSource? HBitmapToSource(IntPtr hBitmap)
    {
        if (NativeMethods.GetObjectW(hBitmap, Marshal.SizeOf<NativeMethods.BITMAP>(), out var bm) == 0
            || bm.bmWidth <= 0 || bm.bmHeight <= 0)
            return null;
        int w = bm.bmWidth, h = bm.bmHeight;
        var pixels = new byte[w * h * 4];
        var bmi = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,          // 自顶向下
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,      // BI_RGB
        };
        IntPtr dc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            if (NativeMethods.GetDIBits(dc, hBitmap, 0, (uint)h, pixels, ref bmi, 0) == 0)
                return null;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, dc);
        }
        NormalizeToPremultiplied(pixels);
        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        source.Freeze();
        return source;
    }

    /// <summary>
    /// 直通 alpha → 预乘归一化（BGRA 原地）。
    /// 判据：预乘像素恒有 R,G,B ≤ A；任一像素违反即说明整张图是直通 alpha，
    /// 此时把所有像素手动预乘——否则 WPF 按 Pbgra32 显示时边缘被二次衰减出黑边。
    /// </summary>
    private static void NormalizeToPremultiplied(byte[] pixels)
    {
        bool straight = false;
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (pixels[i] > a || pixels[i + 1] > a || pixels[i + 2] > a)
            {
                straight = true;
                break;
            }
        }
        if (!straight)
            return;
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a >= 255)
                continue;
            pixels[i] = (byte)(pixels[i] * a / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
        }
    }
}