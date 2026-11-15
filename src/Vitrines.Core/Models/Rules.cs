namespace Vitrines.Core.Models;

/// <summary>Правило авто-сортировки: условия → действие. Применяются по Order (меньше = раньше).</summary>
public sealed class SortingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Правило";
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
    /// <summary>Если true — правило трактуется как исключение/«не трогать» при совпадении (останавливает дальнейший разбор).</summary>
    public bool IsExclusion { get; set; }
    public RuleCondition Condition { get; set; } = new();
    public RuleAction Action { get; set; } = new();
}

/// <summary>Условия совпадения. Пустые/NULL поля не ограничивают. Все заданные группы должны совпасть (И), значения внутри списков — ИЛИ.</summary>
public sealed class RuleCondition
{
    public List<string>? Extensions { get; set; }
    public List<string>? NameContains { get; set; }
    /// <summary>Маски/шаблоны имени (wildcard * ?), напр. "отчёт*", "*.jpg".</summary>
    public List<string>? NamePatterns { get; set; }
    /// <summary>Регулярное выражение (IgnoreCase) для имени файла.</summary>
    public string? NameRegex { get; set; }
    public List<ItemKind>? Kinds { get; set; }
    /// <summary>Ограничение по источникам-наблюдателям; null = любые.</summary>
    public List<string>? SourcePaths { get; set; }

    // Размер (байты). Для папок размер может быть неизвестен/0.
    public long? MinSizeBytes { get; set; }
    public long? MaxSizeBytes { get; set; }
    // Даты (UTC, сравниваются с LastWriteTimeUtc/CreationTimeUtc)
    public DateTimeOffset? ModifiedAfter { get; set; }
    public DateTimeOffset? ModifiedBefore { get; set; }
    public DateTimeOffset? CreatedAfter { get; set; }
    public DateTimeOffset? CreatedBefore { get; set; }

    // Исключения внутри условия (если совпало исключение — условие НЕ выполняется)
    public List<string>? ExcludeExtensions { get; set; }
    public List<string>? ExcludeNameContains { get; set; }
    public List<string>? ExcludeNamePatterns { get; set; }
    public List<string>? ExcludeFolders { get; set; } // пути/префиксы, которые не трогать
}

public sealed class RuleAction
{
    public RuleActionType Type { get; set; } = RuleActionType.PlaceInVitrine;
    public Guid? TargetVitrineId { get; set; }
    /// <summary>Имя витрины: для CreateVitrineAndPlace — создать/найти, для PlaceInVitrine — найти по имени (если не задан Id).</summary>
    public string? CreateVitrineName { get; set; }
    public string? TargetFolderPath { get; set; }
    /// <summary>Необязательный шаблон нового имени файла: {name}, {ext}, {yyyy}, {MM}, {dd}, {HH}, {mm}, {ss}, {counter}.</summary>
    public string? RenameTemplate { get; set; }
}

public enum RuleActionType
{
    PlaceInVitrine,
    CreateVitrineAndPlace,
    MoveToFolder,
    Ignore,
    /// <summary>Отправить в «Неразобранное»/инбокс (не терять, показать причину).</summary>
    SendToInbox
}

/// <summary>Результат оценки правил — для объяснения и dry-run.</summary>
public sealed class RuleDecision
{
    public SortingRule? MatchedRule { get; set; }
    public RuleActionType Action { get; set; } = RuleActionType.Ignore;
    public string Reason { get; set; } = string.Empty;
    public Guid? TargetVitrineId { get; set; }
    public string? TargetVitrineName { get; set; }
    public bool IsExclusion { get; set; }
}
