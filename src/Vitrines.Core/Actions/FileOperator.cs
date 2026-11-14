namespace Vitrines.Core.Actions;

/// <summary>Безопасные файловые операции: move с неразрушающим разрешением коллизий имён.</summary>
public sealed class FileOperator : IFileOperator
{
    public Task<string> MoveAsync(string sourcePath, string targetDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            throw new FileNotFoundException("Источник не найден", sourcePath);

        Directory.CreateDirectory(targetDirectory);
        var name = Path.GetFileName(sourcePath);
        var dest = ResolveCollision(targetDirectory, name);
        Directory.Move(sourcePath, dest); // работает и для файлов, и для папок в .NET
        return Task.FromResult(dest);
    }

    public Task UndoMoveAsync(string currentPath, string originalPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            throw new FileNotFoundException("Текущий путь не найден для отката", currentPath);

        var dir = Path.GetDirectoryName(originalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var dest = File.Exists(originalPath) || Directory.Exists(originalPath)
            ? ResolveCollision(dir ?? Path.GetPathRoot(originalPath) ?? ".", Path.GetFileName(originalPath))
            : originalPath;
        Directory.Move(currentPath, dest);
        return Task.CompletedTask;
    }

    private static string ResolveCollision(string dir, string name)
    {
        var dest = Path.Combine(dir, name);
        if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }
}
