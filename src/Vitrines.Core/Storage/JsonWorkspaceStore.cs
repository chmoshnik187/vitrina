using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vitrines.Core.Models;

namespace Vitrines.Core.Storage;

public sealed class JsonWorkspaceStore : IWorkspaceStore
{
    public const int CurrentSchemaVersion = 1;

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
            data.Vitrines = await ReadVersionedListAsync<Vitrine>(_layoutPath, "vitrines", ct) ?? new();
        if (File.Exists(_rulesPath))
            data.Rules = await ReadVersionedListAsync<SortingRule>(_rulesPath, "rules", ct) ?? new();
        if (File.Exists(_settingsPath))
            data.Watch = await ReadVersionedObjectAsync<WatchSettings>(_settingsPath, "watch", ct) ?? new();
        if (File.Exists(_inboxPath))
            data.Inbox = await ReadVersionedListAsync<InboxEntry>(_inboxPath, "inbox", ct) ?? new();

        Normalize(data);
        return data;
    }

    private static void Normalize(WorkspaceData data)
    {
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
    }

    public async Task SaveLayoutAsync(WorkspaceData data, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        await WriteVersionedListAsync(_layoutPath, "vitrines", data.Vitrines, ct);
    }

    public async Task SaveRulesAsync(WorkspaceData data, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        await WriteVersionedListAsync(_rulesPath, "rules", data.Rules, ct);
    }

    public async Task SaveSettingsAsync(WorkspaceData data, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        await WriteVersionedObjectAsync(_settingsPath, "watch", data.Watch, ct);
        await WriteVersionedListAsync(_inboxPath, "inbox", data.Inbox, ct);
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

    private async Task<List<T>?> ReadVersionedListAsync<T>(string path, string key, CancellationToken ct)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var node = await JsonNode.ParseAsync(fs, cancellationToken: ct);
            if (node is JsonArray arr)
                return arr.Deserialize<List<T>>(JsonOpts); // legacy v0
            if (node is JsonObject obj)
            {
                // envelope v1+: { schemaVersion, <key>: [...] }
                if (obj[key] is JsonArray dataArr)
                    return dataArr.Deserialize<List<T>>(JsonOpts);
                // fallback: try whole object only if not matching (unlikely)
            }
            return null;
        }
        catch
        {
            // повреждённый/частичный файл — не валим загрузку всего рабочего пространства
            return null;
        }
    }

    private async Task<T?> ReadVersionedObjectAsync<T>(string path, string key, CancellationToken ct) where T : class
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var node = await JsonNode.ParseAsync(fs, cancellationToken: ct);
            if (node is JsonObject obj)
            {
                // v1 envelope: { schemaVersion, <key>: { ... } }
                if (obj.ContainsKey("schemaVersion") && obj[key] is JsonObject inner)
                    return inner.Deserialize<T>(JsonOpts);
                // legacy v0: raw object
                if (!obj.ContainsKey("schemaVersion"))
                    return obj.Deserialize<T>(JsonOpts);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteVersionedListAsync<T>(string path, string key, List<T> value, CancellationToken ct)
    {
        var envelope = new JsonObject
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            [key] = JsonSerializer.SerializeToNode(value, JsonOpts)
        };
        await WriteNodeAsync(path, envelope, ct);
    }

    private static async Task WriteVersionedObjectAsync<T>(string path, string key, T value, CancellationToken ct)
    {
        var envelope = new JsonObject
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            [key] = JsonSerializer.SerializeToNode(value, JsonOpts)
        };
        await WriteNodeAsync(path, envelope, ct);
    }

    private static async Task WriteNodeAsync(string path, JsonNode node, CancellationToken ct)
    {
        var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(fs, node, JsonOpts, ct);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
