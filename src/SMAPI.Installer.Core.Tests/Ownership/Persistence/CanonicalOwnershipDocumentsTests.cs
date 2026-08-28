using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
using StardewModdingAPI.Installer.Core.Planning;

namespace StardewModdingAPI.Installer.Core.Tests.Ownership.Persistence;

[TestFixture]
public class CanonicalOwnershipDocumentsTests
{
    [Test]
    public void AllDocumentTypes_RoundTripTheirUniqueCanonicalBytes()
    {
        PackageManifest manifest = CreateManifest();
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        RollbackSnapshot snapshot = CreateRollback(receipt);

        PackageManifest parsedManifest = CanonicalOwnershipDocuments.ParseManifest(
            CanonicalOwnershipDocuments.SerializeManifest(manifest)
        );
        InstallationReceipt parsedReceipt = CanonicalOwnershipDocuments.ParseReceipt(
            CanonicalOwnershipDocuments.SerializeReceipt(receipt),
            parsedManifest
        );
        RollbackSnapshot parsedRollback = CanonicalOwnershipDocuments.ParseRollbackSnapshot(
            CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot),
            parsedReceipt
        );

        parsedManifest.ToCanonicalJson().Should().Be(manifest.ToCanonicalJson());
        parsedReceipt.ToCanonicalJson().Should().Be(receipt.ToCanonicalJson());
        CanonicalOwnershipDocuments.SerializeRollbackSnapshot(parsedRollback).Should().Equal(
            CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot)
        );
    }

    [TestCase("extra")]
    [TestCase("missing")]
    [TestCase("duplicate")]
    public void Manifest_RejectsAnyNonExactRootPropertySet(string mutation)
    {
        string canonical = CreateManifest().ToCanonicalJson();
        string mutated = mutation switch
        {
            "extra" => canonical.Insert(1, "\"unexpected\":true,"),
            "missing" => canonical.Replace("\"schema_version\":1,", "", StringComparison.Ordinal),
            "duplicate" => canonical.Insert(1, "\"schema_version\":1,"),
            _ => throw new InvalidOperationException()
        };

        Action parse = () => CanonicalOwnershipDocuments.ParseManifest(Encoding.UTF8.GetBytes(mutated));
        parse.Should().Throw<OwnershipDocumentException>();
    }

    [Test]
    public void Manifest_RejectsDuplicateNestedFieldsCommentsAndTrailingData()
    {
        string canonical = CreateManifest().ToCanonicalJson();
        string duplicate = canonical.Replace(
            "\"repository\":",
            $"\"repository\":\"{InstallationReleaseIdentity.ReviewedRepository}\",\"repository\":",
            StringComparison.Ordinal
        );

        Action duplicateParse = () => CanonicalOwnershipDocuments.ParseManifest(Encoding.UTF8.GetBytes(duplicate));
        Action commentParse = () => CanonicalOwnershipDocuments.ParseManifest(Encoding.UTF8.GetBytes(canonical.Insert(1, "/*x*/")));
        Action trailingParse = () => CanonicalOwnershipDocuments.ParseManifest(Encoding.UTF8.GetBytes(canonical + "{}"));

        duplicateParse.Should().Throw<OwnershipDocumentException>().WithMessage("*duplicate property*");
        commentParse.Should().Throw<OwnershipDocumentException>();
        trailingParse.Should().Throw<OwnershipDocumentException>();
    }

    [Test]
    public void Manifest_RejectsNoncanonicalWhitespacePropertyOrderAndStringEncoding()
    {
        string canonical = CreateManifest().ToCanonicalJson();
        string whitespace = canonical + "\n";
        string reordered = canonical.Replace(
            "\"schema_version\":1,\"release\":",
            "\"release\":",
            StringComparison.Ordinal
        ).Replace("},\"entries\":", "},\"schema_version\":1,\"entries\":", StringComparison.Ordinal);
        string alternateEncoding = canonical.Replace("StardewValley", "Stardew\\u0056alley", StringComparison.Ordinal);

        Action whitespaceParse = () => CanonicalOwnershipDocuments.ParseManifest(Encoding.UTF8.GetBytes(whitespace));
        Action reorderedParse = () => CanonicalOwnershipDocuments.ParseManifest(Encoding.UTF8.GetBytes(reordered));
        Action encodedParse = () => CanonicalOwnershipDocuments.ParseManifest(Encoding.UTF8.GetBytes(alternateEncoding));

        whitespaceParse.Should().Throw<OwnershipDocumentException>().WithMessage("*canonical byte representation*");
        reorderedParse.Should().Throw<OwnershipDocumentException>().WithMessage("*canonical byte representation*");
        encodedParse.Should().Throw<OwnershipDocumentException>().WithMessage("*canonical byte representation*");
    }

    [Test]
    public void Parser_EnforcesDocumentEntryAndDepthBoundsBeforeTrustingInput()
    {
        byte[] canonical = CanonicalOwnershipDocuments.SerializeManifest(CreateManifest());
        OwnershipPersistenceLimits tooSmall = new(canonical.Length - 1, 16, 100);
        OwnershipPersistenceLimits tooFew = new(canonical.Length, 16, 1);
        OwnershipPersistenceLimits shallow = new(4096, 3, 100);
        byte[] nested = Encoding.UTF8.GetBytes("{\"x\":{\"x\":{\"x\":{\"x\":1}}}}");

        Action bytesParse = () => CanonicalOwnershipDocuments.ParseManifest(canonical, tooSmall);
        Action entriesParse = () => CanonicalOwnershipDocuments.ParseManifest(canonical, tooFew);
        Action depthParse = () => CanonicalOwnershipDocuments.ParseManifest(nested, shallow);

        bytesParse.Should().Throw<OwnershipDocumentException>().WithMessage("*byte limit*");
        entriesParse.Should().Throw<OwnershipDocumentException>().WithMessage("*entry limit*");
        depthParse.Should().Throw<OwnershipDocumentException>();
    }

    [Test]
    public void Receipt_RejectsTamperedManifestDigestEntryModeAndRelease()
    {
        PackageManifest manifest = CreateManifest();
        InstallationReceipt valid = OwnershipTestData.Receipt(manifest);
        PackageManifestEntry launcher = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.Launcher);

        InstallationReceipt wrongDigest = CopyReceipt(valid, manifestSha256: OwnershipTestData.Digest('9'));
        InstallationReceipt wrongMode = CopyReceipt(
            valid,
            entries: valid.Entries.Select(entry => new InstallationReceiptEntry(
                entry.Path,
                entry.InstalledSha256,
                entry.Path == launcher.Path ? entry.UnixMode - 1 : entry.UnixMode,
                entry.Kind
            ))
        );
        InstallationReceipt wrongRelease = CopyReceipt(valid, release: OwnershipTestData.Release(alpha: 2, packageHash: '8'));

        Action digestParse = () => ParseReceipt(wrongDigest, manifest);
        Action modeParse = () => ParseReceipt(wrongMode, manifest);
        Action releaseParse = () => ParseReceipt(wrongRelease, manifest);

        digestParse.Should().Throw<OwnershipDocumentException>().WithMessage("*manifest digest*");
        modeParse.Should().Throw<OwnershipDocumentException>().WithMessage("*entry*");
        releaseParse.Should().Throw<OwnershipDocumentException>().WithMessage("*release identity*");
    }

    [Test]
    public void Receipt_RejectsForgedButPolicyAllowedNamespaceEntry()
    {
        PackageManifest manifest = CreateManifest();
        InstallationReceipt valid = OwnershipTestData.Receipt(manifest);
        InstallationReceipt forged = CopyReceipt(
            valid,
            entries: valid.Entries.Append(
                new InstallationReceiptEntry(
                    OwnershipTestData.Path("smapi-internal/forged.dll"),
                    OwnershipTestData.Digest('9'),
                    420,
                    OwnedEntryKind.InternalFile
                )
            )
        );

        Action parse = () => ParseReceipt(forged, manifest);
        parse.Should().Throw<OwnershipDocumentException>().WithMessage("*entry set*");
    }

    [Test]
    public void Receipt_RejectsMissingDuplicateAndInvalidTransactionIdFields()
    {
        PackageManifest manifest = CreateManifest();
        string canonical = OwnershipTestData.Receipt(manifest).ToCanonicalJson();
        string missing = canonical.Replace("\"transaction_id\":\"" + new string('d', 32) + "\",", "", StringComparison.Ordinal);
        string duplicate = canonical.Replace("\"transaction_id\":", "\"transaction_id\":\"" + new string('d', 32) + "\",\"transaction_id\":", StringComparison.Ordinal);
        string invalid = canonical.Replace(new string('d', 32), "NOT-A-TRANSACTION-ID", StringComparison.Ordinal);

        Action missingParse = () => CanonicalOwnershipDocuments.ParseReceipt(Encoding.UTF8.GetBytes(missing), manifest);
        Action duplicateParse = () => CanonicalOwnershipDocuments.ParseReceipt(Encoding.UTF8.GetBytes(duplicate), manifest);
        Action invalidParse = () => CanonicalOwnershipDocuments.ParseReceipt(Encoding.UTF8.GetBytes(invalid), manifest);

        missingParse.Should().Throw<OwnershipDocumentException>();
        duplicateParse.Should().Throw<OwnershipDocumentException>();
        invalidParse.Should().Throw<OwnershipDocumentException>();
    }

    [Test]
    public void RollbackSnapshot_RejectsWrongReceiptAndInvalidDigestCombination()
    {
        PackageManifest manifest = CreateManifest();
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        RollbackSnapshot snapshot = CreateRollback(receipt);
        InstallationReceipt otherReceipt = CopyReceipt(receipt, transactionId: new string('e', 32));
        string invalidCombination = Encoding.UTF8.GetString(CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot))
            .Replace("\"backup_sha256\":null", $"\"backup_sha256\":\"{OwnershipTestData.Digest('7').Value}\"", StringComparison.Ordinal);

        Action wrongReceipt = () => CanonicalOwnershipDocuments.ParseRollbackSnapshot(
            CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot),
            otherReceipt
        );
        Action invalidEntry = () => CanonicalOwnershipDocuments.ParseRollbackSnapshot(
            Encoding.UTF8.GetBytes(invalidCombination),
            receipt
        );

        wrongReceipt.Should().Throw<OwnershipDocumentException>().WithMessage("*doesn't target*");
        invalidEntry.Should().Throw<OwnershipDocumentException>();
    }

    [Test]
    public void RollbackSnapshot_UninstallTransitionRoundTripsWithoutCurrentReceipt()
    {
        InstallationReceipt priorReceipt = OwnershipTestData.Receipt(CreateManifest());
        RollbackSnapshot snapshot = new(
            expectedCurrentReceiptSha256: null,
            previousReceiptSha256: priorReceipt.GetCanonicalDigest(),
            [
                new RollbackSnapshotEntry(
                    OwnershipTestData.Path("StardewModdingAPI"),
                    OwnedEntryKind.RuntimeFile,
                    RollbackEntryKind.Restore,
                    expectedCurrentSha256: null,
                    backupSha256: OwnershipTestData.Digest('8')
                )
            ]
        );

        RollbackSnapshot parsed = CanonicalOwnershipDocuments.ParseRollbackSnapshot(
            CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot),
            currentReceipt: null
        );

        parsed.ExpectedCurrentReceiptSha256.Should().BeNull();
        parsed.PreviousReceiptSha256.Should().Be(priorReceipt.GetCanonicalDigest());
        CanonicalOwnershipDocuments.SerializeRollbackSnapshot(parsed).Should().Equal(
            CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot)
        );
    }

    private static PackageManifest CreateManifest()
    {
        return OwnershipTestData.Manifest(
            otherEntries:
            [
                OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile, mode: 493),
                OwnershipTestData.Entry("smapi-internal/core.dll", '3', OwnedEntryKind.InternalFile)
            ]
        );
    }

    private static RollbackSnapshot CreateRollback(InstallationReceipt receipt)
    {
        InstallationReceiptEntry runtime = receipt.Entries.Single(entry => entry.Path.Value == "StardewModdingAPI");
        InstallationReceiptEntry internalFile = receipt.Entries.Single(entry => entry.Path.Value == "smapi-internal/core.dll");
        return new RollbackSnapshot(
            receipt.GetCanonicalDigest(),
            OwnershipTestData.Digest('7'),
            [
                new RollbackSnapshotEntry(runtime.Path, runtime.Kind, RollbackEntryKind.Restore, runtime.InstalledSha256, OwnershipTestData.Digest('8')),
                new RollbackSnapshotEntry(internalFile.Path, internalFile.Kind, RollbackEntryKind.Remove, internalFile.InstalledSha256, null)
            ]
        );
    }

    private static void ParseReceipt(InstallationReceipt receipt, PackageManifest manifest)
    {
        CanonicalOwnershipDocuments.ParseReceipt(CanonicalOwnershipDocuments.SerializeReceipt(receipt), manifest);
    }

    private static InstallationReceipt CopyReceipt(
        InstallationReceipt receipt,
        InstallationReleaseIdentity? release = null,
        Sha256Digest? manifestSha256 = null,
        string? transactionId = null,
        IEnumerable<InstallationReceiptEntry>? entries = null
    )
    {
        return new InstallationReceipt(
            release ?? receipt.Release,
            manifestSha256 ?? receipt.ManifestSha256,
            transactionId ?? receipt.TransactionId,
            entries ?? receipt.Entries,
            receipt.Launcher
        );
    }
}
