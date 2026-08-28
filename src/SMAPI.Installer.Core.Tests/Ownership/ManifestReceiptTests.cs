using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;

namespace StardewModdingAPI.Installer.Core.Tests.Ownership;

[TestFixture]
public class ManifestReceiptTests
{
    [Test]
    public void DigestValueObjectAndPublicModelsRejectNullDigests()
    {
        PackageManifestEntry launcher = OwnershipTestData.Entry("StardewValley", '1', OwnedEntryKind.Launcher);
        InstallationReleaseIdentity release = OwnershipTestData.Release();

        Action manifestEntry = () => new PackageManifestEntry(launcher.Path, null!, 1, 420, launcher.Kind);
        Action receiptEntry = () => new InstallationReceiptEntry(launcher.Path, null!, 420, launcher.Kind);
        Action currentFile = () => new CurrentFile(launcher.Path, null!, 420);
        Action releaseIdentity = () => new InstallationReleaseIdentity(
            release.Repository,
            release.Tag,
            release.EmbeddedVersion,
            release.PackageAssetName,
            release.SourceCommit,
            release.SourceTree,
            null!,
            release.PackageSizeBytes,
            release.BuildWorkflow,
            release.BuildConfiguration,
            release.RuntimeIdentifier
        );

        manifestEntry.Should().Throw<ArgumentNullException>();
        receiptEntry.Should().Throw<ArgumentNullException>();
        currentFile.Should().Throw<ArgumentNullException>();
        releaseIdentity.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void ReleaseIdentity_RejectsCrossArtifactMismatch()
    {
        InstallationReleaseIdentity valid = OwnershipTestData.Release();

        Action badVersion = () => new InstallationReleaseIdentity(
            valid.Repository,
            valid.Tag,
            "4.5.3",
            valid.PackageAssetName,
            valid.SourceCommit,
            valid.SourceTree,
            valid.PackageSha256,
            valid.PackageSizeBytes,
            valid.BuildWorkflow,
            valid.BuildConfiguration,
            valid.RuntimeIdentifier
        );
        Action badRepository = () => new InstallationReleaseIdentity(
            "https://github.com/Pathoschild/SMAPI",
            valid.Tag,
            valid.EmbeddedVersion,
            valid.PackageAssetName,
            valid.SourceCommit,
            valid.SourceTree,
            valid.PackageSha256,
            valid.PackageSizeBytes,
            valid.BuildWorkflow,
            valid.BuildConfiguration,
            valid.RuntimeIdentifier
        );

        badVersion.Should().Throw<ArgumentException>().WithMessage("*doesn't match*");
        badRepository.Should().Throw<ArgumentException>().WithMessage("*reviewed SMAPI fork*");
    }

    [Test]
    public void Manifest_IsImmutableSortedAndCanonicalRegardlessOfInputOrder()
    {
        List<PackageManifestEntry> source = new()
        {
            OwnershipTestData.Entry("smapi-internal/config.json", '2', OwnedEntryKind.InternalFile),
            OwnershipTestData.Entry("StardewModdingAPI", '3', OwnedEntryKind.RuntimeFile, mode: 493),
            OwnershipTestData.Entry("StardewValley", '1', OwnedEntryKind.Launcher, mode: 493)
        };
        PackageManifest first = new(OwnershipTestData.Release(), source);
        PackageManifest second = new(OwnershipTestData.Release(), source.AsEnumerable().Reverse());
        source.Clear();

        first.Entries.Select(entry => entry.Path.Value).Should().Equal(
            "StardewModdingAPI",
            "StardewValley",
            "smapi-internal/config.json"
        );
        first.ToCanonicalJson().Should().Be(second.ToCanonicalJson());
        first.GetCanonicalDigest().Should().Be(second.GetCanonicalDigest());
        first.ToCanonicalJson().Should().NotContain("\r").And.NotContain("\n");
    }

    [Test]
    public void Manifest_RejectsCaseCollisionAndFileParentCollision()
    {
        Action caseCollision = () => OwnershipTestData.Manifest(
            otherEntries:
            [
                OwnershipTestData.Entry("smapi-internal/Test.dll", '2', OwnedEntryKind.InternalFile),
                OwnershipTestData.Entry("smapi-internal/test.dll", '3', OwnedEntryKind.InternalFile)
            ]
        );
        Action parentCollision = () => OwnershipTestData.Manifest(
            otherEntries:
            [
                OwnershipTestData.Entry("smapi-internal/a", '2', OwnedEntryKind.InternalFile),
                OwnershipTestData.Entry("smapi-internal/a/b", '3', OwnedEntryKind.InternalFile)
            ]
        );

        caseCollision.Should().Throw<ArgumentException>().WithMessage("*case-insensitive*");
        parentCollision.Should().Throw<ArgumentException>().WithMessage("*parent*");
    }

    [Test]
    public void Manifest_RequiresExactlyOneLauncher()
    {
        Action action = () => new PackageManifest(
            OwnershipTestData.Release(),
            [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile)]
        );
        action.Should().Throw<ArgumentException>().WithMessage("*exactly one installed launcher*");
    }

    [Test]
    public void Receipt_IsCanonicalAndBindsLauncherManifestAndTransaction()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile, mode: 493)]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);

        receipt.ManifestSha256.Should().Be(manifest.GetCanonicalDigest());
        receipt.Entries.Select(entry => entry.Path.Value).Should().BeInAscendingOrder(StringComparer.Ordinal);
        receipt.ToCanonicalJson().Should().Contain($"\"manifest_sha256\":\"{manifest.GetCanonicalDigest().Value}\"");
        receipt.GetCanonicalDigest().Should().Be(Sha256Digest.Hash(System.Text.Encoding.UTF8.GetBytes(receipt.ToCanonicalJson())));
    }

    [Test]
    public void Receipt_RejectsLauncherOrTransactionMismatch()
    {
        PackageManifest manifest = OwnershipTestData.Manifest();
        PackageManifestEntry launcher = manifest.Entries.Single();
        InstallationReceiptEntry receiptEntry = new(launcher.Path, launcher.Sha256, launcher.UnixMode, launcher.Kind);

        Action badLauncher = () => new InstallationReceipt(
            manifest.Release,
            manifest.GetCanonicalDigest(),
            new string('d', 32),
            [receiptEntry],
            new LauncherReceipt(OwnershipTestData.Digest('2'), OwnershipTestData.Digest('f'))
        );
        Action badTransaction = () => new InstallationReceipt(
            manifest.Release,
            manifest.GetCanonicalDigest(),
            "NOT-CANONICAL",
            [receiptEntry],
            new LauncherReceipt(launcher.Sha256, OwnershipTestData.Digest('f'))
        );

        badLauncher.Should().Throw<ArgumentException>().WithMessage("*launcher matching*");
        badTransaction.Should().Throw<ArgumentException>().WithMessage("*transaction ID*");
    }
}
