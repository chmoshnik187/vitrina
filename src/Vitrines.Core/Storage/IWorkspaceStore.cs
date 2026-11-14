using Vitrines.Core.Models;

namespace Vitrines.Core.Storage;

public interface IWorkspaceStore
{
    Task<WorkspaceData> LoadAsync(CancellationToken ct = default);
    Task SaveLayoutAsync(WorkspaceData data, CancellationToken ct = default);
    Task SaveRulesAsync(WorkspaceData data, CancellationToken ct = default);
    Task SaveSettingsAsync(WorkspaceData data, CancellationToken ct = default);
    Task AppendHistoryAsync(HistoryEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(int take = 200, CancellationToken ct = default);
    Task SaveHistoryAsync(IReadOnlyList<HistoryEntry> entries, CancellationToken ct = default);
}

public sealed class WorkspaceData
{
    public List<Vitrine> Vitrines { get; set; } = new();
    public List<SortingRule> Rules { get; set; } = new();
    public WatchSettings Watch { get; set; } = new();
    public List<InboxEntry> Inbox { get; set; } = new();
}

public sealed class WatchSettings
{
    public List<string> Sources { get; set; } = new();
    public int StableSeconds { get; set; } = 3;
    public List<string> TempExtensions { get; set; } = new() { "crdownload", "part", "tmp", "download" };
    /// <summary>Автоматика выключена по умолчанию (безопасно).</summary>
    public bool AutomationEnabled { get; set; } = false;
    /// <summary>Следить за подпапками источников (рекурсивно).</summary>
    public bool Recursive { get; set; } = false;

    // Исключения (глобальные «не трогать»)
    public List<string> ExcludeFolders { get; set; } = new();
    public List<string> ExcludeExtensions { get; set; } = new();
    public List<string> ExcludeNameContains { get; set; } = new();

    // Политика для неподошедших
    public UnmatchedPolicy Unmatched { get; set; } = UnmatchedPolicy.Ignore;
    public string UnmatchedVitrineName { get; set; } = "Неразобранное";

    // Внешний вид/поведение (глобально)
    public AppearanceSettings Appearance { get; set; } = new();
}

public enum UnmatchedPolicy
{
    Ignore,
    SendToInbox,
    PlaceInVitrine
}

public sealed class AppearanceSettings
{
    /// <summary>Тема: dark, light, glass</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>Акцентный цвет (#RRGGBB)</summary>
    public string Accent { get; set; } = "#7C9CFF";
    public int TileSize { get; set; } = 92;
    public string Density { get; set; } = "normal"; // compact, normal, comfortable
    public double FontScale { get; set; } = 1.0;
    public bool Animations { get; set; } = true;
    public bool ShowBadges { get; set; } = true;
}
