using System;
using TaskbarNicifier.App.Interop;

namespace TaskbarNicifier.App.Shell;

public static class WindowActivator
{
    public static void FocusWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        // Only restore if minimized; restoring a maximized/borderless-fullscreen window
        // can force it into a non-fullscreen state.
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(hwnd);
    }
}

