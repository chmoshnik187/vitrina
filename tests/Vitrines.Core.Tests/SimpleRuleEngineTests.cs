using Vitrines.Core.Rules;
using Vitrines.Core.Storage;
using Vitrines.Core.Models;

namespace Vitrines.Core.Tests;

public sealed class SimpleRuleEngineTests
{
    private readonly SimpleRuleEngine _engine = new();

    private static FileFacts Facts(
        string name = "report.pdf",
        ItemKind kind = ItemKind.File,
        long size = 1024,
        DateTimeOffset? modified = null,
        DateTimeOffset? created = null,
        string? sourceRoot = null)
        => new()
        {
            Path = sourceRoot == null ? Path.Combine(Path.GetTempPath(), name) : Path.Combine(sourceRoot, name),
            Name = name,
            Extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant(),
            Kind = kind,
            Size = size,
            Modified = modified,
            Created = created,
            SourceRoot = sourceRoot
        };

    private static SortingRule Rule(
        int order,
        string name,
        RuleCondition condition,
        RuleActionType action = RuleActionType.CreateVitrineAndPlace,
        string? vitrine = null,
        bool enabled = true,
        bool exclusion = false)
        => new()
        {
            Order = order,
            Name = name,
            Enabled = enabled,
            IsExclusion = exclusion,
            Condition = condition,
            Action = new RuleAction { Type = action, CreateVitrineName = vitrine ?? name }
        };

    [Fact]
    public void FirstMatchingRuleWins_ByOrder()
    {
        var rules = new[]
        {
            Rule(20, "Второе", new RuleCondition { Extensions = new() { "pdf" } }, vitrine: "Документы"),
            Rule(10, "Первое", new RuleCondition { Extensions = new() { "pdf" } }, vitrine: "PDF")
        };

        var decision = _engine.Evaluate("report.pdf", ItemKind.File, rules, Facts());

        Assert.Equal("Первое", decision.MatchedRule?.Name);
        Assert.Equal(RuleActionType.CreateVitrineAndPlace, decision.Action);
        Assert.Equal("PDF", decision.TargetVitrineName);
        Assert.Contains("Первое", decision.Reason);
    }

    [Fact]
    public void DisabledRule_IsSkipped()
    {
        var rules = new[]
        {
            Rule(10, "Выключено", new RuleCondition(), enabled: false),
            Rule(20, "Включено", new RuleCondition { Extensions = new() { "pdf" } })
        };

        var decision = _engine.Evaluate("report.pdf", ItemKind.File, rules, Facts());

        Assert.Equal("Включено", decision.MatchedRule?.Name);
    }

    [Fact]
    public void ExclusionRule_StopsProcessing()
    {
        var rules = new[]
        {
            Rule(10, "Не трогать временные", new RuleCondition { NameContains = new() { "temp" } }, exclusion: true),
            Rule(20, "Документы", new RuleCondition { Extensions = new() { "pdf" } })
        };

        var decision = _engine.Evaluate("temp-report.pdf", ItemKind.File, rules, Facts("temp-report.pdf"));

        Assert.Equal(RuleActionType.Ignore, decision.Action);
        Assert.True(decision.IsExclusion);
        Assert.Equal("Не трогать временные", decision.MatchedRule?.Name);
    }

    [Fact]
    public void IgnoreAction_IsTreatedAsExclusion()
    {
        var rules = new[]
        {
            Rule(10, "Пропуск", new RuleCondition { Extensions = new() { "tmp" } }, action: RuleActionType.Ignore),
            Rule(20, "Документы", new RuleCondition { Extensions = new() { "tmp" } })
        };

        var decision = _engine.Evaluate("x.tmp", ItemKind.File, rules, Facts("x.tmp"));

        Assert.Equal(RuleActionType.Ignore, decision.Action);
        Assert.True(decision.IsExclusion);
    }

    [Fact]
    public void ConditionExclusions_PreventMatch()
    {
        var rules = new[]
        {
            Rule(10, "Документы", new RuleCondition
            {
                Extensions = new() { "pdf" },
                ExcludeNameContains = new() { "temp", "~$" }
            })
        };

        var decision = _engine.Evaluate("temp-report.pdf", ItemKind.File, rules, Facts("temp-report.pdf"));

        Assert.Null(decision.MatchedRule);
        Assert.Equal(RuleActionType.Ignore, decision.Action);
        Assert.Contains("Подходящих правил нет", decision.Reason);
    }

