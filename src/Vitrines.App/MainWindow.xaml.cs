using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Vitrines.App.Desktop;
using Vitrines.Core.Actions;
using Vitrines.Core.Models;
using Vitrines.Core.Rules;
using Vitrines.Core.Sorting;
using Vitrines.Core.Storage;

namespace Vitrines.App;

public partial class MainWindow : Window, IDesktopActions
{
    private SortingOrchestrator? _orchestrator;
    private DesktopVitrineManager? _desktop;
    private IWorkspaceStore? _store;
    private IFileOperator? _fileOps;
    private bool _loading;
    private List<HistoryEntry> _historyCache = new();

    public MainWindow() { InitializeComponent(); }

    public WorkspaceData Data => _orchestrator?.Data ?? new WorkspaceData();

    public async Task SaveLayoutAsync() { if (_store == null) return; await _store.SaveLayoutAsync(Data); }

    public async Task<string?> MoveFileToFolderAsync(string sourcePath, string targetFolder)
    {
        if (_fileOps == null) return null;
        try
        {
            _orchestrator?.MarkSelfProduced(sourcePath);
            var final = await _fileOps.MoveAsync(sourcePath, targetFolder);
            _orchestrator?.MarkSelfProduced(final);
            LogManual($"Файл перемещён: {Path.GetFileName(final)} → {targetFolder}");
            return final;
        }
        catch (Exception ex) { LogManual($"Ошибка перемещения: {ex.Message}"); return null; }
    }

    public void LogManual(string message) => Dispatcher.InvokeAsync(() => SetStatus(message));

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _store = new JsonWorkspaceStore();
            var rules = new SimpleRuleEngine();
            _fileOps = new FileOperator();
            var watcher = new Vitrines.Core.Watcher.FileWatcherService();
            _orchestrator = new SortingOrchestrator(_store, rules, _fileOps, watcher);
            _orchestrator.OnHistory += Orchestrator_OnHistory;
            _orchestrator.OnStateChanged += (_, _) => Dispatcher.InvokeAsync(async () => await RefreshAllAsync());
            await _orchestrator.StartAsync();

            _desktop = new DesktopVitrineManager(this);

            _loading = true;
            AutoToggle.IsChecked = _orchestrator.Data.Watch.AutomationEnabled;
            LoadSettingsToUi();
            _loading = false;

            await RefreshAllAsync();
            SetStatus("Готово. Наблюдение активно, автоматика " + (_orchestrator.Data.Watch.AutomationEnabled ? "включена." : "выключена (безопасно)."));

            var args = Environment.GetCommandLineArgs();
            if (args.Any(a => string.Equals(a, "--show-vitrines", StringComparison.OrdinalIgnoreCase))) { _desktop?.ShowAll(); SetStatus("Витрины показаны на рабочем столе."); }
            if (args.Any(a => string.Equals(a, "--scan", StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus("Разбор существующих файлов…");
                var n = await _orchestrator.ScanExistingAsync();
                _desktop?.RefreshOpen();
                await RefreshAllAsync();
                SetStatus($"Разбор завершён: обработано {n} элементов.");
                if (args.Any(a => string.Equals(a, "--exit", StringComparison.OrdinalIgnoreCase))) { await Task.Delay(300); Close(); }
            }
        }
        catch (Exception ex) { SetStatus("Ошибка запуска: " + ex.Message); }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _desktop?.HideAll();
        if (_orchestrator != null) { _orchestrator.OnHistory -= Orchestrator_OnHistory; _orchestrator.Dispose(); }
    }

