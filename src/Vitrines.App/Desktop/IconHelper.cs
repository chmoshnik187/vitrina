using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
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
        try
        {
            var key = CacheKey(path);
            return Cache.GetOrAdd(key, _ => Extract(path));
        }
        catch { return null; }
    }

    private static string CacheKey(string path)
    {
        if (Directory.Exists(path)) return "DIR:" + path.ToLowerInvariant();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        // для ярлыков/исполняемых — по конкретному файлу, для документов — по расширению
        if (ext is ".exe" or ".lnk" or ".ico" or ".url" or ".appref-ms") return "FILE:" + path.ToLowerInvariant();
        return "EXT:" + ext;
    }

    private static ImageSource? Extract(string path)
    {
        try
        {
            Icon? icon = null;
            if (Directory.Exists(path))
            {
                // общая иконка папки
                icon = Icon.ExtractAssociatedIcon(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                // попробуем лучше: SHGetFileInfo - но без p/invoke используем стандартную; для папки вернём null чтобы показать глиф
                icon?.Dispose();
                icon = null;
                return null;
            }

            if (!File.Exists(path)) return null;
            icon = Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            icon.Dispose();
            return src;
        }
        catch { return null; }
    }
}
