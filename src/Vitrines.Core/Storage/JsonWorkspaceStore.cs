using System.Text;
using System.Text.Json;
using Vitrines.Core.Models;

namespace Vitrines.Core.Storage;

public sealed class JsonWorkspaceStore : IWorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _root;
    private readonly string _layoutPath;
    private readonly string _rulesPath;
    private readonly string _settingsPath;
    private readonly string _historyPath;
    private readonly string _inboxPath;

    public JsonWorkspaceStore(string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vitrines");
        _layoutPath = Path.Combine(_root, "layout.json");
        _rulesPath = Path.Combine(_root, "rules.json");
        _settingsPath = Path.Combine(_root, "settings.json");
        _historyPath = Path.Combine(_root, "history.jsonl");
        _inboxPath = Path.Combine(_root, "inbox.json");
    }

    public async Task<WorkspaceData> LoadAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        var data = new WorkspaceData();
        if (File.Exists(_layoutPath))
            data.Vitrines = await ReadAsync<List<Vitrine>>(_layoutPath, ct) ?? new();
        if (File.Exists(_rulesPath))
            data.Rules = await ReadAsync<List<SortingRule>>(_rulesPath, ct) ?? new();
        if (File.Exists(_settingsPath))
            data.Watch = await ReadAsync<WatchSettings>(_settingsPath, ct) ?? new();
        if (File.Exists(_inboxPath))
            data.Inbox = await ReadAsync<List<InboxEntry>>(_inboxPath, ct) ?? new();
        // нормализация на случай новых полей/старых данных
        data.Vitrines ??= new();
        data.Rules ??= new();
        data.Watch ??= new WatchSettings();
        data.Watch.Appearance ??= new AppearanceSettings();
        data.Watch.Sources ??= new();
        data.Watch.TempExtensions ??= new() { "crdownload", "part", "tmp", "download" };
        data.Watch.ExcludeFolders ??= new();
        data.Watch.ExcludeExtensions ??= new();
        data.Watch.ExcludeNameContains ??= new();
        data.Watch.LockedFileRetries = Math.Clamp(data.Watch.LockedFileRetries, 0, 10);
        data.Watch.ScheduleMinutes = Math.Clamp(data.Watch.ScheduleMinutes, 5, 1440);
        data.Watch.StableSeconds = Math.Clamp(data.Watch.StableSeconds, 1, 120);
        data.Inbox ??= new();
        foreach (var r in data.Rules)
        {
            r.Condition ??= new RuleCondition();
            r.Action ??= new RuleAction();
        }
        return data;
    }

    public async Task SaveLayoutAsync(WorkspaceData data, CancellationToken ct = default)
    { Directory.CreateDirectory(_root); await WriteAsync(_layoutPath, data.Vitrines, ct); }

    public async Task SaveRulesAsync(WorkspaceData data, CancellationToken ct = default)
    { Directory.CreateDirectory(_root); await WriteAsync(_rulesPath, data.Rules, ct); }

    public async Task SaveSettingsAsync(WorkspaceData data, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        await WriteAsync(_settingsPath, data.Watch, ct);
        await WriteAsync(_inboxPath, data.Inbox, ct);
    }

    public async Task AppendHistoryAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        var line = JsonSerializer.Serialize(entry);
        await File.AppendAllTextAsync(_historyPath, line + Environment.NewLine, Encoding.UTF8, ct);
    }

    public async Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(int take = 200, CancellationToken ct = default)
    {
        if (!File.Exists(_historyPath)) return Array.Empty<HistoryEntry>();
        // читаем все строки, чтобы корректно поддерживать мульти-undo и фильтры; возвращаем последние take
        var lines = await File.ReadAllLinesAsync(_historyPath, ct);
        var list = new List<HistoryEntry>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                // поддержка и старых многострочных/битых записей маловероятна, но пробуем оба варианта опций
                var e = JsonSerializer.Deserialize<HistoryEntry>(line, JsonOpts) ?? JsonSerializer.Deserialize<HistoryEntry>(line);
                if (e != null) list.Add(e);
            }
            catch { }
        }
        if (take <= 0 || take >= list.Count) return list.AsReadOnly();
        return list.TakeLast(take).ToList().AsReadOnly();
    }

    public async Task SaveHistoryAsync(IReadOnlyList<HistoryEntry> entries, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        var tmp = _historyPath + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var sw = new StreamWriter(fs, Encoding.UTF8))
        {
            foreach (var e in entries)
            {
                var line = JsonSerializer.Serialize(e);
                await sw.WriteLineAsync(line.AsMemory(), ct);
            }
        }
        File.Move(tmp, _historyPath, overwrite: true);
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(fs, JsonOpts, ct);
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, value, JsonOpts, ct);
        File.Move(tmp, path, overwrite: true);
    }
}
