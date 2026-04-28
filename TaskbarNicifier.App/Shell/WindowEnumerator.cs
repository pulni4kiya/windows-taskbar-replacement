using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TaskbarNicifier.App.Interop;

namespace TaskbarNicifier.App.Shell;

public sealed class WindowEnumerator
{
    public List<AppWindowItem> GetOpenAppWindows()
    {
        var results = new List<AppWindowItem>();
        var shellWindow = NativeMethods.GetShellWindow();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (hwnd == IntPtr.Zero || hwnd == shellWindow)
                    return true;

                if (!NativeMethods.IsWindowVisible(hwnd))
                    return true;

                var title = GetWindowTitle(hwnd);
                if (string.IsNullOrWhiteSpace(title))
                    return true;

                if (IsToolWindow(hwnd))
                    return true;

                NativeMethods.GetWindowThreadProcessId(hwnd, out var pidU);
                var pid = unchecked((int)pidU);

                string? processPath = null;
                string? processName = null;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    processName = p.ProcessName;
                    try
                    {
                        // Access to MainModule may fail for some processes; best-effort only.
                        processPath = p.MainModule?.FileName;
                    }
                    catch
                    {
                        processPath = null;
                    }
                }
                catch
                {
                    // Ignore processes we can't open.
                }

                results.Add(new AppWindowItem(
                    Hwnd: hwnd,
                    ProcessId: pid,
                    ProcessPath: processPath,
                    ProcessName: processName,
                    Title: title.Trim()
                ));
            }
            catch
            {
                // Best-effort enumeration: ignore windows that throw.
            }

            return true;
        }, IntPtr.Zero);

        return results;
    }

    public List<AppWindowGroup> GroupWindows(List<AppWindowItem> windows)
    {
        // MVP grouping key: process path if available, else process name, else pid.
        return windows
            .GroupBy(w => AppIdentity.GetAppKey(w))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                return new AppWindowGroup
                {
                    GroupKey = g.Key,
                    DisplayName = AppIdentity.GetDisplayName(first),
                    Windows = g.OrderByDescending(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                    Icon = null,
                };
            })
            .ToList();
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var len = NativeMethods.GetWindowTextLengthW(hwnd);
        if (len <= 0)
            return string.Empty;

        var buf = new char[len + 1];
        var copied = NativeMethods.GetWindowTextW(hwnd, buf, buf.Length);
        if (copied <= 0)
            return string.Empty;

        return new string(buf, 0, copied);
    }

    private static bool IsToolWindow(IntPtr hwnd)
    {
        var exStyle = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);
        var isTool = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        var isApp = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;

        // If it's explicitly a toolwindow, skip it. If it forces appwindow, keep it.
        if (isTool && !isApp)
            return true;

        return false;
    }
}

