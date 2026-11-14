using System.Text;
using System.Text.Json;
using Vitrines.Core.Models;

namespace Vitrines.Core.Storage;

/// <summary>JSON-хранилище раскладки/правил/журнала в %LOCALAPPDATA%\Vitrines.</summary>
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

    public JsonWorkspaceStore(string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vitrines");
        _layoutPath = Path.Combine(_root, "layout.json");
        _rulesPath = Path.Combine(_root, "rules.json");
        _settingsPath = Path.Combine(_root, "settings.json");
        _historyPath = Path.Combine(_root, "history.jsonl");
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
        return data;
    }

    public async Task SaveLayoutAsync(WorkspaceData data, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        await WriteAsync(_layoutPath, data.Vitrines, ct);
    }

    public async Task SaveRulesAsync(WorkspaceData data, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        await WriteAsync(_rulesPath, data.Rules, ct);
    }

    public async Task SaveSettingsAsync(WorkspaceData data, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        await WriteAsync(_settingsPath, data.Watch, ct);
    }

    public async Task AppendHistoryAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        var line = JsonSerializer.Serialize(entry, JsonOpts);
        await File.AppendAllTextAsync(_historyPath, line + Environment.NewLine, Encoding.UTF8, ct);
    }

    public async Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(int take = 200, CancellationToken ct = default)
    {
        if (!File.Exists(_historyPath)) return Array.Empty<HistoryEntry>();
        var lines = await File.ReadAllLinesAsync(_historyPath, ct);
        var list = new List<HistoryEntry>(Math.Min(lines.Length, take));
        foreach (var line in Enumerable.Reverse(lines).Take(take))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { var e = JsonSerializer.Deserialize<HistoryEntry>(line, JsonOpts); if (e != null) list.Add(e); }
            catch { /* повреждённая строка журнала не должна ломать загрузку */ }
        }
        return list;
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
