using System.Text.RegularExpressions;
using Vitrines.Core.Models;
using Vitrines.Core.Storage;

namespace Vitrines.Core.Rules;

public sealed class SimpleRuleEngine : IRuleEngine
{
    public RuleDecision Evaluate(string path, ItemKind kind, IEnumerable<SortingRule> rules, FileFacts? facts = null, WatchSettings? watch = null)
    {
        facts ??= FileFacts.FromPath(path);

        // Глобальные исключения (никогда не трогаем)
        if (watch != null && IsGloballyExcluded(facts, watch, out var gReason))
            return new RuleDecision { Action = RuleActionType.Ignore, Reason = gReason, IsExclusion = true };

        foreach (var rule in rules.Where(r => r.Enabled).OrderBy(r => r.Order))
        {
            if (Matches(rule.Condition, facts, out var why))
            {
                if (rule.IsExclusion || rule.Action.Type == RuleActionType.Ignore)
                {
                    return new RuleDecision
                    {
                        MatchedRule = rule,
                        Action = RuleActionType.Ignore,
                        Reason = $"Исключено правилом «{rule.Name}»: {why}",
                        IsExclusion = true
                    };
                }

                return new RuleDecision
                {
                    MatchedRule = rule,
                    Action = rule.Action.Type,
                    Reason = $"Совпало правило «{rule.Name}»: {why}",
                    TargetVitrineId = rule.Action.TargetVitrineId,
                    TargetVitrineName = rule.Action.CreateVitrineName
                };
            }
        }

        // нет совпадений — применяем политику по умолчанию
        if (watch?.Unmatched == UnmatchedPolicy.SendToInbox)
            return new RuleDecision { Action = RuleActionType.SendToInbox, Reason = "Не подошло ни одно правило", TargetVitrineName = watch.UnmatchedVitrineName };
        if (watch?.Unmatched == UnmatchedPolicy.PlaceInVitrine)
            return new RuleDecision { Action = RuleActionType.CreateVitrineAndPlace, Reason = "Не подошло ни одно правило — размещено по политике по умолчанию", TargetVitrineName = string.IsNullOrWhiteSpace(watch.UnmatchedVitrineName) ? "Неразобранное" : watch.UnmatchedVitrineName };

        return new RuleDecision { Action = RuleActionType.Ignore, Reason = "Подходящих правил нет" };
    }

    public IReadOnlyList<(string Path, RuleDecision Decision)> DryRun(IEnumerable<string> paths, IEnumerable<SortingRule> rules, WatchSettings? watch = null)
    {
        var ordered = rules.ToList();
        var list = new List<(string, RuleDecision)>();
        foreach (var p in paths)
        {
            try
            {
                var facts = FileFacts.FromPath(p);
                list.Add((p, Evaluate(p, facts.Kind, ordered, facts, watch)));
            }
            catch (Exception ex)
            {
                list.Add((p, new RuleDecision { Action = RuleActionType.Ignore, Reason = $"Ошибка проверки: {ex.Message}" }));
            }
        }
        return list;
    }

    private static bool IsGloballyExcluded(FileFacts f, WatchSettings w, out string reason)
    {
        if (w.ExcludeExtensions is { Count: > 0 } && w.ExcludeExtensions.Any(e => string.Equals(NormExt(e), f.Extension, StringComparison.OrdinalIgnoreCase)))
        { reason = $"расширение .{f.Extension} в глобальных исключениях"; return true; }
        if (w.ExcludeNameContains is { Count: > 0 })
        {
            var hit = w.ExcludeNameContains.FirstOrDefault(s => f.Name.Contains(s, StringComparison.OrdinalIgnoreCase));
            if (hit != null) { reason = $"имя содержит «{hit}» (глобальное исключение)"; return true; }
        }
        if (w.ExcludeFolders is { Count: > 0 } && w.ExcludeFolders.Any(p => IsUnder(f.Path, p)))
        { reason = "расположено в исключённой папке"; return true; }
        reason = string.Empty; return false;
    }

