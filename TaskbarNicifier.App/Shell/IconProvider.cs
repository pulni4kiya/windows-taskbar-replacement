using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using TaskbarNicifier.App.Interop;

namespace TaskbarNicifier.App.Shell;

public sealed class IconProvider
{
    public ImageSource? TryGetIconForWindows(IReadOnlyList<AppWindowItem> windows)
    {
        if (windows.Count == 0)
            return null;

        var aumid = windows.Select(w => w.AppUserModelId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (!string.IsNullOrWhiteSpace(aumid))
        {
            var src = TryGetIconFromAumid(aumid!);
            if (src is not null)
                return src;
        }

        var identityPath = windows.Select(w => w.IdentityProcessPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (!string.IsNullOrWhiteSpace(identityPath))
        {
            var src = TryGetIconFromPackageAssets(identityPath!);
            if (src is not null)
                return src;
        }

        // Best effort: try window icons first, then fallback to file icon.
        var hwnd = windows[0].Hwnd;
        if (hwnd != IntPtr.Zero)
        {
            var src = TryGetWindowIcon(hwnd);
            if (src is not null)
                return src;
        }

        var path =
            identityPath ??
            windows.Select(w => w.ProcessPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var src = TryGetIconFromFile(path!);
            if (src is not null)
                return src;
        }

        return null;
    }

    private static ImageSource? TryGetIconFromPackageAssets(string processPath)
    {
        try
        {
            var exe = new FileInfo(processPath);
            var packageDir = exe.Directory;
            if (packageDir is null)
                return null;

            var manifestPath = Path.Combine(packageDir.FullName, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
                manifestPath = Path.Combine(packageDir.FullName, "Package.appxmanifest");
            if (!File.Exists(manifestPath))
                return null;

            var doc = XDocument.Load(manifestPath);
            var visualElements = doc
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "VisualElements");
            if (visualElements is null)
                return null;

            var logoCandidates = new[]
            {
                "Square44x44Logo",
                "Square150x150Logo",
                "Logo",
            };

            foreach (var attrName in logoCandidates)
            {
                var relativeLogo = visualElements.Attribute(attrName)?.Value;
                var logoPath = ResolvePackageAssetPath(packageDir.FullName, relativeLogo);
                if (logoPath is null)
                    continue;

                var src = TryLoadBitmapImage(logoPath);
                if (src is not null)
                    return src;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? ResolvePackageAssetPath(string packageDir, string? manifestAssetPath)
    {
        if (string.IsNullOrWhiteSpace(manifestAssetPath))
            return null;

        var direct = Path.Combine(packageDir, manifestAssetPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct))
            return direct;

        var dir = Path.GetDirectoryName(direct);
        var fileName = Path.GetFileNameWithoutExtension(direct);
        var ext = Path.GetExtension(direct);
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(ext) || !Directory.Exists(dir))
            return null;

        return Directory
            .EnumerateFiles(dir, $"{fileName}*{ext}", SearchOption.TopDirectoryOnly)
            .OrderByDescending(GetPackageAssetScore)
            .FirstOrDefault();
    }

    private static int GetPackageAssetScore(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Contains("scale-400", StringComparison.OrdinalIgnoreCase)) return 400;
        if (name.Contains("scale-300", StringComparison.OrdinalIgnoreCase)) return 300;
        if (name.Contains("scale-200", StringComparison.OrdinalIgnoreCase)) return 200;
        if (name.Contains("scale-150", StringComparison.OrdinalIgnoreCase)) return 150;
        if (name.Contains("scale-125", StringComparison.OrdinalIgnoreCase)) return 125;
        if (name.Contains("scale-100", StringComparison.OrdinalIgnoreCase)) return 100;
        return 0;
    }

    private static ImageSource? TryLoadBitmapImage(string path)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 64;
            img.UriSource = new Uri(path, UriKind.Absolute);
            img.EndInit();
            img.Freeze();
            return TrimTransparentPadding(img) ?? img;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TrimTransparentPadding(BitmapSource source)
    {
        try
        {
            var bitmap = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            if (width <= 0 || height <= 0)
                return null;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);

            var left = width;
            var top = height;
            var right = -1;
            var bottom = -1;

            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var alpha = pixels[row + (x * 4) + 3];
                    if (alpha <= 8)
                        continue;

                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }

            if (right < left || bottom < top)
                return null;

            var contentWidth = right - left + 1;
            var contentHeight = bottom - top + 1;
            var padding = Math.Max(1, (int)Math.Round(Math.Max(contentWidth, contentHeight) * 0.08));

            left = Math.Max(0, left - padding);
            top = Math.Max(0, top - padding);
            right = Math.Min(width - 1, right + padding);
            bottom = Math.Min(height - 1, bottom + padding);

            if (left == 0 && top == 0 && right == width - 1 && bottom == height - 1)
                return source;

            var cropped = new CroppedBitmap(bitmap, new System.Windows.Int32Rect(
                left,
                top,
                right - left + 1,
                bottom - top + 1));
            cropped.Freeze();
            return cropped;
        }
        catch
        {
            return null;
        }
    }

    public ImageSource? TryGetIconForGroup(AppWindowGroup group)
        => TryGetIconForWindows(group.Windows);

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

    private static ImageSource? TryGetIconFromAumid(string appUserModelId)
    {
        var hbm = NativeMethods.TryGetAppsFolderIconBitmap(appUserModelId, 64);
        if (hbm == IntPtr.Zero)
            return null;

        try
        {
            var img = Imaging.CreateBitmapSourceFromHBitmap(
                hbm,
                IntPtr.Zero,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(64, 64));
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = NativeMethods.DeleteObject(hbm);
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

