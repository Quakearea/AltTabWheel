namespace AltTabWheel.Services;

/// <summary>
/// Foreground juggling to activate a window from a background tool process.
/// Uses the classic AttachThreadInput handshake so SetForegroundWindow succeeds
/// even under foreground-lock restrictions; restores minimized windows first.
/// </summary>
public static class WindowActivator
{
    public static void Activate(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return;

        uint targetThread = NativeMethods.GetWindowThreadProcessId(
            NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOTOWNER), out _);

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        uint foregroundThread = foreground != IntPtr.Zero
            ? NativeMethods.GetWindowThreadProcessId(foreground, out _)
            : 0;

        uint currentThread = NativeMethods.GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread;
        if (attached)
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            if (NativeMethods.IsIconic(hWnd))
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
            NativeMethods.BringWindowToTop(hWnd);
            NativeMethods.SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }
}
