using Vitrines.Core.Models;

namespace Vitrines.Core.Rules;

/// <summary>Минимальный движок правил для MVP: первое совпадение по приоритету.</summary>
public sealed class SimpleRuleEngine : IRuleEngine
{
    public RuleDecision Evaluate(string path, ItemKind kind, IEnumerable<SortingRule> rules)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        foreach (var rule in rules.Where(r => r.Enabled).OrderBy(r => r.Order))
        {
            if (Matches(rule.Condition, path, name, ext, kind))
            {
                return new RuleDecision
                {
                    MatchedRule = rule,
                    Action = rule.Action.Type,
                    Reason = $"Совпало правило «{rule.Name}»",
                    TargetVitrineId = rule.Action.TargetVitrineId,
                    TargetVitrineName = rule.Action.CreateVitrineName
                };
            }
        }

        return new RuleDecision { Action = RuleActionType.Ignore, Reason = "Подходящих правил нет" };
    }

    public IReadOnlyList<(string Path, RuleDecision Decision)> DryRun(IEnumerable<string> paths, IEnumerable<SortingRule> rules)
    {
        var ordered = rules.ToList();
        var list = new List<(string, RuleDecision)>();
        foreach (var p in paths)
        {
            var kind = DetectKind(p);
            list.Add((p, Evaluate(p, kind, ordered)));
        }
        return list;
    }

    private static bool Matches(RuleCondition c, string path, string name, string ext, ItemKind kind)
    {
        if (c.Kinds is { Count: > 0 } && !c.Kinds.Contains(kind)) return false;

        if (c.Extensions is { Count: > 0 })
        {
            var set = c.Extensions.Select(e => e.TrimStart('.').ToLowerInvariant()).ToHashSet();
            if (!set.Contains(ext)) return false;
        }

        if (c.NameContains is { Count: > 0 })
        {
            var lower = name.ToLowerInvariant();
            if (!c.NameContains.Any(s => lower.Contains(s.ToLowerInvariant()))) return false;
        }

        // SourcePaths: применяется, если путь находится внутри одного из источников (или список пуст).
        if (c.SourcePaths is { Count: > 0 })
        {
            if (!c.SourcePaths.Any(src => path.StartsWith(src, StringComparison.OrdinalIgnoreCase))) return false;
        }

        return true;
    }

    private static ItemKind DetectKind(string path)
    {
        if (Directory.Exists(path)) return ItemKind.Folder;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".lnk") return ItemKind.Shortcut;
        if (ext is ".exe" or ".msi" or ".bat" or ".cmd") return ItemKind.Application;
        return ItemKind.File;
    }
}
