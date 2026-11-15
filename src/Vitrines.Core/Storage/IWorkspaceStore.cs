using Vitrines.Core.Actions;
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

    // Хранение коллекций и конфликты имён
    /// <summary>Перемещать элементы коллекций в управляемое хранилище или оставлять на месте (ссылка).</summary>
    public CollectionPlacement CollectionPlacement { get; set; } = CollectionPlacement.Move;
    public NameConflictPolicy ConflictPolicy { get; set; } = NameConflictPolicy.Rename;
    /// <summary>Повторы при временной блокировке файла (антивирус/индексатор).</summary>
    public int LockedFileRetries { get; set; } = 4;

    // Расписание (необязательно; по умолчанию выключено)
    public bool ScheduleEnabled { get; set; } = false;
    /// <summary>Интервал фонового разбора существующих элементов, минуты.</summary>
    public int ScheduleMinutes { get; set; } = 30;

    /// <summary>Расширенный локальный аудит: писать в журнал и пропуски/решения без действий.</summary>
    public bool LocalAudit { get; set; } = false;

    // Внешний вид/поведение (глобально)
    public AppearanceSettings Appearance { get; set; } = new();
}

public enum CollectionPlacement
{
    /// <summary>Физически перемещать элемент в папку хранилища витрины (по умолчанию).</summary>
    Move,
    /// <summary>Оставлять файл на месте и хранить ссылку в витрине.</summary>
    Reference
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
    /// <summary>Использовать системный Acrylic/Blur за окном витрины, если доступен.</summary>
    public bool UseAcrylic { get; set; } = true;
    /// <summary>Удерживать окна витрин на слое рабочего стола (внизу z-order).</summary>
    public bool PinToDesktopLayer { get; set; } = true;
    /// <summary>Не активировать окна витрин при клике (чтобы не перекрывать другие окна).</summary>
    public bool NoActivateWindows { get; set; } = true;
}
