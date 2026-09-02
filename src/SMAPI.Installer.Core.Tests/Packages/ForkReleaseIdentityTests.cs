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

    [TestCase(
        "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.12-linux-x64-installer.zip",
        "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.12",
        "4.5.3",
        12
    )]
    [TestCase(
        "SMAPI-0.0.0-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip",
        "fork-4eh5xitv6787h645ebv-linux-v0.0.0-alpha.1",
        "0.0.0",
        1
    )]
    [TestCase(
        "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2147483647-linux-x64-installer.zip",
        "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2147483647",
        "4.5.3",
        int.MaxValue
    )]
    public void ParsePackageAssetName_CanonicalName_DerivesExactIdentity(
        string assetName,
        string expectedTag,
        string expectedVersion,
        int expectedAlpha
    )
    {
        ForkReleaseIdentity identity = ForkReleaseIdentity.ParsePackageAssetName(assetName);

        identity.Tag.Should().Be(expectedTag);
        identity.Version.Should().Be(expectedVersion);
        identity.AlphaSequence.Should().Be(expectedAlpha);
        identity.PackageAssetName.Should().Be(assetName);
    }

    [Test]
    public void ParsePackageAssetName_LargeCanonicalVersionComponent_RoundTripsWithoutFixedWidthConversion()
    {
        string component = new('9', 80);
        string version = $"{component}.0.0";
        string assetName = $"SMAPI-{version}-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip";

        ForkReleaseIdentity identity = ForkReleaseIdentity.ParsePackageAssetName(assetName);

        identity.Version.Should().Be(version);
        identity.Tag.Should().Be($"fork-4eh5xitv6787h645ebv-linux-v{version}-alpha.1");
        identity.PackageAssetName.Should().Be(assetName);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("SMAPI-4.5.3-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer")]
    [TestCase("SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-arm64-installer.zip")]
    [TestCase("smapi-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3-UNOFFICIAL.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3-unofficial.4EH5XITV6787H645EBV.linux.alpha.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3-unofficial.other.linux.alpha.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.windows.alpha.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.beta.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3.0-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-04.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.05.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.03-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.-5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.x-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.0-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.01-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2147483648-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.+1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.١-linux-x64-installer.zip")]
    [TestCase("/tmp/SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip\n")]
    public void ParsePackageAssetName_NonCanonicalOrMismatchedName_Rejects(string? assetName)
    {
        Action action = () => ForkReleaseIdentity.ParsePackageAssetName(assetName!);

        action.Should().Throw<PackageSecurityException>()
            .WithMessage("*filename*canonical SMAPI Linux fork alpha package*");
    }

    [Test]
    public void ParsePackageAssetName_ExcessiveFilenameOrDerivedTag_RejectsBeforeIdentityPublication()
    {
        string excessiveFilename = new('9', 256);
        string excessiveVersion = new('9', 150);
        string excessiveTagName = $"SMAPI-{excessiveVersion}.0.0-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip";

        FluentActions.Invoking(() => ForkReleaseIdentity.ParsePackageAssetName(excessiveFilename))
            .Should().Throw<PackageSecurityException>();
        FluentActions.Invoking(() => ForkReleaseIdentity.ParsePackageAssetName(excessiveTagName))
            .Should().Throw<PackageSecurityException>();
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

    [TestCase("4.10.0", 1, "4.9.0", 1, 1)]
    [TestCase("4.9.0", 1, "4.10.0", 1, -1)]
    [TestCase("4.5.4", 10, "4.5.4", 2, 1)]
    [TestCase("5.0.0", 1, "4.999999999999999999999999.999999999999999999", 999, 1)]
    [TestCase("4.5.4", 2, "4.5.4", 2, 0)]
    [TestCase("9999999999999999999999999999999999999999.0.0", 1, "2147483648.0.0", 1, 1)]
    public void Compare_UsesCanonicalUnboundedNumericAndAlphaOrdering(
        string leftVersion,
        int leftAlpha,
        string rightVersion,
        int rightAlpha,
        int expectedSign
    )
    {
        ForkReleaseIdentity left = Identity(leftVersion, leftAlpha);
        ForkReleaseIdentity right = Identity(rightVersion, rightAlpha);

        Math.Sign(ForkReleaseIdentity.Compare(left, right)).Should().Be(expectedSign);
        Math.Sign(ForkReleaseIdentity.Compare(right, left)).Should().Be(-expectedSign);
    }

    [Test]
    public void Compare_IsTransitiveAcrossBaseAndAlphaBoundaries()
    {
        ForkReleaseIdentity first = Identity("4.9.999999999999999999999999", 10);
        ForkReleaseIdentity second = Identity("4.10.0", 1);
        ForkReleaseIdentity third = Identity("9999999999999999999999999999999999999999.0.0", 1);

        ForkReleaseIdentity.Compare(first, second).Should().BeNegative();
        ForkReleaseIdentity.Compare(second, third).Should().BeNegative();
        ForkReleaseIdentity.Compare(first, third).Should().BeNegative();
    }

    private static ForkReleaseIdentity Identity(string version, int alpha)
        => ForkReleaseIdentity.Parse($"fork-4eh5xitv6787h645ebv-linux-v{version}-alpha.{alpha}");
}
