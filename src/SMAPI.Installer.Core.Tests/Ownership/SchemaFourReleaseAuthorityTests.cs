using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
using StardewModdingAPI.Installer.Core.Planning;

namespace StardewModdingAPI.Installer.Core.Tests.Ownership;

[TestFixture]
public sealed class SchemaFourReleaseAuthorityTests
{
    [Test]
    public void CompatibilityMatrix_AcceptsOnlyTwoThreeThreeThreeAndFourFour()
    {
        InstallationReleaseIdentity release = OwnershipTestData.Release();
        PackageManifest schema2 = new(
            release,
            [OwnershipTestData.Entry("StardewValley", '1', OwnedEntryKind.Launcher, mode: 493)],
            schemaVersion: PackageManifest.LegacySchemaVersion
        );
        PackageManifest schema3 = OwnershipTestData.Manifest(release);
        PackageManifest schema4 = OwnershipTestData.AuthorityManifest(release);

        InstallationReceipt receipt2or3 = OwnershipTestData.Receipt(schema2);
        InstallationReceipt receipt3or3 = OwnershipTestData.Receipt(schema3);
        InstallationReceipt receipt4or4 = OwnershipTestData.AuthorityReceipt(schema4);
        InstallationReceipt receipt4or3 = CreateReceipt(schema4, null, InstallationReceipt.LegacySchemaVersion);
        InstallationReceipt receiptLegacyOr4 = CreateReceipt(schema3, OwnershipTestData.Trust(schema4), InstallationReceipt.CurrentSchemaVersion);

        Parse(receipt2or3, schema2).SchemaVersion.Should().Be(InstallationReceipt.LegacySchemaVersion);
        Parse(receipt3or3, schema3).SchemaVersion.Should().Be(InstallationReceipt.LegacySchemaVersion);
        Parse(receipt4or4, schema4).SchemaVersion.Should().Be(InstallationReceipt.CurrentSchemaVersion);
        ((Action)(() => Parse(receipt4or3, schema4))).Should().Throw<OwnershipDocumentException>().WithMessage("*compatibility pair*");
        ((Action)(() => Parse(receiptLegacyOr4, schema3))).Should().Throw<OwnershipDocumentException>().WithMessage("*compatibility pair*");
        ((Action)(() => Parse(CreateReceipt(schema2, OwnershipTestData.Trust(schema4), InstallationReceipt.CurrentSchemaVersion), schema2)))
            .Should().Throw<OwnershipDocumentException>().WithMessage("*compatibility pair*");
    }

    [Test]
    public void SchemaFour_EvolutionPreservesPolicyAndAttestedTemplateIdentity()
    {
        PackageManifest template = OwnershipTestData.AuthorityManifest();
        (Sha256Digest Sha256, long SizeBytes) expected = template.GetAttestedTemplateIdentity();
        GeneratedFileRecipe recipe = template.GeneratedFiles.Single();
        PackageManifest resolved = template.ResolveGeneratedFiles(new Dictionary<string, RecoveryFileIdentity>(StringComparer.Ordinal)
        {
            [recipe.SourcePath.Value] = new(OwnershipTestData.Digest('e'), 42, 420)
        });

        resolved.SchemaVersion.Should().Be(PackageManifest.CurrentSchemaVersion);
        resolved.ReleaseAuthorityPolicy.Should().Be(template.ReleaseAuthorityPolicy);
        resolved.GetAttestedTemplateIdentity().Should().Be(expected);
        CanonicalOwnershipDocuments.ParseManifest(Encoding.UTF8.GetBytes(resolved.ToCanonicalJson()))
            .GetAttestedTemplateIdentity().Should().Be(expected);

        InstallationReceipt receipt = OwnershipTestData.AuthorityReceipt(resolved);
        Parse(receipt, resolved).ReleaseTrust.Should().Be(receipt.ReleaseTrust);
    }

    [Test]
    public void SchemaFourPersistence_IsCanonicalAndAuthorityObjectsContainNoPersistedUrlsOrRawEvidence()
    {
        PackageManifest manifest = OwnershipTestData.AuthorityManifest();
        InstallationReceipt receipt = OwnershipTestData.AuthorityReceipt(manifest);
        string receiptJson = receipt.ToCanonicalJson();

        using JsonDocument manifestDocument = JsonDocument.Parse(manifest.ToCanonicalJson());
        using JsonDocument receiptDocument = JsonDocument.Parse(receiptJson);
        string policyJson = manifestDocument.RootElement.GetProperty("release_authority_policy").GetRawText();
        string evidenceJson = receiptDocument.RootElement.GetProperty("release_authority_evidence").GetRawText();

        policyJson.Should().NotContain("://");
        evidenceJson.Should().NotContain("://");
        evidenceJson.Should().NotContainAny("bundle", "certificate", "token", "signed_url", "repository_url");
        evidenceJson.Should().Contain("\"run_id\":\"123456\"").And.Contain("\"run_attempt\":2");
        Parse(receipt, manifest).ToCanonicalJson().Should().Be(receiptJson);
    }

    [TestCase("\"run_id\":\"123456\"", "\"run_id\":\"0123456\"")]
    [TestCase("\"run_attempt\":2", "\"run_attempt\":0")]
    [TestCase("github_artifact_attestation_v1", "github_artifact_attestation_v2")]
    [TestCase("\"size_bytes\":123456", "\"size_bytes\":123455")]
    public void SchemaFourReceipt_RejectsAuthorityEvidenceMutation(string original, string replacement)
    {
        PackageManifest manifest = OwnershipTestData.AuthorityManifest();
        string canonical = OwnershipTestData.AuthorityReceipt(manifest).ToCanonicalJson();
        string mutated = canonical.Replace(original, replacement, StringComparison.Ordinal);
        mutated.Should().NotBe(canonical);

        Action parse = () => CanonicalOwnershipDocuments.ParseReceipt(Encoding.UTF8.GetBytes(mutated), manifest);
        parse.Should().Throw<OwnershipDocumentException>();
    }

    [TestCase("github_artifact_attestation_v1", "github_artifact_attestation_v2")]
    [TestCase("github-hosted", "self-hosted")]
    [TestCase("1336010508", "1336010509")]
    public void SchemaFourManifest_RejectsPolicyMutation(string original, string replacement)
    {
        PackageManifest manifest = OwnershipTestData.AuthorityManifest();
        string canonical = manifest.ToCanonicalJson();
        string mutated = canonical.Replace(original, replacement, StringComparison.Ordinal);

        Action parse = () => CanonicalOwnershipDocuments.ParseManifest(Encoding.UTF8.GetBytes(mutated));
        parse.Should().Throw<OwnershipDocumentException>();
    }

    private static InstallationReceipt Parse(InstallationReceipt receipt, PackageManifest manifest)
        => CanonicalOwnershipDocuments.ParseReceipt(CanonicalOwnershipDocuments.SerializeReceipt(receipt), manifest);

    private static InstallationReceipt CreateReceipt(
        PackageManifest manifest,
        VerifiedTaggedPackageTrust? trust,
        int schemaVersion
    )
    {
        PackageManifestEntry launcher = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.Launcher);
        return new InstallationReceipt(
            manifest.Release,
            manifest.GetCanonicalDigest(),
            new string('d', 32),
            manifest.Entries.Select(entry => new InstallationReceiptEntry(entry.Path, entry.Sha256, entry.UnixMode, entry.Kind)),
            new LauncherReceipt(launcher.Sha256, OwnershipTestData.Digest('f')),
            trust,
            schemaVersion
        );
    }
}
