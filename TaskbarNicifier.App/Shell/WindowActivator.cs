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

    public static void MinimizeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
    }

    public static void ActivateOrMinimizeIfForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            FocusWindow(hwnd);
            return;
        }

        // Compare root-owner windows so we treat owned popups/child activations as the same "focused app".
        var fgRoot = NativeMethods.GetAncestor(fg, NativeMethods.GA_ROOTOWNER);
        if (fgRoot == IntPtr.Zero) fgRoot = fg;

        var targetRoot = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOTOWNER);
        if (targetRoot == IntPtr.Zero) targetRoot = hwnd;

        if (fgRoot == targetRoot)
        {
            MinimizeWindow(hwnd);
            return;
        }

        FocusWindow(hwnd);
    }
}

