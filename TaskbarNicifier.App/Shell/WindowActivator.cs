using System;
using System.Threading.Tasks;
using TaskbarNicifier.App.Interop;

namespace TaskbarNicifier.App.Shell;

public static class WindowActivator
{
    public static void FocusWindow(IntPtr hwnd)
        => RunSafe(() => FocusWindowCore(hwnd));

    public static void MinimizeWindow(IntPtr hwnd)
        => RunSafe(() => MinimizeWindowCore(hwnd));

    public static void CloseWindow(IntPtr hwnd)
        => RunSafe(() => CloseWindowCore(hwnd));

    public static void ActivateOrMinimizeIfForeground(IntPtr hwnd)
        => RunSafe(() => ActivateOrMinimizeIfForegroundCore(hwnd));

    private static void RunSafe(Action action)
    {
        _ = Task.Run(() =>
        {
            try
            {
                action();
            }
            catch
            {
                // Window activation is best-effort; a frozen target must not block the overlay UI.
            }
        });
    }

    private static void FocusWindowCore(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        // Only restore if minimized; restoring a maximized/borderless-fullscreen window
        // can force it into a non-fullscreen state.
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    private static void MinimizeWindowCore(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
    }

    private static void CloseWindowCore(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        NativeMethods.PostMessageW(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private static void ActivateOrMinimizeIfForegroundCore(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            FocusWindowCore(hwnd);
            return;
        }

        // Compare root-owner windows so we treat owned popups/child activations as the same "focused app".
        var fgRoot = NativeMethods.GetAncestor(fg, NativeMethods.GA_ROOTOWNER);
        if (fgRoot == IntPtr.Zero) fgRoot = fg;

        var targetRoot = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOTOWNER);
        if (targetRoot == IntPtr.Zero) targetRoot = hwnd;

        if (fgRoot == targetRoot)
        {
            MinimizeWindowCore(hwnd);
            return;
        }

        FocusWindowCore(hwnd);
    }
}

