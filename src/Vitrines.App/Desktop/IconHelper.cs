using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vitrines.App.Desktop;

public static class IconHelper
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string path)
    {
        try { return Cache.GetOrAdd(CacheKey(path), _ => Extract(path)); }
        catch { return null; }
    }

    private static string CacheKey(string path)
    {
        if (Directory.Exists(path)) return "DIR:" + path.ToLowerInvariant();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".exe" or ".lnk" or ".ico" or ".url" or ".appref-ms" or ".msi") return "FILE:" + path.ToLowerInvariant();
        return "EXT:" + ext;
    }

    private static ImageSource? Extract(string path)
    {
        try
        {
            // SHGetFileInfo даёт корректные иконки и для папок, и для файлов
            var flags = SHGFI_ICON | SHGFI_LARGEICON;
            if (!File.Exists(path) && !Directory.Exists(path)) return null;
            if (Directory.Exists(path)) flags |= SHGFI_USEFILEATTRIBUTES;
            var shinfo = new SHFILEINFO();
            var res = SHGetFileInfo(path, Directory.Exists(path) ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);
            if (res == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
            {
                // fallback
                if (File.Exists(path))
                {
                    using var ico = Icon.ExtractAssociatedIcon(path);
                    if (ico == null) return null;
                    var s = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    s.Freeze(); return s;
                }
                return null;
            }
            var src = Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            DestroyIcon(shinfo.hIcon);
            return src;
        }
        catch { return null; }
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