    private static bool Matches(RuleCondition c, FileFacts f, out string why)
    {
        var reasons = new List<string>();
        if (IsExcluded(c, f)) { why = "совпало исключение условия"; return false; }

        if (c.Kinds is { Count: > 0 })
        { if (!c.Kinds.Contains(f.Kind)) { why = string.Empty; return false; } reasons.Add($"тип {f.Kind}"); }

        if (c.Extensions is { Count: > 0 })
        { if (!c.Extensions.Select(NormExt).Contains(f.Extension, StringComparer.OrdinalIgnoreCase)) { why = string.Empty; return false; } reasons.Add($".{f.Extension}"); }

        if (c.NameContains is { Count: > 0 })
        { var hit = c.NameContains.FirstOrDefault(s => f.Name.Contains(s, StringComparison.OrdinalIgnoreCase)); if (hit == null) { why = string.Empty; return false; } reasons.Add($"имя содержит «{hit}»"); }

        if (c.NamePatterns is { Count: > 0 })
        { var hit = c.NamePatterns.FirstOrDefault(p => WildcardMatch(f.Name, p)); if (hit == null) { why = string.Empty; return false; } reasons.Add($"маска «{hit}»"); }

        if (!string.IsNullOrWhiteSpace(c.NameRegex))
        {
            try { if (!Regex.IsMatch(f.Name, c.NameRegex, RegexOptions.IgnoreCase)) { why = string.Empty; return false; } reasons.Add("regex"); }
            catch { why = string.Empty; return false; }
        }

        if (c.SourcePaths is { Count: > 0 })
        { if (!c.SourcePaths.Any(src => IsUnder(f.Path, src))) { why = string.Empty; return false; } reasons.Add("источник"); }

        if (c.MinSizeBytes is long min && f.Size < min) { why = string.Empty; return false; }
        if (c.MaxSizeBytes is long max && f.Size > max) { why = string.Empty; return false; }
        if (c.MinSizeBytes != null || c.MaxSizeBytes != null) reasons.Add("размер");

        if (c.ModifiedAfter is DateTimeOffset ma && (f.Modified == null || f.Modified < ma)) { why = string.Empty; return false; }
        if (c.ModifiedBefore is DateTimeOffset mb && (f.Modified == null || f.Modified > mb)) { why = string.Empty; return false; }
        if (c.CreatedAfter is DateTimeOffset ca && (f.Created == null || f.Created < ca)) { why = string.Empty; return false; }
        if (c.CreatedBefore is DateTimeOffset cb && (f.Created == null || f.Created > cb)) { why = string.Empty; return false; }
        if (c.ModifiedAfter != null || c.ModifiedBefore != null || c.CreatedAfter != null || c.CreatedBefore != null) reasons.Add("дата");

        why = reasons.Count == 0 ? "совпадение" : string.Join(", ", reasons);
        return true;
    }

    private static bool IsExcluded(RuleCondition c, FileFacts f)
    {
        if (c.ExcludeExtensions is { Count: > 0 } && c.ExcludeExtensions.Select(NormExt).Contains(f.Extension, StringComparer.OrdinalIgnoreCase)) return true;
        if (c.ExcludeNameContains is { Count: > 0 } && c.ExcludeNameContains.Any(s => f.Name.Contains(s, StringComparison.OrdinalIgnoreCase))) return true;
        if (c.ExcludeNamePatterns is { Count: > 0 } && c.ExcludeNamePatterns.Any(p => WildcardMatch(f.Name, p))) return true;
        if (c.ExcludeFolders is { Count: > 0 } && c.ExcludeFolders.Any(p => IsUnder(f.Path, p))) return true;
        return false;
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var fp = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fr = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fp.StartsWith(fr, StringComparison.OrdinalIgnoreCase) || string.Equals(fp.TrimEnd(Path.DirectorySeparatorChar), fr.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch { return path.StartsWith(root, StringComparison.OrdinalIgnoreCase); }
    }

    private static string NormExt(string e) => e.Trim().TrimStart('.').ToLowerInvariant();

    private static bool WildcardMatch(string name, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        // поддержка *.ext и простых * ? масок
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }
}
