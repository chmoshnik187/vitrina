using Vitrines.Core.Models;
using Vitrines.Core.Storage;

namespace Vitrines.Core.Tests;

public sealed class VitrineStorageTests
{
    [Fact]
    public void IsInDirectory_TrueForChild_FalseForSibling()
    {
        using var dir = new TempDir();
        var root = Directory.CreateDirectory(dir.Combine("vitrine")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;
        var sibling = Directory.CreateDirectory(dir.Combine("other")).FullName;

        Assert.True(VitrineStorage.IsInDirectory(Path.Combine(root, "file.txt"), root));
        Assert.True(VitrineStorage.IsInDirectory(Path.Combine(child, "file.txt"), root));
        Assert.True(VitrineStorage.IsInDirectory(root, root));
        Assert.False(VitrineStorage.IsInDirectory(Path.Combine(sibling, "file.txt"), root));
    }

    [Fact]
    public void GetManagedFolder_CreatesSafeFolder()
    {
        var vitrine = new Vitrine { Name = "Фото: отпуск/2026*", Kind = VitrineKind.Collection };
        var folder = VitrineStorage.GetManagedFolder(vitrine);
        try
        {
            Assert.True(Directory.Exists(folder));
            Assert.True(VitrineStorage.IsInDirectory(Path.Combine(folder, "a.jpg"), folder));
            var name = Path.GetFileName(folder);
            Assert.DoesNotContain(':', name);
            Assert.DoesNotContain('/', name);
            Assert.DoesNotContain('\\', name);
            Assert.DoesNotContain('*', name);
            Assert.Contains(vitrine.Id.ToString("N"), name);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }
}
