using Vitrines.Core.Models;

namespace Vitrines.Core.Rules;

/// <summary>Стартовые пресеты правил (специфичные/узкие — с меньшим Order).</summary>
public static class DefaultRules
{
    public static List<SortingRule> Create(string? imagesVitrine = null, string? docsVitrine = null, string? archivesVitrine = null, string? installersVitrine = null)
    {
        // На MVP создаём витрины по имени (CreateVitrineAndPlace). Имена можно менять в настройках.
        return new List<SortingRule>
        {
            Rule(order: 10, name: "Изображения", new() { Extensions = new(){"jpg","jpeg","png","gif","bmp","webp","svg","heic"} }, Create(imagesVitrine ?? "Изображения")),
            Rule(order: 20, name: "Документы", new() { Extensions = new(){"pdf","doc","docx","xls","xlsx","ppt","pptx","txt","rtf","csv","md"} }, Create(docsVitrine ?? "Документы")),
            Rule(order: 30, name: "Архивы", new() { Extensions = new(){"zip","rar","7z","tar","gz"} }, Create(archivesVitrine ?? "Архивы")),
            Rule(order: 40, name: "Установщики", new() { Extensions = new(){"exe","msi"} }, Create(installersVitrine ?? "Установщики")),
            Rule(order: 50, name: "Ярлыки", new() { Kinds = new(){ ItemKind.Shortcut } }, Create("Ярлыки")),
        };
    }

    private static SortingRule Rule(int order, string name, RuleCondition cond, RuleAction action) => new()
    {
        Name = name, Order = order, Enabled = true, Condition = cond, Action = action
    };

    private static RuleAction Create(string vitrineName) => new()
    {
        Type = RuleActionType.CreateVitrineAndPlace,
        CreateVitrineName = vitrineName
    };
}
