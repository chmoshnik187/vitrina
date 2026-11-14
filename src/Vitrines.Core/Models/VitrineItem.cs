namespace Vitrines.Core.Models;

/// <summary>Элемент внутри витрины: ярлык/приложение, файл или папка.</summary>
public sealed class VitrineItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Реальный путь в файловой системе (или целевой путь ярлыка).</summary>
    public string Path { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public ItemKind Kind { get; set; } = ItemKind.File;

    /// <summary>Закреплён вручную: автоматика не перемещает элемент.</summary>
    public bool IsPinned { get; set; }

    /// <summary>Идентификатор правила, разместившего элемент (для объяснения/аудита).</summary>
    public Guid? PlacedByRuleId { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
}

public enum ItemKind
{
    File,
    Folder,
    Shortcut,
    Application
}
