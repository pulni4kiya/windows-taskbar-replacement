using System;
using TaskbarNicifier.App.Interop;

namespace TaskbarNicifier.App.Shell;

internal sealed class TaskbarPlacementService
{
    public bool TryGetPrimaryTaskbarRect(out NativeMethods.RECT rect)
    {
        var hwnd = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (hwnd == IntPtr.Zero)
        {
            rect = default;
            return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out rect))
            return false;

        return true;
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
}

