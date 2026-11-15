using Vitrines.Core.Models;

namespace Vitrines.App;

/// <summary>Единое человекочитаемое описание правил (список, подсказки, сводка редактора).</summary>
public static class RuleFormatting
{
    public static string DescribeCondition(RuleCondition? c)
    {
        if (c == null) return "всегда";
        var parts = new List<string>();
        if (c.Extensions is { Count: > 0 }) parts.Add("ext:" + string.Join(",", c.Extensions));
        if (c.Kinds is { Count: > 0 }) parts.Add("тип:" + string.Join(",", c.Kinds.Select(KindName)));
        if (c.NameContains is { Count: > 0 }) parts.Add("имя~" + string.Join("|", c.NameContains));
        if (c.NamePatterns is { Count: > 0 }) parts.Add("маска:" + string.Join("|", c.NamePatterns));
        if (!string.IsNullOrWhiteSpace(c.NameRegex)) parts.Add("regex");
        if (c.SourcePaths is { Count: > 0 }) parts.Add("источников:" + c.SourcePaths.Count);
        if (c.MinSizeBytes is not null || c.MaxSizeBytes is not null) parts.Add("размер");
        if (c.ModifiedAfter != null || c.ModifiedBefore != null || c.CreatedAfter != null || c.CreatedBefore != null) parts.Add("дата");
        if (c.ExcludeExtensions is { Count: > 0 } || c.ExcludeNameContains is { Count: > 0 } ||
            c.ExcludeNamePatterns is { Count: > 0 } || c.ExcludeFolders is { Count: > 0 }) parts.Add("исключения");
        return parts.Count == 0 ? "всегда" : string.Join("; ", parts);
    }

    public static string DescribeAction(SortingRule r, Func<Guid, string?>? vitrineName = null)
    {
        var a = r.Action;
        if (r.IsExclusion) return "не трогать (исключение)";

        var target = a.CreateVitrineName;
        if (string.IsNullOrWhiteSpace(target) && a.TargetVitrineId is Guid id && vitrineName != null)
            target = vitrineName(id);

        var rename = string.IsNullOrWhiteSpace(a.RenameTemplate) ? "" : $" + имя «{a.RenameTemplate}»";
        return a.Type switch
        {
            RuleActionType.CreateVitrineAndPlace => $"создать/найти витрину «{target}» и поместить{rename}",
            RuleActionType.PlaceInVitrine => $"поместить в витрину «{target}»{rename}",
            RuleActionType.MoveToFolder => $"переместить в папку «{a.TargetFolderPath}»{rename}",
            RuleActionType.SendToInbox => "отправить в «Неразобранное»",
            _ => "пропустить"
        };
    }

    public static string DescribeRule(SortingRule r, Func<Guid, string?>? vitrineName = null)
        => $"#{r.Order:D2} {r.Name}" + (r.Enabled ? "" : " [выкл]") + (r.IsExclusion ? " [ИСКЛЮЧЕНИЕ]" : "")
           + $" | если {DescribeCondition(r.Condition)} | {DescribeAction(r, vitrineName)}";

    public static string KindName(ItemKind kind) => kind switch
    {
        ItemKind.Folder => "Папка",
        ItemKind.Shortcut => "Ярлык",
        ItemKind.Application => "Программа",
        _ => "Файл"
    };
}
