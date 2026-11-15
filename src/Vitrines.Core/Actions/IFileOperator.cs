namespace Vitrines.Core.Actions;

public interface IFileOperator
{
    /// <summary>Переместить файл/папку. При коллизии имён — согласно <see cref="FileOpOptions"/>. Возвращает фактический путь назначения.</summary>
    Task<string> MoveAsync(string sourcePath, string targetDirectory, CancellationToken ct = default);

    /// <summary>Перемещение с настройками конфликтов/повторов (для залоченных файлов).</summary>
    Task<string> MoveAsync(string sourcePath, string targetDirectory, FileOpOptions? options, CancellationToken ct = default);

    /// <summary>Переименовать файл/папку в её текущей папке (неразрушающе при коллизии).</summary>
    Task<string> RenameAsync(string currentPath, string newName, CancellationToken ct = default);

    /// <summary>Откат перемещения (используя UndoData = исходный путь).</summary>
    Task UndoMoveAsync(string currentPath, string originalPath, CancellationToken ct = default);
}

/// <summary>Политика разрешения конфликта имён при перемещении.</summary>
public enum NameConflictPolicy
{
    /// <summary>Безопасно переименовать («файл (1).ext»). По умолчанию.</summary>
    Rename,
    /// <summary>Пропустить перемещение, если цель уже существует.</summary>
    Skip,
    /// <summary>Заменить существующий элемент (деструктивно; только по явному выбору).</summary>
    Replace
}

public sealed class FileOpOptions
{
    public NameConflictPolicy Conflict { get; init; } = NameConflictPolicy.Rename;
    /// <summary>Сколько раз повторять операцию при временной блокировке файла (0 = без повторов).</summary>
    public int MaxRetries { get; init; } = 4;
    /// <summary>Базовая пауза между повторами (умножается на номер попытки).</summary>
    public int RetryDelayMs { get; init; } = 350;

    public static FileOpOptions Default { get; } = new();
}

/// <summary>Конфликт имён при политике <see cref="NameConflictPolicy.Skip"/>.</summary>
public sealed class FileConflictException : IOException
{
    public string Destination { get; }
    public FileConflictException(string destination)
        : base($"В папке назначения уже есть элемент с таким именем: {Path.GetFileName(destination)}")
        => Destination = destination;
}

public sealed class FileOpResult
{
    public bool Success { get; init; }
    public string? FinalPath { get; init; }
    public string? Error { get; init; }
    public static FileOpResult Ok(string final) => new() { Success = true, FinalPath = final };
    public static FileOpResult Fail(string err) => new() { Success = false, Error = err };
}
