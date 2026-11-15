using Vitrines.Core.Actions;

namespace Vitrines.Core.Tests;

public sealed class FileOperatorTests
{
    private readonly FileOperator _ops = new();

    [Fact]
    public async Task Move_RenamesOnCollision_ByDefault()
    {
        using var dir = new TempDir();
        var source = dir.WriteFile("src\\a.txt", "new");
        var target = Directory.CreateDirectory(dir.Combine("dst")).FullName;
        dir.WriteFile("dst\\a.txt", "old");

        var final = await _ops.MoveAsync(source, target);

        Assert.Equal("a (1).txt", Path.GetFileName(final));
        Assert.Equal("new", await File.ReadAllTextAsync(final));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(target, "a.txt")));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task Move_SkipPolicy_ThrowsConflict()
    {
        using var dir = new TempDir();
        var source = dir.WriteFile("src\\a.txt", "new");
        var target = Directory.CreateDirectory(dir.Combine("dst")).FullName;
        dir.WriteFile("dst\\a.txt", "old");

        var ex = await Assert.ThrowsAsync<FileConflictException>(() =>
            _ops.MoveAsync(source, target, new FileOpOptions { Conflict = NameConflictPolicy.Skip }));

        Assert.Contains("a.txt", ex.Message);
        Assert.True(File.Exists(source));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public async Task Move_ReplacePolicy_ReplacesDestination()
    {
        using var dir = new TempDir();
        var source = dir.WriteFile("src\\a.txt", "new");
        var target = Directory.CreateDirectory(dir.Combine("dst")).FullName;
        dir.WriteFile("dst\\a.txt", "old");

        var final = await _ops.MoveAsync(source, target, new FileOpOptions { Conflict = NameConflictPolicy.Replace });

        Assert.Equal(Path.Combine(target, "a.txt"), final);
        Assert.Equal("new", await File.ReadAllTextAsync(final));
    }

    [Fact]
    public async Task Rename_ResolvesCollisionAndKeepsExtension()
    {
        using var dir = new TempDir();
        var source = dir.WriteFile("a.txt", "one");
        dir.WriteFile("b.txt", "two");

        var renamed = await _ops.RenameAsync(source, "b.txt");

        Assert.Equal("b (1).txt", Path.GetFileName(renamed));
        Assert.Equal("one", await File.ReadAllTextAsync(renamed));
        Assert.Equal("two", await File.ReadAllTextAsync(dir.Combine("b.txt")));
    }

    [Fact]
    public async Task Rename_SameName_IsNoOp()
    {
        using var dir = new TempDir();
        var source = dir.WriteFile("a.txt", "one");

        var renamed = await _ops.RenameAsync(source, "a.txt");

        Assert.Equal(source, renamed);
        Assert.Equal("one", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task UndoMove_RestoresOriginalLocation()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("src\\a.txt", "data");
        var target = Directory.CreateDirectory(dir.Combine("dst")).FullName;
        var moved = await _ops.MoveAsync(original, target);

        await _ops.UndoMoveAsync(moved, original);

        Assert.True(File.Exists(original));
        Assert.False(File.Exists(moved));
        Assert.Equal("data", await File.ReadAllTextAsync(original));
    }

    [Fact]
    public async Task Move_MissingSource_Throws()
    {
        using var dir = new TempDir();
        var target = Directory.CreateDirectory(dir.Combine("dst")).FullName;

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _ops.MoveAsync(dir.Combine("missing.txt"), target));
    }

    [Fact]
    public async Task Move_FolderIntoItself_Throws()
    {
        using var dir = new TempDir();
        var source = Directory.CreateDirectory(dir.Combine("parent")).FullName;
        var child = Path.Combine(source, "child");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _ops.MoveAsync(source, child));
    }

    [Fact]
    public async Task Move_Directory_Works()
    {
        using var dir = new TempDir();
        var source = Directory.CreateDirectory(dir.Combine("folder")).FullName;
        File.WriteAllText(Path.Combine(source, "inside.txt"), "x");
        var target = Directory.CreateDirectory(dir.Combine("dst")).FullName;

        var final = await _ops.MoveAsync(source, target);

        Assert.True(Directory.Exists(final));
        Assert.True(File.Exists(Path.Combine(final, "inside.txt")));
        Assert.False(Directory.Exists(source));
    }
}
