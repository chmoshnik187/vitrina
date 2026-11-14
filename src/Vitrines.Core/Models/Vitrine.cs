namespace Vitrines.Core.Models;

/// <summary>Визуальный контейнер на рабочем столе.</summary>
public sealed class Vitrine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Новая витрина";

    /// <summary>Коллекция элементов или «портал» на реальную папку.</summary>
    public VitrineKind Kind { get; set; } = VitrineKind.Collection;

    /// <summary>Путь папки для режима Portals (реальная папка проводника).</summary>
    public string? FolderPath { get; set; }

    // Позиция/размер в координатах рабочего стола (логические единицы).
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 240;

    public bool IsCollapsed { get; set; }
    public VitrineStyle Style { get; set; } = new();

    public List<VitrineItem> Items { get; set; } = new();
}

public enum VitrineKind
{
    /// <summary>Набор размещённых элементов (ссылки/ярлыки/файлы), управляемый витриной.</summary>
    Collection,
    /// <summary>Отражает содержимое реальной папки проводника.</summary>
    Portal
}

/// <summary>Параметры внешнего вида «стеклянной» витрины.</summary>
public sealed class VitrineStyle
{
    public double Opacity { get; set; } = 0.72;
    public string? Accent { get; set; }
    public int IconSize { get; set; } = 48;
}
