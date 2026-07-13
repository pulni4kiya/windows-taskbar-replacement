using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using TaskbarNicifier.App.Interop;
using TaskbarNicifier.App.ViewModels;

namespace TaskbarNicifier.App.Views;

/// <summary>One low-level mouse hook per process, shared by all overlay windows.</summary>
internal static class OverlayMouseHookHub
{
    private static readonly List<TaskbarOverlayWindow> Windows = new();
    private static IntPtr _hook;
    private static readonly NativeMethods.LowLevelMouseProc HookProc = LowLevelMouseHookProc;

    public static void Register(TaskbarOverlayWindow window)
    {
        if (!Windows.Contains(window))
            Windows.Add(window);

        if (_hook != IntPtr.Zero)
            return;

        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, HookProc, IntPtr.Zero, 0);
    }

    public static void Unregister(TaskbarOverlayWindow window)
    {
        Windows.Remove(window);

        if (_hook == IntPtr.Zero || Windows.Count > 0)
            return;

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static IntPtr LowLevelMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsMouseButtonDownMessage(wParam.ToInt32()))
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var p = new Point(info.pt.X, info.pt.Y);

            foreach (var window in Windows.ToArray())
            {
                if (window.DataContext is not TaskbarOverlayViewModel { HasDismissiblePopupOpen: true } vm)
                    continue;

                if (window.IsScreenPointInsideOverlayChrome(p)
                    || window.IsScreenPointInsideSettingsPopup(p)
                    || window.IsScreenPointInsideGroupSettingsPopup(p)
                    || vm.IsScreenPointInsideGeneratedPopup(p)
                    || window.IsScreenPointInsideCurrentProcessWindow(p))
                    continue;

                vm.CloseActivePopups();
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsMouseButtonDownMessage(int msg)
        => msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN;
}
