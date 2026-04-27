using System;
using System.IO;
using System.Linq;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskbarNicifier.App.Interop;

namespace TaskbarNicifier.App.Shell;

public sealed class IconProvider
{
    public ImageSource? TryGetIconForGroup(AppWindowGroup group)
    {
        // Best effort: try window icons first, then fallback to file icon.
        var hwnd = group.Windows.FirstOrDefault()?.Hwnd ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            var src = TryGetWindowIcon(hwnd);
            if (src is not null)
                return src;
        }

        var path = group.Windows.Select(w => w.ProcessPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var src = TryGetIconFromFile(path!);
            if (src is not null)
                return src;
        }

        return null;
    }

    private static ImageSource? TryGetWindowIcon(IntPtr hwnd)
    {
        // Prefer large icons first for better scaling quality.
        foreach (var kind in new[] { NativeMethods.ICON_BIG, NativeMethods.ICON_SMALL2, NativeMethods.ICON_SMALL })
        {
            var hIcon = NativeMethods.SendMessageW(hwnd, NativeMethods.WM_GETICON, new IntPtr(kind), IntPtr.Zero);
            if (hIcon != IntPtr.Zero)
                return CreateImageSourceFromHIcon(hIcon);
        }

        // Fallback to class icon.
        var classBig = NativeMethods.GetClassLongPtrW(hwnd, NativeMethods.CLASS_LONG_INDEX_GCLP_HICON);
        if (classBig != IntPtr.Zero)
            return CreateImageSourceFromHIcon(classBig);

        var classSmall = NativeMethods.GetClassLongPtrW(hwnd, NativeMethods.CLASS_LONG_INDEX_GCLP_HICONSM);
        if (classSmall != IntPtr.Zero)
            return CreateImageSourceFromHIcon(classSmall);

        return null;
    }

    private static ImageSource? TryGetIconFromFile(string path)
    {
        try
        {
            _ = NativeMethods.SHGetFileInfoW(
                path,
                0,
                out var info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFOW>(),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);

            if (info.hIcon == IntPtr.Zero)
                return null;

            try
            {
                return CreateImageSourceFromHIcon(info.hIcon);
            }
            finally
            {
                NativeMethods.DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? CreateImageSourceFromHIcon(IntPtr hIcon)
    {
        try
        {
            var img = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(64, 64));
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }
}

