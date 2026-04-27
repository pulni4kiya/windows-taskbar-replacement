using System;
using System.Runtime.InteropServices;

namespace TaskbarNicifier.App.Shell;

internal sealed class VirtualDesktopService
{
    private readonly IVirtualDesktopManager? _vdm;

    public VirtualDesktopService()
    {
        try
        {
            _vdm = (IVirtualDesktopManager)new VirtualDesktopManager();
        }
        catch
        {
            _vdm = null;
        }
    }

    public bool IsWindowOnCurrentDesktop(IntPtr hwnd)
    {
        if (_vdm is null || hwnd == IntPtr.Zero)
            return true; // best-effort: don't hide apps if API not available

        try
        {
            _vdm.IsWindowOnCurrentVirtualDesktop(hwnd, out var onCurrent);
            return onCurrent;
        }
        catch
        {
            // Best-effort; avoid hiding windows due to COM flakiness.
            return true;
        }
    }

    // COM interop definitions
    [ComImport]
    [Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    private class VirtualDesktopManager
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    private interface IVirtualDesktopManager
    {
        int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out bool onCurrentDesktop);
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        int MoveWindowToDesktop(IntPtr topLevelWindow, [In] ref Guid desktopId);
    }
}

