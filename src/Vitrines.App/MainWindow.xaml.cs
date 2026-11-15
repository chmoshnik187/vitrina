using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private const string RuleDragFormat = "Vitrines.RuleOrder";

    private SortingOrchestrator? _orchestrator;
    private DesktopVitrineManager? _desktop;
    private IWorkspaceStore? _store;
    private IFileOperator? _fileOps;
    private WorkspaceSnapshotService? _snapshots;
    private DispatcherTimer? _scheduleTimer;
    private bool _loading;
    private bool _scheduledScanRunning;
    private List<HistoryEntry> _historyCache = new();
    private Point _ruleDragStart;
    private SortingRule? _ruleDragRule;

    public MainWindow() { InitializeComponent(); }

    public WorkspaceData Data => _orchestrator?.Data ?? new WorkspaceData();

    public async Task SaveLayoutAsync() { if (_store == null) return; await _store.SaveLayoutAsync(Data); }

    public async Task<string?> MoveFileToFolderAsync(string sourcePath, string targetFolder)
    {
        if (_fileOps == null) return null;
        try
        {
            var options = new FileOpOptions
            {
                Conflict = Data.Watch.ConflictPolicy,
                MaxRetries = Math.Clamp(Data.Watch.LockedFileRetries, 0, 10)
            };
            _orchestrator?.MarkSelfProduced(sourcePath);
            var final = await _fileOps.MoveAsync(sourcePath, targetFolder, options);
            _orchestrator?.MarkSelfProduced(final);
            LogManual($"Файл перемещён: {Path.GetFileName(final)} → {targetFolder}");
            return final;
        }
        catch (FileConflictException ex) { LogManual("Пропущено (конфликт имён): " + ex.Message); return null; }
        catch (Exception ex) { LogManual($"Ошибка перемещения: {ex.Message}"); return null; }
    }

    public void LogManual(string message) => Dispatcher.InvokeAsync(() => SetStatus(message));

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _store = new JsonWorkspaceStore();
            _snapshots = new WorkspaceSnapshotService();
            var rules = new SimpleRuleEngine();
            _fileOps = new FileOperator();
            var watcher = new Vitrines.Core.Watcher.FileWatcherService();
            _orchestrator = new SortingOrchestrator(_store, rules, _fileOps, watcher);
            _orchestrator.OnHistory += Orchestrator_OnHistory;
            _orchestrator.OnStateChanged += (_, _) => Dispatcher.InvokeAsync(async () => await RefreshAllAsync());
            await _orchestrator.StartAsync();

            _desktop = new DesktopVitrineManager(this);

            _loading = true;
            try
            {
                AutoToggle.IsChecked = _orchestrator.Data.Watch.AutomationEnabled;
                LoadSettingsToUi();
            }
            finally { _loading = false; }

            _scheduleTimer = new DispatcherTimer();
            _scheduleTimer.Tick += async (_, _) => await RunScheduledScanAsync();
            UpdateScheduleTimer();

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
        _scheduleTimer?.Stop();
        _desktop?.HideAll();
        if (_orchestrator != null) { _orchestrator.OnHistory -= Orchestrator_OnHistory; _orchestrator.Dispose(); }
    }

    private void Orchestrator_OnHistory(object? sender, HistoryEntry entry)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (entry.Event is HistoryEvent.CreatedVitrine or HistoryEvent.Placed or HistoryEvent.Moved) _desktop?.ShowAll(); else _desktop?.RefreshOpen();
            if (entry.Event is HistoryEvent.Placed or HistoryEvent.Moved) _desktop?.PulseForPath(entry.ItemPath);
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
        UpdateScheduleTimer();
        SetStatus("Автоматика " + (_orchestrator.Data.Watch.AutomationEnabled ? "ВКЛЮЧЕНА — новые стабильные файлы будут сортироваться." : "выключена."));
    }

    private async Task RunScheduledScanAsync()
    {
        if (_orchestrator == null || _scheduledScanRunning) return;
        var watch = _orchestrator.Data.Watch;
        if (!watch.ScheduleEnabled || !watch.AutomationEnabled) { UpdateScheduleTimer(); return; }

        _scheduledScanRunning = true;
        try
        {
            SetStatus("Плановый разбор существующих файлов…");
            var n = await _orchestrator.ScanExistingAsync();
            _desktop?.ShowAll();
            await RefreshAllAsync();
            SetStatus(n > 0 ? $"Плановый разбор: обработано {n} элементов." : "Плановый разбор: новых элементов нет.");
        }
        catch (Exception ex) { SetStatus("Ошибка планового разбора: " + ex.Message); }
        finally { _scheduledScanRunning = false; }
    }

    private void UpdateScheduleTimer()
    {
        if (_scheduleTimer == null) return;
        _scheduleTimer.Stop();
        if (_orchestrator == null) return;
        var watch = _orchestrator.Data.Watch;
        if (!watch.ScheduleEnabled || !watch.AutomationEnabled) return;
        _scheduleTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(watch.ScheduleMinutes, 5, 1440));
        _scheduleTimer.Start();
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

            var w = _orchestrator.Data.Watch;
            var inboxPending = _orchestrator.Data.Inbox.Count(i => i.Status == InboxStatus.Pending || i.Status == InboxStatus.Failed);
            var schedule = w.ScheduleEnabled ? $"{Math.Clamp(w.ScheduleMinutes, 5, 1440)} мин" : "выкл";
            SummaryText.Text =
                $"Витрин: {_orchestrator.Data.Vitrines.Count} · Правил: {_orchestrator.Data.Rules.Count} · " +
                $"Источников: {w.Sources.Count} · Неразобранное: {inboxPending} · " +
                $"Стабильность: {w.StableSeconds} c · Рекурсивно: {(w.Recursive ? "да" : "нет")} · " +
                $"Коллекции: {(w.CollectionPlacement == CollectionPlacement.Move ? "перемещение" : "ссылки")} · " +
                $"Конфликты: {w.ConflictPolicy} · Расписание: {schedule} · " +
                $"Аудит пропусков: {(w.LocalAudit ? "да" : "нет")} · Данные: %LOCALAPPDATA%\\Vitrines";
        }
        catch (Exception ex) { SetStatus("Ошибка обновления: " + ex.Message); }
    }

    private void RefreshRulesList()
    {
        if (_orchestrator == null) return;
        var selectedId = (RulesList.SelectedItem as RuleRow)?.Rule.Id;
        RulesList.Items.Clear();
        foreach (var r in _orchestrator.Data.Rules.OrderBy(r => r.Order))
            RulesList.Items.Add(new RuleRow(r, RuleFormatting.DescribeRule(r, id => _orchestrator.Data.Vitrines.FirstOrDefault(v => v.Id == id)?.Name)));
        if (selectedId is Guid id) SelectRule(id);
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
            InboxList.Items.Add($"[{i.Status}] {i.DisplayName} | {i.Path} | {i.Reason}" + (string.IsNullOrWhiteSpace(i.LastError) ? "" : $" | err: {i.LastError}"));

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

    private SortingRule? SelectedRule =>
        (RulesList.SelectedItem as RuleRow)?.Rule ??
        (RulesList.SelectedItem as SortingRule);

    private void SelectRule(Guid id)
    {
        foreach (var item in RulesList.Items)
        {
            if (item is RuleRow row && row.Rule.Id == id) { RulesList.SelectedItem = item; return; }
        }
    }

    private int NextOrder() => Data.Rules.Count == 0 ? 10 : Data.Rules.Max(r => r.Order) + 10;

    // --- rule drag/drop reorder ---
    private void RulesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ruleDragStart = e.GetPosition(RulesList);
        _ruleDragRule = RowAt(e.GetPosition(RulesList))?.Rule;
    }

    private void RulesList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _ruleDragRule == null) return;
        var pos = e.GetPosition(RulesList);
        if (Math.Abs(pos.X - _ruleDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _ruleDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var rule = _ruleDragRule;
        _ruleDragRule = null;
        var data = new DataObject();
        data.SetData(RuleDragFormat, rule.Id.ToString("N"));
        try { DragDrop.DoDragDrop(RulesList, data, DragDropEffects.Move); }
        catch { }
    }

    private void RulesList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(RuleDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void RulesList_Drop(object sender, DragEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        if (e.Data.GetData(RuleDragFormat) is not string text || !Guid.TryParse(text, out var id)) return;
        var moved = _orchestrator.Data.Rules.FirstOrDefault(r => r.Id == id);
        if (moved == null) return;
        var target = RowAt(e.GetPosition(RulesList))?.Rule;
        await ReorderRuleAsync(moved, target);
    }

    private RuleRow? RowAt(Point point)
    {
        var hit = RulesList.InputHitTest(point) as DependencyObject;
        while (hit != null)
        {
            if (hit is ListBoxItem item) return item.DataContext as RuleRow;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    private async Task ReorderRuleAsync(SortingRule moved, SortingRule? target)
    {
        if (_orchestrator == null || _store == null) return;
        var ordered = _orchestrator.Data.Rules.OrderBy(r => r.Order).ToList();
        if (!ordered.Remove(moved)) return;
        var index = target == null ? ordered.Count : ordered.IndexOf(target);
        if (index < 0) index = ordered.Count;
        ordered.Insert(index, moved);
        for (int i = 0; i < ordered.Count; i++) ordered[i].Order = (i + 1) * 10;

        await _store.SaveRulesAsync(_orchestrator.Data);
        RefreshRulesList();
        SelectRule(moved.Id);
        SetStatus($"Правило «{moved.Name}» перемещено на позицию {index + 1}.");
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
        _loading = true;
        try { AutoToggle.IsChecked = _orchestrator.Data.Watch.AutomationEnabled; }
        finally { _loading = false; }
        UpdateScheduleTimer();
    }

    // --- rules ---
    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var name = Prompt("Название правила:", "Новое правило"); if (string.IsNullOrWhiteSpace(name)) return;
        var exts = Prompt("Расширения через запятую (пусто = любые):", "");
        var target = Prompt("Имя витрины для размещения:", name); if (string.IsNullOrWhiteSpace(target)) target = name;
        var rule = new SortingRule
        {
            Name = name.Trim(),
            Order = NextOrder(),
            Enabled = true,
            Condition = new RuleCondition(),
            Action = new RuleAction { Type = RuleActionType.CreateVitrineAndPlace, CreateVitrineName = target.Trim() }
        };
        if (!string.IsNullOrWhiteSpace(exts)) rule.Condition.Extensions = SplitList(exts);
        _orchestrator.Data.Rules.Add(rule);
        await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SelectRule(rule.Id);
        SetStatus($"Правило добавлено: {rule.Name}");
    }

    private async void AddAdvancedRule_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var r = new SortingRule
        {
            Name = "Новое правило",
            Order = NextOrder(),
            Action = new RuleAction { Type = RuleActionType.CreateVitrineAndPlace, CreateVitrineName = "Новая витрина" }
        };
        if (!RuleEditorWindow.Edit(this, r, _orchestrator.Data.Vitrines)) return;
        _orchestrator.Data.Rules.Add(r);
        await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SelectRule(r.Id);
        SetStatus($"Правило добавлено: {r.Name}");
    }

    private async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var r = SelectedRule; if (r == null) { SetStatus("Выберите правило."); return; }
        var copy = CloneRule(r);
        if (!RuleEditorWindow.Edit(this, copy, _orchestrator.Data.Vitrines)) return;
        CopyRule(copy, r);
        await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SelectRule(r.Id);
        SetStatus($"Правило изменено: {r.Name}");
    }

    private async void ToggleRule_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var r = SelectedRule; if (r == null) { SetStatus("Выберите правило."); return; }
        r.Enabled = !r.Enabled;
        await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SelectRule(r.Id);
        SetStatus($"Правило {(r.Enabled ? "включено" : "выключено")}: {r.Name}");
    }

    private async void ToggleExclusionRule_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var r = SelectedRule; if (r == null) { SetStatus("Выберите правило."); return; }
        r.IsExclusion = !r.IsExclusion;
        if (r.IsExclusion) r.Action.Type = RuleActionType.Ignore;
        await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SelectRule(r.Id);
        SetStatus(r.IsExclusion ? $"Правило стало исключением: {r.Name}" : $"Правило больше не исключение: {r.Name}");
    }

    private async void MoveRuleUp_Click(object sender, RoutedEventArgs e) => await MoveRule(-1);
    private async void MoveRuleDown_Click(object sender, RoutedEventArgs e) => await MoveRule(1);
    private async Task MoveRule(int dir)
    {
        if (_orchestrator == null || _store == null) return;
        var ordered = _orchestrator.Data.Rules.OrderBy(r => r.Order).ToList();
        var moved = SelectedRule; if (moved == null) return;
        var idx = ordered.IndexOf(moved);
        if (idx < 0) return;
        var j = idx + dir; if (j < 0 || j >= ordered.Count) return;
        (ordered[idx], ordered[j]) = (ordered[j], ordered[idx]);
        for (int i = 0; i < ordered.Count; i++) ordered[i].Order = (i + 1) * 10;
        await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SelectRule(moved.Id);
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var r = SelectedRule; if (r == null) { SetStatus("Выберите правило."); return; }
        _orchestrator.Data.Rules.Remove(r);
        await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList();
        SetStatus($"Правило удалено: {r.Name}");
    }

    private async void SaveRules_Click(object sender, RoutedEventArgs e)
    { if (_orchestrator == null || _store == null) return; await _store.SaveRulesAsync(_orchestrator.Data); SetStatus("Правила сохранены."); }

    private async void Presets_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var choice = Prompt("Пресет: 1=добавить базовые (изображения/документы/архивы/установщики/ярлыки), 2=добавить медиа+код, 3=очистить все правила. Введите 1/2/3:", "1");
        if (choice == "1") { foreach (var r in DefaultRules.Create()) { r.Order = NextOrder(); _orchestrator.Data.Rules.Add(r); } }
        else if (choice == "2") { AddPreset("Видео", new() { "mp4", "mkv", "avi", "mov", "webm" }, "Видео"); AddPreset("Аудио", new() { "mp3", "wav", "flac", "ogg", "m4a" }, "Аудио"); AddPreset("Код", new() { "cs", "js", "ts", "py", "go", "rs", "java", "cpp", "h", "json", "xml", "yml", "yaml" }, "Код"); }
        else if (choice == "3") { if (MessageBox.Show(this, "Удалить все правила?", "Подтверждение", MessageBoxButton.YesNo) == MessageBoxResult.Yes) _orchestrator.Data.Rules.Clear(); }
        else return;
        await _store.SaveRulesAsync(_orchestrator.Data); RefreshRulesList(); SetStatus("Пресеты применены.");
    }

    private void AddPreset(string name, List<string> exts, string vitrine)
    {
        if (_orchestrator == null) return;
        _orchestrator.Data.Rules.Add(new SortingRule
        {
            Name = name,
            Order = NextOrder(),
            Enabled = true,
            Condition = new RuleCondition { Extensions = exts },
            Action = new RuleAction { Type = RuleActionType.CreateVitrineAndPlace, CreateVitrineName = vitrine }
        });
    }

    // --- inbox ---
    private async void InboxMove_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        var it = SelectedInboxEntry(); if (it == null) { SetStatus("Выберите элемент в «Неразобранном»."); return; }
        var target = Prompt("В какую витрину отправить (будет создана при необходимости):", SuggestVitrineFor(it));
        if (string.IsNullOrWhiteSpace(target)) return;
        try { await _orchestrator.ResolveInboxToVitrineAsync(it.Id, target.Trim()); _desktop?.ShowAll(); await RefreshAllAsync(); SetStatus($"Элемент отправлен в витрину «{target}»."); }
        catch (Exception ex) { SetStatus("Ошибка: " + ex.Message); }
    }

    private async void InboxCreateRule_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var entry = SelectedInboxEntry();
        if (entry == null) { SetStatus("Выберите элемент в «Неразобранном»."); return; }

        var ext = Path.GetExtension(entry.Path).TrimStart('.').ToLowerInvariant();
        var rule = new SortingRule
        {
            Name = "Авто: " + (string.IsNullOrEmpty(ext) ? (Directory.Exists(entry.Path) ? "папки" : "файлы") : ext),
            Order = NextOrder(),
            Enabled = true,
            Condition = new RuleCondition(),
            Action = new RuleAction { Type = RuleActionType.CreateVitrineAndPlace, CreateVitrineName = SuggestVitrineFor(entry) }
        };

        if (!string.IsNullOrEmpty(ext)) rule.Condition.Extensions = new() { ext };
        else if (Directory.Exists(entry.Path)) rule.Condition.Kinds = new() { ItemKind.Folder };
        else
        {
            var stem = Path.GetFileNameWithoutExtension(entry.Path);
            if (!string.IsNullOrWhiteSpace(stem)) rule.Condition.NameContains = new() { stem };
        }

        if (!RuleEditorWindow.Edit(this, rule, _orchestrator.Data.Vitrines)) return;
        _orchestrator.Data.Rules.Add(rule);
        await _store.SaveRulesAsync(_orchestrator.Data);
        RefreshRulesList();
        SelectRule(rule.Id);

        var apply = MessageBox.Show(this,
            $"Правило «{rule.Name}» создано.\nПрименить его к элементу «{entry.DisplayName}» сейчас?",
            "Новое правило", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (apply != MessageBoxResult.Yes) { SetStatus($"Правило добавлено: {rule.Name}"); return; }

        try
        {
            if (rule.Action.Type is RuleActionType.CreateVitrineAndPlace or RuleActionType.PlaceInVitrine)
            {
                var vitrineName = string.IsNullOrWhiteSpace(rule.Action.CreateVitrineName)
                    ? _orchestrator.Data.Watch.UnmatchedVitrineName
                    : rule.Action.CreateVitrineName!;
                await _orchestrator.ResolveInboxToVitrineAsync(entry.Id, vitrineName);
                _desktop?.ShowAll();
                SetStatus($"Правило добавлено, элемент отправлен в «{vitrineName}».");
            }
            else if (rule.Action.Type == RuleActionType.MoveToFolder && !string.IsNullOrWhiteSpace(rule.Action.TargetFolderPath))
            {
                var final = await MoveFileToFolderAsync(entry.Path, rule.Action.TargetFolderPath!);
                if (final != null) await _orchestrator.DismissInboxAsync(entry.Id);
                _desktop?.RefreshOpen();
                SetStatus(final != null ? "Правило добавлено, элемент перемещён в папку правила." : "Правило добавлено. Перемещение не выполнено.");
            }
            else { SetStatus($"Правило добавлено: {rule.Name}"); }
            await RefreshAllAsync();
        }
        catch (Exception ex) { SetStatus("Ошибка применения правила: " + ex.Message); }
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
        SelectByTag(UnmatchedPolicyBox, w.Unmatched.ToString());
        SelectByTag(CollectionPlacementBox, w.CollectionPlacement.ToString());
        SelectByTag(ConflictPolicyBox, w.ConflictPolicy.ToString());
        RetriesBox.Text = w.LockedFileRetries.ToString();
        ScheduleEnabledBox.IsChecked = w.ScheduleEnabled;
        ScheduleMinutesBox.Text = w.ScheduleMinutes.ToString();
        LocalAuditBox.IsChecked = w.LocalAudit;

        ExcludeFoldersBox.Text = string.Join(Environment.NewLine, w.ExcludeFolders ?? new());
        ExcludeExtBox.Text = string.Join(", ", w.ExcludeExtensions ?? new());
        ExcludeNameBox.Text = string.Join(", ", w.ExcludeNameContains ?? new());

        var a = w.Appearance ?? new AppearanceSettings();
        SelectByContent(ThemeBox, a.Theme);
        AccentBox.Text = a.Accent;
        TileSizeBox.Text = a.TileSize.ToString();
        FontScaleBox.Text = a.FontScale.ToString("0.##");
        AnimationsBox.IsChecked = a.Animations;
        UseAcrylicBox.IsChecked = a.UseAcrylic;
        PinToDesktopLayerBox.IsChecked = a.PinToDesktopLayer;
        NoActivateWindowsBox.IsChecked = a.NoActivateWindows;
        SelectByContent(DensityBox, a.Density);

        ApplyPanelAccent();
        RefreshProfilesList();
    }

    private void ReadSettingsFromUi()
    {
        if (_orchestrator == null) return;
        var w = _orchestrator.Data.Watch;
        w.Recursive = RecursiveWatch.IsChecked == true;
        if (int.TryParse(StableSecondsBox.Text, out var ss) && ss >= 1 && ss <= 120) w.StableSeconds = ss;
        w.UnmatchedVitrineName = string.IsNullOrWhiteSpace(UnmatchedVitrineBox.Text) ? "Неразобранное" : UnmatchedVitrineBox.Text.Trim();
        if (Enum.TryParse<UnmatchedPolicy>((UnmatchedPolicyBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var pol)) w.Unmatched = pol;
        if (Enum.TryParse<CollectionPlacement>((CollectionPlacementBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var placement)) w.CollectionPlacement = placement;
        if (Enum.TryParse<NameConflictPolicy>((ConflictPolicyBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var conflict)) w.ConflictPolicy = conflict;
        if (int.TryParse(RetriesBox.Text, out var retries)) w.LockedFileRetries = Math.Clamp(retries, 0, 10);
        w.ScheduleEnabled = ScheduleEnabledBox.IsChecked == true;
        if (int.TryParse(ScheduleMinutesBox.Text, out var minutes)) w.ScheduleMinutes = Math.Clamp(minutes, 5, 1440);
        w.LocalAudit = LocalAuditBox.IsChecked == true;

        w.ExcludeFolders = SplitLines(ExcludeFoldersBox.Text);
        w.ExcludeExtensions = SplitList(ExcludeExtBox.Text);
        w.ExcludeNameContains = SplitList(ExcludeNameBox.Text);

        w.Appearance ??= new AppearanceSettings();
        w.Appearance.Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "dark";
        if (!string.IsNullOrWhiteSpace(AccentBox.Text)) w.Appearance.Accent = AccentBox.Text.Trim();
        if (int.TryParse(TileSizeBox.Text, out var ts)) w.Appearance.TileSize = Math.Clamp(ts, 72, 160);
        if (double.TryParse(FontScaleBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fs) || double.TryParse(FontScaleBox.Text, out fs)) w.Appearance.FontScale = Math.Clamp(fs, 0.8, 1.6);
        w.Appearance.Density = (DensityBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "normal";
        w.Appearance.Animations = AnimationsBox.IsChecked == true;
        w.Appearance.UseAcrylic = UseAcrylicBox.IsChecked == true;
        w.Appearance.PinToDesktopLayer = PinToDesktopLayerBox.IsChecked == true;
        w.Appearance.NoActivateWindows = NoActivateWindowsBox.IsChecked == true;
    }

    private void ApplyPanelAccent()
    {
        var accent = ParseAccentColor(AccentBox?.Text);
        var soft = Color.FromArgb(0x33, accent.R, accent.G, accent.B);
        Resources["AccentBrush"] = new SolidColorBrush(accent);
        Resources["AccentSoftBrush"] = new SolidColorBrush(soft);
    }

    private static Color ParseAccentColor(string? text)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text) is Color c) return c;
        }
        catch { }
        return Color.FromRgb(0x7C, 0x9C, 0xFF);
    }

    private static void SelectByTag(ComboBox box, string? tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = item; return; }
        if (box.SelectedItem == null && box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static void SelectByContent(ComboBox box, string? content)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = item; return; }
        if (box.SelectedItem == null && box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        ReadSettingsFromUi();
        await _store.SaveSettingsAsync(_orchestrator.Data);
        _orchestrator.ReconfigureWatcher();
        UpdateScheduleTimer();
        ApplyPanelAccent();
        _desktop?.RefreshOpen();
        await RefreshAllAsync();
        SetStatus("Настройки сохранены, наблюдение обновлено.");
    }

    private void ApplyAppearance_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromUi();
        ApplyPanelAccent();
        _desktop?.RefreshOpen();
        SetStatus("Оформление применено к открытым витринам (сохраните настройки, чтобы закрепить).");
    }

    private async void RebuildFromRules_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        foreach (var r in _orchestrator.Data.Rules.Where(r =>
                     r.Action.Type is RuleActionType.CreateVitrineAndPlace or RuleActionType.PlaceInVitrine &&
                     !string.IsNullOrWhiteSpace(r.Action.CreateVitrineName)))
        {
            var name = r.Action.CreateVitrineName!;
            if (!_orchestrator.Data.Vitrines.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
                _orchestrator.Data.Vitrines.Add(new Vitrine { Name = name, Kind = VitrineKind.Collection, X = 40 + (_orchestrator.Data.Vitrines.Count % 6) * 40, Y = 40 + (_orchestrator.Data.Vitrines.Count % 6) * 30 });
        }
        await _store.SaveLayoutAsync(_orchestrator.Data);
        _desktop?.ShowAll();
        await RefreshAllAsync();
        SetStatus("Витрины для правил созданы/показаны.");
    }

    // --- profiles ---
    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _snapshots == null) return;
        var name = Prompt("Имя профиля:", "profile-" + DateTime.Now.ToString("yyyyMMdd-HHmm"));
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            ReadSettingsFromUi();
            await _orchestrator.SaveAllAsync();
            var info = await _snapshots.CreateAsync(name.Trim());
            RefreshProfilesList(info.Name);
            SetStatus($"Профиль сохранён: {info.Name}");
        }
        catch (Exception ex) { SetStatus("Ошибка сохранения профиля: " + ex.Message); }
    }

    private async void RestoreProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _snapshots == null) return;
        var snapshot = SelectedSnapshot();
        if (snapshot == null) { SetStatus("Выберите профиль для восстановления."); return; }
        if (MessageBox.Show(this, $"Восстановить профиль «{snapshot.Name}»?\nТекущие правила/раскладка/настройки будут заменены (перед этим создаётся авто-копия).",
                "Восстановление профиля", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _snapshots.CreateAsync("auto-before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            await _snapshots.RestoreAsync(snapshot);
            await ReloadWorkspaceAsync();
            RefreshProfilesList(snapshot.Name);
            SetStatus($"Профиль восстановлен: {snapshot.Name}");
        }
        catch (Exception ex) { SetStatus("Ошибка восстановления профиля: " + ex.Message); }
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshots == null) return;
        var snapshot = SelectedSnapshot();
        if (snapshot == null) { SetStatus("Выберите профиль."); return; }
        var name = Prompt("Новое имя профиля:", snapshot.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var renamed = _snapshots.Rename(snapshot, name.Trim());
            RefreshProfilesList(renamed.Name);
            SetStatus($"Профиль переименован: {renamed.Name}");
        }
        catch (Exception ex) { SetStatus("Ошибка переименования профиля: " + ex.Message); }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshots == null) return;
        var snapshot = SelectedSnapshot();
        if (snapshot == null) { SetStatus("Выберите профиль."); return; }
        if (MessageBox.Show(this, $"Удалить профиль «{snapshot.Name}»?", "Удаление профиля", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _snapshots.Delete(snapshot);
        RefreshProfilesList();
        SetStatus($"Профиль удалён: {snapshot.Name}");
    }

    private void RefreshProfiles_Click(object sender, RoutedEventArgs e) => RefreshProfilesList();

    private void RefreshProfilesList(string? selectName = null)
    {
        if (_snapshots == null) return;
        var keep = selectName ?? ProfilesBox.SelectedItem as string;
        ProfilesBox.Items.Clear();
        var list = _snapshots.List();
        foreach (var s in list) ProfilesBox.Items.Add(s.Name);
        if (!string.IsNullOrWhiteSpace(keep) && ProfilesBox.Items.Contains(keep)) ProfilesBox.SelectedItem = keep;
        else if (ProfilesBox.Items.Count > 0) ProfilesBox.SelectedIndex = 0;

        ProfilesInfo.Text = list.Count == 0
            ? $"Профилей пока нет. Папка: {_snapshots.SnapshotsDirectory}"
            : $"Профилей: {list.Count} · Папка: {_snapshots.SnapshotsDirectory}";
    }

    private SnapshotInfo? SelectedSnapshot()
    {
        if (_snapshots == null || ProfilesBox.SelectedItem is not string name) return null;
        return _snapshots.List().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ReloadWorkspaceAsync()
    {
        if (_orchestrator == null) return;
        _orchestrator.Stop();
        await _orchestrator.StartAsync();
        _loading = true;
        try
        {
            AutoToggle.IsChecked = _orchestrator.Data.Watch.AutomationEnabled;
            LoadSettingsToUi();
        }
        finally { _loading = false; }
        UpdateScheduleTimer();
        // Данные перезагружены с диска: окна должны ссылаться на новые объекты моделей.
        _desktop?.HideAll();
        _desktop?.ShowAll();
        await RefreshAllAsync();
    }

    // --- export/import ---
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _snapshots == null) return;
        var dlg = new SaveFileDialog { Title = "Экспорт снапшота Витрин", Filter = "Zip (*.zip)|*.zip", FileName = $"vitrines-snapshot-{DateTime.Now:yyyyMMdd-HHmm}.zip" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ReadSettingsFromUi();
            await _orchestrator.SaveAllAsync();
            await _snapshots.ExportAsync(dlg.FileName);
            SetStatus($"Экспорт выполнен: {dlg.FileName}");
        }
        catch (Exception ex) { SetStatus("Ошибка экспорта: " + ex.Message); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _snapshots == null) return;
        var dlg = new OpenFileDialog { Title = "Импорт снапшота Витрин", Filter = "Zip (*.zip)|*.zip" };
        if (dlg.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "Импорт заменит текущие layout/rules/settings/inbox/history. Продолжить?", "Импорт", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _snapshots.ImportAsync(dlg.FileName);
            await ReloadWorkspaceAsync();
            SetStatus("Импорт выполнен. Наблюдение перезапущено.");
        }
        catch (Exception ex) { SetStatus("Ошибка импорта: " + ex.Message); }
    }

    // --- helpers ---
    private string SuggestVitrineFor(InboxEntry entry)
    {
        var ext = Path.GetExtension(entry.Path).TrimStart('.').ToLowerInvariant();
        var name = ext switch
        {
            "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "svg" or "heic" or "tif" or "tiff" => "Изображения",
            "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "txt" or "rtf" or "csv" or "md" or "odt" => "Документы",
            "zip" or "rar" or "7z" or "tar" or "gz" or "bz2" or "xz" => "Архивы",
            "exe" or "msi" or "msix" or "appx" => "Установщики",
            "mp4" or "mkv" or "avi" or "mov" or "webm" or "wmv" => "Видео",
            "mp3" or "wav" or "flac" or "ogg" or "m4a" or "aac" => "Аудио",
            "cs" or "js" or "ts" or "py" or "go" or "rs" or "java" or "cpp" or "h" or "json" or "xml" or "yml" or "yaml" => "Код",
            _ => ""
        };
        if (!string.IsNullOrWhiteSpace(name)) return name;
        if (Directory.Exists(entry.Path)) return "Папки";
        if (Path.GetExtension(entry.Path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return "Ярлыки";
        return string.IsNullOrWhiteSpace(ext) ? Data.Watch.UnmatchedVitrineName : ext.ToUpperInvariant();
    }

    private static List<string> SplitList(string? s) => string.IsNullOrWhiteSpace(s) ? new() : s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
    private static List<string> SplitLines(string? s) => string.IsNullOrWhiteSpace(s) ? new() : s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

    private static SortingRule CloneRule(SortingRule r) => System.Text.Json.JsonSerializer.Deserialize<SortingRule>(System.Text.Json.JsonSerializer.Serialize(r))!;
    private static void CopyRule(SortingRule src, SortingRule dst)
    {
        dst.Name = src.Name; dst.Enabled = src.Enabled; dst.Order = src.Order; dst.IsExclusion = src.IsExclusion; dst.Condition = src.Condition; dst.Action = src.Action;
    }

    private string? Prompt(string text, string def)
    {
        var w = new Window
        {
            Title = "Витрины", Width = 460, Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow,
            Owner = this,
            Content = new StackPanel { Margin = new Thickness(12) }
        };
        var sp = (StackPanel)w.Content;
        sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        var tb = new TextBox { Text = def, Margin = new Thickness(0, 0, 0, 10) };
        sp.Children.Add(tb);
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => { w.DialogResult = true; w.Close(); };
        sp.Children.Add(ok);
        w.Loaded += (_, _) => { tb.SelectAll(); tb.Focus(); };
        return w.ShowDialog() == true ? tb.Text : null;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private sealed class RuleRow
    {
        public RuleRow(SortingRule rule, string text) { Rule = rule; Text = text; }
        public SortingRule Rule { get; }
        public string Text { get; }
        public override string ToString() => Text;
    }
}
