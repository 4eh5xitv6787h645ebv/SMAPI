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
        string schemaProperty = $"\"schema_version\":{PackageManifest.CurrentSchemaVersion},";
        string mutated = mutation switch
        {
            "extra" => canonical.Insert(1, "\"unexpected\":true,"),
            "missing" => canonical.Replace(schemaProperty, "", StringComparison.Ordinal),
            "duplicate" => canonical.Insert(1, schemaProperty),
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
        string schemaProperty = $"\"schema_version\":{PackageManifest.CurrentSchemaVersion}";
        string whitespace = canonical + "\n";
        string reordered = canonical.Replace(
            $"{schemaProperty},\"release\":",
            "\"release\":",
            StringComparison.Ordinal
        ).Replace("},\"entries\":", $"}},{schemaProperty},\"entries\":", StringComparison.Ordinal);
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
        PackageManifestEntry modeTarget = manifest.Entries.First(entry => entry.Kind != OwnedEntryKind.Launcher);

        InstallationReceipt wrongDigest = CopyReceipt(valid, manifestSha256: OwnershipTestData.Digest('9'));
        InstallationReceipt wrongMode = CopyReceipt(
            valid,
            entries: valid.Entries.Select(entry => new InstallationReceiptEntry(
                entry.Path,
                entry.InstalledSha256,
                entry.Path == modeTarget.Path ? entry.UnixMode - 1 : entry.UnixMode,
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
    public void RollbackSnapshot_RejectsWrongReceiptAndInvalidIdentityCombination()
    {
        PackageManifest manifest = CreateManifest();
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        RollbackSnapshot snapshot = CreateRollback(receipt);
        InstallationReceipt otherReceipt = CopyReceipt(receipt, transactionId: new string('e', 32));
        string invalidCombination = Encoding.UTF8.GetString(CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot))
            .Replace(
                "\"backup\":null",
                $"\"backup\":{{\"sha256\":\"{OwnershipTestData.Digest('7').Value}\",\"size_bytes\":10,\"unix_mode\":420,\"file_type\":\"regular_file\"}}",
                StringComparison.Ordinal
            );

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
    public void RollbackSnapshot_V1IsExplicitlyRetiredBecauseMissingMetadataCannotBeMigrated()
    {
        InstallationReceipt receipt = OwnershipTestData.Receipt(CreateManifest());
        string legacy = $"{{\"schema_version\":1,\"expected_current_receipt_sha256\":\"{receipt.GetCanonicalDigest().Value}\",\"previous_receipt_sha256\":null,\"entries\":[]}}";

        Action parse = () => CanonicalOwnershipDocuments.ParseRollbackSnapshot(Encoding.UTF8.GetBytes(legacy), receipt);

        parse.Should().Throw<OwnershipDocumentException>()
            .WithMessage("*version 1 is retired*file size*Unix mode*file type*create a new snapshot*");
    }

    [Test]
    public void RollbackSnapshot_SizeAndModeAreCanonicalDigestInputsAndUnknownTypeIsRejected()
    {
        InstallationReceipt receipt = OwnershipTestData.Receipt(CreateManifest());
        string canonical = Encoding.UTF8.GetString(CanonicalOwnershipDocuments.SerializeRollbackSnapshot(CreateRollback(receipt)));
        string changedSize = canonical.Replace("\"size_bytes\":10", "\"size_bytes\":11", StringComparison.Ordinal);
        string changedMode = canonical.Replace("\"unix_mode\":493", "\"unix_mode\":492", StringComparison.Ordinal);
        string changedType = canonical.Replace("\"file_type\":\"regular_file\"", "\"file_type\":\"symbolic_link\"", StringComparison.Ordinal);

        RollbackSnapshot sizeSnapshot = CanonicalOwnershipDocuments.ParseRollbackSnapshot(Encoding.UTF8.GetBytes(changedSize), receipt);
        RollbackSnapshot modeSnapshot = CanonicalOwnershipDocuments.ParseRollbackSnapshot(Encoding.UTF8.GetBytes(changedMode), receipt);
        Action parseType = () => CanonicalOwnershipDocuments.ParseRollbackSnapshot(Encoding.UTF8.GetBytes(changedType), receipt);

        Sha256Digest.Hash(CanonicalOwnershipDocuments.SerializeRollbackSnapshot(sizeSnapshot))
            .Should().NotBe(Sha256Digest.Hash(Encoding.UTF8.GetBytes(canonical)));
        Sha256Digest.Hash(CanonicalOwnershipDocuments.SerializeRollbackSnapshot(modeSnapshot))
            .Should().NotBe(Sha256Digest.Hash(Encoding.UTF8.GetBytes(canonical)));
        parseType.Should().Throw<OwnershipDocumentException>().WithMessage("*Unknown recovery file type*");
    }

    [Test]
    public void RollbackSnapshot_RejectsCaseAndParentChildCollisions()
    {
        RecoveryFileIdentity expected = Identity('2');
        Action caseCollision = () => new RollbackSnapshot(
            OwnershipTestData.Digest('1'),
            null,
            [
                new RollbackSnapshotEntry(OwnershipTestData.Path("smapi-internal/A"), OwnedEntryKind.InternalFile, RollbackEntryKind.Remove, expected, null),
                new RollbackSnapshotEntry(OwnershipTestData.Path("smapi-internal/a"), OwnedEntryKind.InternalFile, RollbackEntryKind.Remove, expected, null)
            ]
        );
        Action parentCollision = () => new RollbackSnapshot(
            OwnershipTestData.Digest('1'),
            null,
            [
                new RollbackSnapshotEntry(OwnershipTestData.Path("smapi-internal/a"), OwnedEntryKind.InternalFile, RollbackEntryKind.Remove, expected, null),
                new RollbackSnapshotEntry(OwnershipTestData.Path("smapi-internal/a/b"), OwnedEntryKind.InternalFile, RollbackEntryKind.Remove, expected, null)
            ]
        );

        caseCollision.Should().Throw<ArgumentException>().WithMessage("*unique even on case-insensitive filesystems*");
        parentCollision.Should().Throw<ArgumentException>().WithMessage("*parent*");
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
                    expectedCurrent: null,
                    backup: Identity('8', mode: 420)
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

    [Test]
    public void RollbackSnapshot_ReceiptOnlyTransitionRoundTrips()
    {
        InstallationReceipt receipt = OwnershipTestData.Receipt(CreateManifest());
        RollbackSnapshot snapshot = new(receipt.GetCanonicalDigest(), OwnershipTestData.Digest('7'), []);

        RollbackSnapshot parsed = CanonicalOwnershipDocuments.ParseRollbackSnapshot(
            CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot),
            receipt
        );

        parsed.Entries.Should().BeEmpty();
        parsed.PreviousReceiptSha256.Should().Be(OwnershipTestData.Digest('7'));
    }

    [Test]
    public void RollbackSnapshot_OriginalLauncherUsesRecoveryOnlyOwnershipKind()
    {
        RollbackSnapshot snapshot = new(
            null,
            OwnershipTestData.Digest('1'),
            [
                new RollbackSnapshotEntry(
                    OwnershipTestData.Path("StardewValley-original"),
                    OwnedEntryKind.RecoveryLauncherBackup,
                    RollbackEntryKind.Remove,
                    Identity('f', mode: 493),
                    null
                )
            ]
        );
        byte[] bytes = CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot);

        Encoding.UTF8.GetString(bytes).Should().Contain("\"owned_kind\":\"recovery_launcher_backup\"");
        CanonicalOwnershipDocuments.ParseRollbackSnapshot(bytes, currentReceipt: null).Entries.Single().OwnedKind
            .Should().Be(OwnedEntryKind.RecoveryLauncherBackup);
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
                new RollbackSnapshotEntry(runtime.Path, runtime.Kind, RollbackEntryKind.Restore, Identity(runtime.InstalledSha256, mode: runtime.UnixMode), Identity('8')),
                new RollbackSnapshotEntry(internalFile.Path, internalFile.Kind, RollbackEntryKind.Remove, Identity(internalFile.InstalledSha256, mode: internalFile.UnixMode), null)
            ]
        );
    }

    private static RecoveryFileIdentity Identity(char digest, long size = 10, int mode = 420)
        => Identity(OwnershipTestData.Digest(digest), size, mode);

    private static RecoveryFileIdentity Identity(Sha256Digest digest, long size = 10, int mode = 420)
        => new(digest, size, mode);

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
