using System;
using TaskbarNicifier.App.Interop;

namespace TaskbarNicifier.App.Shell;

internal sealed class FullscreenDetector
{
    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

    /// <summary>
    /// Hides the taskbar when the active app on this monitor is fullscreen. Windows only has
    /// one global foreground window, so monitors without focus use their topmost app window.
    /// </summary>
    public bool ShouldHideTaskbarOnMonitor(IntPtr monitorHandle, params IntPtr[] excludeHwnds)
    {
        if (monitorHandle == IntPtr.Zero)
            return false;

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        var foregroundMonitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var foregroundFullscreen = IsWindowFullscreenOnMonitor(foreground, foregroundMonitor);

        if (foregroundMonitor == monitorHandle)
            return foregroundFullscreen;

        return TryGetTopmostAppWindowOnMonitor(monitorHandle, excludeHwnds, out var topmost)
            && IsWindowFullscreenOnMonitor(topmost, monitorHandle);
    }

    public bool TryGetForegroundFullscreenMonitor(out IntPtr monitor)
    {
        monitor = IntPtr.Zero;

        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;

        monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        if (!IsWindowFullscreenOnMonitor(hwnd, monitor))
        {
            monitor = IntPtr.Zero;
            return false;
        }

        return true;
    }

    public bool IsForegroundWindowFullscreenOnItsMonitor()
        => TryGetForegroundFullscreenMonitor(out _);

    private static bool TryGetTopmostAppWindowOnMonitor(
        IntPtr monitorHandle,
        IntPtr[] excludeHwnds,
        out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;

        var foundHwnd = IntPtr.Zero;
        var found = false;
        var shellWindow = NativeMethods.GetShellWindow();

        NativeMethods.EnumWindows((candidate, _) =>
        {
            if (!ShouldConsiderWindow(candidate, shellWindow, excludeHwnds))
                return true;

            if (NativeMethods.MonitorFromWindow(candidate, NativeMethods.MONITOR_DEFAULTTONEAREST) != monitorHandle)
                return true;

            foundHwnd = candidate;
            found = true;
            return false;
        }, IntPtr.Zero);

        hwnd = foundHwnd;
        return found;
    }

    private static bool IsWindowFullscreenOnMonitor(IntPtr hwnd, IntPtr monitorHandle)
    {
        if (hwnd == IntPtr.Zero || monitorHandle == IntPtr.Zero)
            return false;

        if (!TryGetMonitorBounds(monitorHandle, out var monitorRect))
            return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            return false;

        return CoversMonitor(rect, monitorRect);
    }

    private static bool TryGetMonitorBounds(IntPtr monitorHandle, out NativeMethods.RECT monitorRect)
    {
        monitorRect = default;

        var mi = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (!NativeMethods.GetMonitorInfoW(monitorHandle, ref mi))
            return false;

        monitorRect = mi.rcMonitor;
        return true;
    }

    private static bool ShouldConsiderWindow(IntPtr hwnd, IntPtr shellWindow, IntPtr[] excludeHwnds)
    {
        if (hwnd == IntPtr.Zero || hwnd == shellWindow)
            return false;

        foreach (var excluded in excludeHwnds)
        {
            if (excluded != IntPtr.Zero && hwnd == excluded)
                return false;
        }

        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            return false;

        if (IsToolWindow(hwnd))
            return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == CurrentProcessId)
            return false;

        return true;
    }

    private static bool CoversMonitor(NativeMethods.RECT windowRect, NativeMethods.RECT monitorRect)
        => windowRect.Left <= monitorRect.Left
           && windowRect.Top <= monitorRect.Top
           && windowRect.Right >= monitorRect.Right
           && windowRect.Bottom >= monitorRect.Bottom;

    private static bool IsToolWindow(IntPtr hwnd)
    {
        var exStyle = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);
        var isTool = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        var isApp = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
        return isTool && !isApp;
    }
}
