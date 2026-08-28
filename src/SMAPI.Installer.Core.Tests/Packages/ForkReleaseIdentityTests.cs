using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
public sealed class ForkReleaseIdentityTests
{
    [Test]
    public void Parse_ValidTag_DerivesExactIdentity()
    {
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(
            "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.12"
        );

        identity.Version.Should().Be("4.5.3");
        identity.AlphaSequence.Should().Be(12);
        identity.EmbeddedVersion.Should().Be("4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.12");
        identity.PackageAssetName.Should().Be(
            "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.12-linux-x64-installer.zip"
        );
    }

    [TestCase("4.5.3")]
    [TestCase("fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.0")]
    [TestCase("fork-4eh5xitv6787h645ebv-linux-v04.5.3-alpha.1")]
    [TestCase("fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.01")]
    [TestCase("fork-other-linux-v4.5.3-alpha.1")]
    [TestCase("fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1/extra")]
    public void Parse_InvalidTag_Rejects(string tag)
    {
        Action action = () => ForkReleaseIdentity.Parse(tag);

        action.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void AssertMatches_Mismatch_RejectsVersionAndFilename()
    {
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(
            "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1"
        );

        identity.Invoking(p => p.AssertMatches("4.5.3", p.PackageAssetName))
            .Should().Throw<PackageSecurityException>();
        identity.Invoking(p => p.AssertMatches(p.EmbeddedVersion, "other.zip"))
            .Should().Throw<PackageSecurityException>();
    }
}
