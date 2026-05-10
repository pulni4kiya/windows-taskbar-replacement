using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaskbarNicifier.App.Interop;

namespace TaskbarNicifier.App.Shell;

internal sealed class TaskbarPlacementService
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";

    public bool TryGetPrimaryTaskbarRect(out NativeMethods.RECT rect)
    {
        var hwnd = NativeMethods.FindWindowW(PrimaryTaskbarClass, null);
        if (hwnd == IntPtr.Zero)
        {
            rect = default;
            return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out rect))
            return false;

        return true;
    }

    public IntPtr GetPrimaryTaskbarHwnd()
    {
        return NativeMethods.FindWindowW(PrimaryTaskbarClass, null);
    }

    public bool TryGetPrimaryTaskbarMonitorInfo(out NativeMethods.MONITORINFO info)
    {
        info = default;

        var taskbarHwnd = GetPrimaryTaskbarHwnd();
        if (taskbarHwnd == IntPtr.Zero)
            return false;

        var monitor = NativeMethods.MonitorFromWindow(taskbarHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        info = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        return NativeMethods.GetMonitorInfoW(monitor, ref info);
    }

    public IReadOnlyList<TaskbarTarget> GetTaskbarTargets()
    {
        var taskbarHwnds = FindAllTaskbarWindows();
        var primaryHwnd = GetPrimaryTaskbarHwnd();

        var byMonitor = new Dictionary<IntPtr, (IntPtr hwnd, NativeMethods.RECT rect, bool isPrimary)>();
        foreach (var hwnd in taskbarHwnds)
        {
            if (hwnd == IntPtr.Zero)
                continue;

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
                continue;

            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                continue;

            var isPrimary = hwnd == primaryHwnd;
            if (!byMonitor.TryGetValue(monitor, out var existing) || (isPrimary && !existing.isPrimary))
                byMonitor[monitor] = (hwnd, rect, isPrimary);
        }

        // Ensure we still enumerate monitors even if no secondary taskbar windows were discovered.
        var monitors = EnumerateMonitors();
        var results = new List<TaskbarTarget>(monitors.Count);

        foreach (var m in monitors.OrderByDescending(x => x.isPrimary).ThenBy(x => x.key, StringComparer.OrdinalIgnoreCase))
        {
            var taskbar = byMonitor.TryGetValue(m.hmonitor, out var tb)
                ? tb
                : (hwnd: IntPtr.Zero, rect: default(NativeMethods.RECT), isPrimary: m.isPrimary);

            NativeMethods.RECT? taskbarRect = taskbar.hwnd == IntPtr.Zero ? null : taskbar.rect;

            results.Add(new TaskbarTarget(
                MonitorKey: m.key,
                MonitorHandle: m.hmonitor,
                MonitorInfo: m.mi,
                IsPrimary: m.isPrimary,
                TaskbarHwnd: taskbar.hwnd,
                TaskbarRect: taskbarRect));
        }

        return results;
    }

    private static List<IntPtr> FindAllTaskbarWindows()
    {
        var results = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero)
                return true;

            var cls = TryGetClassName(hwnd);
            if (cls is null)
                return true;

            if (string.Equals(cls, PrimaryTaskbarClass, StringComparison.Ordinal) ||
                string.Equals(cls, SecondaryTaskbarClass, StringComparison.Ordinal))
            {
                results.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);

        return results;
    }

    private static string? TryGetClassName(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            var n = NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
            if (n <= 0)
                return null;
            return sb.ToString(0, n);
        }
        catch
        {
            return null;
        }
    }

    private static List<(IntPtr hmonitor, NativeMethods.MONITORINFOEX mi, string key, bool isPrimary)> EnumerateMonitors()
    {
        var monitors = new List<(IntPtr, NativeMethods.MONITORINFOEX, string, bool)>();
        IntPtr primaryMonitor = IntPtr.Zero;

        // First, compute the "primary" monitor by looking at Shell_TrayWnd.
        try
        {
            var hwnd = NativeMethods.FindWindowW(PrimaryTaskbarClass, null);
            if (hwnd != IntPtr.Zero)
                primaryMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        }
        catch
        {
            primaryMonitor = IntPtr.Zero;
        }

        NativeMethods.MonitorEnumProc proc = (IntPtr hMon, IntPtr _, ref NativeMethods.RECT __, IntPtr ___) =>
        {
            try
            {
                var mi = new NativeMethods.MONITORINFOEX
                {
                    cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                    szDevice = string.Empty,
                };

                if (!NativeMethods.GetMonitorInfoW(hMon, ref mi))
                    return true;

                var key = !string.IsNullOrWhiteSpace(mi.szDevice) ? mi.szDevice : $"monitor_{hMon.ToInt64():X}";
                var isPrimary = primaryMonitor != IntPtr.Zero && hMon == primaryMonitor;
                monitors.Add((hMon, mi, key, isPrimary));
            }
            catch
            {
                // ignore
            }

            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);

        // If monitor enumeration failed, at least return the primary taskbar monitor (if available).
        if (monitors.Count == 0)
        {
            var hwnd = NativeMethods.FindWindowW(PrimaryTaskbarClass, null);
            if (hwnd != IntPtr.Zero)
            {
                var mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (mon != IntPtr.Zero)
                {
                    var mi = new NativeMethods.MONITORINFOEX
                    {
                        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                        szDevice = string.Empty,
                    };
                    if (NativeMethods.GetMonitorInfoW(mon, ref mi))
                    {
                        var key = !string.IsNullOrWhiteSpace(mi.szDevice) ? mi.szDevice : "primary";
                        monitors.Add((mon, mi, key, isPrimary: true));
                    }
                }
            }
        }

        // Ensure keys are unique.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < monitors.Count; i++)
        {
            var (h, mi, key, isPrimary) = monitors[i];
            var unique = key;
            var suffix = 2;
            while (!seen.Add(unique))
            {
                unique = $"{key}#{suffix}";
                suffix++;
            }
            monitors[i] = (h, mi, unique, isPrimary);
        }

        return monitors;
    }

    public (int X, int Y, int Width, int Height) GetCenteredOverlayBounds(NativeMethods.RECT taskbarRect, int desiredWidth, int desiredHeight, int margin = 4)
    {
        var left = taskbarRect.Left;
        var top = taskbarRect.Top;
        var right = taskbarRect.Right;
        var bottom = taskbarRect.Bottom;

        var taskbarWidth = Math.Max(0, right - left);
        var taskbarHeight = Math.Max(0, bottom - top);

        // Taskbar can be vertical; MVP assumes bottom taskbar but still tries to center.
        var width = Math.Min(desiredWidth, Math.Max(0, taskbarWidth - margin * 2));
        var height = Math.Min(desiredHeight, Math.Max(0, taskbarHeight - margin * 2));

        var x = left + (taskbarWidth - width) / 2;
        var y = top + (taskbarHeight - height) / 2;

        return (x, y, width, height);
    }

    public (int X, int Y, int Width, int Height) GetIntegratedOverlayBounds(
        NativeMethods.RECT taskbarRect,
        int desiredHeight,
        int reserveLeft,
        int reserveRight,
        int margin = 4)
    {
        var left = taskbarRect.Left;
        var top = taskbarRect.Top;
        var right = taskbarRect.Right;
        var bottom = taskbarRect.Bottom;

        var taskbarWidth = Math.Max(0, right - left);
        var taskbarHeight = Math.Max(0, bottom - top);

        var width = Math.Max(0, taskbarWidth - reserveLeft - reserveRight - margin * 2);
        var height = Math.Min(desiredHeight, Math.Max(0, taskbarHeight - margin * 2));

        var x = left + reserveLeft + margin;
        var y = top + (taskbarHeight - height) / 2;

        return (x, y, width, height);
    }
}

