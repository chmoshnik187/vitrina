using System.Collections.Concurrent;
using Vitrines.Core.Storage;

namespace Vitrines.Core.Watcher;

/// <summary>
/// Наблюдает за папками-источниками: дебаунс, проверка стабильности файла,
/// фильтр временных расширений и защита от повторной обработки собственных операций.
/// </summary>
public sealed class FileWatcherService : IFileWatcherService
{
    private WatchSettings _settings;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _selfProduced = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    public event EventHandler<StableFileEventArgs>? FileReady;

    public FileWatcherService(WatchSettings? settings = null) => _settings = settings ?? new WatchSettings();

    public void Configure(WatchSettings settings) => _settings = settings;

    public void Start(IEnumerable<string> sourcePaths)
    {
        StopWatchers();
        foreach (var path in sourcePaths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true
            };
            w.Created += OnFsEvent;
            w.Changed += OnFsEvent;
            w.Renamed += OnRenamed;
            _watchers.Add(w);
        }
        _ = Task.Run(LoopAsync);
    }

    public void Stop()
    {
        _cts.Cancel();
        StopWatchers();
    }

    public void MarkSelfProduced(string path)
    {
        var full = Path.GetFullPath(path);
        _selfProduced[full] = DateTimeOffset.UtcNow.AddSeconds(Math.Max(10, _settings.StableSeconds * 4));
    }

    private void StopWatchers()
    {
        lock (_gate)
        {
            foreach (var w in _watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
            _watchers.Clear();
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => Touch(e.FullPath, e.ChangeType);
    private void OnRenamed(object sender, RenamedEventArgs e) => Touch(e.FullPath, e.ChangeType);

    private void Touch(string path, WatcherChangeTypes changeType)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (_settings.TempExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return;
        if (IsSelfProduced(path)) return;

        var full = Path.GetFullPath(path);
        _pending.AddOrUpdate(full,
            _ => Pending.New(changeType),
            (_, p) => { p.Touch(changeType); return p; });
    }

    private bool IsSelfProduced(string path)
    {
        var full = Path.GetFullPath(path);
        if (_selfProduced.TryGetValue(full, out var until))
        {
            if (DateTimeOffset.UtcNow <= until) return true;
            _selfProduced.TryRemove(full, out _);
        }
        return false;
    }

    private async Task LoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(750, token); } catch { break; }
            var now = DateTimeOffset.UtcNow;
            foreach (var kv in _pending)
            {
                if (token.IsCancellationRequested) break;
                var path = kv.Key;
                var p = kv.Value;
                if (IsSelfProduced(path)) { _pending.TryRemove(path, out _); continue; }
                if ((now - p.LastTouched).TotalSeconds < _settings.StableSeconds) continue;

                if (!TryGetSnapshot(path, out var len, out var write))
                {
                    // файл исчез — просто снимаем с очереди
                    _pending.TryRemove(path, out _);
                    continue;
                }

                if (p.LastLength == len && p.LastWrite == write)
                {
                    // стабилен: финальная проверка, что не временный и не залочен "жёстко" на чтение метаданных
                    _pending.TryRemove(path, out _);
                    var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                    if (_settings.TempExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                    if (IsSelfProduced(path)) continue;
                    try { FileReady?.Invoke(this, new StableFileEventArgs { FullPath = path, ChangeType = p.ChangeType }); }
                    catch { /* обработчик не должен ронять цикл наблюдателя */ }
                }
                else
                {
                    p.LastLength = len;
                    p.LastWrite = write;
                    p.LastTouched = now; // ждём ещё один стабильный интервал
                }
            }
        }
    }

    private static bool TryGetSnapshot(string path, out long length, out DateTimeOffset write)
    {
        length = 0; write = default;
        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                length = fi.Length;
                write = fi.LastWriteTimeUtc;
                return true;
            }
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                length = 0;
                write = di.LastWriteTimeUtc;
                return true;
            }
        }
        catch { /* недоступен/залочен — попробуем позже */ }
        return false;
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }

    private sealed class Pending
    {
        public WatcherChangeTypes ChangeType { get; private set; }
        public DateTimeOffset LastTouched { get; set; } = DateTimeOffset.UtcNow;
        public long LastLength { get; set; } = -1;
        public DateTimeOffset LastWrite { get; set; } = DateTimeOffset.MinValue;

        public static Pending New(WatcherChangeTypes t) => new() { ChangeType = t };
        public void Touch(WatcherChangeTypes t) { ChangeType = t; LastTouched = DateTimeOffset.UtcNow; }
    }
}
