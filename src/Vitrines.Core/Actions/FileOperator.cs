namespace Vitrines.Core.Actions;

/// <summary>Безопасные файловые операции: move с настраиваемым разрешением коллизий, повторами и rename.</summary>
public sealed class FileOperator : IFileOperator
{
    public Task<string> MoveAsync(string sourcePath, string targetDirectory, CancellationToken ct = default)
        => MoveAsync(sourcePath, targetDirectory, FileOpOptions.Default, ct);

    public async Task<string> MoveAsync(string sourcePath, string targetDirectory, FileOpOptions? options, CancellationToken ct = default)
    {
        options ??= FileOpOptions.Default;
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            throw new FileNotFoundException("Источник не найден", sourcePath);
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("Не задана папка назначения", nameof(targetDirectory));

        Directory.CreateDirectory(targetDirectory);
        EnsureNotInsideItself(sourcePath, targetDirectory);

        var name = Path.GetFileName(sourcePath);
        var dest = ResolveDestination(targetDirectory, name, options.Conflict);
        await MoveWithRetryAsync(sourcePath, dest, options, ct).ConfigureAwait(false);
        return dest;
    }

    public async Task<string> RenameAsync(string currentPath, string newName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            throw new FileNotFoundException("Элемент не найден", currentPath);
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Пустое новое имя", nameof(newName));

        newName = Path.GetFileName(newName.Trim());
        if (string.IsNullOrWhiteSpace(newName) || newName is "." or "..")
            throw new ArgumentException("Некорректное новое имя", nameof(newName));

        var dir = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrEmpty(dir)) dir = Path.GetPathRoot(currentPath) ?? ".";
        var dest = Path.Combine(dir, newName);
        if (string.Equals(dest, currentPath, StringComparison.OrdinalIgnoreCase)) return currentPath;

        if (File.Exists(dest) || Directory.Exists(dest))
            dest = ResolveCollision(dir, newName);

        await MoveWithRetryAsync(currentPath, dest, FileOpOptions.Default, ct).ConfigureAwait(false);
        return dest;
    }

    public async Task UndoMoveAsync(string currentPath, string originalPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            throw new FileNotFoundException("Текущий путь не найден для отката", currentPath);

        var dir = Path.GetDirectoryName(originalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var dest = File.Exists(originalPath) || Directory.Exists(originalPath)
            ? ResolveCollision(dir ?? Path.GetPathRoot(originalPath) ?? ".", Path.GetFileName(originalPath))
            : originalPath;

        await MoveWithRetryAsync(currentPath, dest, FileOpOptions.Default, ct).ConfigureAwait(false);
    }

    private static string ResolveDestination(string dir, string name, NameConflictPolicy policy)
    {
        var dest = Path.Combine(dir, name);
        var exists = File.Exists(dest) || Directory.Exists(dest);
        if (!exists) return dest;

        switch (policy)
        {
            case NameConflictPolicy.Skip:
                throw new FileConflictException(dest);
            case NameConflictPolicy.Replace:
                RemoveExisting(dest);
                return dest;
            default:
                return ResolveCollision(dir, name);
        }
    }

    private static void RemoveExisting(string dest)
    {
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        else if (File.Exists(dest)) File.Delete(dest);
    }

    private static async Task MoveWithRetryAsync(string source, string dest, FileOpOptions options, CancellationToken ct)
    {
        var attempts = Math.Clamp(options.MaxRetries, 0, 20);
        var delay = Math.Clamp(options.RetryDelayMs, 0, 10_000);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(source, dest);
                return;
            }
            catch (Exception ex) when (attempt < attempts && IsTransient(ex))
            {
                if (delay > 0) await Task.Delay(delay * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private static void EnsureNotInsideItself(string source, string targetDirectory)
    {
        if (!Directory.Exists(source)) return;
        try
        {
            var src = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dst = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase) ||
                dst.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Нельзя переместить папку внутрь самой себя.");
        }
        catch (InvalidOperationException) { throw; }
        catch { }
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
