using Vitrines.Core.Actions;
using Vitrines.Core.Models;
using Vitrines.Core.Rules;
using Vitrines.Core.Storage;
using Vitrines.Core.Watcher;

namespace Vitrines.Core.Sorting;

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
    public event EventHandler? OnStateChanged;

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
    public IWorkspaceStore Store => _store;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _data = await _store.LoadAsync(ct);
        var changed = false;
        if (_data.Watch.Sources.Count == 0) { _data.Watch.Sources = DefaultSources(); changed = true; }
        if (_data.Rules.Count == 0) { _data.Rules = DefaultRules.Create(); await _store.SaveRulesAsync(_data, ct); changed = true; }
        if (_data.Vitrines.Count == 0) { _data.Vitrines = CreateDefaultVitrines(); changed = true; }
        _data.Watch.Appearance ??= new AppearanceSettings();
        _data.Inbox ??= new();
        Normalize();
        if (changed) { await _store.SaveLayoutAsync(_data, ct); await _store.SaveSettingsAsync(_data, ct); }
        _watcher.Configure(_data.Watch);
        _watcher.Start(_data.Watch.Sources);
        _started = true;
    }

    private void Normalize()
    {
        _data.Watch.Sources ??= new();
        _data.Watch.TempExtensions ??= new() { "crdownload", "part", "tmp", "download" };
        _data.Watch.ExcludeFolders ??= new();
        _data.Watch.ExcludeExtensions ??= new();
        _data.Watch.ExcludeNameContains ??= new();
        _data.Watch.Appearance ??= new AppearanceSettings();
        foreach (var r in _data.Rules) r.Condition ??= new RuleCondition();
        // обеспечить наличие витрины "Неразобранное" при политике inbox/placement
        if (_data.Watch.Unmatched != UnmatchedPolicy.Ignore && !_data.Vitrines.Any(v => string.Equals(v.Name, _data.Watch.UnmatchedVitrineName, StringComparison.OrdinalIgnoreCase)))
        {
            _data.Vitrines.Add(new Vitrine { Name = _data.Watch.UnmatchedVitrineName, Kind = VitrineKind.Collection, X = 80, Y = 80, Width = 320, Height = 240 });
        }
    }

    public void Stop() { _watcher.Stop(); _started = false; }

    public void ReconfigureWatcher()
    {
        _watcher.Configure(_data.Watch);
        _watcher.Start(_data.Watch.Sources);
    }

    public IReadOnlyList<(string Path, RuleDecision Decision)> DryRunFolder(string folder)
    {
        if (!Directory.Exists(folder)) return Array.Empty<(string, RuleDecision)>();
        var paths = EnumerateEntries(folder, _data.Watch.Recursive);
        return _rules.DryRun(paths, _data.Rules, _data.Watch);
    }

    public IReadOnlyList<(string Path, RuleDecision Decision, string Source)> DryRunAllSources()
    {
        var list = new List<(string, RuleDecision, string)>();
        foreach (var src in _data.Watch.Sources.Where(Directory.Exists))
        {
            foreach (var e in EnumerateEntries(src, _data.Watch.Recursive))
            {
                if (ShouldSkipScanPath(e)) continue;
                var facts = FileFacts.FromPath(e, src);
                var d = _rules.Evaluate(e, facts.Kind, _data.Rules, facts, _data.Watch);
                list.Add((e, d, src));
            }
        }
        return list;
    }

    public async Task<int> ScanExistingAsync(CancellationToken ct = default)
    {
        int acted = 0;
        foreach (var src in _data.Watch.Sources.Where(Directory.Exists).ToList())
        {
            foreach (var path in EnumerateEntries(src, _data.Watch.Recursive))
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                if (ShouldSkipScanPath(path)) continue;
                if (IsPinned(path) || IsAlreadyPlaced(path)) continue;

                await _gate.WaitAsync(ct);
                try
                {
                    var facts = FileFacts.FromPath(path, src);
                    var decision = _rules.Evaluate(path, facts.Kind, _data.Rules, facts, _data.Watch);
                    if (await ApplyAsync(path, facts.Kind, decision, src)) acted++;
                }
                catch (Exception ex)
                {
                    await LogAsync(HistoryEvent.Error, path, null, null, null, null, ex.Message, source: src);
                    await AddInboxAsync(path, $"Ошибка разбора: {ex.Message}", src, InboxStatus.Failed, ex.Message);
                }
                finally { _gate.Release(); }
            }
        }
        return acted;
    }

    private static IEnumerable<string> EnumerateEntries(string folder, bool recursive)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System
            });
        }
        catch { return Array.Empty<string>(); }
    }

    private bool IsAlreadyPlaced(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        return _data.Vitrines.SelectMany(v => v.Items).Any(i =>
        {
            if (string.IsNullOrWhiteSpace(i.Path)) return false;
            try { return string.Equals(Path.GetFullPath(i.Path), full, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });
    }

    private bool ShouldSkipScanPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var storageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vitrines", "storage");
            if (VitrineStorage.IsInDirectory(full, storageRoot)) return true;
            if (_data.Watch.ExcludeFolders.Any(f => VitrineStorage.IsInDirectory(full, f) || string.Equals(Path.GetFullPath(f).TrimEnd('\\','/'), full.TrimEnd('\\','/'), StringComparison.OrdinalIgnoreCase))) return true;
        }
        catch { }
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (_data.Watch.TempExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return true;
        if (_data.Watch.ExcludeExtensions.Any(e => string.Equals(e.TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase))) return true;
        var name = Path.GetFileName(path);
        if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
        if (_data.Watch.ExcludeNameContains.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    private async Task HandleFileReadyAsync(string path)
    {
        if (!_started || !_data.Watch.AutomationEnabled) return;
        if (!File.Exists(path) && !Directory.Exists(path)) return;

        await _gate.WaitAsync();
        try
        {
            if (IsPinned(path)) { await LogAsync(HistoryEvent.Skipped, path, null, null, null, null, "Элемент закреплён вручную"); return; }
            var facts = FileFacts.FromPath(path);
            var decision = _rules.Evaluate(path, facts.Kind, _data.Rules, facts, _data.Watch);
            await ApplyAsync(path, facts.Kind, decision);
        }
        catch (Exception ex)
        {
            await LogAsync(HistoryEvent.Error, path, null, null, null, null, ex.Message);
            await AddInboxAsync(path, $"Ошибка авто-сортировки: {ex.Message}", null, InboxStatus.Failed, ex.Message);
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> ApplyAsync(string path, ItemKind kind, RuleDecision decision, string? source = null)
    {
        switch (decision.Action)
        {
            case RuleActionType.Ignore:
                // исключения — тихий пропуск в журнал; "нет правил" обрабатывается политикой в движке (SendToInbox/Place/Ignore)
                await LogAsync(HistoryEvent.Skipped, path, null, null, decision.MatchedRule?.Id, decision.MatchedRule?.Name, decision.Reason, source: source);
                return false;

            case RuleActionType.SendToInbox:
                await AddInboxAsync(path, decision.Reason, source, InboxStatus.Pending);
                await LogAsync(HistoryEvent.SentToInbox, path, path, _data.Watch.UnmatchedVitrineName, decision.MatchedRule?.Id, decision.MatchedRule?.Name, decision.Reason, source: source);
                return true;

            case RuleActionType.MoveToFolder:
            {
                var targetDir = decision.MatchedRule?.Action.TargetFolderPath;
                if (string.IsNullOrWhiteSpace(targetDir))
                { await LogAsync(HistoryEvent.Skipped, path, null, null, decision.MatchedRule?.Id, decision.MatchedRule?.Name, "Не задана целевая папка", source: source); return false; }
                var final = await MoveTrackedAsync(path, targetDir);
                await LogAsync(HistoryEvent.Moved, path, path, final, decision.MatchedRule?.Id, decision.MatchedRule?.Name, decision.Reason, undoData: path, source: source);
                return true;
            }

            case RuleActionType.PlaceInVitrine:
            case RuleActionType.CreateVitrineAndPlace:
            {
                var vitrine = ResolveOrCreateVitrine(decision);
                if (vitrine == null)
                {
                    await AddInboxAsync(path, "Не удалось определить витрину", source, InboxStatus.Failed);
                    await LogAsync(HistoryEvent.Error, path, null, null, decision.MatchedRule?.Id, decision.MatchedRule?.Name, "Не удалось определить витрину", source: source);
                    return false;
                }

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
                if (!vitrine.Items.Any(i => string.Equals(i.Path, effectivePath, StringComparison.OrdinalIgnoreCase)))
                    vitrine.Items.Add(item);

                await _store.SaveLayoutAsync(_data);
                await LogAsync(HistoryEvent.Placed, effectivePath, path, vitrine.Name, decision.MatchedRule?.Id, decision.MatchedRule?.Name, decision.Reason, undoData: undo, source: source);
                return true;
            }
        }
        return false;
    }

    private Vitrine? ResolveOrCreateVitrine(RuleDecision decision)
    {
        if (decision.TargetVitrineId is Guid id)
        {
            var v = _data.Vitrines.FirstOrDefault(x => x.Id == id);
            if (v != null) return v;
        }
        var name = string.IsNullOrWhiteSpace(decision.TargetVitrineName) ? decision.MatchedRule?.Action.CreateVitrineName : decision.TargetVitrineName;
        if (decision.Action == RuleActionType.CreateVitrineAndPlace || !string.IsNullOrWhiteSpace(name))
        {
            name = string.IsNullOrWhiteSpace(name) ? "Новая витрина" : name!;
            var existing = _data.Vitrines.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;
            var created = new Vitrine { Name = name, Kind = VitrineKind.Collection };
            created.X = 40 + (_data.Vitrines.Count % 6) * 40;
            created.Y = 40 + (_data.Vitrines.Count % 6) * 30;
            _data.Vitrines.Add(created);
            _ = _store.SaveLayoutAsync(_data);
            _ = LogAsync(HistoryEvent.CreatedVitrine, name, null, name, decision.MatchedRule?.Id, decision.MatchedRule?.Name, "Создана новая витрина");
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

    private bool IsPinned(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { full = path; }
        return _data.Vitrines.SelectMany(v => v.Items).Any(i =>
        {
            if (!i.IsPinned || string.IsNullOrWhiteSpace(i.Path)) return false;
            try { return string.Equals(Path.GetFullPath(i.Path), full, StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase); }
        });
    }

    public async Task AddInboxAsync(string path, string reason, string? source, InboxStatus status, string? error = null)
    {
        try
        {
            string full = path;
            try { full = Path.GetFullPath(path); } catch { }
            var existing = _data.Inbox.FirstOrDefault(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase) && i.Status == InboxStatus.Pending);
            if (existing != null) { existing.Reason = reason; existing.At = DateTimeOffset.Now; existing.LastError = error; existing.Status = status; existing.Source = source ?? existing.Source; }
            else _data.Inbox.Insert(0, new InboxEntry { Path = full, DisplayName = Path.GetFileName(full), Reason = reason, Source = source, Status = status, LastError = error });
            if (_data.Inbox.Count > 500) _data.Inbox = _data.Inbox.Take(500).ToList();
            await _store.SaveSettingsAsync(_data);
            OnStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { }
    }

    public async Task ResolveInboxToVitrineAsync(Guid id, string vitrineName, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var item = _data.Inbox.FirstOrDefault(i => i.Id == id);
            if (item == null) return;
            if (File.Exists(item.Path) || Directory.Exists(item.Path))
            {
                var vit = _data.Vitrines.FirstOrDefault(v => string.Equals(v.Name, vitrineName, StringComparison.OrdinalIgnoreCase))
                          ?? new Vitrine { Name = vitrineName, Kind = VitrineKind.Collection };
                if (!_data.Vitrines.Contains(vit)) _data.Vitrines.Add(vit);
                var storage = VitrineStorage.GetManagedFolder(vit);
                var final = await MoveTrackedAsync(item.Path, storage);
                var vi = new VitrineItem { Path = final, DisplayName = Path.GetFileName(final), Kind = FileFacts.DetectKind(final) };
                if (!vit.Items.Any(i => string.Equals(i.Path, final, StringComparison.OrdinalIgnoreCase))) vit.Items.Add(vi);
                item.Status = InboxStatus.Resolved;
                await LogAsync(HistoryEvent.Placed, final, item.Path, vit.Name, null, null, "Разобрано вручную из «Неразобранного»", undoData: item.Path, source: item.Source);
            }
            else { item.Status = InboxStatus.Failed; item.LastError = "Файл не найден"; }
            await _store.SaveLayoutAsync(_data, ct);
            await _store.SaveSettingsAsync(_data, ct);
            OnStateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public async Task DismissInboxAsync(Guid id, CancellationToken ct = default)
    {
        var item = _data.Inbox.FirstOrDefault(i => i.Id == id);
        if (item != null) { item.Status = InboxStatus.Dismissed; await _store.SaveSettingsAsync(_data, ct); OnStateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public async Task ClearResolvedInboxAsync(CancellationToken ct = default)
    {
        _data.Inbox.RemoveAll(i => i.Status is InboxStatus.Resolved or InboxStatus.Dismissed);
        await _store.SaveSettingsAsync(_data, ct);
        OnStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LogAsync(HistoryEvent ev, string itemPath, string? from, string? to, Guid? ruleId, string? ruleName, string reason, string? undoData = null, string? source = null)
    {
        var entry = new HistoryEntry
        {
            Event = ev, ItemPath = itemPath, From = from, To = to,
            RuleId = ruleId, RuleName = ruleName ?? _data.Rules.FirstOrDefault(r => r.Id == ruleId)?.Name,
            Reason = reason, UndoData = undoData, Source = source
        };
        await _store.AppendHistoryAsync(entry);
        OnHistory?.Invoke(this, entry);
    }

    private static List<Vitrine> CreateDefaultVitrines()
    {
        var names = new[] { "Изображения", "Документы", "Архивы", "Установщики", "Ярлыки" };
        var list = new List<Vitrine>();
        for (int i = 0; i < names.Length; i++)
            list.Add(new Vitrine { Name = names[i], Kind = VitrineKind.Collection, X = 40 + (i % 3) * 340, Y = 40 + (i / 3) * 270, Width = 320, Height = 240 });
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

    public async Task<bool> UndoLastAsync(CancellationToken ct = default) => await UndoManyAsync(1, ct) > 0;

    public async Task<int> UndoManyAsync(int count, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = (await _store.ReadHistoryAsync(0, ct)).ToList();
            var targets = all.Where(h => !h.Undone && (h.Event is HistoryEvent.Moved or HistoryEvent.Placed) && !string.IsNullOrEmpty(h.UndoData)).TakeLast(count).ToList();
            int undone = 0;
            foreach (var entry in targets)
            {
                try
                {
                    var current = entry.ItemPath;
                    var original = entry.UndoData!;
                    if (!File.Exists(current) && !Directory.Exists(current)) continue;
                    _watcher.MarkSelfProduced(current);
                    _watcher.MarkSelfProduced(original);
                    await _fileOps.UndoMoveAsync(current, original, ct);
                    _watcher.MarkSelfProduced(original);
                    foreach (var v in _data.Vitrines)
                    {
                        var it = v.Items.FirstOrDefault(i => string.Equals(i.Path, current, StringComparison.OrdinalIgnoreCase));
                        if (it != null) it.Path = original;
                    }
                    entry.Undone = true;
                    all.Add(new HistoryEntry { Event = HistoryEvent.Undone, ItemPath = original, From = current, To = original, RuleId = entry.RuleId, RuleName = entry.RuleName, Reason = $"Откат: {Path.GetFileName(original)}" });
                    undone++;
                }
                catch { }
            }
            if (undone > 0)
            {
                await _store.SaveLayoutAsync(_data, ct);
                await _store.SaveHistoryAsync(all, ct);
                OnStateChanged?.Invoke(this, EventArgs.Empty);
            }
            return undone;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAllAsync(CancellationToken ct = default)
    {
        await _store.SaveLayoutAsync(_data, ct);
        await _store.SaveRulesAsync(_data, ct);
        await _store.SaveSettingsAsync(_data, ct);
    }

    public void Dispose()
    {
        _watcher.FileReady -= _onFileReady;
        _watcher.Dispose();
        _gate.Dispose();
    }
}