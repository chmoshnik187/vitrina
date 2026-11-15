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
    private CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Task? _loopTask;

    public event EventHandler<StableFileEventArgs>? FileReady;
    public event EventHandler<WatcherErrorEventArgs>? Error;

    public FileWatcherService(WatchSettings? settings = null) => _settings = settings ?? new WatchSettings();

    public void Configure(WatchSettings settings) => _settings = settings;

    public void Start(IEnumerable<string> sourcePaths)
    {
        StopWatchers();

        CancellationToken token;
        lock (_gate)
        {
            if (_cts.IsCancellationRequested)
            {
                try { _cts.Dispose(); } catch { }
                _cts = new CancellationTokenSource();
            }
            token = _cts.Token;
            _pending.Clear();
        }

        foreach (var path in sourcePaths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var w = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = _settings.Recursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };
                w.Created += OnFsEvent;
                w.Changed += OnFsEvent;
                w.Deleted += OnDeleted;
                w.Renamed += OnRenamed;
                w.Error += (_, e) => OnWatcherError(path, e.GetException());
                lock (_gate) _watchers.Add(w);
            }
            catch { /* источник недоступен — пропускаем */ }
        }

        _loopTask = Task.Run(() => LoopAsync(token), token);
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        StopWatchers();
    }

    public void MarkSelfProduced(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            _selfProduced[full] = DateTimeOffset.UtcNow.AddSeconds(Math.Max(10, _settings.StableSeconds * 4));
        }
        catch { }
    }

    private void StopWatchers()
    {
        lock (_gate)
        {
            foreach (var w in _watchers)
            {
                try { w.EnableRaisingEvents = false; } catch { }
                try { w.Dispose(); } catch { }
            }
            _watchers.Clear();
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => Touch(e.FullPath, e.ChangeType);
    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        try { _pending.TryRemove(Path.GetFullPath(e.FullPath), out _); } catch { }
    }
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        try { _pending.TryRemove(Path.GetFullPath(e.OldFullPath), out _); } catch { }
        Touch(e.FullPath, e.ChangeType);
    }

    private void OnWatcherError(string sourcePath, Exception? ex)
    {
        var overflow = ex is InternalBufferOverflowException;
        try
        {
            Error?.Invoke(this, new WatcherErrorEventArgs
            {
                SourcePath = sourcePath,
                Exception = ex,
                BufferOverflow = overflow,
                Message = overflow
                    ? "Переполнение буфера наблюдателя: возможна потеря части событий, рекомендуется выполнить «Разобрать сейчас»."
                    : (ex?.Message ?? "Ошибка наблюдателя файловой системы.")
            });
        }
        catch { }
    }

    private void Touch(string path, WatcherChangeTypes changeType)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full;
        try { full = Path.GetFullPath(path); } catch { return; }
        var ext = Path.GetExtension(full).TrimStart('.').ToLowerInvariant();
        if (_settings.TempExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return;
        if (_settings.ExcludeExtensions.Any(e => string.Equals(e.TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase))) return;
        var name = Path.GetFileName(full);
        if (_settings.ExcludeNameContains.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) return;
        if (_settings.ExcludeFolders.Any(f => IsUnder(full, f))) return;
        // не реагируем на изменения внутри управляемого хранилища витрин
        try
        {
            var storageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vitrines", "storage");
            if (IsUnder(full, storageRoot)) return;
        }
        catch { }
        if (IsSelfProduced(full)) return;

        if (_pending.Count > 20000 && !_pending.ContainsKey(full)) return;
        _pending.AddOrUpdate(full,
            _ => Pending.New(changeType),
            (_, p) => { p.Touch(changeType); return p; });
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var fp = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fr = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fp.StartsWith(fr, StringComparison.OrdinalIgnoreCase);
        }
        catch { return path.StartsWith(root, StringComparison.OrdinalIgnoreCase); }
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

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(750, token); } catch { break; }
            var now = DateTimeOffset.UtcNow;

            // периодическая очистка устаревших меток self-produced
            foreach (var sp in _selfProduced)
                if (sp.Value < now.AddSeconds(-2)) _selfProduced.TryRemove(sp.Key, out _);

            // защита от неограниченного роста очереди при всплесках событий
            const int maxPending = 10000;
            if (_pending.Count > maxPending)
            {
                foreach (var kv in _pending.OrderBy(k => k.Value.LastTouched).Take(_pending.Count - maxPending))
                    _pending.TryRemove(kv.Key, out _);
            }

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
        try { _cts.Dispose(); } catch { }
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
