using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;

namespace StardewModdingAPI.Installer.Core.Tests.Ownership;

[TestFixture]
internal sealed class InstallationPackageIdentityTests
{
    private const string Tag = "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2";
    private const string EmbeddedVersion = "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2";
    private const string PackageName = "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip";
    private const string SourceCommit = "1111111111111111111111111111111111111111";
    private const string SourceTree = "2222222222222222222222222222222222222222";
    private const string Workflow = "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2";
    private static readonly Sha256Digest PackageSha256 = Sha256Digest.Parse(new string('a', 64));

    [Test]
    public void TaggedIdentityPreservesExactReleaseApiAndOrigin()
    {
        InstallationReleaseIdentity release = CreateRelease();

        release.Origin.Should().Be(InstallationPackageOrigin.TaggedRelease);
        release.Repository.Should().Be(InstallationReleaseIdentity.ReviewedRepository);
        release.Tag.Should().Be(Tag);
        release.EmbeddedVersion.Should().Be(EmbeddedVersion);
        release.PackageAssetName.Should().Be(PackageName);
        release.SourceCommit.Should().Be(SourceCommit);
        release.SourceTree.Should().Be(SourceTree);
        release.PackageSha256.Should().Be(PackageSha256);
        release.PackageSizeBytes.Should().Be(123456);
        release.BuildWorkflow.Should().Be(Workflow);
        release.BuildConfiguration.Should().Be("Release");
        release.RuntimeIdentifier.Should().Be("linux-x64");
    }

    [Test]
    public void LocalIdentityPreservesTaggedLookingByteLabelsWithoutTaggedClaims()
    {
        InstallationLocalPackageIdentity local = CreateLocal();

        local.Origin.Should().Be(InstallationPackageOrigin.LocalManual);
        local.EmbeddedVersion.Should().Be(EmbeddedVersion);
        local.PackageAssetName.Should().Be(PackageName);
        local.PackageSha256.Should().Be(PackageSha256);
        local.PackageSizeBytes.Should().Be(123456);
        typeof(InstallationLocalPackageIdentity).GetProperties().Select(property => property.Name).Should().NotContain(
            ["Repository", "Tag", "SourceCommit", "SourceTree", "BuildWorkflow", "BuildConfiguration", "RuntimeIdentifier"]
        );
    }

    [Test]
    public void SameCommonBytesAcrossOriginsRemainUnequalAndDistinctInHashCollections()
    {
        InstallationReleaseIdentity release = CreateRelease();
        InstallationLocalPackageIdentity local = CreateLocal();
        InstallationPackageIdentity releaseBase = release;
        InstallationPackageIdentity localBase = local;

        releaseBase.Should().NotBe(localBase);
        localBase.Should().NotBe(releaseBase);
        new HashSet<InstallationPackageIdentity> { release, local }.Should().HaveCount(2);
        release.Should().Be(CreateRelease());
        local.Should().Be(CreateLocal());
    }

    [TestCase(InstallationPackageOrigin.TaggedRelease)]
    [TestCase(InstallationPackageOrigin.LocalManual)]
    public void OriginEnumHasOnlyClosedExpectedValues(InstallationPackageOrigin origin)
    {
        Enum.GetValues<InstallationPackageOrigin>().Should().Equal(
            InstallationPackageOrigin.TaggedRelease,
            InstallationPackageOrigin.LocalManual
        );
        Enum.IsDefined(typeof(InstallationPackageOrigin), origin).Should().BeTrue();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("4.5")]
    [TestCase("4.05.3")]
    [TestCase("4.5.3-")]
    [TestCase("4.5.3")]
    [TestCase("4.5.3-beta.1")]
    [TestCase("4.5.3+local")]
    [TestCase("4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.0")]
    [TestCase("4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.01")]
    [TestCase("4.5.3 unofficial")]
    [TestCase("4.5.3/../../escape")]
    public void LocalIdentityRejectsNonCanonicalEmbeddedVersion(string? embeddedVersion)
    {
        string candidate = embeddedVersion ?? "";
        Action construct = () => new InstallationLocalPackageIdentity(
            embeddedVersion!,
            $"SMAPI-{candidate}-linux-x64-installer.zip",
            PackageSha256,
            1
        );

        construct.Should().Throw<ArgumentException>().WithParameterName("embeddedVersion");
    }

    [Test]
    public void LocalIdentityRejectsOversizedEmbeddedVersionBeforeFilenameAuthority()
    {
        string embeddedVersion = $"4.5.3-{new string('a', 161)}";
        Action construct = () => new InstallationLocalPackageIdentity(
            embeddedVersion,
            $"SMAPI-{embeddedVersion}-linux-x64-installer.zip",
            PackageSha256,
            1
        );

        construct.Should().Throw<ArgumentException>().WithParameterName("embeddedVersion");
    }

