using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;

namespace StardewModdingAPI.Installer.Core.Tests.Ownership;

[TestFixture]
public class NormalizedRelativePathTests
{
    [TestCase("StardewModdingAPI")]
    [TestCase("smapi-internal/config.json")]
    [TestCase("Mods/ConsoleCommands/manifest.json")]
    [TestCase("folder/file name.json")]
    [TestCase("smapi-internal/café.json")]
    public void Parse_AcceptsCanonicalRelativePath(string value)
    {
        NormalizedRelativePath.Parse(value).Value.Should().Be(value);
    }

    [TestCase("")]
    [TestCase("/absolute")]
    [TestCase("../escape")]
    [TestCase("a/../escape")]
    [TestCase("a/./b")]
    [TestCase("a//b")]
    [TestCase("a/")]
    [TestCase("a\\b")]
    [TestCase("a:b")]
    [TestCase("a/b.")]
    [TestCase("a/b ")]
    [TestCase("a/\u0001b")]
    public void Parse_RejectsUnsafeOrNonCanonicalPath(string value)
    {
        Action action = () => NormalizedRelativePath.Parse(value);
        action.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Parse_RejectsNonCanonicalUnicode()
    {
        string decomposed = "smapi-internal/cafe\u0301.json";
        Action action = () => NormalizedRelativePath.Parse(decomposed);
        action.Should().Throw<ArgumentException>().WithMessage("*normalization form C*");
    }

    [Test]
    public void Parse_RejectsExcessiveSegment()
    {
        Action action = () => NormalizedRelativePath.Parse($"smapi-internal/{new string('a', 256)}");
        action.Should().Throw<ArgumentException>().WithMessage("*segment exceeds*");
    }

    [TestCase("smapi-internal/config.user.json", OwnedEntryKind.InternalFile)]
    [TestCase("Mods/PrivateMod/manifest.json", OwnedEntryKind.BundledModFile)]
    [TestCase("unrelated.txt", OwnedEntryKind.RuntimeFile)]
    [TestCase("StardewValley-original", OwnedEntryKind.Launcher)]
    [TestCase("StardewModdingAPI-net10.deps.json", OwnedEntryKind.GeneratedFile)]
    public void OwnedNamespacePolicy_RejectsUnownedOrMislabeledPath(string value, OwnedEntryKind kind)
    {
        Action action = () => OwnedNamespacePolicy.AssertAllowed(NormalizedRelativePath.Parse(value), kind);
        action.Should().Throw<ArgumentException>().WithMessage("*isn't in the compiled installer-owned namespace*");
    }

    [Test]
    public void Sha256Digest_RequiresCanonicalLowercaseHex()
    {
        Action uppercase = () => Sha256Digest.Parse(new string('A', 64));
        Action shortValue = () => Sha256Digest.Parse("abc");

        uppercase.Should().Throw<ArgumentException>();
        shortValue.Should().Throw<ArgumentException>();
        Sha256Digest.Parse(new string('a', 64)).Value.Should().Be(new string('a', 64));
    }
}
