using Vitrines.Core.Actions;
using Vitrines.Core.Models;
using Vitrines.Core.Rules;
using Vitrines.Core.Storage;
using Vitrines.Core.Watcher;

namespace Vitrines.Core.Sorting;

/// <summary>
/// Связывает наблюдатель → движок правил → действие (размещение/создание витрины/перемещение)
/// → журнал. Автоматика объяснима: решение и причина пишутся в историю.
/// </summary>
public sealed class SortingOrchestrator : IDisposable
{
    private readonly IWorkspaceStore _store;
    private readonly IRuleEngine _rules;
    private readonly IFileOperator _fileOps;
    private readonly IFileWatcherService _watcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly EventHandler<StableFileEventArgs> _onFileReady;

    private WorkspaceData _data = new();
    private bool _started;

    public event EventHandler<HistoryEntry>? OnHistory;

    public SortingOrchestrator(IWorkspaceStore store, IRuleEngine rules, IFileOperator fileOps, IFileWatcherService watcher)
    {
        _store = store;
        _rules = rules;
        _fileOps = fileOps;
        _watcher = watcher;
        _onFileReady = (_, e) => _ = HandleFileReadyAsync(e.FullPath);
        _watcher.FileReady += _onFileReady;
    }

    public WorkspaceData Data => _data;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _data = await _store.LoadAsync(ct);
        var changed = false;
        if (_data.Watch.Sources.Count == 0)
        {
            // разумные дефолты-источники для первого запуска
            _data.Watch.Sources = DefaultSources();
            changed = true;
        }
        if (_data.Rules.Count == 0)
        {
            _data.Rules = Rules.DefaultRules.Create();
            await _store.SaveRulesAsync(_data, ct);
            changed = true;
        }
        if (_data.Vitrines.Count == 0)
        {
            // Создаём витрины-категории заранее, чтобы рабочий стол сразу выглядел как набор «витрин».
            _data.Vitrines = CreateDefaultVitrines();
            changed = true;
        }
        if (changed)
        {
            await _store.SaveLayoutAsync(_data, ct);
            await _store.SaveSettingsAsync(_data, ct);
        }
        _watcher.Configure(_data.Watch);
        _watcher.Start(_data.Watch.Sources);
        _started = true;
    }

    public void Stop() { _watcher.Stop(); _started = false; }

    /// <summary>Dry-run: оценить файлы в папке без применения действий.</summary>
    public IReadOnlyList<(string Path, RuleDecision Decision)> DryRunFolder(string folder)
    {
        if (!Directory.Exists(folder)) return Array.Empty<(string, RuleDecision)>();
        var paths = Directory.EnumerateFileSystemEntries(folder);
        return _rules.DryRun(paths, _data.Rules);
    }

    /// <summary>Разобрать уже существующие файлы в источниках (явное действие пользователя).</summary>
    public async Task<int> ScanExistingAsync(CancellationToken ct = default)
    {
        int acted = 0;
        var sources = _data.Watch.Sources.Where(Directory.Exists).ToList();
        foreach (var src in sources)
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(src); }
            catch { continue; }
            foreach (var path in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                if (ShouldSkipScanPath(path)) continue;
                if (IsPinned(path) || IsAlreadyPlaced(path)) continue;

                await _gate.WaitAsync(ct);
                try
                {
                    var kind = DetectKind(path);
                    var decision = _rules.Evaluate(path, kind, _data.Rules);
                    if (decision.Action != RuleActionType.Ignore)
                    {
                        await ApplyAsync(path, kind, decision);
                        acted++;
                    }
                }
                catch (Exception ex)
                {
                    await LogAsync(HistoryEvent.Error, path, null, null, null, ex.Message);
                }
                finally { _gate.Release(); }
            }
        }
        return acted;
    }

    private bool IsAlreadyPlaced(string path)
    {
        var full = Path.GetFullPath(path);
        return _data.Vitrines.SelectMany(v => v.Items).Any(i =>
            string.Equals(Path.GetFullPath(i.Path), full, StringComparison.OrdinalIgnoreCase));
    }

    private bool ShouldSkipScanPath(string path)
    {
        // не трогаем управляемое хранилище витрин и служебные папки
        try
        {
            var full = Path.GetFullPath(path);
            var storageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vitrines", "storage");
            if (VitrineStorage.IsInDirectory(full, storageRoot)) return true;
        }
        catch { }
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (_data.Watch.TempExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileName(path);
        if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private async Task HandleFileReadyAsync(string path)
    {
        if (!_started || !_data.Watch.AutomationEnabled) return;
        if (!File.Exists(path) && !Directory.Exists(path)) return;

        await _gate.WaitAsync();
        try
        {
            // не трогаем закреплённые элементы
            if (IsPinned(path)) { await LogAsync(HistoryEvent.Skipped, path, null, null, null, "Элемент закреплён вручную"); return; }

            var kind = DetectKind(path);
            var decision = _rules.Evaluate(path, kind, _data.Rules);
            await ApplyAsync(path, kind, decision);
        }
        catch (Exception ex)
        {
            await LogAsync(HistoryEvent.Error, path, null, null, null, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyAsync(string path, ItemKind kind, RuleDecision decision)
    {
        switch (decision.Action)
        {
            case RuleActionType.Ignore:
                await LogAsync(HistoryEvent.Skipped, path, null, null, decision.MatchedRule?.Id, decision.Reason);
                return;

            case RuleActionType.MoveToFolder:
            {
                var targetDir = decision.MatchedRule?.Action.TargetFolderPath;
                if (string.IsNullOrWhiteSpace(targetDir))
                { await LogAsync(HistoryEvent.Skipped, path, null, null, decision.MatchedRule?.Id, "Не задана целевая папка"); return; }
                var final = await MoveTrackedAsync(path, targetDir);
                await LogAsync(HistoryEvent.Moved, path, path, final, decision.MatchedRule?.Id, decision.Reason, undoData: path);
                return;
            }

            case RuleActionType.PlaceInVitrine:
            case RuleActionType.CreateVitrineAndPlace:
            {
                var vitrine = ResolveOrCreateVitrine(decision);
                if (vitrine == null)
                { await LogAsync(HistoryEvent.Skipped, path, null, null, decision.MatchedRule?.Id, "Не удалось определить витрину"); return; }

                // Портал = перенос в реальную папку. Коллекция = перенос в управляемую папку витрины (физическая сортировка).
                string effectivePath = path;
                string? undo = null;
                if (vitrine.Kind == VitrineKind.Portal && !string.IsNullOrWhiteSpace(vitrine.FolderPath))
                {
                    effectivePath = await MoveTrackedAsync(path, vitrine.FolderPath);
                    undo = path;
                }
                else if (vitrine.Kind == VitrineKind.Collection)
                {
                    var storage = VitrineStorage.GetManagedFolder(vitrine);
                    // если файл уже лежит в целевой управляемой папке — не двигаем
                    if (!VitrineStorage.IsInDirectory(path, storage))
                    {
                        effectivePath = await MoveTrackedAsync(path, storage);
                        undo = path;
                    }
                }

                var item = new VitrineItem
                {
                    Path = effectivePath,
                    DisplayName = Path.GetFileName(effectivePath),
                    Kind = kind,
                    PlacedByRuleId = decision.MatchedRule?.Id
                };
                // не дублируем один и тот же путь
                if (!vitrine.Items.Any(i => string.Equals(i.Path, effectivePath, StringComparison.OrdinalIgnoreCase)))
                    vitrine.Items.Add(item);

                await _store.SaveLayoutAsync(_data);
                await LogAsync(HistoryEvent.Placed, effectivePath, path, vitrine.Name, decision.MatchedRule?.Id, decision.Reason, undoData: undo);
                return;
            }
        }
    }

    private Vitrine? ResolveOrCreateVitrine(RuleDecision decision)
    {
        if (decision.TargetVitrineId is Guid id)
        {
            var v = _data.Vitrines.FirstOrDefault(x => x.Id == id);
            if (v != null) return v;
        }
        if (decision.Action == RuleActionType.CreateVitrineAndPlace)
        {
            var name = string.IsNullOrWhiteSpace(decision.TargetVitrineName) ? "Новая витрина" : decision.TargetVitrineName!;
            var existing = _data.Vitrines.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;
            var created = new Vitrine { Name = name, Kind = VitrineKind.Collection };
            // простая раскладка каскадом, чтобы не накладывать
            created.X = 40 + (_data.Vitrines.Count % 6) * 40;
            created.Y = 40 + (_data.Vitrines.Count % 6) * 30;
            _data.Vitrines.Add(created);
            _ = _store.SaveLayoutAsync(_data);
            _ = LogAsync(HistoryEvent.CreatedVitrine, name, null, name, decision.MatchedRule?.Id, "Создана новая витрина для класса элементов");
            return created;
        }
        return null;
    }

    private async Task<string> MoveTrackedAsync(string source, string targetDir)
    {
        var fullTarget = Path.GetFullPath(Path.Combine(targetDir, Path.GetFileName(source)));
        _watcher.MarkSelfProduced(source);
        _watcher.MarkSelfProduced(fullTarget);
        var final = await _fileOps.MoveAsync(source, targetDir);
        _watcher.MarkSelfProduced(final);
        return final;
    }

    private bool IsPinned(string path) =>
        _data.Vitrines.SelectMany(v => v.Items).Any(i => i.IsPinned && string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    private static ItemKind DetectKind(string path)
    {
        if (Directory.Exists(path)) return ItemKind.Folder;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".lnk") return ItemKind.Shortcut;
        if (ext is ".exe" or ".msi" or ".bat" or ".cmd") return ItemKind.Application;
        return ItemKind.File;
    }

    // storage helpers moved to VitrineStorage

    private async Task LogAsync(HistoryEvent ev, string itemPath, string? from, string? to, Guid? ruleId, string reason, string? undoData = null)
    {
        var entry = new HistoryEntry
        {
            Event = ev,
            ItemPath = itemPath,
            From = from,
            To = to,
            RuleId = ruleId,
            Reason = reason,
            UndoData = undoData
        };
        await _store.AppendHistoryAsync(entry);
        OnHistory?.Invoke(this, entry);
    }

    private static List<Vitrine> CreateDefaultVitrines()
    {
        var names = new[] { "Изображения", "Документы", "Архивы", "Установщики", "Ярлыки" };
        var list = new List<Vitrine>();
        for (int i = 0; i < names.Length; i++)
        {
            list.Add(new Vitrine
            {
                Name = names[i],
                Kind = VitrineKind.Collection,
                X = 40 + (i % 3) * 340,
                Y = 40 + (i / 3) * 270,
                Width = 320,
                Height = 240
            });
        }
        // отдельная витрина для «Неразобранное» (пользователь может складывать вручную)
        list.Add(new Vitrine { Name = "Неразобранное", Kind = VitrineKind.Collection, X = 40 + (2 * 340), Y = 40 + (1 * 270), Width = 320, Height = 240 });
        return list;
    }

    private static List<string> DefaultSources()
    {
        var list = new List<string>();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(desktop)) list.Add(desktop);
        if (Directory.Exists(downloads)) list.Add(downloads);
        return list;
    }

    public void MarkSelfProduced(string path) => _watcher.MarkSelfProduced(path);

    /// <summary>Откат последнего обратимого действия (перемещения).</summary>
    public async Task<bool> UndoLastAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var history = await _store.ReadHistoryAsync(50, ct);
            var entry = history.FirstOrDefault(h => h.Event is HistoryEvent.Moved or HistoryEvent.Placed && !string.IsNullOrEmpty(h.UndoData));
            if (entry == null || string.IsNullOrEmpty(entry.UndoData)) return false;

            var current = entry.ItemPath;
            var original = entry.UndoData!;
            if (!File.Exists(current) && !Directory.Exists(current)) return false;

            _watcher.MarkSelfProduced(current);
            _watcher.MarkSelfProduced(original);
            await _fileOps.UndoMoveAsync(current, original, ct);
            _watcher.MarkSelfProduced(original);

            // убрать/обновить ссылку в витрине, если указывает на current
            foreach (var v in _data.Vitrines)
            {
                var it = v.Items.FirstOrDefault(i => string.Equals(i.Path, current, StringComparison.OrdinalIgnoreCase));
                if (it != null) it.Path = original;
            }
            await _store.SaveLayoutAsync(_data, ct);
            await LogAsync(HistoryEvent.Undone, original, current, original, entry.RuleId, "Откат перемещения", undoData: null);
            return true;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _watcher.FileReady -= _onFileReady;
        _watcher.Dispose();
        _gate.Dispose();
    }
}