    private void Orchestrator_OnHistory(object? sender, HistoryEntry entry)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (entry.Event is HistoryEvent.CreatedVitrine or HistoryEvent.Placed or HistoryEvent.Moved) _desktop?.ShowAll(); else _desktop?.RefreshOpen();
            await RefreshHistoryAsync();
            await RefreshInboxAsync();
            await RefreshAllAsync(light: true);
        });
    }

    private async void AutoToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _orchestrator == null || _store == null) return;
        _orchestrator.Data.Watch.AutomationEnabled = AutoToggle.IsChecked == true;
        await _store.SaveSettingsAsync(_orchestrator.Data);
        SetStatus("Автоматика " + (_orchestrator.Data.Watch.AutomationEnabled ? "ВКЛЮЧЕНА — новые стабильные файлы будут сортироваться." : "выключена."));
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        try
        {
            SetStatus("Разбор существующих файлов в источниках…");
            var n = await _orchestrator.ScanExistingAsync();
            _desktop?.ShowAll();
            await RefreshAllAsync();
            SetStatus($"Разбор завершён: обработано {n} элементов.");
        }
        catch (Exception ex) { SetStatus("Ошибка разбора: " + ex.Message); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();
    private void ShowVitrines_Click(object sender, RoutedEventArgs e) { _desktop?.ShowAll(); SetStatus("Витрины показаны на рабочем столе."); }
    private void HideVitrines_Click(object sender, RoutedEventArgs e) { _desktop?.HideAll(); SetStatus("Витрины скрыты."); }

    private async void NewVitrine_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        var v = new Vitrine { Name = "Новая витрина", Kind = VitrineKind.Collection, X = 60 + (Data.Vitrines.Count % 8) * 30, Y = 60 + (Data.Vitrines.Count % 8) * 24, Width = 320, Height = 240 };
        Data.Vitrines.Add(v); await SaveLayoutAsync(); _desktop?.ShowAll(); await RefreshAllAsync();
        SetStatus($"Создана витрина «{v.Name}».");
    }

    private async void NewPortal_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        var dlg = new OpenFolderDialog { Title = "Выберите папку для портала" };
        if (dlg.ShowDialog(this) != true) return;
        var folder = dlg.FolderName;
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) name = "Портал";
        var v = new Vitrine { Name = name, Kind = VitrineKind.Portal, FolderPath = folder, X = 80 + (Data.Vitrines.Count % 8) * 30, Y = 80 + (Data.Vitrines.Count % 8) * 24, Width = 340, Height = 260 };
        Data.Vitrines.Add(v); await SaveLayoutAsync(); _desktop?.ShowAll(); await RefreshAllAsync();
        SetStatus($"Создан портал «{v.Name}» → {folder}");
    }

    private async Task RefreshAllAsync(bool light = false)
    {
        if (_orchestrator == null) return;
        try
        {
            SourcesList.Items.Clear();
            foreach (var s in _orchestrator.Data.Watch.Sources) SourcesList.Items.Add(s);
            VitrinesList.Items.Clear();
            foreach (var v in _orchestrator.Data.Vitrines)
                VitrinesList.Items.Add($"[{v.Kind}] {v.Name} — элементов: {v.Items.Count}" + (v.Kind == VitrineKind.Portal ? $" (папка: {v.FolderPath})" : ""));
            RefreshRulesList();
            if (!light) { await RefreshHistoryAsync(); await RefreshInboxAsync(); }
            var inboxPending = _orchestrator.Data.Inbox.Count(i => i.Status == InboxStatus.Pending || i.Status == InboxStatus.Failed);
            SummaryText.Text = $"Витрин: {_orchestrator.Data.Vitrines.Count} · Правил: {_orchestrator.Data.Rules.Count} · Источников: {_orchestrator.Data.Watch.Sources.Count} · Неразобранное: {inboxPending} · Стабильность: {_orchestrator.Data.Watch.StableSeconds} c · Рекурсивно: {(_orchestrator.Data.Watch.Recursive ? "да" : "нет")} · Данные: %LOCALAPPDATA%\\Vitrines";
        }
        catch (Exception ex) { SetStatus("Ошибка обновления: " + ex.Message); }
    }

    private void RefreshRulesList()
    {
        if (_orchestrator == null) return;
        RulesList.Items.Clear();
        foreach (var r in _orchestrator.Data.Rules.OrderBy(r => r.Order))
        {
            var cond = DescribeCondition(r.Condition);
            var mark = r.Enabled ? "on" : "off";
            var ex = r.IsExclusion ? " [ИСКЛЮЧЕНИЕ]" : "";
            var target = r.Action.Type == RuleActionType.CreateVitrineAndPlace ? $" → витрина '{r.Action.CreateVitrineName}'"
                       : r.Action.Type == RuleActionType.PlaceInVitrine ? $" → витрина ({r.Action.TargetVitrineId})"
                       : r.Action.Type == RuleActionType.MoveToFolder ? $" → папка '{r.Action.TargetFolderPath}'"
                       : r.Action.Type == RuleActionType.SendToInbox ? " → Неразобранное" : " → пропуск";
            RulesList.Items.Add($"[{mark}] #{r.Order:D2} {r.Name}{ex} | если {cond} | {r.Action.Type}{target}");
        }
    }

    private static string DescribeCondition(RuleCondition c)
    {
        var parts = new List<string>();
        if (c.Extensions is { Count: > 0 }) parts.Add("ext:" + string.Join(",", c.Extensions));
        if (c.Kinds is { Count: > 0 }) parts.Add("kind:" + string.Join(",", c.Kinds));
        if (c.NameContains is { Count: > 0 }) parts.Add("name~" + string.Join("|", c.NameContains));
        if (c.NamePatterns is { Count: > 0 }) parts.Add("mask:" + string.Join("|", c.NamePatterns));
        if (!string.IsNullOrWhiteSpace(c.NameRegex)) parts.Add("regex");
        if (c.SourcePaths is { Count: > 0 }) parts.Add("src:" + c.SourcePaths.Count);
        if (c.MinSizeBytes != null || c.MaxSizeBytes != null) parts.Add("size");
        if (c.ModifiedAfter != null || c.ModifiedBefore != null || c.CreatedAfter != null || c.CreatedBefore != null) parts.Add("date");
        if (c.ExcludeExtensions is { Count: > 0 } || c.ExcludeNameContains is { Count: > 0 } || c.ExcludeNamePatterns is { Count: > 0 } || c.ExcludeFolders is { Count: > 0 }) parts.Add("excl");
        return parts.Count == 0 ? "всегда" : string.Join("; ", parts);
    }

    private async Task RefreshHistoryAsync()
    {
        if (_store == null) return;
        _historyCache = (await _store.ReadHistoryAsync(0)).ToList();
        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        var ev = (HistoryEventFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Все";
        var txt = HistoryTextFilter.Text?.Trim() ?? "";
        IEnumerable<HistoryEntry> q = _historyCache.AsEnumerable().Reverse();
        if (ev != "Все" && Enum.TryParse<HistoryEvent>(ev, out var hev)) q = q.Where(h => h.Event == hev);
        if (!string.IsNullOrWhiteSpace(txt)) q = q.Where(h => (h.ItemPath + " " + h.To + " " + h.Reason + " " + h.RuleName + " " + h.From).Contains(txt, StringComparison.OrdinalIgnoreCase));
        HistoryList.Items.Clear();
        foreach (var h in q.Take(500)) HistoryList.Items.Add(FormatHistory(h));
    }

    private async Task RefreshInboxAsync()
    {
        if (_orchestrator == null) return;
        InboxList.Items.Clear();
        foreach (var i in _orchestrator.Data.Inbox.OrderByDescending(x => x.At))
        {
            InboxList.Items.Add($"[{i.Status}] {i.DisplayName} | {i.Path} | {i.Reason}" + (string.IsNullOrWhiteSpace(i.LastError) ? "" : $" | err: {i.LastError}"));
            // store id in tag mapping via parallel list? easier to keep separate
        }
        var pending = _orchestrator.Data.Inbox.Count(i => i.Status == InboxStatus.Pending || i.Status == InboxStatus.Failed);
        InboxInfo.Text = $"Ожидают: {pending}, всего: {_orchestrator.Data.Inbox.Count}";
        await Task.CompletedTask;
    }

    private InboxEntry? SelectedInboxEntry()
    {
        if (_orchestrator == null) return null;
        var idx = InboxList.SelectedIndex;
        var ordered = _orchestrator.Data.Inbox.OrderByDescending(x => x.At).ToList();
        if (idx < 0 || idx >= ordered.Count) return null;
        return ordered[idx];
    }

    private SortingRule? SelectedRule()
    {
        if (_orchestrator == null) return null;
        var idx = RulesList.SelectedIndex;
        var ordered = _orchestrator.Data.Rules.OrderBy(r => r.Order).ToList();
        if (idx < 0 || idx >= ordered.Count) return null;
        return ordered[idx];
    }

    private void DryRun_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        var dlg = new OpenFolderDialog { Title = "Выберите папку для dry-run" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var folder = dlg.FolderName;
            DryRunList.Items.Clear();
            var results = _orchestrator.DryRunFolder(folder);
            int willAct = 0;
            foreach (var (path, decision) in results)
            {
                var name = Path.GetFileName(path);
                var mark = decision.Action == RuleActionType.Ignore ? "—" : "→";
                if (decision.Action != RuleActionType.Ignore) willAct++;
                var target = decision.TargetVitrineName ?? decision.TargetVitrineId?.ToString() ?? decision.MatchedRule?.Action.TargetFolderPath ?? "";
                DryRunList.Items.Add($"{mark} {name}: {decision.Action} {target} ({decision.Reason})");
            }
            DryRunInfo.Text = $"Проверено: {results.Count}, с действием: {willAct}, без изменений: {results.Count - willAct}. Папка: {folder}";
            SetStatus("Dry-run завершён.");
        }
        catch (Exception ex) { SetStatus("Ошибка dry-run: " + ex.Message); }
    }

    private void DryRunAll_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        try
        {
            DryRunList.Items.Clear();
            var results = _orchestrator.DryRunAllSources();
            int willAct = 0;
            foreach (var (path, decision, src) in results)
            {
                var mark = decision.Action == RuleActionType.Ignore ? "—" : "→";
                if (decision.Action != RuleActionType.Ignore) willAct++;
                DryRunList.Items.Add($"{mark} [{Path.GetFileName(src)}] {Path.GetFileName(path)}: {decision.Action} {decision.TargetVitrineName ?? ""} ({decision.Reason})");
            }
            DryRunInfo.Text = $"Все источники: проверено {results.Count}, с действием {willAct}, без изменений {results.Count - willAct}.";
            SetStatus("Dry-run по всем источникам завершён.");
        }
        catch (Exception ex) { SetStatus("Ошибка dry-run: " + ex.Message); }
    }

    private async void Undo_Click(object sender, RoutedEventArgs e) => await DoUndo(1);
    private async void Undo5_Click(object sender, RoutedEventArgs e) => await DoUndo(5);
    private async Task DoUndo(int n)
    {
        if (_orchestrator == null) return;
        try
        {
            var done = await _orchestrator.UndoManyAsync(n);
            SetStatus(done > 0 ? $"Отменено операций: {done}." : "Нет обратимых действий для отката.");
            _desktop?.RefreshOpen();
            await RefreshAllAsync();
        }
        catch (Exception ex) { SetStatus("Ошибка отката: " + ex.Message); }
    }

    private static string FormatHistory(HistoryEntry h)
        => $"{h.At:yyyy-MM-dd HH:mm:ss} [{h.Event}{(h.Undone ? ",undone" : "")}] {h.ItemPath}"
           + (h.From != null && h.From != h.ItemPath ? $" <= {h.From}" : "")
           + (h.To != null ? $" => {h.To}" : "")
           + (string.IsNullOrWhiteSpace(h.RuleName) ? "" : $" [правило: {h.RuleName}]")
           + (string.IsNullOrWhiteSpace(h.Source) ? "" : $" (источник: {h.Source})")
           + (string.IsNullOrWhiteSpace(h.Reason) ? "" : $" — {h.Reason}");

    // --- sources ---
    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        var dlg = new OpenFolderDialog { Title = "Выберите папку-источник" };
        if (dlg.ShowDialog(this) != true) return;
        if (!_orchestrator.Data.Watch.Sources.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase))
            _orchestrator.Data.Watch.Sources.Add(dlg.FolderName);
        await PersistSettingsAndRestartWatcherAsync();
        await RefreshAllAsync();
        SetStatus($"Источник добавлен: {dlg.FolderName}");
    }

    private async void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        if (SourcesList.SelectedItem is not string path) { SetStatus("Выберите источник."); return; }
        _orchestrator.Data.Watch.Sources.RemoveAll(s => string.Equals(s, path, StringComparison.OrdinalIgnoreCase));
        await PersistSettingsAndRestartWatcherAsync();
        await RefreshAllAsync();
        SetStatus($"Источник удалён: {path}");
    }

    private async Task PersistSettingsAndRestartWatcherAsync()
    {
        if (_orchestrator == null || _store == null) return;
        ReadSettingsFromUi();
        await _store.SaveSettingsAsync(_orchestrator.Data);
        _orchestrator.Stop();
        await _orchestrator.StartAsync();
        _loading = true; AutoToggle.IsChecked = _orchestrator.Data.Watch.AutomationEnabled; _loading = false;
    }

    // --- rules ---
    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var name = Prompt("Название правила:", "Новое правило"); if (string.IsNullOrWhiteSpace(name)) return;
        var exts = Prompt("Расширения через запятую (пусто = любые):", "");
        var target = Prompt("Имя витрины для размещения:", name); if (string.IsNullOrWhiteSpace(target)) target = name;
        var rule = new SortingRule { Name = name.Trim(), Order = (_orchestrator.Data.Rules.Count == 0 ? 10 : _orchestrator.Data.Rules.Max(r => r.Order) + 10), Enabled = true, Condition = new RuleCondition(), Action = new RuleAction { Type = RuleActionType.CreateVitrineAndPlace, CreateVitrineName = target.Trim() } };
        if (!string.IsNullOrWhiteSpace(exts)) rule.Condition.Extensions = SplitList(exts);
        _orchestrator.Data.Rules.Add(rule);
        await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SetStatus($"Правило добавлено: {rule.Name}");
    }

    private async void AddAdvancedRule_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var r = new SortingRule { Name = "Новое правило", Order = (_orchestrator.Data.Rules.Count == 0 ? 10 : _orchestrator.Data.Rules.Max(r => r.Order) + 10) };
        if (EditRuleDialog(r)) { _orchestrator.Data.Rules.Add(r); await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SetStatus($"Правило добавлено: {r.Name}"); }
    }

    private async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var r = SelectedRule(); if (r == null) { SetStatus("Выберите правило."); return; }
        // edit copy to allow cancel
        var copy = CloneRule(r);
        if (EditRuleDialog(copy)) { CopyRule(copy, r); await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SetStatus($"Правило изменено: {r.Name}"); }
    }

    private async void ToggleRule_Click(object sender, RoutedEventArgs e)
    { if (_orchestrator == null || _store == null) return; var r = SelectedRule(); if (r == null) { SetStatus("Выберите правило."); return; } r.Enabled = !r.Enabled; await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SetStatus($"Правило {(r.Enabled ? "включено" : "выключено")}: {r.Name}"); }

    private async void ToggleExclusionRule_Click(object sender, RoutedEventArgs e)
    { if (_orchestrator == null || _store == null) return; var r = SelectedRule(); if (r == null) { SetStatus("Выберите правило."); return; } r.IsExclusion = !r.IsExclusion; if (r.IsExclusion) r.Action.Type = RuleActionType.Ignore; await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SetStatus(r.IsExclusion ? $"Правило стало исключением: {r.Name}" : $"Правило больше не исключение: {r.Name}"); }

    private async void MoveRuleUp_Click(object sender, RoutedEventArgs e) => await MoveRule(-1);
    private async void MoveRuleDown_Click(object sender, RoutedEventArgs e) => await MoveRule(1);
    private async Task MoveRule(int dir)
    {
        if (_orchestrator == null || _store == null) return;
        var ordered = _orchestrator.Data.Rules.OrderBy(r => r.Order).ToList();
        var idx = RulesList.SelectedIndex; if (idx < 0 || idx >= ordered.Count) return;
        var j = idx + dir; if (j < 0 || j >= ordered.Count) return;
        (ordered[idx].Order, ordered[j].Order) = (ordered[j].Order, ordered[idx].Order);
        // normalize
        var ren = _orchestrator.Data.Rules.OrderBy(r => r.Order).ToList();
        for (int i = 0; i < ren.Count; i++) ren[i].Order = (i + 1) * 10;
        await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); RulesList.SelectedIndex = j;
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    { if (_orchestrator == null || _store == null) return; var r = SelectedRule(); if (r == null) { SetStatus("Выберите правило."); return; } _orchestrator.Data.Rules.Remove(r); await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SetStatus($"Правило удалено: {r.Name}"); }

    private async void SaveRules_Click(object sender, RoutedEventArgs e)
    { if (_orchestrator == null || _store == null) return; await _store.SaveRulesAsync(_orchestrator.Data); SetStatus("Правила сохранены."); }

    private async void Presets_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var choice = Prompt("Пресет: 1=добавить базовые (изображения/документы/архивы/установщики/ярлыки), 2=добавить медиа+код, 3=очистить все правила. Введите 1/2/3:", "1");
        if (choice == "1") { foreach (var r in DefaultRules.Create()) { r.Order = (_orchestrator.Data.Rules.Count == 0 ? 0 : _orchestrator.Data.Rules.Max(x => x.Order)) + 10; _orchestrator.Data.Rules.Add(r); } }
        else if (choice == "2") { AddPreset("Видео", new() { "mp4", "mkv", "avi", "mov", "webm" }, "Видео"); AddPreset("Аудио", new() { "mp3", "wav", "flac", "ogg", "m4a" }, "Аудио"); AddPreset("Код", new() { "cs", "js", "ts", "py", "go", "rs", "java", "cpp", "h", "json", "xml", "yml", "yaml" }, "Код"); }
        else if (choice == "3") { if (MessageBox.Show(this, "Удалить все правила?", "Подтверждение", MessageBoxButton.YesNo) == MessageBoxResult.Yes) _orchestrator.Data.Rules.Clear(); }
        else return;
        await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SetStatus("Пресеты применены.");
    }

    private void AddPreset(string name, List<string> exts, string vitrine)
    {
        if (_orchestrator == null) return;
        _orchestrator.Data.Rules.Add(new SortingRule { Name = name, Order = (_orchestrator.Data.Rules.Count == 0 ? 10 : _orchestrator.Data.Rules.Max(x => x.Order) + 10), Enabled = true, Condition = new RuleCondition { Extensions = exts }, Action = new RuleAction { Type = RuleActionType.CreateVitrineAndPlace, CreateVitrineName = vitrine } });
    }

    // --- inbox ---
    private async void InboxMove_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        var it = SelectedInboxEntry(); if (it == null) { SetStatus("Выберите элемент в «Неразобранном»."); return; }
        var target = Prompt("В какую витрину отправить (будет создана при необходимости):", _orchestrator.Data.Watch.UnmatchedVitrineName);
        if (string.IsNullOrWhiteSpace(target)) return;
        try { await _orchestrator.ResolveInboxToVitrineAsync(it.Id, target.Trim()); _desktop?.RefreshOpen(); await RefreshAllAsync(); SetStatus($"Элемент отправлен в витрину «{target}»."); }
        catch (Exception ex) { SetStatus("Ошибка: " + ex.Message); }
    }
    private void InboxReveal_Click(object sender, RoutedEventArgs e)
    {
        var it = SelectedInboxEntry(); if (it == null) return;
        try { var p = it.Path; if (File.Exists(p) || Directory.Exists(p)) Process.Start("explorer.exe", "/select,\"" + p + "\""); else { var d = Path.GetDirectoryName(p); if (d != null && Directory.Exists(d)) Process.Start(new ProcessStartInfo(d) { UseShellExecute = true }); } } catch { }
    }
    private async void InboxDismiss_Click(object sender, RoutedEventArgs e)
    { if (_orchestrator == null) return; var it = SelectedInboxEntry(); if (it == null) return; await _orchestrator.DismissInboxAsync(it.Id); await RefreshAllAsync(); }
    private async void InboxClear_Click(object sender, RoutedEventArgs e)
    { if (_orchestrator == null) return; await _orchestrator.ClearResolvedInboxAsync(); await RefreshAllAsync(); SetStatus("Решённые элементы скрыты из списка."); }

    // --- history ---
    private void HistoryFilter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) ApplyHistoryFilter(); }
    private void HistoryFilter_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded) ApplyHistoryFilter(); }
    private async void RefreshHistory_Click(object sender, RoutedEventArgs e) => await RefreshHistoryAsync();

    // --- settings/appearance ---
    private void LoadSettingsToUi()
    {
        if (_orchestrator == null) return;
        var w = _orchestrator.Data.Watch;
        RecursiveWatch.IsChecked = w.Recursive;
        StableSecondsBox.Text = w.StableSeconds.ToString();
        UnmatchedVitrineBox.Text = w.UnmatchedVitrineName;
        foreach (var item in UnmatchedPolicyBox.Items.OfType<ComboBoxItem>()) if (string.Equals(item.Tag?.ToString(), w.Unmatched.ToString(), StringComparison.OrdinalIgnoreCase)) UnmatchedPolicyBox.SelectedItem = item;
        if (UnmatchedPolicyBox.SelectedItem == null) UnmatchedPolicyBox.SelectedIndex = 0;
        ExcludeFoldersBox.Text = string.Join(Environment.NewLine, w.ExcludeFolders ?? new());
        ExcludeExtBox.Text = string.Join(", ", w.ExcludeExtensions ?? new());
        ExcludeNameBox.Text = string.Join(", ", w.ExcludeNameContains ?? new());
        var a = w.Appearance ?? new AppearanceSettings();
        AccentBox.Text = a.Accent; TileSizeBox.Text = a.TileSize.ToString(); FontScaleBox.Text = a.FontScale.ToString("0.##"); AnimationsBox.IsChecked = a.Animations;
        foreach (var item in DensityBox.Items.OfType<ComboBoxItem>()) if (string.Equals(item.Content?.ToString(), a.Density, StringComparison.OrdinalIgnoreCase)) DensityBox.SelectedItem = item;
        if (DensityBox.SelectedItem == null) DensityBox.SelectedIndex = 1;
    }

    private void ReadSettingsFromUi()
    {
        if (_orchestrator == null) return;
        var w = _orchestrator.Data.Watch;
        w.Recursive = RecursiveWatch.IsChecked == true;
        if (int.TryParse(StableSecondsBox.Text, out var ss) && ss >= 1 && ss <= 120) w.StableSeconds = ss;
        w.UnmatchedVitrineName = string.IsNullOrWhiteSpace(UnmatchedVitrineBox.Text) ? "Неразобранное" : UnmatchedVitrineBox.Text.Trim();
        var tag = (UnmatchedPolicyBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (Enum.TryParse<UnmatchedPolicy>(tag, out var pol)) w.Unmatched = pol;
        w.ExcludeFolders = SplitLines(ExcludeFoldersBox.Text);
        w.ExcludeExtensions = SplitList(ExcludeExtBox.Text);
        w.ExcludeNameContains = SplitList(ExcludeNameBox.Text);
        w.Appearance ??= new AppearanceSettings();
        if (!string.IsNullOrWhiteSpace(AccentBox.Text)) w.Appearance.Accent = AccentBox.Text.Trim();
        if (int.TryParse(TileSizeBox.Text, out var ts)) w.Appearance.TileSize = Math.Clamp(ts, 72, 160);
        if (double.TryParse(FontScaleBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fs) || double.TryParse(FontScaleBox.Text, out fs)) w.Appearance.FontScale = Math.Clamp(fs, 0.8, 1.6);
        w.Appearance.Density = (DensityBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "normal";
        w.Appearance.Animations = AnimationsBox.IsChecked == true;
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        ReadSettingsFromUi();
        await _store.SaveSettingsAsync(_orchestrator.Data);
        _orchestrator.ReconfigureWatcher();
        await RefreshAllAsync();
        SetStatus("Настройки сохранены, наблюдение обновлено.");
    }

    private void ApplyAppearance_Click(object sender, RoutedEventArgs e)
    { ReadSettingsFromUi(); _desktop?.RefreshOpen(); SetStatus("Оформление применено к открытым витринам (сохраните настройки, чтобы закрепить)."); }

    private async void RebuildFromRules_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        // гарантируем витрины для правил CreateVitrineAndPlace
        foreach (var r in _orchestrator.Data.Rules.Where(r => r.Action.Type == RuleActionType.CreateVitrineAndPlace && !string.IsNullOrWhiteSpace(r.Action.CreateVitrineName)))
        {
            if (!_orchestrator.Data.Vitrines.Any(v => string.Equals(v.Name, r.Action.CreateVitrineName, StringComparison.OrdinalIgnoreCase)))
                _orchestrator.Data.Vitrines.Add(new Vitrine { Name = r.Action.CreateVitrineName!, Kind = VitrineKind.Collection, X = 40 + (_orchestrator.Data.Vitrines.Count % 6) * 40, Y = 40 + (_orchestrator.Data.Vitrines.Count % 6) * 30 });
        }
        await _store.SaveLayoutAsync(_orchestrator.Data);
        _desktop?.ShowAll();
        await RefreshAllAsync();
        SetStatus("Витрины для правил созданы/показаны.");
    }

    // --- export/import ---
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        var dlg = new SaveFileDialog { Title = "Экспорт снапшота Витрин", Filter = "Zip (*.zip)|*.zip", FileName = $"vitrines-snapshot-{DateTime.Now:yyyyMMdd-HHmm}.zip" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ReadSettingsFromUi();
            await _orchestrator.SaveAllAsync();
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vitrines");
            if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
            using var zip = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create);
            foreach (var name in new[] { "layout.json", "rules.json", "settings.json", "inbox.json", "history.jsonl" })
            {
                var p = Path.Combine(root, name);
                if (File.Exists(p)) zip.CreateEntryFromFile(p, name);
            }
            SetStatus($"Экспорт выполнен: {dlg.FileName}");
        }
        catch (Exception ex) { SetStatus("Ошибка экспорта: " + ex.Message); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var dlg = new OpenFileDialog { Title = "Импорт снапшота Витрин", Filter = "Zip (*.zip)|*.zip" };
        if (dlg.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "Импорт заменит текущие layout/rules/settings/inbox/history. Продолжить?", "Импорт", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vitrines");
            Directory.CreateDirectory(root);
            using (var zip = ZipFile.OpenRead(dlg.FileName))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                    var dest = Path.Combine(root, entry.Name);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }
            _orchestrator.Stop();
            await _orchestrator.StartAsync();
            _loading = true; AutoToggle.IsChecked = _orchestrator.Data.Watch.AutomationEnabled; LoadSettingsToUi(); _loading = false;
            _desktop?.RefreshOpen();
            await RefreshAllAsync();
            SetStatus("Импорт выполнен. Наблюдение перезапущено.");
        }
        catch (Exception ex) { SetStatus("Ошибка импорта: " + ex.Message); }
    }

    // --- rule dialog/helpers ---
    private bool EditRuleDialog(SortingRule r)
    {
        var w = new Window { Title = "Правило", Width = 640, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15,17,21)), Foreground = System.Windows.Media.Brushes.WhiteSmoke };
        var root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Margin = new Thickness(14) } };
        var sp = (StackPanel)root.Content;
        TextBox Add(string label, string? val = "", bool multi = false, double h = 0)
        {
            sp.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2), Foreground = System.Windows.Media.Brushes.Gainsboro });
            var tb = new TextBox { Text = val ?? "", AcceptsReturn = multi, Height = h > 0 ? h : double.NaN, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18,23,34)), Foreground = System.Windows.Media.Brushes.WhiteSmoke };
            sp.Children.Add(tb); return tb;
        }
        var name = Add("Название", r.Name);
        var enabled = new CheckBox { Content = "Включено", IsChecked = r.Enabled, Margin = new Thickness(0, 6, 0, 0) }; sp.Children.Add(enabled);
        var excl = new CheckBox { Content = "Это правило-исключение (не трогать совпавшие файлы)", IsChecked = r.IsExclusion, Margin = new Thickness(0, 4, 0, 0) }; sp.Children.Add(excl);
        var exts = Add("Расширения (через запятую)", Join(r.Condition.Extensions));
        var kinds = Add("Типы (File,Folder,Shortcut,Application через запятую)", Join(r.Condition.Kinds?.Select(k=>k.ToString())));
        var ncontains = Add("Имя содержит (через запятую)", Join(r.Condition.NameContains));
        var nmasks = Add("Маски имени (* ?) (через запятую)", Join(r.Condition.NamePatterns));
        var nregex = Add("Regex имени (необязательно)", r.Condition.NameRegex ?? "");
        var sources = Add("Ограничить источниками (пути, по строке). Пусто = любые", JoinLines(r.Condition.SourcePaths), true, 50);
        var minKb = Add("Мин. размер (КБ, пусто = нет)", r.Condition.MinSizeBytes.HasValue ? (r.Condition.MinSizeBytes.Value/1024).ToString() : "");
        var maxKb = Add("Макс. размер (КБ, пусто = нет)", r.Condition.MaxSizeBytes.HasValue ? (r.Condition.MaxSizeBytes.Value/1024).ToString() : "");
        var xext = Add("Исключить расширения", Join(r.Condition.ExcludeExtensions));
        var xname = Add("Исключить имя содержит", Join(r.Condition.ExcludeNameContains));
        var xmask = Add("Исключить маски имени", Join(r.Condition.ExcludeNamePatterns));
        var xfold = Add("Исключить папки (пути, по строке)", JoinLines(r.Condition.ExcludeFolders), true, 50);
        sp.Children.Add(new TextBlock { Text = "Действие", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        var action = new ComboBox { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18,23,34)), Foreground = System.Windows.Media.Brushes.WhiteSmoke };
        foreach (var a in new[] { RuleActionType.CreateVitrineAndPlace, RuleActionType.PlaceInVitrine, RuleActionType.MoveToFolder, RuleActionType.SendToInbox, RuleActionType.Ignore }) action.Items.Add(a);
        action.SelectedItem = r.Action.Type; sp.Children.Add(action);
        var target = Add("Целевая витрина (имя) / для MoveToFolder — путь папки", r.Action.Type == RuleActionType.MoveToFolder ? r.Action.TargetFolderPath : r.Action.CreateVitrineName);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Сохранить", Width = 120, Margin = new Thickness(0,0,8,0), IsDefault = true };
        var cancel = new Button { Content = "Отмена", Width = 100, IsCancel = true };
        ok.Click += (_, _) => { w.DialogResult = true; w.Close(); };
        cancel.Click += (_, _) => { w.DialogResult = false; w.Close(); };
        btns.Children.Add(ok); btns.Children.Add(cancel); sp.Children.Add(btns);
        w.Content = root;

        if (w.ShowDialog() != true) return false;
        r.Name = string.IsNullOrWhiteSpace(name.Text) ? r.Name : name.Text.Trim();
        r.Enabled = enabled.IsChecked == true;
        r.IsExclusion = excl.IsChecked == true;
        r.Condition.Extensions = NullIfEmpty(SplitList(exts.Text));
        r.Condition.Kinds = ParseKinds(kinds.Text);
        r.Condition.NameContains = NullIfEmpty(SplitList(ncontains.Text));
        r.Condition.NamePatterns = NullIfEmpty(SplitList(nmasks.Text));
        r.Condition.NameRegex = string.IsNullOrWhiteSpace(nregex.Text) ? null : nregex.Text.Trim();
        r.Condition.SourcePaths = NullIfEmpty(SplitLines(sources.Text));
        r.Condition.MinSizeBytes = long.TryParse(minKb.Text, out var mn) ? mn * 1024 : null;
        r.Condition.MaxSizeBytes = long.TryParse(maxKb.Text, out var mx) ? mx * 1024 : null;
        r.Condition.ExcludeExtensions = NullIfEmpty(SplitList(xext.Text));
        r.Condition.ExcludeNameContains = NullIfEmpty(SplitList(xname.Text));
        r.Condition.ExcludeNamePatterns = NullIfEmpty(SplitList(xmask.Text));
        r.Condition.ExcludeFolders = NullIfEmpty(SplitLines(xfold.Text));
        r.Action.Type = action.SelectedItem is RuleActionType at ? at : r.Action.Type;
        if (r.IsExclusion) r.Action.Type = RuleActionType.Ignore;
        if (r.Action.Type == RuleActionType.MoveToFolder) r.Action.TargetFolderPath = target.Text?.Trim(); else r.Action.CreateVitrineName = target.Text?.Trim();
        return true;
    }

    private static List<string> SplitList(string? s) => string.IsNullOrWhiteSpace(s) ? new() : s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
    private static List<string> SplitLines(string? s) => string.IsNullOrWhiteSpace(s) ? new() : s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
    private static List<string>? NullIfEmpty(List<string> l) => l.Count == 0 ? null : l;
    private static string Join(IEnumerable<string>? v) => v == null ? "" : string.Join(", ", v);
    private static string JoinLines(IEnumerable<string>? v) => v == null ? "" : string.Join(Environment.NewLine, v);
    private static List<ItemKind>? ParseKinds(string? s)
    {
        var list = SplitList(s);
        if (list.Count == 0) return null;
        var res = new List<ItemKind>();
        foreach (var x in list) if (Enum.TryParse<ItemKind>(x, true, out var k)) res.Add(k);
        return res.Count == 0 ? null : res;
    }

    private static SortingRule CloneRule(SortingRule r) => System.Text.Json.JsonSerializer.Deserialize<SortingRule>(System.Text.Json.JsonSerializer.Serialize(r))!;
    private static void CopyRule(SortingRule src, SortingRule dst)
    {
        dst.Name = src.Name; dst.Enabled = src.Enabled; dst.Order = src.Order; dst.IsExclusion = src.IsExclusion; dst.Condition = src.Condition; dst.Action = src.Action;
    }

    private static string? Prompt(string text, string def)
    {
        var w = new Window { Title = "Витрины", Width = 460, Height = 170, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow, Content = new StackPanel { Margin = new Thickness(12) } };
        var sp = (StackPanel)w.Content; sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        var tb = new TextBox { Text = def, Margin = new Thickness(0, 0, 0, 10) }; sp.Children.Add(tb);
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => { w.DialogResult = true; w.Close(); }; sp.Children.Add(ok);
        w.Loaded += (_, _) => { tb.SelectAll(); tb.Focus(); };
        return w.ShowDialog() == true ? tb.Text : null;
    }

    private void SetStatus(string text) => StatusText.Text = text;
}