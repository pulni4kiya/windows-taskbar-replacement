using System;
using TaskbarNicifier.App.Interop;

namespace TaskbarNicifier.App.Shell;

internal sealed class FullscreenDetector
{
    public bool IsForegroundWindowFullscreenOnItsMonitor()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            return false;

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var mi = new NativeMethods.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref mi))
            return false;

        // Compare against full monitor bounds (not work area) to detect actual fullscreen.
        return rect.Left <= mi.rcMonitor.Left
            && rect.Top <= mi.rcMonitor.Top
            && rect.Right >= mi.rcMonitor.Right
            && rect.Bottom >= mi.rcMonitor.Bottom;
    }
}

