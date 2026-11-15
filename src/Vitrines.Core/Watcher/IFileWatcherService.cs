using Vitrines.Core.Storage;

namespace Vitrines.Core.Watcher;

/// <summary>
/// Наблюдение за источниками: дебаунс, детектор стабильности, фильтр временных файлов,
/// защита от повторной обработки (анти-петля). Реализация — в фазе 1.
/// </summary>
public interface IFileWatcherService : IDisposable
{
    event EventHandler<StableFileEventArgs>? FileReady;
    /// <summary>Ошибки наблюдателя (например, переполнение буфера). Сигнализирует о риске пропуска событий.</summary>
    event EventHandler<WatcherErrorEventArgs>? Error;

    void Configure(WatchSettings settings);
    void Start(IEnumerable<string> sourcePaths);
    void Stop();

    /// <summary>Пометить путь как результат нашей операции, чтобы не обрабатывать повторно.</summary>
    void MarkSelfProduced(string path);
}

public sealed class StableFileEventArgs : EventArgs
{
    public required string FullPath { get; init; }
    public required WatcherChangeTypes ChangeType { get; init; }
}

public sealed class WatcherErrorEventArgs : EventArgs
{
    public string? SourcePath { get; init; }
    public Exception? Exception { get; init; }
    public string Message { get; init; } = string.Empty;
    /// <summary>Признак переполнения буфера FileSystemWatcher (возможна потеря событий).</summary>
    public bool BufferOverflow { get; init; }
}
