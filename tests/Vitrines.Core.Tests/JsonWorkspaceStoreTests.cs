using Vitrines.Core.Actions;
using Vitrines.Core.Models;
using Vitrines.Core.Storage;

namespace Vitrines.Core.Tests;

public sealed class JsonWorkspaceStoreTests
{
    [Fact]
    public async Task LoadAsync_MissingFiles_ReturnsDefaults()
    {
        using var dir = new TempDir();
        var store = new JsonWorkspaceStore(dir.Path);

        var data = await store.LoadAsync();

        Assert.Empty(data.Vitrines);
        Assert.Empty(data.Rules);
        Assert.Empty(data.Inbox);
        Assert.NotNull(data.Watch);
        Assert.NotNull(data.Watch.Appearance);
        Assert.Equal(3, data.Watch.StableSeconds);
        Assert.Equal(4, data.Watch.LockedFileRetries);
        Assert.Equal(30, data.Watch.ScheduleMinutes);
        Assert.False(data.Watch.AutomationEnabled);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsWorkspace()
    {
        using var dir = new TempDir();
        var store = new JsonWorkspaceStore(dir.Path);
        var data = new WorkspaceData
        {
            Watch = new WatchSettings
            {
                Sources = new() { dir.Path },
                AutomationEnabled = true,
                Recursive = true,
                CollectionPlacement = CollectionPlacement.Reference,
                ConflictPolicy = NameConflictPolicy.Skip,
                LockedFileRetries = 7,
                ScheduleEnabled = true,
                ScheduleMinutes = 45,
                LocalAudit = true,
                Appearance = new AppearanceSettings
                {
                    Theme = "glass",
                    Accent = "#FF8800",
                    TileSize = 120,
                    Density = "compact",
                    FontScale = 1.2,
                    Animations = false,
                    UseAcrylic = false,
                    PinToDesktopLayer = false,
                    NoActivateWindows = false
                }
            },
            Inbox = new() { new InboxEntry { Path = dir.Combine("x.txt"), DisplayName = "x.txt", Reason = "test" } }
        };
        data.Vitrines.Add(new Vitrine { Name = "Изображения", Kind = VitrineKind.Collection });
        data.Rules.Add(new SortingRule
        {
            Name = "Фото",
            Order = 10,
            Condition = new RuleCondition { Extensions = new() { "jpg", "png" } },
            Action = new RuleAction
            {
                Type = RuleActionType.MoveToFolder,
                TargetFolderPath = dir.Combine("photos"),
                RenameTemplate = "{name}-{yyyy}"
            }
        });

        await store.SaveLayoutAsync(data);
        await store.SaveRulesAsync(data);
        await store.SaveSettingsAsync(data);
        var loaded = await store.LoadAsync();

        Assert.Single(loaded.Vitrines);
        Assert.Single(loaded.Rules);
        Assert.Single(loaded.Inbox);
        Assert.Equal(CollectionPlacement.Reference, loaded.Watch.CollectionPlacement);
        Assert.Equal(NameConflictPolicy.Skip, loaded.Watch.ConflictPolicy);
        Assert.Equal(7, loaded.Watch.LockedFileRetries);
        Assert.True(loaded.Watch.ScheduleEnabled);
        Assert.Equal(45, loaded.Watch.ScheduleMinutes);
        Assert.True(loaded.Watch.LocalAudit);
        Assert.Equal("glass", loaded.Watch.Appearance.Theme);
        Assert.Equal("#FF8800", loaded.Watch.Appearance.Accent);
        Assert.Equal(120, loaded.Watch.Appearance.TileSize);
        Assert.False(loaded.Watch.Appearance.UseAcrylic);
        Assert.Equal("{name}-{yyyy}", loaded.Rules[0].Action.RenameTemplate);
        Assert.Equal(RuleActionType.MoveToFolder, loaded.Rules[0].Action.Type);
    }

    [Fact]
    public async Task LoadAsync_ClampsOutOfRangeSettings()
    {
        using var dir = new TempDir();
        dir.WriteFile("settings.json",
            "{\"StableSeconds\":999,\"ScheduleMinutes\":1,\"LockedFileRetries\":99,\"Appearance\":null}");
        var store = new JsonWorkspaceStore(dir.Path);

        var data = await store.LoadAsync();

        Assert.Equal(120, data.Watch.StableSeconds);
        Assert.Equal(5, data.Watch.ScheduleMinutes);
        Assert.Equal(10, data.Watch.LockedFileRetries);
        Assert.NotNull(data.Watch.Appearance);
    }

    [Fact]
    public async Task History_AppendReadAndReplace()
    {
        using var dir = new TempDir();
        var store = new JsonWorkspaceStore(dir.Path);

        await store.AppendHistoryAsync(new HistoryEntry { ItemPath = "a", Reason = "one" });
        await store.AppendHistoryAsync(new HistoryEntry { ItemPath = "b", Reason = "two" });
        await store.AppendHistoryAsync(new HistoryEntry { ItemPath = "c", Reason = "three" });

        var all = await store.ReadHistoryAsync(0);
        Assert.Equal(3, all.Count);
        Assert.Equal("one", all[0].Reason);

        var lastTwo = await store.ReadHistoryAsync(2);
        Assert.Equal(2, lastTwo.Count);
        Assert.Equal("two", lastTwo[0].Reason);
        Assert.Equal("three", lastTwo[1].Reason);

        await store.SaveHistoryAsync(new[] { new HistoryEntry { ItemPath = "only", Reason = "kept" } });
        var replaced = await store.ReadHistoryAsync(0);
        Assert.Single(replaced);
        Assert.Equal("kept", replaced[0].Reason);
    }

    [Fact]
    public async Task LoadAsync_LegacyRawFiles_AreSupported()
    {
        using var dir = new TempDir();
        dir.WriteFile("layout.json", "[{\"Name\":\"A\",\"Kind\":0,\"Items\":[]}]");
        dir.WriteFile("rules.json", "[{\"Name\":\"R\",\"Enabled\":true,\"Order\":1,\"Condition\":{},\"Action\":{\"Type\":0}}]");
        dir.WriteFile("settings.json", "{\"StableSeconds\":10,\"Recursive\":true,\"Appearance\":{\"Theme\":\"light\"}}");
        dir.WriteFile("inbox.json", "[{\"Path\":\"C:\\\\x.txt\",\"DisplayName\":\"x.txt\",\"Reason\":\"r\"}]");
        var store = new JsonWorkspaceStore(dir.Path);

        var data = await store.LoadAsync();

        Assert.Single(data.Vitrines);
        Assert.Equal("A", data.Vitrines[0].Name);
        Assert.Single(data.Rules);
        Assert.Equal("R", data.Rules[0].Name);
        Assert.Equal(10, data.Watch.StableSeconds);
        Assert.True(data.Watch.Recursive);
        Assert.Equal("light", data.Watch.Appearance.Theme);
        Assert.Single(data.Inbox);
    }

    [Fact]
    public async Task Save_WritesVersionedEnvelopes()
    {
        using var dir = new TempDir();
        var store = new JsonWorkspaceStore(dir.Path);
        var data = new WorkspaceData();
        data.Vitrines.Add(new Vitrine { Name = "V" });
        data.Rules.Add(new SortingRule { Name = "R" });
        data.Inbox.Add(new InboxEntry { Path = "p", DisplayName = "p" });
        data.Watch.StableSeconds = 5;

        await store.SaveLayoutAsync(data);
        await store.SaveRulesAsync(data);
        await store.SaveSettingsAsync(data);

        var layout = await File.ReadAllTextAsync(dir.Combine("layout.json"));
        var rules = await File.ReadAllTextAsync(dir.Combine("rules.json"));
        var settings = await File.ReadAllTextAsync(dir.Combine("settings.json"));
        var inbox = await File.ReadAllTextAsync(dir.Combine("inbox.json"));

        Assert.Contains("\"schemaVersion\": 1", layout);
        Assert.Contains("\"vitrines\"", layout);
        Assert.Contains("\"schemaVersion\": 1", rules);
        Assert.Contains("\"rules\"", rules);
        Assert.Contains("\"schemaVersion\": 1", settings);
        Assert.Contains("\"watch\"", settings);
        Assert.Contains("\"schemaVersion\": 1", inbox);
        Assert.Contains("\"inbox\"", inbox);
    }

    [Fact]
    public async Task LoadAsync_CorruptFiles_FallBackSafely()
    {
        using var dir = new TempDir();
        dir.WriteFile("layout.json", "{not-json");
        dir.WriteFile("rules.json", "[broken");
        dir.WriteFile("settings.json", "null-bad");
        dir.WriteFile("inbox.json", "oops");
        var store = new JsonWorkspaceStore(dir.Path);

        var data = await store.LoadAsync();

        Assert.NotNull(data);
        Assert.Empty(data.Vitrines);
        Assert.Empty(data.Rules);
        Assert.Empty(data.Inbox);
        Assert.NotNull(data.Watch);
        Assert.NotNull(data.Watch.Appearance);
    }
}
