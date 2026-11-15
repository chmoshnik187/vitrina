using Vitrines.Core.Models;
using Vitrines.Core.Rules;

namespace Vitrines.Core.Tests;

public sealed class NameTemplateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 8, 7, TimeSpan.Zero);

    [Fact]
    public void Render_ReplacesTokensAndKeepsOriginalExtension()
    {
        var result = NameTemplate.Render("{name} ({yyyy}-{MM}-{dd})", @"C:\in\photo.JPG", ItemKind.File, Now);

        Assert.Equal("photo (2026-11-15).JPG", result);
    }

    [Fact]
    public void Render_TimeAndCounterTokens()
    {
        var result = NameTemplate.Render("{name}_{HH}-{mm}-{ss}_{counter}", "clip.mp4", ItemKind.File, Now, counter: 3);

        Assert.Equal("clip_09-08-07_03.mp4", result);
    }

    [Fact]
    public void Render_DoesNotAppendExtensionWhenTemplateProvidesOne()
    {
        var result = NameTemplate.Render("{name}.bak", "photo.png", ItemKind.File, Now);

        Assert.Equal("photo.bak", result);
    }

    [Fact]
    public void Render_SanitizesInvalidCharacters()
    {
        var result = NameTemplate.Render("a/b:c*?\"<>|", "x.txt", ItemKind.File, Now);

        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('*', result);
        Assert.DoesNotContain('?', result);
        Assert.DoesNotContain('"', result);
        Assert.EndsWith(".txt", result);
        Assert.True(result.Length > 4);
    }

    [Fact]
    public void Render_UsesStemWhenTemplateIsBlank()
    {
        var result = NameTemplate.Render("   ", "notes.md", ItemKind.File, Now);

        Assert.Equal("notes.md", result);
    }

    [Fact]
    public void Render_AvoidsReservedDeviceName()
    {
        var result = NameTemplate.Render("CON", "x.txt", ItemKind.File, Now);

        Assert.Equal("CON_.txt", result);
    }

    [Fact]
    public void Render_KindToken_IsHumanReadable()
    {
        var folder = NameTemplate.Render("{kind}-{name}", @"C:\in\Photos", ItemKind.Folder, Now);
        var shortcut = NameTemplate.Render("{kind}-{name}", @"C:\in\App.lnk", ItemKind.Shortcut, Now);

        Assert.Equal("папка-Photos", folder);
        Assert.Equal("ярлык-App.lnk", shortcut);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasTemplate_FalseForEmpty(string? template)
    {
        Assert.False(NameTemplate.HasTemplate(template));
    }

    [Fact]
    public void HasTemplate_TrueForNonEmpty()
    {
        Assert.True(NameTemplate.HasTemplate("{name}"));
    }
}