    [TestCase("SMAPI-4.5.3-linux-x64-installer.zip")]
    [TestCase("smapi-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip")]
    [TestCase("../SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip")]
    [TestCase("SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip\n")]
    public void LocalIdentityRejectsAnythingExceptExactDerivedSafeFilename(string packageName)
    {
        Action construct = () => new InstallationLocalPackageIdentity(
            EmbeddedVersion,
            packageName,
            PackageSha256,
            1
        );

        construct.Should().Throw<ArgumentException>().WithParameterName("packageAssetName");
    }

    [TestCase(1L)]
    [TestCase(InstallationPackageIdentity.MaximumPackageSizeBytes)]
    public void LocalIdentityAcceptsExactPackageSizeBounds(long size)
    {
        InstallationLocalPackageIdentity local = new(EmbeddedVersion, PackageName, PackageSha256, size);

        local.PackageSizeBytes.Should().Be(size);
    }

    [TestCase(0L)]
    [TestCase(-1L)]
    [TestCase(InstallationPackageIdentity.MaximumPackageSizeBytes + 1)]
    public void LocalIdentityRejectsPackageSizeOutsideExactBounds(long size)
    {
        Action construct = () => new InstallationLocalPackageIdentity(EmbeddedVersion, PackageName, PackageSha256, size);

        construct.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("packageSizeBytes");
    }

    [Test]
    public void LocalIdentityRequiresDigest()
    {
        Action construct = () => new InstallationLocalPackageIdentity(EmbeddedVersion, PackageName, null!, 1);

        construct.Should().Throw<ArgumentNullException>().WithParameterName("packageSha256");
    }

    [Test]
    public void TaggedIdentityStillRejectsMismatchedExactFields()
    {
        Action repository = () => CreateRelease(repository: "https://github.com/Pathoschild/SMAPI");
        Action nullTag = () => CreateRelease(tag: null!);
        Action tag = () => CreateRelease(tag: "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3");
        const string otherVersion = "4.5.4-unofficial.4eh5xitv6787h645ebv.linux.alpha.2";
        Action version = () => CreateRelease(
            embeddedVersion: otherVersion,
            packageName: $"SMAPI-{otherVersion}-linux-x64-installer.zip"
        );
        Action package = () => CreateRelease(packageName: "SMAPI-other-linux-x64-installer.zip");
        Action commit = () => CreateRelease(sourceCommit: new string('A', 40));
        Action tree = () => CreateRelease(sourceTree: new string('B', 40));
        Action workflow = () => CreateRelease(workflow: Workflow.Replace("refs/tags/", "refs/heads/", StringComparison.Ordinal));
        Action configuration = () => CreateRelease(configuration: "Debug");
        Action runtime = () => CreateRelease(runtimeIdentifier: "linux-arm64");

        repository.Should().Throw<ArgumentException>().WithParameterName("repository");
        nullTag.Should().Throw<ArgumentNullException>().WithParameterName("tag");
        tag.Should().Throw<ArgumentException>().WithParameterName("embeddedVersion");
        version.Should().Throw<ArgumentException>().WithParameterName("embeddedVersion");
        package.Should().Throw<ArgumentException>().WithParameterName("packageAssetName");
        commit.Should().Throw<ArgumentException>().WithParameterName("sourceCommit");
        tree.Should().Throw<ArgumentException>().WithParameterName("sourceTree");
        workflow.Should().Throw<ArgumentException>().WithParameterName("buildWorkflow");
        configuration.Should().Throw<ArgumentException>().WithParameterName("buildConfiguration");
        runtime.Should().Throw<ArgumentException>().WithParameterName("runtimeIdentifier");
    }

    private static InstallationLocalPackageIdentity CreateLocal()
    {
        return new InstallationLocalPackageIdentity(EmbeddedVersion, PackageName, PackageSha256, 123456);
    }

    private static InstallationReleaseIdentity CreateRelease(
        string repository = InstallationReleaseIdentity.ReviewedRepository,
        string tag = Tag,
        string embeddedVersion = EmbeddedVersion,
        string packageName = PackageName,
        string sourceCommit = SourceCommit,
        string sourceTree = SourceTree,
        string workflow = Workflow,
        string configuration = "Release",
        string runtimeIdentifier = "linux-x64"
    )
    {
        return new InstallationReleaseIdentity(
            repository,
            tag,
            embeddedVersion,
            packageName,
            sourceCommit,
            sourceTree,
            PackageSha256,
            123456,
            workflow,
            configuration,
            runtimeIdentifier
        );
    }
}
