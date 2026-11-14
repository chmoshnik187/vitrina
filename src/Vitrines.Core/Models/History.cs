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
    public string Reason { get; set; } = string.Empty;

    /// <summary>Данные для отката (напр. исходный путь после перемещения).</summary>
    public string? UndoData { get; set; }
}

public enum HistoryEvent
{
    Placed,
    Moved,
    CreatedVitrine,
    Skipped,
    Error,
    Undone
}
