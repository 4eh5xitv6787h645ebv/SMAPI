using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
using StardewModdingAPI.Installer.Core.Planning;

namespace StardewModdingAPI.Installer.Core.Tests.Ownership;

[TestFixture]
public sealed class GeneratedManifestTests
{
    [Test]
    public void Schema3_UnresolvedAndResolvedRecipesRoundTripCanonically()
    {
        GeneratedFileRecipe templateRecipe = Recipe();
        PackageManifest template = new(
            OwnershipTestData.Release(),
            new[] { OwnershipTestData.Entry("StardewValley", '1', OwnedEntryKind.Launcher, mode: 493) },
            new[] { templateRecipe }
        );

        PackageManifest reparsedTemplate = CanonicalOwnershipDocuments.ParseManifest(
            Encoding.UTF8.GetBytes(template.ToCanonicalJson())
        );
        reparsedTemplate.SchemaVersion.Should().Be(3);
        reparsedTemplate.GeneratedFiles.Should().ContainSingle().Which.SourceIdentity.Should().BeNull();
        reparsedTemplate.Entries.Should().NotContain(entry => entry.Kind == OwnedEntryKind.GeneratedFile);

        RecoveryFileIdentity source = new(OwnershipTestData.Digest('d'), 42, 416);
        PackageManifest resolved = template.ResolveGeneratedFiles(new Dictionary<string, RecoveryFileIdentity>(StringComparer.Ordinal)
        {
            [templateRecipe.SourcePath.Value] = source
        });
        PackageManifest reparsedResolved = CanonicalOwnershipDocuments.ParseManifest(
            Encoding.UTF8.GetBytes(resolved.ToCanonicalJson())
        );

        reparsedResolved.GeneratedFiles.Single().SourceIdentity.Should().Be(source);
        PackageManifestEntry result = reparsedResolved.Entries.Single(entry => entry.Kind == OwnedEntryKind.GeneratedFile);
        result.Path.Should().Be(templateRecipe.Path);
        result.Sha256.Should().Be(source.Sha256);
        result.SizeBytes.Should().Be(source.SizeBytes);
        result.UnixMode.Should().Be(source.UnixMode);
        reparsedResolved.ToCanonicalJson().Should().Be(resolved.ToCanonicalJson());
    }

    [Test]
    public void Schema2_RemainsReadableAndRetainsCanonicalDigest()
    {
        InstallationReleaseIdentity release = OwnershipTestData.Release();
        PackageManifest legacy = new(
            release,
            new[] { OwnershipTestData.Entry("StardewValley", '1', OwnedEntryKind.Launcher, mode: 493) },
            schemaVersion: PackageManifest.LegacySchemaVersion
        );
        byte[] bytes = Encoding.UTF8.GetBytes(legacy.ToCanonicalJson());

        PackageManifest reparsed = CanonicalOwnershipDocuments.ParseManifest(bytes);

        reparsed.SchemaVersion.Should().Be(2);
        reparsed.GeneratedFiles.Should().BeEmpty();
        Encoding.UTF8.GetBytes(reparsed.ToCanonicalJson()).Should().Equal(bytes);
        reparsed.GetCanonicalDigest().Should().Be(legacy.GetCanonicalDigest());
    }

    [Test]
    public void Schema3_RejectsUnrecognizedRecipePathsAndUnboundGeneratedEntries()
    {
        Action badSource = () => new GeneratedFileRecipe(
            OwnershipTestData.Path("StardewModdingAPI-net6.deps.json"),
            GeneratedFileRecipe.CopyGameDepsRecipe,
            OwnershipTestData.Path("frontend-selected.json")
        );
        PackageManifestEntry unbound = OwnershipTestData.Entry(
            "StardewModdingAPI-net6.deps.json",
            '2',
            OwnedEntryKind.GeneratedFile
        );
        Action unboundResult = () => new PackageManifest(
            OwnershipTestData.Release(),
            new[] { OwnershipTestData.Entry("StardewValley", '1', OwnedEntryKind.Launcher, mode: 493), unbound }
        );

        badSource.Should().Throw<ArgumentException>();
        unboundResult.Should().Throw<ArgumentException>();
    }

    private static GeneratedFileRecipe Recipe()
        => new(
            OwnershipTestData.Path("StardewModdingAPI-net6.deps.json"),
            GeneratedFileRecipe.CopyGameDepsRecipe,
            OwnershipTestData.Path("Stardew Valley.deps.json")
        );
}
