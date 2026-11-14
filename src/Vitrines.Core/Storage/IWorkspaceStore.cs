using Vitrines.Core.Models;

namespace Vitrines.Core.Storage;

/// <summary>Хранение раскладки, правил и журнала (JSON в %LOCALAPPDATA%\Vitrines).</summary>
public interface IWorkspaceStore
{
    Task<WorkspaceData> LoadAsync(CancellationToken ct = default);
    Task SaveLayoutAsync(WorkspaceData data, CancellationToken ct = default);
    Task SaveRulesAsync(WorkspaceData data, CancellationToken ct = default);
    Task SaveSettingsAsync(WorkspaceData data, CancellationToken ct = default);
    Task AppendHistoryAsync(HistoryEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(int take = 200, CancellationToken ct = default);
}

public sealed class WorkspaceData
{
    public List<Vitrine> Vitrines { get; set; } = new();
    public List<SortingRule> Rules { get; set; } = new();
    public WatchSettings Watch { get; set; } = new();
}

public sealed class WatchSettings
{
    /// <summary>Папки-источники для наблюдения (Рабочий стол, Загрузки и т.д.).</summary>
    public List<string> Sources { get; set; } = new();

    /// <summary>Сколько секунд файл должен быть стабильным (размер/время не меняются), прежде чем сортировать.</summary>
    public int StableSeconds { get; set; } = 3;

    /// <summary>Игнорируемые расширения временных/незавершённых файлов.</summary>
    public List<string> TempExtensions { get; set; } = new() { "crdownload", "part", "tmp", "download" };

    /// <summary>Безопасный дефолт: автоматика выключена, пока пользователь явно не включит.</summary>
    public bool AutomationEnabled { get; set; } = false;
}
