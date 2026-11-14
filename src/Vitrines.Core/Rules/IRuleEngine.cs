using Vitrines.Core.Models;
using Vitrines.Core.Storage;

namespace Vitrines.Core.Rules;

public interface IRuleEngine
{
    RuleDecision Evaluate(string path, ItemKind kind, IEnumerable<SortingRule> rules, FileFacts? facts = null, WatchSettings? watch = null);
    IReadOnlyList<(string Path, RuleDecision Decision)> DryRun(IEnumerable<string> paths, IEnumerable<SortingRule> rules, WatchSettings? watch = null);
}

public sealed class FileFacts
{
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public ItemKind Kind { get; init; }
    public long Size { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public DateTimeOffset? Created { get; init; }
    public string? SourceRoot { get; init; }

    public static FileFacts FromPath(string path, string? sourceRoot = null)
    {
        var name = System.IO.Path.GetFileName(path);
        var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var kind = DetectKind(path);
        long size = 0; DateTimeOffset? mod = null; DateTimeOffset? created = null;
        try
        {
            if (File.Exists(path)) { var fi = new FileInfo(path); size = fi.Length; mod = fi.LastWriteTimeUtc; created = fi.CreationTimeUtc; }
            else if (Directory.Exists(path)) { var di = new DirectoryInfo(path); mod = di.LastWriteTimeUtc; created = di.CreationTimeUtc; }
        }
        catch { }
        return new FileFacts { Path = path, Name = name, Extension = ext, Kind = kind, Size = size, Modified = mod, Created = created, SourceRoot = sourceRoot };
    }

    public static ItemKind DetectKind(string path)
    {
        if (Directory.Exists(path)) return ItemKind.Folder;
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".lnk" || ext == ".url") return ItemKind.Shortcut;
        if (ext is ".exe" or ".msi" or ".bat" or ".cmd" or ".com" or ".appref-ms") return ItemKind.Application;
        return ItemKind.File;
    }
}
