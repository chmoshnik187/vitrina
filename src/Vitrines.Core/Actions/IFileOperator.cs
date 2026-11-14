namespace Vitrines.Core.Actions;

public interface IFileOperator
{
    /// <summary>Переместить файл/папку. При коллизии имён — безопасное переименование. Возвращает фактический путь назначения.</summary>
    Task<string> MoveAsync(string sourcePath, string targetDirectory, CancellationToken ct = default);

    /// <summary>Откат перемещения (используя UndoData = исходный путь).</summary>
    Task UndoMoveAsync(string currentPath, string originalPath, CancellationToken ct = default);
}

public sealed class FileOpResult
{
    public bool Success { get; init; }
    public string? FinalPath { get; init; }
    public string? Error { get; init; }
    public static FileOpResult Ok(string final) => new() { Success = true, FinalPath = final };
    public static FileOpResult Fail(string err) => new() { Success = false, Error = err };
}
