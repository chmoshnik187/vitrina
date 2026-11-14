using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Vitrines.Core.Actions;
using Vitrines.Core.Models;
using Vitrines.Core.Rules;
using Vitrines.Core.Sorting;
using Vitrines.Core.Storage;
using Vitrines.Core.Watcher;
using Vitrines.App.Desktop;

namespace Vitrines.App;

public partial class MainWindow : Window, IDesktopActions
{
    private SortingOrchestrator? _orchestrator;
    private DesktopVitrineManager? _desktop;
    private IWorkspaceStore? _store;
    private IFileOperator? _fileOps;
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();
    }

    public WorkspaceData Data => _orchestrator?.Data ?? new WorkspaceData();

    public async Task SaveLayoutAsync()
    {
        if (_store == null) return;
        await _store.SaveLayoutAsync(Data);
    }

    public async Task<string?> MoveFileToFolderAsync(string sourcePath, string targetFolder)
    {
        if (_fileOps == null) return null;
        try
        {
            _orchestrator?.MarkSelfProduced(sourcePath);
            var final = await _fileOps.MoveAsync(sourcePath, targetFolder);
            _orchestrator?.MarkSelfProduced(final);
            LogManual($"Файл перемещён в папку портала: {Path.GetFileName(final)}");
            return final;
        }
        catch (Exception ex)
        {
            LogManual($"Ошибка перемещения: {ex.Message}");
            return null;
        }
    }

    public void LogManual(string message) => Dispatcher.InvokeAsync(() => SetStatus(message));

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _store = new JsonWorkspaceStore();
            var rules = new SimpleRuleEngine();
            _fileOps = new FileOperator();
            var watcher = new FileWatcherService();
            _orchestrator = new SortingOrchestrator(_store, rules, _fileOps, watcher);
            _orchestrator.OnHistory += Orchestrator_OnHistory;
            await _orchestrator.StartAsync();

            _desktop = new DesktopVitrineManager(this);

            _loading = true;
            AutoToggle.IsChecked = _orchestrator.Data.Watch.AutomationEnabled;
            _loading = false;

            await RefreshAllAsync();
            SetStatus("Готово. Наблюдение активно, автоматика " + (_orchestrator.Data.Watch.AutomationEnabled ? "включена." : "выключена (безопасно)."));

            var args = Environment.GetCommandLineArgs();
            if (args.Any(a => string.Equals(a, "--show-vitrines", StringComparison.OrdinalIgnoreCase)))
            {
                _desktop?.ShowAll();
                SetStatus("Витрины показаны на рабочем столе.");
            }
            if (args.Any(a => string.Equals(a, "--scan", StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus("Разбор существующих файлов…");
                var n = await _orchestrator.ScanExistingAsync();
                _desktop?.RefreshOpen();
                await RefreshAllAsync();
                SetStatus($"Разбор завершён: обработано {n} элементов.");
                if (args.Any(a => string.Equals(a, "--exit", StringComparison.OrdinalIgnoreCase)))
                {
                    await Task.Delay(300);
                    Close();
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка запуска: " + ex.Message);
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _desktop?.HideAll();
        if (_orchestrator != null)
        {
            _orchestrator.OnHistory -= Orchestrator_OnHistory;
            _orchestrator.Dispose();
        }
    }

    private void Orchestrator_OnHistory(object? sender, Vitrines.Core.Models.HistoryEntry entry)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            HistoryList.Items.Insert(0, FormatHistory(entry));
            while (HistoryList.Items.Count > 300) HistoryList.Items.RemoveAt(HistoryList.Items.Count - 1);
            // новые/изменённые витрины показываем, чтобы было видно результат авто-сортировки
            if (entry.Event == Vitrines.Core.Models.HistoryEvent.CreatedVitrine ||
                entry.Event == Vitrines.Core.Models.HistoryEvent.Placed ||
                entry.Event == Vitrines.Core.Models.HistoryEvent.Moved)
            {
                _desktop?.ShowAll();
            }
            else
            {
                _desktop?.RefreshOpen();
            }
            await RefreshAllAsync();
        });
    }

    private async void AutoToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _orchestrator == null) return;
        _orchestrator.Data.Watch.AutomationEnabled = AutoToggle.IsChecked == true;
        try
        {
            var store = new JsonWorkspaceStore();
            await store.SaveSettingsAsync(_orchestrator.Data);
        }
        catch { }
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
            SetStatus($"Разбор завершён: обработано {n} элементов (действует по текущим правилам)." + (_orchestrator.Data.Watch.AutomationEnabled ? "" : " Автоматика для новых файлов выключена."));
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка разбора: " + ex.Message);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

    private void ShowVitrines_Click(object sender, RoutedEventArgs e)
    {
        _desktop?.ShowAll();
        SetStatus("Витрины показаны на рабочем столе.");
    }

    private void HideVitrines_Click(object sender, RoutedEventArgs e)
    {
        _desktop?.HideAll();
        SetStatus("Витрины скрыты.");
    }

    private async void NewVitrine_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        var v = new Vitrines.Core.Models.Vitrine
        {
            Name = "Новая витрина",
            Kind = Vitrines.Core.Models.VitrineKind.Collection,
            X = 60 + (Data.Vitrines.Count % 8) * 30,
            Y = 60 + (Data.Vitrines.Count % 8) * 24,
            Width = 320,
            Height = 240
        };
        Data.Vitrines.Add(v);
        await SaveLayoutAsync();
        _desktop?.ShowAll();
        await RefreshAllAsync();
        SetStatus($"Создана витрина «{v.Name}». Переименовать/удалить — через меню заголовка витрины.");
    }

    private async void NewPortal_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        var dlg = new OpenFolderDialog { Title = "Выберите папку для портала (будет отражаться на рабочем столе)" };
        if (dlg.ShowDialog(this) != true) return;
        var folder = dlg.FolderName;
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) name = "Портал";
        var v = new Vitrines.Core.Models.Vitrine
        {
            Name = name,
            Kind = Vitrines.Core.Models.VitrineKind.Portal,
            FolderPath = folder,
            X = 80 + (Data.Vitrines.Count % 8) * 30,
            Y = 80 + (Data.Vitrines.Count % 8) * 24,
            Width = 340,
            Height = 260
        };
        Data.Vitrines.Add(v);
        await SaveLayoutAsync();
        _desktop?.ShowAll();
        await RefreshAllAsync();
        SetStatus($"Создан портал «{v.Name}» → {folder}");
    }

    private async Task RefreshAllAsync()
    {
        if (_orchestrator == null) return;
        try
        {
            var store = new JsonWorkspaceStore();
            var history = await store.ReadHistoryAsync(200);

            HistoryList.Items.Clear();
            foreach (var h in history) HistoryList.Items.Add(FormatHistory(h));

            SourcesList.Items.Clear();
            foreach (var s in _orchestrator.Data.Watch.Sources) SourcesList.Items.Add(s);

            VitrinesList.Items.Clear();
            foreach (var v in _orchestrator.Data.Vitrines)
                VitrinesList.Items.Add($"[{v.Kind}] {v.Name} — элементов: {v.Items.Count}" + (v.Kind == Vitrines.Core.Models.VitrineKind.Portal ? $" (папка: {v.FolderPath})" : ""));

            RulesList.Items.Clear();
            foreach (var r in _orchestrator.Data.Rules.OrderBy(r => r.Order))
                RulesList.Items.Add($"{(r.Enabled ? "[on]" : "[off]")} #{r.Order} {r.Name} → {r.Action.Type}" + (r.Action.CreateVitrineName != null ? $" '{r.Action.CreateVitrineName}'" : ""));

            SummaryText.Text = $"Витрин: {_orchestrator.Data.Vitrines.Count} · Правил: {_orchestrator.Data.Rules.Count} · Источников: {_orchestrator.Data.Watch.Sources.Count} · Стабильность: {_orchestrator.Data.Watch.StableSeconds} c · Данные: %LOCALAPPDATA%\\Vitrines";
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка обновления: " + ex.Message);
        }
    }

    private void DryRun_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        var dlg = new OpenFolderDialog { Title = "Выберите папку для dry-run (файлы не будут перемещены)" };
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
                var mark = decision.Action == Vitrines.Core.Models.RuleActionType.Ignore ? "—" : "→";
                if (decision.Action != Vitrines.Core.Models.RuleActionType.Ignore) willAct++;
                var target = decision.TargetVitrineName ?? decision.TargetVitrineId?.ToString() ?? decision.MatchedRule?.Action.TargetFolderPath ?? "";
                DryRunList.Items.Add($"{mark} {name}: {decision.Action} {target} ({decision.Reason})");
            }
            DryRunInfo.Text = $"Проверено: {results.Count}, с действием: {willAct}, без изменений: {results.Count - willAct}. Папка: {folder}";
            SetStatus("Dry-run завершён, файлы не трогались.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка dry-run: " + ex.Message);
        }
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        try
        {
            var ok = await _orchestrator.UndoLastAsync();
            SetStatus(ok ? "Последнее перемещение отменено." : "Нет обратимых действий для отката.");
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка отката: " + ex.Message);
        }
    }

    private static string FormatHistory(Vitrines.Core.Models.HistoryEntry h)
        => $"{h.At:HH:mm:ss} [{h.Event}] {h.ItemPath}" +
           (h.To != null ? $" => {h.To}" : "") +
           (string.IsNullOrWhiteSpace(h.Reason) ? "" : $" — {h.Reason}");

    // --- источники ---
    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        var dlg = new OpenFolderDialog { Title = "Выберите папку-источник для наблюдения" };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FolderName;
        if (!_orchestrator.Data.Watch.Sources.Contains(path, StringComparer.OrdinalIgnoreCase))
            _orchestrator.Data.Watch.Sources.Add(path);
        await PersistSettingsAndRestartWatcherAsync();
        await RefreshAllAsync();
        SetStatus($"Источник добавлен: {path}");
    }

    private async void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null) return;
        if (SourcesList.SelectedItem is not string path) { SetStatus("Выберите источник для удаления."); return; }
        _orchestrator.Data.Watch.Sources.RemoveAll(s => string.Equals(s, path, StringComparison.OrdinalIgnoreCase));
        await PersistSettingsAndRestartWatcherAsync();
        await RefreshAllAsync();
        SetStatus($"Источник удалён: {path}");
    }

    private async Task PersistSettingsAndRestartWatcherAsync()
    {
        if (_orchestrator == null || _store == null) return;
        await _store.SaveSettingsAsync(_orchestrator.Data);
        // перезапуск наблюдения с новыми источниками
        _orchestrator.Stop();
        await _orchestrator.StartAsync();
        _loading = true; AutoToggle.IsChecked = _orchestrator.Data.Watch.AutomationEnabled; _loading = false;
    }

    // --- правила ---
    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        var name = Prompt("Название правила:", "Новое правило");
        if (string.IsNullOrWhiteSpace(name)) return;
        var exts = Prompt("Расширения через запятую (напр. jpg,png,pdf), пусто = не ограничивать:", "");
        var target = Prompt("Имя витрины для размещения (будет создана при необходимости):", name);
        if (string.IsNullOrWhiteSpace(target)) target = name;

        var rule = new Vitrines.Core.Models.SortingRule
        {
            Name = name.Trim(),
            Order = (_orchestrator.Data.Rules.Count == 0 ? 10 : _orchestrator.Data.Rules.Max(r => r.Order) + 10),
            Enabled = true,
            Condition = new Vitrines.Core.Models.RuleCondition(),
            Action = new Vitrines.Core.Models.RuleAction { Type = Vitrines.Core.Models.RuleActionType.CreateVitrineAndPlace, CreateVitrineName = target.Trim() }
        };
        if (!string.IsNullOrWhiteSpace(exts))
            rule.Condition.Extensions = exts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        _orchestrator.Data.Rules.Add(rule);
        await _store.SaveRulesAsync(_orchestrator.Data);
        await RefreshAllAsync();
        SetStatus($"Правило добавлено: {rule.Name}");
    }

    private async void ToggleRule_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        if (RulesList.SelectedIndex < 0) { SetStatus("Выберите правило."); return; }
        var ordered = _orchestrator.Data.Rules.OrderBy(r => r.Order).ToList();
        if (RulesList.SelectedIndex >= ordered.Count) return;
        var r = ordered[RulesList.SelectedIndex];
        r.Enabled = !r.Enabled;
        await _store.SaveRulesAsync(_orchestrator.Data);
        await RefreshAllAsync();
        SetStatus($"Правило {(r.Enabled ? "включено" : "выключено")}: {r.Name}");
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        if (RulesList.SelectedIndex < 0) { SetStatus("Выберите правило."); return; }
        var ordered = _orchestrator.Data.Rules.OrderBy(r => r.Order).ToList();
        if (RulesList.SelectedIndex >= ordered.Count) return;
        var r = ordered[RulesList.SelectedIndex];
        _orchestrator.Data.Rules.Remove(r);
        await _store.SaveRulesAsync(_orchestrator.Data);
        await RefreshAllAsync();
        SetStatus($"Правило удалено: {r.Name}");
    }

    private async void SaveRules_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator == null || _store == null) return;
        await _store.SaveRulesAsync(_orchestrator.Data);
        SetStatus("Правила сохранены.");
    }

    private static string? Prompt(string text, string def)
    {
        var w = new Window
        {
            Title = "Витрины",
            Width = 420, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
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
}
