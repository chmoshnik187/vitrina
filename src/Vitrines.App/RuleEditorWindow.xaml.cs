using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Vitrines.Core.Models;

namespace Vitrines.App;

public partial class RuleEditorWindow : Window
{
    private readonly SortingRule _rule;

    public RuleEditorWindow(SortingRule rule, IEnumerable<Vitrine>? vitrines = null)
    {
        InitializeComponent();
        _rule = rule;

        AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateSummary()));
        AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler((_, _) => UpdateSummary()));
        AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler((_, _) => UpdateSummary()));
        AddHandler(DatePicker.SelectedDateChangedEvent, new EventHandler<SelectionChangedEventArgs>((_, _) => UpdateSummary()));

        LoadRule(vitrines);
        UpdateActionUi();
        UpdateSummary();
    }

    public static bool Edit(Window owner, SortingRule rule, IEnumerable<Vitrine>? vitrines = null)
    {
        var window = new RuleEditorWindow(rule, vitrines) { Owner = owner };
        return window.ShowDialog() == true;
    }

    private void LoadRule(IEnumerable<Vitrine>? vitrines)
    {
        NameBox.Text = _rule.Name;
        EnabledCheck.IsChecked = _rule.Enabled;
        ExclusionCheck.IsChecked = _rule.IsExclusion;

        var c = _rule.Condition ?? new RuleCondition();
        ExtensionsBox.Text = Join(c.Extensions);
        KindFileCheck.IsChecked = c.Kinds?.Contains(ItemKind.File) == true;
        KindFolderCheck.IsChecked = c.Kinds?.Contains(ItemKind.Folder) == true;
        KindShortcutCheck.IsChecked = c.Kinds?.Contains(ItemKind.Shortcut) == true;
        KindApplicationCheck.IsChecked = c.Kinds?.Contains(ItemKind.Application) == true;
        NameContainsBox.Text = Join(c.NameContains);
        NamePatternsBox.Text = Join(c.NamePatterns);
        RegexBox.Text = c.NameRegex ?? "";
        SourcesBox.Text = JoinLines(c.SourcePaths);
        MinSizeBox.Text = c.MinSizeBytes is long min ? (min / 1024).ToString(CultureInfo.InvariantCulture) : "";
        MaxSizeBox.Text = c.MaxSizeBytes is long max ? (max / 1024).ToString(CultureInfo.InvariantCulture) : "";
        ModifiedAfterPicker.SelectedDate = c.ModifiedAfter?.LocalDateTime.Date;
        ModifiedBeforePicker.SelectedDate = c.ModifiedBefore?.LocalDateTime.Date;
        CreatedAfterPicker.SelectedDate = c.CreatedAfter?.LocalDateTime.Date;
        CreatedBeforePicker.SelectedDate = c.CreatedBefore?.LocalDateTime.Date;
        ExcludeExtensionsBox.Text = Join(c.ExcludeExtensions);
        ExcludeNameBox.Text = Join(c.ExcludeNameContains);
        ExcludeMasksBox.Text = Join(c.ExcludeNamePatterns);
        ExcludeFoldersBox.Text = JoinLines(c.ExcludeFolders);

        SelectAction(_rule.Action?.Type ?? RuleActionType.PlaceInVitrine);
        var target = _rule.Action?.CreateVitrineName ?? _rule.Action?.TargetFolderPath ?? "";
        if (string.IsNullOrWhiteSpace(target) && _rule.Action?.TargetVitrineId is Guid id)
            target = vitrines?.FirstOrDefault(v => v.Id == id)?.Name ?? "";
        TargetBox.Text = target;
        RenameTemplateBox.Text = _rule.Action?.RenameTemplate ?? "";
    }

    private void SelectAction(RuleActionType type)
    {
        foreach (var item in ActionBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), type.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                ActionBox.SelectedItem = item;
                return;
            }
        }
        ActionBox.SelectedIndex = 0;
    }

    private RuleActionType SelectedAction =>
        Enum.TryParse<RuleActionType>((ActionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var type)
            ? type
            : RuleActionType.PlaceInVitrine;

    private void ActionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionUi();

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Целевая папка для правила" };
        if (dlg.ShowDialog(this) == true) TargetBox.Text = dlg.FolderName;
    }

    private void UpdateActionUi()
    {
        if (TargetLabel == null) return;
        var isExclusion = ExclusionCheck.IsChecked == true;
        var action = SelectedAction;
        ActionBox.IsEnabled = !isExclusion;
        TargetBox.IsEnabled = !isExclusion && action is RuleActionType.CreateVitrineAndPlace or RuleActionType.PlaceInVitrine or RuleActionType.MoveToFolder;
        BrowseFolderButton.Visibility = !isExclusion && action == RuleActionType.MoveToFolder ? Visibility.Visible : Visibility.Collapsed;
        RenameTemplateBox.IsEnabled = !isExclusion && action is RuleActionType.CreateVitrineAndPlace or RuleActionType.PlaceInVitrine or RuleActionType.MoveToFolder;
        TargetLabel.Text = action switch
        {
            RuleActionType.MoveToFolder => "Путь папки назначения",
            RuleActionType.CreateVitrineAndPlace => "Имя витрины (создастся при необходимости)",
            RuleActionType.PlaceInVitrine => "Имя существующей витрины",
            _ => "Цель не требуется"
        };
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (SummaryText == null) return;
        var condition = new RuleCondition();
        FillCondition(condition);
        var action = new RuleAction();
        FillAction(action);
        var preview = new SortingRule
        {
            Order = _rule.Order,
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Правило" : NameBox.Text.Trim(),
            Enabled = EnabledCheck.IsChecked == true,
            IsExclusion = ExclusionCheck.IsChecked == true,
            Condition = condition,
            Action = action
        };
        SummaryText.Text = "Предпросмотр: " + RuleFormatting.DescribeRule(preview);
    }

    private void FillCondition(RuleCondition c)
    {
        c.Extensions = NullIfEmpty(SplitList(ExtensionsBox.Text));
        var kinds = new List<ItemKind>();
        if (KindFileCheck.IsChecked == true) kinds.Add(ItemKind.File);
        if (KindFolderCheck.IsChecked == true) kinds.Add(ItemKind.Folder);
        if (KindShortcutCheck.IsChecked == true) kinds.Add(ItemKind.Shortcut);
        if (KindApplicationCheck.IsChecked == true) kinds.Add(ItemKind.Application);
        c.Kinds = kinds.Count == 0 ? null : kinds;
        c.NameContains = NullIfEmpty(SplitList(NameContainsBox.Text));
        c.NamePatterns = NullIfEmpty(SplitList(NamePatternsBox.Text));
        c.NameRegex = string.IsNullOrWhiteSpace(RegexBox.Text) ? null : RegexBox.Text.Trim();
        c.SourcePaths = NullIfEmpty(SplitLines(SourcesBox.Text));
        c.MinSizeBytes = ParseKilobytes(MinSizeBox.Text);
        c.MaxSizeBytes = ParseKilobytes(MaxSizeBox.Text);
        c.ModifiedAfter = StartOfDay(ModifiedAfterPicker.SelectedDate);
        c.ModifiedBefore = EndOfDay(ModifiedBeforePicker.SelectedDate);
        c.CreatedAfter = StartOfDay(CreatedAfterPicker.SelectedDate);
        c.CreatedBefore = EndOfDay(CreatedBeforePicker.SelectedDate);
        c.ExcludeExtensions = NullIfEmpty(SplitList(ExcludeExtensionsBox.Text));
        c.ExcludeNameContains = NullIfEmpty(SplitList(ExcludeNameBox.Text));
        c.ExcludeNamePatterns = NullIfEmpty(SplitList(ExcludeMasksBox.Text));
        c.ExcludeFolders = NullIfEmpty(SplitLines(ExcludeFoldersBox.Text));
    }

    private void FillAction(RuleAction action)
    {
        action.RenameTemplate = string.IsNullOrWhiteSpace(RenameTemplateBox.Text) ? null : RenameTemplateBox.Text.Trim();
        if (ExclusionCheck.IsChecked == true)
        {
            action.Type = RuleActionType.Ignore;
            return;
        }

        action.Type = SelectedAction;
        var target = TargetBox.Text?.Trim() ?? "";
        switch (action.Type)
        {
            case RuleActionType.CreateVitrineAndPlace:
            case RuleActionType.PlaceInVitrine:
                action.CreateVitrineName = target;
                action.TargetVitrineId = null;
                action.TargetFolderPath = null;
                break;
            case RuleActionType.MoveToFolder:
                action.TargetFolderPath = target;
                action.TargetVitrineId = null;
                break;
            default:
                action.TargetVitrineId = null;
                action.TargetFolderPath = null;
                break;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(RegexBox.Text))
        {
            try { _ = new Regex(RegexBox.Text, RegexOptions.IgnoreCase); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Некорректное регулярное выражение:\n" + ex.Message, "Правило", MessageBoxButton.OK, MessageBoxImage.Warning);
                RegexBox.Focus();
                return;
            }
        }

        var actionType = ExclusionCheck.IsChecked == true ? RuleActionType.Ignore : SelectedAction;
        if ((actionType is RuleActionType.CreateVitrineAndPlace or RuleActionType.PlaceInVitrine) && string.IsNullOrWhiteSpace(TargetBox.Text))
        {
            MessageBox.Show(this, "Укажите имя витрины для правила.", "Правило", MessageBoxButton.OK, MessageBoxImage.Warning);
            TargetBox.Focus();
            return;
        }
        if (actionType == RuleActionType.MoveToFolder && string.IsNullOrWhiteSpace(TargetBox.Text))
        {
            MessageBox.Show(this, "Укажите папку назначения для правила.", "Правило", MessageBoxButton.OK, MessageBoxImage.Warning);
            TargetBox.Focus();
            return;
        }

        var min = ParseKilobytes(MinSizeBox.Text);
        var max = ParseKilobytes(MaxSizeBox.Text);
        if (min is not null && max is not null && min > max)
        {
            MessageBox.Show(this, "Минимальный размер больше максимального.", "Правило", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _rule.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Правило" : NameBox.Text.Trim();
        _rule.Enabled = EnabledCheck.IsChecked == true;
        _rule.IsExclusion = ExclusionCheck.IsChecked == true;
        _rule.Condition ??= new RuleCondition();
        _rule.Action ??= new RuleAction();
        FillCondition(_rule.Condition);
        FillAction(_rule.Action);
        if (_rule.IsExclusion) _rule.Action.Type = RuleActionType.Ignore;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static long? ParseKilobytes(string? text)
    {
        if (!long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) &&
            !long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out kb))
            return null;
        return kb < 0 ? null : kb * 1024;
    }

    private static DateTimeOffset? StartOfDay(DateTime? date)
        => date is DateTime d ? new DateTimeOffset(d.Date) : null;

    private static DateTimeOffset? EndOfDay(DateTime? date)
        => date is DateTime d ? new DateTimeOffset(d.Date.AddDays(1).AddTicks(-1)) : null;

    private static List<string> SplitList(string? s)
        => string.IsNullOrWhiteSpace(s)
            ? new List<string>()
            : s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

    private static List<string> SplitLines(string? s)
        => string.IsNullOrWhiteSpace(s)
            ? new List<string>()
            : s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

    private static List<string>? NullIfEmpty(List<string> list) => list.Count == 0 ? null : list;
    private static string Join(IEnumerable<string>? values) => values == null ? "" : string.Join(", ", values);
    private static string JoinLines(IEnumerable<string>? values) => values == null ? "" : string.Join(Environment.NewLine, values);
}
