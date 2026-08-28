using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Recovery;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Tests.Ownership;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Tests.Recovery;

[TestFixture]
[SupportedOSPlatform("linux")]
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

    [TestCase("files/00000000")]
    [TestCase("previous-pointer.json")]
    [TestCase("snapshot.json")]
    public void Prune_AfterEntryUnlinkTermination_ResumesAuthenticatedCleanupAndRecoversCapacity(string entryPath)
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        {
            RecoveryPrunePlan plan = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            EntryUnlinkTerminationFaultInjector fault = new(entryPath);
            LinuxInstallerEngine crashing = new(new InstallerTransactionExecutor(), fault);

            Action execute = () => crashing.ExecuteRecoveryPruneAsync(plan, plan.ConfirmationDigest).GetAwaiter().GetResult();

            execute.Should().Throw<SimulatedProcessTerminationException>();
            fault.GenerationId.Should().NotBeNull();
            normal.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().ContainSingle();
            RecoveryPrunePlan resume = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            resume.RemovedGenerationIds.Should().BeEmpty();
            resume.CleanupGenerationIds.Should().Contain(fault.GenerationId!.Value);
            normal.ExecuteRecoveryPruneAsync(resume, resume.ConfirmationDigest).GetAwaiter().GetResult().Should().BeGreaterThan(0);

            RecoveryHistory retained = normal.ListRecoveriesAsync(game).GetAwaiter().GetResult();
            retained.Generations.Should().ContainSingle();
            Directory.EnumerateDirectories(Path.Combine(game, ".smapi-installer", "recovery", "generations"))
                .Should().ContainSingle();

            Execute(normal.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult(), normal);
            normal.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().HaveCount(2);
            Directory.EnumerateDirectories(Path.Combine(game, ".smapi-installer", "recovery", "generations"))
                .Should().HaveCount(2);
        }
    }

    [Test]
    public void Prune_PartialDeletionOfRetainedGenerationStillFailsClosed()
    {
        (string game, LinuxInstallerEngine engine, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        {
            RecoveryPrunePlan prune = engine.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            engine.ExecuteRecoveryPruneAsync(prune, prune.ConfirmationDigest).GetAwaiter().GetResult();
            Guid retained = engine.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Single().GenerationId;
            File.Delete(Path.Combine(
                game,
                ".smapi-installer",
                "recovery",
                "generations",
                retained.ToString("N"),
                "snapshot.json"
            ));

            Action list = () => engine.ListRecoveriesAsync(game).GetAwaiter().GetResult();

            list.Should().Throw<Exception>();
        }
    }

    [Test]
    public void Prune_PendingRetentionPathSwapBeforePublicationIsRejected()
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        {
            RecoveryPrunePlan plan = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            PendingRetentionSwapFaultInjector fault = new(game);
            LinuxInstallerEngine engine = new(new InstallerTransactionExecutor(), fault);

            Action execute = () => engine.ExecuteRecoveryPruneAsync(plan, plan.ConfirmationDigest).GetAwaiter().GetResult();

            execute.Should().Throw<OwnershipDocumentException>().WithMessage("*path changed*");
            fault.DisplacedPath.Should().NotBeNull();
            File.Exists(fault.DisplacedPath!).Should().BeTrue();
            normal.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().HaveCount(3);
        }
    }

    [TestCase(".")]
    [TestCase("files")]
    public void Prune_CleanupDirectoryPathSwapBeforeOpenIsRejected(string relativeDirectoryPath)
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        {
            RecoveryPrunePlan plan = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            CleanupDirectorySwapFaultInjector fault = new(game, relativeDirectoryPath);
            LinuxInstallerEngine engine = new(new InstallerTransactionExecutor(), fault);

            Action execute = () => engine.ExecuteRecoveryPruneAsync(plan, plan.ConfirmationDigest).GetAwaiter().GetResult();

            execute.Should().Throw<OwnershipDocumentException>().WithMessage("*changed before cleanup*");
            fault.DisplacedPath.Should().NotBeNull();
            Directory.Exists(fault.DisplacedPath!).Should().BeTrue();
            Directory.EnumerateFileSystemEntries(fault.DisplacedPath!).Should().NotBeEmpty();
            normal.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().ContainSingle();
        }
    }

    private string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-recovery-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        this.TemporaryDirectories.Add(path);
        return path;
    }

    private (string Game, LinuxInstallerEngine Engine, FilePackageAuthority Package) CreateRecoveryHistory(int generationCount)
    {
        string game = this.CreateDirectory();
        WriteText(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        FilePackageAuthority package = this.CreatePackage();
        Execute(Inspect(engine, game, InstallationAction.Install, package), engine);
        for (int index = 1; index < generationCount; index++)
            Execute(engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult(), engine);
        return (game, engine, package);
    }

    private FilePackageAuthority CreatePackage()
    {
        string root = this.CreateDirectory();
        WriteText(root, "StardewValley", "smapi launcher", 0x1ed);
        WriteText(root, "StardewModdingAPI.dll", "runtime", 0x1a4);
        PackageManifest manifest = new(
            OwnershipTestData.Release(),
            [
                Entry("StardewValley", "smapi launcher", 0x1ed, OwnedEntryKind.Launcher),
                Entry("StardewModdingAPI.dll", "runtime", 0x1a4, OwnedEntryKind.RuntimeFile)
            ]
        );
        return new FilePackageAuthority(manifest, root);
    }

    private static InspectedInstallationState Inspect(
        LinuxInstallerEngine engine,
        string game,
        InstallationAction action,
        IVerifiedPackageContentAuthority? package = null
    )
    {
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        return engine.InspectLocked(lease, action, package, null);
    }

    private static void Execute(InspectedInstallationState inspection, LinuxInstallerEngine engine)
    {
        using (inspection)
        {
            inspection.Plan.CanExecute.Should().BeTrue(string.Join(", ", inspection.Plan.Conflicts.Select(conflict => conflict.Code)));
            engine.ExecuteAsync(inspection, inspection.ConfirmationDigest).GetAwaiter().GetResult().Status
                .Should().Be(TransactionStatus.Committed);
        }
    }

    private static void Write(string root, string relativePath, byte[] bytes)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteText(string root, string relativePath, string contents, int mode)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, (UnixFileMode)mode);
    }

    private static PackageManifestEntry Entry(string path, string contents, int mode, OwnedEntryKind kind)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(contents);
        return new PackageManifestEntry(
            NormalizedRelativePath.Parse(path),
            Sha256Digest.Hash(bytes),
            bytes.LongLength,
            mode,
            kind
        );
    }

    private static TransactionFileOperation WriteOperation(string destination, string source, byte[] bytes)
    {
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new TransactionFileOperation(TransactionOperationKind.WriteFile, destination, null, source, sha, 0x180);
    }

    private sealed class FilePackageAuthority : IVerifiedPackageContentAuthority, IDisposable
    {
        private readonly LinuxAnchoredFileSystem Payload;
        public PackageManifest Manifest { get; }
        public Sha256Digest ManifestSha256 => this.Manifest.GetCanonicalDigest();

        public FilePackageAuthority(PackageManifest manifest, string payloadRoot)
        {
            this.Manifest = manifest;
            this.Payload = new LinuxAnchoredFileSystem(payloadRoot);
        }

        public LinuxAnchoredFile OpenFile(PackageManifestEntry expected, CancellationToken cancellationToken = default)
        {
            if (!this.Manifest.Entries.Contains(expected))
                throw new InvalidOperationException("The requested entry isn't in this test package.");
            LinuxAnchoredFile file = this.Payload.OpenRegularFileForRead(expected.Path.Value);
            try
            {
                if (
                    file.Identity.Size != expected.SizeBytes
                    || file.Identity.UnixMode != expected.UnixMode
                    || Sha256Digest.Parse(this.Payload.ComputeSha256(file, cancellationToken)) != expected.Sha256
                )
                    throw new InvalidOperationException("The test package payload doesn't match its manifest.");
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public void AssertUsable() => this.Payload.GetCurrentRootIdentity().Should().Be(this.Payload.Identity);
        public void Dispose() => this.Payload.Dispose();
    }

    private sealed class EntryUnlinkTerminationFaultInjector : IRecoveryPruneFaultInjector
    {
        private readonly string TargetPath;
        public Guid? GenerationId { get; private set; }

        public EntryUnlinkTerminationFaultInjector(string targetPath)
        {
            this.TargetPath = targetPath;
        }

        public void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null) { }

        public void AfterCleanupEntryUnlink(Guid generationId, string relativeEntryPath)
        {
            if (this.GenerationId is null && relativeEntryPath == this.TargetPath)
            {
                this.GenerationId = generationId;
                throw new SimulatedProcessTerminationException($"Simulated termination after unlinking '{relativeEntryPath}'.");
            }
        }
    }

    private sealed class PendingRetentionSwapFaultInjector : IRecoveryPruneFaultInjector
    {
        private readonly string Game;
        public string? DisplacedPath { get; private set; }

        public PendingRetentionSwapFaultInjector(string game)
        {
            this.Game = game;
        }

        public void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null)
        {
            if (boundary != RecoveryPruneBoundary.BeforePendingRetentionIdentityCheck || this.DisplacedPath is not null)
                return;
            string pending = Path.Combine(this.Game, ".smapi-installer", "recovery", "retention.pending");
            this.DisplacedPath = Path.Combine(this.Game, "displaced-retention.pending");
            File.Move(pending, this.DisplacedPath);
            File.Copy(this.DisplacedPath, pending);
            File.SetUnixFileMode(pending, (UnixFileMode)0x180);
        }
    }

    private sealed class CleanupDirectorySwapFaultInjector : IRecoveryPruneFaultInjector
    {
        private readonly string Game;
        private readonly string Target;
        public string? DisplacedPath { get; private set; }

        public CleanupDirectorySwapFaultInjector(string game, string target)
        {
            this.Game = game;
            this.Target = target;
        }

        public void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null) { }

        public void BeforeCleanupDirectoryOpen(Guid generationId, string relativeDirectoryPath)
        {
            if (relativeDirectoryPath != this.Target || this.DisplacedPath is not null)
                return;
            string generation = Path.Combine(
                this.Game,
                ".smapi-installer",
                "recovery",
                "generations",
                generationId.ToString("N")
            );
            string original = relativeDirectoryPath == "." ? generation : Path.Combine(generation, relativeDirectoryPath);
            this.DisplacedPath = Path.Combine(this.Game, $"displaced-{generationId:N}-{(relativeDirectoryPath == "." ? "generation" : "files")}");
            Directory.Move(original, this.DisplacedPath);
            Directory.CreateDirectory(original);
            File.SetUnixFileMode(original, (UnixFileMode)0x1c0);
        }
    }
}
