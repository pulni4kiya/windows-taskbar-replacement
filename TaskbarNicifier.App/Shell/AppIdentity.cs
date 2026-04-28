using System;
using System.IO;

namespace TaskbarNicifier.App.Shell;

/// <summary>
/// Stable app identity for grouping and persistence (matches automatic group key semantics).
/// </summary>
public static class AppIdentity
{
    public static string GetAppKey(AppWindowItem w)
    {
        if (!string.IsNullOrWhiteSpace(w.AppUserModelId))
            return $"aumid:{w.AppUserModelId!.Trim()}";
        if (!string.IsNullOrWhiteSpace(w.IdentityProcessPath))
            return NormalizePath(w.IdentityProcessPath!);
        if (!string.IsNullOrWhiteSpace(w.IdentityProcessName))
            return w.IdentityProcessName!.Trim();
        if (!string.IsNullOrWhiteSpace(w.ProcessPath))
            return NormalizePath(w.ProcessPath!);
        if (!string.IsNullOrWhiteSpace(w.ProcessName))
            return w.ProcessName!.Trim();
        return $"pid:{w.ProcessId}";
    }

    public static string GetDisplayName(AppWindowItem w)
    {
        // For hosted packaged apps, the frame process name isn't useful; the title is often the real app name.
        if (!string.IsNullOrWhiteSpace(w.AppUserModelId) &&
            string.Equals(w.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(w.Title))
        {
            return w.Title.Trim();
        }

        if (!string.IsNullOrWhiteSpace(w.IdentityProcessPath))
            return Path.GetFileNameWithoutExtension(w.IdentityProcessPath);
        if (!string.IsNullOrWhiteSpace(w.IdentityProcessName))
            return w.IdentityProcessName!;
        if (!string.IsNullOrWhiteSpace(w.ProcessPath))
            return Path.GetFileNameWithoutExtension(w.ProcessPath);
        if (!string.IsNullOrWhiteSpace(w.ProcessName))
            return w.ProcessName!;
        return $"PID {w.ProcessId}";
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }
}
