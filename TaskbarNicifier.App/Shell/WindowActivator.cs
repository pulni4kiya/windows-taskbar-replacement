using System;
using TaskbarNicifier.App.Interop;

namespace TaskbarNicifier.App.Shell;

public static class WindowActivator
{
    public static void FocusWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        // Restore in case it's minimized, then bring to foreground.
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(hwnd);
    }
}

