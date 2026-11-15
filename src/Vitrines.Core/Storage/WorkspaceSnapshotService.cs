using System.IO.Compression;

namespace Vitrines.Core.Storage;

/// <summary>
/// Снапшоты рабочего пространства: экспорт/импорт zip и именованные локальные профили
/// в <c>%LOCALAPPDATA%\Vitrines\snapshots</c>.
/// </summary>
public sealed class WorkspaceSnapshotService
{
    public static readonly string[] SnapshotFiles =
    {
        "layout.json", "rules.json", "settings.json", "inbox.json", "history.jsonl"
    };

    private readonly string _root;
    private readonly string _snapshotsDir;

    public WorkspaceSnapshotService(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vitrines");
        _snapshotsDir = Path.Combine(_root, "snapshots");
    }

    public string Root => _root;
    public string SnapshotsDirectory => _snapshotsDir;

    public async Task ExportAsync(string zipPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(zipPath)) File.Delete(zipPath);

        await using var zip = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(zip, ZipArchiveMode.Create);
        foreach (var name in SnapshotFiles)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(_root, name);
            if (File.Exists(path)) archive.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
        }
    }

    public async Task ImportAsync(string zipPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        // Сначала распаковываем во временную папку: битый архив не должен разрушить текущие данные.
        var staging = Path.Combine(_root, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await using (var stream = File.OpenRead(zipPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrWhiteSpace(name) || !SnapshotFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    var dest = Path.Combine(staging, name);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            foreach (var name in SnapshotFiles)
            {
                var src = Path.Combine(staging, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(_root, name), overwrite: true);
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    public IReadOnlyList<SnapshotInfo> List()
    {
        if (!Directory.Exists(_snapshotsDir)) return Array.Empty<SnapshotInfo>();
        try
        {
            return Directory.EnumerateFiles(_snapshotsDir, "*.zip")
                .Select(path =>
                {
                    var fi = new FileInfo(path);
                    return new SnapshotInfo(Path.GetFileNameWithoutExtension(fi.Name), fi.FullName, fi.CreationTime, fi.Length);
                })
                .OrderByDescending(s => s.CreatedAt)
                .ToList()
                .AsReadOnly();
        }
        catch { return Array.Empty<SnapshotInfo>(); }
    }

    public async Task<SnapshotInfo> CreateAsync(string name, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_snapshotsDir);
        var safe = SanitizeName(name);
        var path = Path.Combine(_snapshotsDir, safe + ".zip");
        if (File.Exists(path)) File.Delete(path);
        await ExportAsync(path, ct);
        var fi = new FileInfo(path);
        return new SnapshotInfo(safe, path, fi.CreationTime, fi.Length);
    }

    public Task RestoreAsync(SnapshotInfo snapshot, CancellationToken ct = default)
        => ImportAsync(snapshot.Path, ct);

    public Task RestoreAsync(string snapshotName, CancellationToken ct = default)
    {
        var snapshot = List().FirstOrDefault(s => string.Equals(s.Name, snapshotName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new FileNotFoundException("Снапшот не найден", snapshotName);
        return RestoreAsync(snapshot, ct);
    }

    public SnapshotInfo Rename(SnapshotInfo snapshot, string newName)
    {
        Directory.CreateDirectory(_snapshotsDir);
        var safe = SanitizeName(newName);
        var path = Path.Combine(_snapshotsDir, safe + ".zip");
        if (!string.Equals(path, snapshot.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(path)) throw new IOException("Профиль с таким именем уже существует.");
            File.Move(snapshot.Path, path);
        }
        var fi = new FileInfo(path);
        return new SnapshotInfo(safe, path, fi.CreationTime, fi.Length);
    }

    public void Delete(SnapshotInfo snapshot)
    {
        try { if (File.Exists(snapshot.Path)) File.Delete(snapshot.Path); } catch { }
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "snapshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") : cleaned;
    }
}

public sealed record SnapshotInfo(string Name, string Path, DateTime CreatedAt, long Size);
