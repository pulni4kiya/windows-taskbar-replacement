using System;
using System.Runtime.InteropServices;

namespace TaskbarNicifier.App.Interop;

internal static class NativeMethods
{
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_APPWINDOW = 0x00040000;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    // Shell hook
    internal const int HSHELL_FLASH = 0x8006;
    internal const int HSHELL_FLASHW = 0x800C;
    internal const int HSHELL_WINDOWACTIVATED = 4;
    internal const int HSHELL_RUDEAPPACTIVATED = 0x8004;

    internal const uint GA_ROOTOWNER = 3;

    internal const int SW_RESTORE = 9;
    internal const int SW_SHOW = 5;
    internal const int SW_MINIMIZE = 6;

    internal const int WM_NCHITTEST = 0x0084;
    internal const int WM_MOUSEACTIVATE = 0x0021;
    internal const int MA_NOACTIVATE = 3;

    internal const int HTCLIENT = 1;
    internal const int HTLEFT = 10;
    internal const int HTRIGHT = 11;
    internal const int HTTOP = 12;
    internal const int HTTOPLEFT = 13;
    internal const int HTTOPRIGHT = 14;

    internal const uint WM_GETICON = 0x007F;
    internal const int ICON_SMALL2 = 2;
    internal const int ICON_SMALL = 0;
    internal const int ICON_BIG = 1;

    internal const int CLASS_LONG_INDEX_GCLP_HICON = -14;
    internal const int CLASS_LONG_INDEX_GCLP_HICONSM = -34;

    internal const uint SHGFI_ICON = 0x000000100;
    internal const uint SHGFI_SMALLICON = 0x000000001;
    internal const uint SHGFI_LARGEICON = 0x000000000;

    // SetWindowPos / z-order helpers
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetLastActivePopup(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterShellHookWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DeregisterShellHookWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    internal static IntPtr GetWindowExStyle(IntPtr hWnd)
        => IntPtr.Size == 8 ? GetWindowLongPtrW(hWnd, GWL_EXSTYLE) : new IntPtr(GetWindowLongW(hWnd, GWL_EXSTYLE));

    internal static void SetWindowExStyle(IntPtr hWnd, IntPtr exStyle)
    {
        if (IntPtr.Size == 8)
            _ = SetWindowLongPtrW(hWnd, GWL_EXSTYLE, exStyle);
        else
            _ = SetWindowLongW(hWnd, GWL_EXSTYLE, exStyle.ToInt32());
    }

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetClassLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        out SHFILEINFOW psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // --- AppUserModelID (AUMID) interop ---

    [Flags]
    internal enum GETPROPERTYSTOREFLAGS : uint
    {
        GPS_DEFAULT = 0,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    // PKEY_AppUserModel_ID
    internal static readonly PROPERTYKEY PKEY_AppUserModel_ID =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    [StructLayout(LayoutKind.Explicit)]
    internal struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    internal const ushort VT_LPWSTR = 31;

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        uint GetCount(out uint cProps);
        uint GetAt(uint iProp, out PROPERTYKEY pkey);
        uint GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        uint SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
        uint Commit();
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PROPVARIANT pvar);

    internal static string? TryGetAppUserModelIdForWindow(IntPtr hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            var hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out var store);
            if (hr < 0 || store is null)
                return null;

            var key = PKEY_AppUserModel_ID;
            hr = unchecked((int)store.GetValue(ref key, out var pv));
            if (hr < 0)
                return null;

            try
            {
                if (pv.vt != VT_LPWSTR || pv.pointerValue == IntPtr.Zero)
                    return null;

                var s = Marshal.PtrToStringUni(pv.pointerValue);
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }
            finally
            {
                _ = PropVariantClear(ref pv);
            }
        }
        catch
        {
            return null;
        }
    }

    // --- AppsFolder icon extraction (AUMID -> HBITMAP) ---

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx;
        public int cy;

        public SIZE(int cx, int cy)
        {
            this.cx = cx;
            this.cy = cy;
        }
    }

    [Flags]
    internal enum SIIGBF
    {
        SIIGBF_RESIZETOFIT = 0x00,
        SIIGBF_BIGGERSIZEOK = 0x01,
        SIIGBF_ICONONLY = 0x04,
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    // {1E87508D-89C2-42F0-8A7E-645A0F50CA58}
    internal static readonly Guid FOLDERID_AppsFolder = new("1E87508D-89C2-42F0-8A7E-645A0F50CA58");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHCreateItemInKnownFolder(
        [In] ref Guid kfid,
        uint dwKFFlags,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszItem,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr hObject);

    internal static IntPtr TryGetAppsFolderIconBitmap(string appUserModelId, int sizePx)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(appUserModelId))
                return IntPtr.Zero;

            var iid = typeof(IShellItemImageFactory).GUID;
            var kfid = FOLDERID_AppsFolder;
            var hr = SHCreateItemInKnownFolder(ref kfid, 0, appUserModelId.Trim(), ref iid, out var obj);
            if (hr < 0 || obj is not IShellItemImageFactory factory)
                return IntPtr.Zero;

            var sz = new SIZE(sizePx, sizePx);
            hr = factory.GetImage(sz, SIIGBF.SIIGBF_ICONONLY | SIIGBF.SIIGBF_BIGGERSIZEOK, out var hbm);
            return hr < 0 ? IntPtr.Zero : hbm;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}

