namespace Vitrines.Core.Models;

/// <summary>Правило авто-сортировки: условия → действие.</summary>
public sealed class SortingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Правило";
    public bool Enabled { get; set; } = true;

    /// <summary>Меньше — выше приоритет (специфичные правила раньше общих).</summary>
    public int Order { get; set; }

    public RuleCondition Condition { get; set; } = new();
    public RuleAction Action { get; set; } = new();
}

/// <summary>Условия совпадения. Пустые/NULL поля не ограничивают.</summary>
public sealed class RuleCondition
{
    /// <summary>Расширения без точки, напр. ["jpg","png"].</summary>
    public List<string>? Extensions { get; set; }

    /// <summary>Подстроки/шаблоны в имени (простое contains на MVP).</summary>
    public List<string>? NameContains { get; set; }

    public List<ItemKind>? Kinds { get; set; }

    /// <summary>Источники-наблюдатели, к которым применимо правило; null = любые.</summary>
    public List<string>? SourcePaths { get; set; }
}

public sealed class RuleAction
{
    public RuleActionType Type { get; set; } = RuleActionType.PlaceInVitrine;

    /// <summary>Целевая витрина (для PlaceInVitrine).</summary>
    public Guid? TargetVitrineId { get; set; }

    /// <summary>Имя/категория для создания новой витрины, если нет подходящей.</summary>
    public string? CreateVitrineName { get; set; }

    /// <summary>Целевая реальная папка (для MoveToFolder/порталов).</summary>
    public string? TargetFolderPath { get; set; }
}

public enum RuleActionType
{
    PlaceInVitrine,
    CreateVitrineAndPlace,
    MoveToFolder,
    Ignore
}

/// <summary>Результат оценки правил — для объяснения и dry-run.</summary>
public sealed class RuleDecision
{
    public SortingRule? MatchedRule { get; set; }
    public RuleActionType Action { get; set; } = RuleActionType.Ignore;
    public string Reason { get; set; } = string.Empty;
    public Guid? TargetVitrineId { get; set; }
    public string? TargetVitrineName { get; set; }
}
