using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;

namespace SMAPI.Tests.Framework.Health;

[TestFixture]
internal sealed class ModHealthTextSanitizerTests
{
    [Test]
    public void SanitizeIdentity_RemovesPathsControlsAnsiAndLineBreaks()
    {
        string actual = ModHealthTextSanitizer.SanitizeIdentity("Example\u001b[31m\r\n/home/private-user/Mods/Test\tC:\\Users\\private-user\\Mods\\Test\u0001", 80);

        actual.Should().Be("Example [path] [path]");
        actual.Should().NotContain("private-user").And.NotContain("\u001b").And.NotContain("\r").And.NotContain("\n").And.NotContain("\t");
    }

    [Test]
    public void SanitizeIdentity_EnforcesMaximumLength()
    {
        ModHealthTextSanitizer.SanitizeIdentity(new string('x', 20), 8).Should().Be("xxxxxxxx");
    }

    [Test]
    public void SanitizeIdentity_RemovesSingleComponentAndUncAbsolutePaths()
    {
        string actual = ModHealthTextSanitizer.SanitizeIdentity(@"/private-canary C:\private-canary C:/private-forward \\private-server\share");

        actual.Should().Be("[path] [path] [path] [path]");
        actual.Should().NotContain("private");
    }

    [Test]
    public void SanitizeIdentity_RemovesPathsImmediatelyAfterLabels()
    {
        string actual = ModHealthTextSanitizer.SanitizeIdentity(@"path:/home/private-canary win:C:\private-canary unc:\\private-server\share");

        actual.Should().Be("path:[path] win:[path] unc:[path]");
        actual.Should().NotContain("private");
    }

    [Test]
    public void SanitizeIdentity_RemovesBidirectionalFormattingControls()
    {
        string actual = ModHealthTextSanitizer.SanitizeIdentity("safe\u202eevil\u202c \u2066isolated\u2069");

        actual.Should().Be("safeevil isolated");
        actual.Should().NotContain("\u202e").And.NotContain("\u202c").And.NotContain("\u2066").And.NotContain("\u2069");
    }

    [Test]
    public void SanitizeIdentity_PreservesValidUnicode()
    {
        ModHealthTextSanitizer.SanitizeIdentity("Café 🌻 Mod").Should().Be("Café 🌻 Mod");
    }
}
