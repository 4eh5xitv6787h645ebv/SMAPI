using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;

namespace StardewModdingAPI.Installer.Core.Tests.Ownership;

[TestFixture]
public class InstallationInventoryTests
{
    [Test]
    public void Create_ClassifiesEveryOwnershipStateDeterministically()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries:
            [
                OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile, mode: 493),
                OwnershipTestData.Entry("smapi-internal/changed.dll", '3', OwnedEntryKind.InternalFile),
                OwnershipTestData.Entry("smapi-internal/missing.dll", '4', OwnedEntryKind.InternalFile)
            ]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        PackageManifestEntry unchanged = manifest.Entries.Single(entry => entry.Path.Value == "StardewModdingAPI");
        PackageManifestEntry changed = manifest.Entries.Single(entry => entry.Path.Value == "smapi-internal/changed.dll");
        NormalizedRelativePath legacyPath = OwnershipTestData.Path("StardewModdingAPI.xml");
        NormalizedRelativePath unknownPath = OwnershipTestData.Path("smapi-internal/unknown.dll");
        NormalizedRelativePath preservedPath = OwnershipTestData.Path("Mods/PrivateMod/manifest.json");

        InstallationInventory inventory = InstallationInventory.Create(
            manifest,
            receipt,
            [
                OwnershipTestData.Current(changed, digest: 'a'),
                new CurrentFile(preservedPath, OwnershipTestData.Digest('b'), 420),
                OwnershipTestData.Current(unchanged),
                new CurrentFile(unknownPath, OwnershipTestData.Digest('c'), 420),
                new CurrentFile(legacyPath, OwnershipTestData.Digest('d'), 420)
            ],
            preservedPaths: [preservedPath],
            legacyPaths: [legacyPath]
        );

        inventory.Entries.Select(entry => entry.Path.Value).Should().BeInAscendingOrder(StringComparer.Ordinal);
        inventory.Entries.Single(entry => entry.Path.Equals(unchanged.Path)).Classification.Should().Be(InventoryClassification.UnchangedOwned);
        inventory.Entries.Single(entry => entry.Path.Equals(changed.Path)).Classification.Should().Be(InventoryClassification.ModifiedOwned);
        inventory.Entries.Single(entry => entry.Path.Value == "smapi-internal/missing.dll").Classification.Should().Be(InventoryClassification.Absent);
        inventory.Entries.Single(entry => entry.Path.Equals(legacyPath)).Classification.Should().Be(InventoryClassification.Legacy);
        inventory.Entries.Single(entry => entry.Path.Equals(unknownPath)).Classification.Should().Be(InventoryClassification.UnknownCollision);
        inventory.Entries.Single(entry => entry.Path.Equals(preservedPath)).Classification.Should().Be(InventoryClassification.Preserved);
    }

    [Test]
    public void Create_TreatsPermissionDriftAsModification()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile, mode: 493)]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        PackageManifestEntry runtime = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.RuntimeFile);

        InstallationInventory inventory = InstallationInventory.Create(
            manifest,
            receipt,
            [OwnershipTestData.Current(runtime, mode: 420)]
        );

        inventory.Entries.Single(entry => entry.Path.Equals(runtime.Path)).Classification.Should().Be(InventoryClassification.ModifiedOwned);
    }

    [Test]
    public void Create_RejectsCaseCollidingObservations()
    {
        Action action = () => InstallationInventory.Create(
            null,
            null,
            [
                new CurrentFile(OwnershipTestData.Path("unknown/File"), OwnershipTestData.Digest('1'), 420),
                new CurrentFile(OwnershipTestData.Path("unknown/file"), OwnershipTestData.Digest('2'), 420)
            ]
        );
        action.Should().Throw<ArgumentException>().WithMessage("*case-insensitive*");
    }
}