    [Fact]
    public void GlobalExclusions_TakePrecedenceOverRules()
    {
        var rules = new[] { Rule(10, "Документы", new RuleCondition { Extensions = new() { "pdf" } }) };
        var watch = new WatchSettings { ExcludeExtensions = new() { ".PDF" } };

        var decision = _engine.Evaluate("report.pdf", ItemKind.File, rules, Facts(), watch);

        Assert.Equal(RuleActionType.Ignore, decision.Action);
        Assert.True(decision.IsExclusion);
        Assert.Contains("глобальных исключениях", decision.Reason);
    }

    [Fact]
    public void GlobalExcludedFolder_IsRespected()
    {
        using var dir = new TempDir();
        var sub = Directory.CreateDirectory(Path.Combine(dir.Path, "private")).FullName;
        var facts = Facts("secret.pdf", sourceRoot: sub);
        var watch = new WatchSettings { ExcludeFolders = new() { sub } };

        var decision = _engine.Evaluate(facts.Path, ItemKind.File, Array.Empty<SortingRule>(), facts, watch);

        Assert.Equal(RuleActionType.Ignore, decision.Action);
        Assert.Contains("исключённой папке", decision.Reason);
    }

    [Fact]
    public void UnmatchedPolicy_SendToInbox()
    {
        var watch = new WatchSettings { Unmatched = UnmatchedPolicy.SendToInbox, UnmatchedVitrineName = "Разобрать" };

        var decision = _engine.Evaluate("unknown.xyz", ItemKind.File, Array.Empty<SortingRule>(), Facts("unknown.xyz"), watch);

        Assert.Equal(RuleActionType.SendToInbox, decision.Action);
        Assert.Equal("Разобрать", decision.TargetVitrineName);
    }

    [Fact]
    public void UnmatchedPolicy_PlaceInVitrine()
    {
        var watch = new WatchSettings { Unmatched = UnmatchedPolicy.PlaceInVitrine, UnmatchedVitrineName = "Разное" };

        var decision = _engine.Evaluate("unknown.xyz", ItemKind.File, Array.Empty<SortingRule>(), Facts("unknown.xyz"), watch);

        Assert.Equal(RuleActionType.CreateVitrineAndPlace, decision.Action);
        Assert.Equal("Разное", decision.TargetVitrineName);
    }

    [Fact]
    public void Matches_KindsSizeDatesAndSource()
    {
        using var dir = new TempDir();
        var source = Directory.CreateDirectory(Path.Combine(dir.Path, "inbox")).FullName;
        var modified = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var facts = Facts("archive.zip", ItemKind.File, size: 4096, modified: modified, sourceRoot: source);
        var rules = new[]
        {
            Rule(10, "Крупные свежие", new RuleCondition
            {
                Kinds = new() { ItemKind.File },
                Extensions = new() { "zip" },
                MinSizeBytes = 2048,
                MaxSizeBytes = 8192,
                ModifiedAfter = modified.AddDays(-1),
                ModifiedBefore = modified.AddDays(1),
                SourcePaths = new() { source }
            })
        };

        var decision = _engine.Evaluate(facts.Path, ItemKind.File, rules, facts);

        Assert.Equal("Крупные свежие", decision.MatchedRule?.Name);
        Assert.Contains("размер", decision.Reason);
        Assert.Contains("дата", decision.Reason);
    }

    [Fact]
    public void Matches_WildcardAndRegex()
    {
        var rules = new[]
        {
            Rule(10, "Отчёты", new RuleCondition
            {
                NamePatterns = new() { "report-??.pdf" },
                NameRegex = @"^report-\d+\.pdf$"
            })
        };

        var hit = _engine.Evaluate("report-42.pdf", ItemKind.File, rules, Facts("report-42.pdf"));
        var miss = _engine.Evaluate("report-4.pdf", ItemKind.File, rules, Facts("report-4.pdf"));

        Assert.Equal("Отчёты", hit.MatchedRule?.Name);
        Assert.Null(miss.MatchedRule);
    }

    [Fact]
    public void DryRun_ProducesDecisionPerPath()
    {
        var rules = new[] { Rule(10, "PDF", new RuleCondition { Extensions = new() { "pdf" } }) };

        var results = _engine.DryRun(new[] { "a.pdf", "b.txt" }, rules);

        Assert.Equal(2, results.Count);
        Assert.Equal(RuleActionType.CreateVitrineAndPlace, results[0].Decision.Action);
        Assert.Equal(RuleActionType.Ignore, results[1].Decision.Action);
    }
}
