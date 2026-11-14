using Vitrines.Core.Models;

namespace Vitrines.Core.Storage;

public static class VitrineStorage
{
    public static string GetManagedFolder(Vitrine vitrine)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vitrines", "storage");
        var safeName = SanitizeName(string.IsNullOrWhiteSpace(vitrine.Name) ? "Витрина" : vitrine.Name);
        var suffix = vitrine.Id.ToString("N");
        var name = $"{safeName}_{suffix}";
        if (name.Length > 80) name = name[..80];
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static bool IsInDirectory(string path, string dir)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(s) ? "Витрина" : s;
    }
}
