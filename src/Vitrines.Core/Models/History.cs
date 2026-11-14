namespace Vitrines.Core.Models;

/// <summary>Запись журнала для прозрачности и undo.</summary>
public sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;

    public HistoryEvent Event { get; set; }
    public string ItemPath { get; set; } = string.Empty;
    public string? From { get; set; }
    public string? To { get; set; }

    public Guid? RuleId { get; set; }
    public string? RuleName { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>Данные для отката (исходный путь после перемещения). Null если откат невозможен/уже выполнен.</summary>
    public string? UndoData { get; set; }
    /// <summary>Отмечается после успешного отката этой записи.</summary>
    public bool Undone { get; set; }
    /// <summary>Источник-наблюдатель, из которого пришло событие (если применимо).</summary>
    public string? Source { get; set; }
}

public enum HistoryEvent
{
    Placed,
    Moved,
    CreatedVitrine,
    Skipped,
    Error,
    Undone,
    /// <summary>Отправлено в «Неразобранное» с причиной.</summary>
    SentToInbox
}

/// <summary>Элемент «Неразобранного» (инбокс): не подошёл под правила или не удалось обработать.</summary>
public sealed class InboxEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string Path { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? Source { get; set; }
    public InboxStatus Status { get; set; } = InboxStatus.Pending;
    public string? LastError { get; set; }
}

public enum InboxStatus
{
    Pending,
    Resolved,
    Dismissed,
    Failed
}
