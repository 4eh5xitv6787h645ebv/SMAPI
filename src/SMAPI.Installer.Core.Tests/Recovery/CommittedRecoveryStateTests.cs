using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Recovery;
using StardewModdingAPI.Installer.Core.Tests.Ownership;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Tests.Recovery;

[TestFixture]
public sealed class CommittedRecoveryStateTests
{
    private readonly List<string> TemporaryDirectories = new();

    [TearDown]
    public void TearDown()
    {
        foreach (string path in this.TemporaryDirectories)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Best-effort private test cleanup.
            }
        }
    }

    [Test]
    public void Pointer_RoundTripsCanonicalExactIdentity()
    {
        CommittedRecoveryPointer pointer = new(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            InstallationAction.Update,
            OwnershipTestData.Digest('a'),
            OwnershipTestData.Digest('b'),
            OwnershipTestData.Digest('c'),
            OwnershipTestData.Digest('d'),
            OwnershipTestData.Digest('e'),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            OwnershipTestData.Digest('f')
        );

        byte[] bytes = CanonicalRecoveryPointerDocument.Serialize(pointer);

        CanonicalRecoveryPointerDocument.Parse(bytes).Should().Be(pointer);
        Encoding.UTF8.GetString(bytes).Should().Be(
            "{\"schema_version\":1,\"generation_id\":\"11111111222233334444555555555555\",\"action\":\"update\",\"snapshot_sha256\":\"" + new string('a', 64) +
            "\",\"result_manifest_sha256\":\"" + new string('b', 64) + "\",\"result_receipt_sha256\":\"" + new string('c', 64) +
            "\",\"previous_manifest_sha256\":\"" + new string('d', 64) + "\",\"previous_receipt_sha256\":\"" + new string('e', 64) +
            "\",\"previous_generation_id\":\"aaaaaaaabbbbccccddddeeeeeeeeeeee\",\"previous_pointer_sha256\":\"" + new string('f', 64) + "\"}"
        );
    }

    [TestCase("{}")]
    [TestCase("[]")]
    [TestCase("{\"schema_version\":1}")]
    [TestCase("{\"schema_version\":2}")]
    public void Pointer_RejectsIncompleteOrWrongShape(string json)
    {
        Action parse = () => CanonicalRecoveryPointerDocument.Parse(Encoding.UTF8.GetBytes(json));

        parse.Should().Throw<OwnershipDocumentException>();
    }

    [TestCase(InstallationAction.Install, true, true, false)]
    [TestCase(InstallationAction.Update, true, false, true)]
    [TestCase(InstallationAction.Repair, true, false, true)]
    [TestCase(InstallationAction.Uninstall, true, true, true)]
    [TestCase(InstallationAction.Backup, true, true, true)]
    [TestCase(InstallationAction.Rollback, false, false, false)]
    public void Pointer_RejectsActionTupleMismatch(
        InstallationAction action,
        bool hasResult,
        bool hasPrevious,
        bool useDifferentTuples
    )
    {
        Sha256Digest resultManifest = OwnershipTestData.Digest('b');
        Sha256Digest resultReceipt = OwnershipTestData.Digest('c');
        Sha256Digest previousManifest = useDifferentTuples ? OwnershipTestData.Digest('d') : resultManifest;
        Sha256Digest previousReceipt = useDifferentTuples ? OwnershipTestData.Digest('e') : resultReceipt;

        Action create = () => _ = new CommittedRecoveryPointer(
            Guid.NewGuid(),
            action,
            OwnershipTestData.Digest('a'),
            hasResult ? resultManifest : null,
            hasResult ? resultReceipt : null,
            hasPrevious ? previousManifest : null,
            hasPrevious ? previousReceipt : null,
            null,
            null
        );

        create.Should().Throw<ArgumentException>();
    }

    [Test]
    public void OpenCurrent_AuthenticatesCommittedSnapshotAndOwnershipTuple()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI.dll", '2', OwnedEntryKind.RuntimeFile, mode: 493)]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        RecoveryFileIdentity installedLauncher = new(OwnershipTestData.Digest('1'), 10, 493);
        RollbackSnapshot snapshot = new(
            receipt.GetCanonicalDigest(),
            null,
            [new RollbackSnapshotEntry(OwnershipTestData.Path("StardewValley"), OwnedEntryKind.Launcher, RollbackEntryKind.Remove, installedLauncher, null)]
        );
        byte[] snapshotBytes = CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot);
        byte[] manifestBytes = CanonicalOwnershipDocuments.SerializeManifest(manifest);
        byte[] receiptBytes = CanonicalOwnershipDocuments.SerializeReceipt(receipt);
        Guid generation = Guid.NewGuid();
        CommittedRecoveryPointer pointer = new(
            generation,
            InstallationAction.Install,
            Sha256Digest.Hash(snapshotBytes),
            Sha256Digest.Hash(manifestBytes),
            Sha256Digest.Hash(receiptBytes),
            null,
            null,
            null,
            null
        );
        byte[] pointerBytes = CanonicalRecoveryPointerDocument.Serialize(pointer);
        Write(payload, "snapshot", snapshotBytes);
        Write(payload, "manifest", manifestBytes);
        Write(payload, "receipt", receiptBytes);
        Write(payload, "pointer", pointerBytes);
        string prefix = $".smapi-installer/recovery/generations/{generation:N}";
        TransactionPlan plan = TransactionPlan.CreateWithCoreState(
            generation,
            [WriteOperation($"{prefix}/snapshot.json", "snapshot", snapshotBytes)],
            Array.Empty<TransactionFileOperation>(),
            WriteOperation(TransactionPlan.CoreManifestRelativePath, "manifest", manifestBytes),
            WriteOperation(TransactionPlan.CoreReceiptRelativePath, "receipt", receiptBytes),
            WriteOperation(TransactionPlan.CoreRecoveryPointerRelativePath, "pointer", pointerBytes)
        );
        new InstallerTransactionExecutor().Apply(game, payload, plan);

        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
        using CommittedRecoveryHandle handle = CommittedRecoveryHandle.OpenCurrent(lease, state);

        state.ManifestSha256.Should().Be(manifest.GetCanonicalDigest());
        state.ReceiptSha256.Should().Be(receipt.GetCanonicalDigest());
        state.PointerSha256.Should().Be(Sha256Digest.Hash(pointerBytes));
        handle.GenerationId.Should().Be(generation);
        handle.Action.Should().Be(InstallationAction.Install);
        handle.SnapshotSha256.Should().Be(Sha256Digest.Hash(snapshotBytes));

        File.WriteAllText(Path.Combine(game, prefix, "snapshot.json"), "{}");
        Action reuse = () => ((ICommittedRecoveryContentAuthority)handle).AssertUsable();
        reuse.Should().Throw<OwnershipDocumentException>();
    }

    private string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-recovery-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        this.TemporaryDirectories.Add(path);
        return path;
    }

    private static void Write(string root, string relativePath, byte[] bytes)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static TransactionFileOperation WriteOperation(string destination, string source, byte[] bytes)
    {
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new TransactionFileOperation(TransactionOperationKind.WriteFile, destination, null, source, sha, 0x180);
    }
}
