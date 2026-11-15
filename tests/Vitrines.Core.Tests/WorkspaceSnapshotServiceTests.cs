using System.IO.Compression;
using Vitrines.Core.Storage;

namespace Vitrines.Core.Tests;

public sealed class WorkspaceSnapshotServiceTests
{
    private static void Seed(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "layout.json"), "[]");
        File.WriteAllText(Path.Combine(root, "rules.json"), "[]");
        File.WriteAllText(Path.Combine(root, "settings.json"), "{\"StableSeconds\":5}");
        File.WriteAllText(Path.Combine(root, "inbox.json"), "[]");
        File.WriteAllText(Path.Combine(root, "history.jsonl"), "");
    }

    [Fact]
    public async Task ExportImport_RestoresSnapshotFiles()
    {
        using var dir = new TempDir();
        Seed(dir.Path);
        var service = new WorkspaceSnapshotService(dir.Path);
        var zip = dir.Combine("export.zip");

        await service.ExportAsync(zip);
        Assert.True(File.Exists(zip));

        File.WriteAllText(Path.Combine(dir.Path, "layout.json"), "[{\"changed\":true}]");
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), "{\"StableSeconds\":99}");

        await service.ImportAsync(zip);

        Assert.Equal("[]", File.ReadAllText(Path.Combine(dir.Path, "layout.json")));
        Assert.Equal("{\"StableSeconds\":5}", File.ReadAllText(Path.Combine(dir.Path, "settings.json")));
    }

    [Fact]
    public async Task Import_IgnoresEntriesOutsideSnapshotList()
    {
        using var dir = new TempDir();
        Seed(dir.Path);
        var zip = dir.Combine("malicious.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var layout = archive.CreateEntry("layout.json");
            using (var writer = new StreamWriter(layout.Open())) writer.Write("[{\"ok\":true}]");
            var evil = archive.CreateEntry("../evil.txt");
            using (var writer = new StreamWriter(evil.Open())) writer.Write("bad");
        }

        var service = new WorkspaceSnapshotService(dir.Path);
        await service.ImportAsync(zip);

        Assert.Equal("[{\"ok\":true}]", File.ReadAllText(Path.Combine(dir.Path, "layout.json")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "..", "evil.txt")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "evil.txt")));
    }

    [Fact]
    public async Task Profiles_CreateListRenameRestoreDelete()
    {
        using var dir = new TempDir();
        Seed(dir.Path);
        var service = new WorkspaceSnapshotService(dir.Path);

        var profile = await service.CreateAsync("рабочий");
        Assert.True(File.Exists(profile.Path));
        Assert.Contains(service.List(), s => s.Name == "рабочий");

        var renamed = service.Rename(profile, "домашний");
        Assert.Equal("домашний", renamed.Name);
        Assert.True(File.Exists(renamed.Path));
        Assert.DoesNotContain(service.List(), s => s.Name == "рабочий");

        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), "{\"StableSeconds\":77}");
        await service.RestoreAsync(renamed);
        Assert.Equal("{\"StableSeconds\":5}", File.ReadAllText(Path.Combine(dir.Path, "settings.json")));

        service.Delete(renamed);
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task RestoreByName_ThrowsWhenMissing()
    {
        using var dir = new TempDir();
        var service = new WorkspaceSnapshotService(dir.Path);

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.RestoreAsync("missing"));
    }

    [Fact]
    public async Task Rename_RejectsDuplicateName()
    {
        using var dir = new TempDir();
        Seed(dir.Path);
        var service = new WorkspaceSnapshotService(dir.Path);
        var first = await service.CreateAsync("one");
        _ = await service.CreateAsync("two");

        Assert.Throws<IOException>(() => service.Rename(first, "two"));
    }
}
