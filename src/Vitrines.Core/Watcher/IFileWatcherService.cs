using Vitrines.Core.Storage;

namespace Vitrines.Core.Watcher;

/// <summary>
/// Наблюдение за источниками: дебаунс, детектор стабильности, фильтр временных файлов,
/// защита от повторной обработки (анти-петля). Реализация — в фазе 1.
/// </summary>
public interface IFileWatcherService : IDisposable
{
    event EventHandler<StableFileEventArgs>? FileReady;

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
