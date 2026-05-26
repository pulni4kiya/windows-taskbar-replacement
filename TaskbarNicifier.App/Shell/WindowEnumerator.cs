using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TaskbarNicifier.App.Interop;

namespace TaskbarNicifier.App.Shell;

public sealed class WindowEnumerator
{
    private static readonly Dictionary<long, (int pid, string? name, string? path)> IdentityCacheByFrameHwnd = new();
    private static readonly object IdentityCacheSync = new();

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

                var title = GetWindowTitle(hwnd);
                if (string.IsNullOrWhiteSpace(title))
                {
                    if (!ShouldIncludeUntitledWindow(hwnd))
                        return true;

                    title = BuildFallbackTitle(processName, processPath);
                }

                // AUMID isn't always present on the top-level frame HWND (notably ApplicationFrameHost).
                // Try root-owner / active popup first to match shell taskbar identity more closely.
                var aumid = TryGetBestAumidForWindow(hwnd);

                int? identityPid = null;
                string? identityName = null;
                string? identityPath = null;
                if (string.Equals(processName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                {
                    // If the frame itself doesn't expose an AUMID, try child windows.
                    aumid ??= TryGetAumidFromChildWindows(hwnd);

                    if (TryResolveIdentityProcessFromChildWindows(hwnd, pid, out var ipid, out var iname, out var ipath))
                    {
                        identityPid = ipid;
                        identityName = iname;
                        identityPath = ipath;

                        // Cache identity for minimized scenarios where children disappear.
                        lock (IdentityCacheSync)
                            IdentityCacheByFrameHwnd[hwnd.ToInt64()] = (ipid, iname, ipath);
                    }
                    else if (NativeMethods.IsIconic(hwnd))
                    {
                        lock (IdentityCacheSync)
                        {
                            if (IdentityCacheByFrameHwnd.TryGetValue(hwnd.ToInt64(), out var cached))
                            {
                                identityPid = cached.pid;
                                identityName = cached.name;
                                identityPath = cached.path;
                            }
                        }
                    }
                }

                results.Add(new AppWindowItem(
                    Hwnd: hwnd,
                    ProcessId: pid,
                    ProcessPath: processPath,
                    ProcessName: processName,
                    Title: title.Trim(),
                    AppUserModelId: aumid,
                    IdentityProcessId: identityPid,
                    IdentityProcessPath: identityPath,
                    IdentityProcessName: identityName
                ));
            }
            catch
            {
                // Best-effort enumeration: ignore windows that throw.
            }

            return true;
        }, IntPtr.Zero);

        // If we started with hosted apps minimized, the frame may not expose identity yet.
        // However, the app processes often have their own top-level windows; pair by title.
        ApplyIdentityFromPeerWindows(results);

        // De-dupe hosted windows: when a hosted frame exists, drop the app-process top-level window entries.
        // Evidence: when minimized, we see both an ApplicationFrameHost frame and separate CalculatorApp/SystemSettings windows.
        var hostedIdentityPids = results
            .Where(w =>
                string.Equals(w.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
                w.IdentityProcessId is not null)
            .Select(w => w.IdentityProcessId!.Value)
            .ToHashSet();

        if (hostedIdentityPids.Count > 0)
        {
            results = results
                .Where(w =>
                    string.Equals(w.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
                    !hostedIdentityPids.Contains(w.ProcessId))
                .ToList();
        }

        return results;
    }

    private static void ApplyIdentityFromPeerWindows(List<AppWindowItem> results)
    {
        try
        {
            // Map Title -> best non-frame candidate
            var peersByTitle = results
                .Where(w =>
                    !string.Equals(w.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(w.Title))
                .GroupBy(w => w.Title.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.First(), // any is fine; titles here are already highly specific for Settings/Calculator
                    StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < results.Count; i++)
            {
                var w = results[i];

                if (!string.Equals(w.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(w.AppUserModelId))
                    continue;
                if (w.IdentityProcessId is not null)
                    continue;
                if (string.IsNullOrWhiteSpace(w.Title))
                    continue;

                var t = w.Title.Trim();
                if (!peersByTitle.TryGetValue(t, out var peer))
                {
                    continue;
                }

                var updated = w with
                {
                    IdentityProcessId = peer.ProcessId,
                    IdentityProcessName = peer.ProcessName,
                    IdentityProcessPath = peer.ProcessPath,
                };

                results[i] = updated;
            }
        }
        catch
        {
            // ignore
        }
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

    /// <summary>
    /// Mirrors shell taskbar rules for visible top-level windows with no caption text.
    /// </summary>
    private static bool ShouldIncludeUntitledWindow(IntPtr hwnd)
    {
        var exStyle = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_APPWINDOW) != 0)
            return true;

        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            return false;

        return NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) == IntPtr.Zero;
    }

    private static string BuildFallbackTitle(string? processName, string? processPath)
    {
        if (!string.IsNullOrWhiteSpace(processPath))
            return Path.GetFileNameWithoutExtension(processPath);
        if (!string.IsNullOrWhiteSpace(processName))
            return processName.Trim();
        return "Untitled";
    }

    private static string? TryGetBestAumidForWindow(IntPtr hwnd)
    {
        var v = NativeMethods.TryGetAppUserModelIdForWindow(hwnd);
        if (!string.IsNullOrWhiteSpace(v))
            return v;

        try
        {
            var rootOwner = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOTOWNER);
            if (rootOwner != IntPtr.Zero)
            {
                var popup = NativeMethods.GetLastActivePopup(rootOwner);
                if (popup != IntPtr.Zero)
                {
                    v = NativeMethods.TryGetAppUserModelIdForWindow(popup);
                    if (!string.IsNullOrWhiteSpace(v))
                        return v;
                }

                v = NativeMethods.TryGetAppUserModelIdForWindow(rootOwner);
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? TryGetAumidFromChildWindows(IntPtr hwnd)
    {
        string? found = null;
        try
        {
            NativeMethods.EnumChildWindows(hwnd, (child, _) =>
            {
                if (child == IntPtr.Zero)
                    return true;

                var aumid = NativeMethods.TryGetAppUserModelIdForWindow(child);
                if (!string.IsNullOrWhiteSpace(aumid))
                {
                    found = aumid;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // ignore
        }

        return found;
    }

    private static bool TryResolveIdentityProcessFromChildWindows(
        IntPtr hwnd,
        int framePid,
        out int identityPid,
        out string? identityProcessName,
        out string? identityProcessPath)
    {
        identityPid = 0;
        identityProcessName = null;
        identityProcessPath = null;

        try
        {
            var found = false;
            var localPid = 0;
            string? localName = null;
            string? localPath = null;
            NativeMethods.EnumChildWindows(hwnd, (child, _) =>
            {
                // When hosted apps are minimized, their child windows may not be visible,
                // but their owning PID is still the best identity signal.
                if (child == IntPtr.Zero)
                    return true;

                NativeMethods.GetWindowThreadProcessId(child, out var childPidU);
                var childPid = unchecked((int)childPidU);
                if (childPid == 0 || childPid == framePid)
                    return true;

                try
                {
                    using var p = Process.GetProcessById(childPid);
                    localPid = childPid;
                    localName = p.ProcessName;
                    try
                    {
                        localPath = p.MainModule?.FileName;
                    }
                    catch
                    {
                        localPath = null;
                    }

                    found = true;
                    return false; // stop enumeration
                }
                catch
                {
                    return true;
                }
            }, IntPtr.Zero);

            if (found)
            {
                identityPid = localPid;
                identityProcessName = localName;
                identityProcessPath = localPath;
            }

            return found;
        }
        catch
        {
            return false;
        }
    }
}

