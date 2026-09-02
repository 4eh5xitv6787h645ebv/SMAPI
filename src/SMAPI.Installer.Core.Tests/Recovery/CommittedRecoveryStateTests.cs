using System.Runtime.Versioning;
using System.Security.Cryptography;
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
    public void RecoveryPrunePlan_ConfirmationDigestBindsAuxiliaryCleanupDecision()
    {
        GameRootIdentity root = new("/game", 1, 2, 3);
        Guid generation = Guid.ParseExact("11111111111111111111111111111111", "N");
        Sha256Digest head = OwnershipTestData.Digest('a');
        RecoveryPrunePlan noWork = new(root, 7, head, 1, [generation], [generation], [], [], [], null, false);
        RecoveryPrunePlan auxiliary = new(root, 7, head, 1, [generation], [generation], [], [], [], null, true);

        noWork.ConfirmationDigest.Should().NotBe(auxiliary.ConfirmationDigest);
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

    [Test]
    public void Pointer_V2RoundTripsOptionalAuthenticatedRetentionDigest()
    {
        CommittedRecoveryPointer pointer = new(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            InstallationAction.Backup,
            OwnershipTestData.Digest('a'),
            OwnershipTestData.Digest('b'),
            OwnershipTestData.Digest('c'),
            OwnershipTestData.Digest('b'),
            OwnershipTestData.Digest('c'),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            OwnershipTestData.Digest('f'),
            OwnershipTestData.Digest('9'),
            CommittedRecoveryPointer.CurrentSchemaVersion
        );

        byte[] bytes = CanonicalRecoveryPointerDocument.Serialize(pointer);

        CanonicalRecoveryPointerDocument.Parse(bytes).Should().Be(pointer);
        Encoding.UTF8.GetString(bytes).Should().EndWith(
            ",\"retention_sha256\":\"" + new string('9', 64) + "\"}"
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
    public void PruneOutcome_ReportsLogicalPublicationPhysicalCleanupAndPendingWorkAtFaultBoundaries()
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        {
            RecoveryPrunePlan plan = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            LinuxInstallerEngine interrupted = new(
                new InstallerTransactionExecutor(),
                new BoundaryTerminationFaultInjector(RecoveryPruneBoundary.AfterPointerPublish)
            );

            RecoveryPruneOutcome outcome = interrupted.ExecuteRecoveryPruneWithOutcomeAsync(plan, plan.ConfirmationDigest).GetAwaiter().GetResult();

            outcome.Status.Should().Be(RecoveryPruneOutcomeStatus.Interrupted);
            outcome.LogicallyRemovedGenerationIds.Should().Equal(plan.RemovedGenerationIds);
            outcome.PhysicallyCleanedGenerationIds.Should().BeEmpty();
            outcome.PendingCleanupGenerationIds.Should().BeEquivalentTo(plan.CleanupGenerationIds);
            outcome.AuxiliaryCleanupPending.Should().BeFalse();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PruneOutcome_CleanupOnlyFaultReportsExactPhysicalAndAuxiliaryPending(bool afterLastGeneration)
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        {
            RecoveryPrunePlan initial = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            LinuxInstallerEngine publishing = new(
                new InstallerTransactionExecutor(),
                new BoundaryTerminationFaultInjector(RecoveryPruneBoundary.AfterPointerPublish)
            );
            publishing.ExecuteRecoveryPruneWithOutcomeAsync(initial, initial.ConfirmationDigest).GetAwaiter().GetResult().Status
                .Should().Be(RecoveryPruneOutcomeStatus.Interrupted);
            RecoveryPrunePlan cleanup = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            cleanup.RemovedGenerationIds.Should().BeEmpty();
            int failAfter = afterLastGeneration ? cleanup.CleanupGenerationIds.Count : 1;
            LinuxInstallerEngine failing = new(new InstallerTransactionExecutor(), new CleanupCountTerminationFaultInjector(failAfter));

            RecoveryPruneOutcome outcome = failing.ExecuteRecoveryPruneWithOutcomeAsync(cleanup, cleanup.ConfirmationDigest).GetAwaiter().GetResult();

            outcome.Status.Should().Be(RecoveryPruneOutcomeStatus.Interrupted);
            outcome.LogicallyRemovedGenerationIds.Should().BeEmpty();
            outcome.PhysicallyCleanedGenerationIds.Should().HaveCount(failAfter);
            outcome.PendingCleanupGenerationIds.Should().HaveCount(cleanup.CleanupGenerationIds.Count - failAfter);
            outcome.AuxiliaryCleanupPending.Should().BeFalse();
            outcome.RequiresCleanup.Should().Be(!afterLastGeneration);
        }
    }

    [TestCase(false, RecoveryPruneOutcomeStatus.FailedAfterApply)]
    [TestCase(true, RecoveryPruneOutcomeStatus.CancelledAfterApply)]
    public void PruneOutcome_AfterLastCleanupFaultRequiresRefreshWithoutClaimingPendingWork(bool cancelled, RecoveryPruneOutcomeStatus expectedStatus)
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        {
            RecoveryPrunePlan plan = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            Exception failure = cancelled
                ? new OperationCanceledException("Simulated cancellation after the last cleanup.")
                : new IOException("Simulated failure after the last cleanup.");
            LinuxInstallerEngine failing = new(new InstallerTransactionExecutor(), new CleanupCountFailureFaultInjector(plan.CleanupGenerationIds.Count, failure));

            RecoveryPruneOutcome outcome = failing.ExecuteRecoveryPruneWithOutcomeAsync(plan, plan.ConfirmationDigest).GetAwaiter().GetResult();

            outcome.Status.Should().Be(expectedStatus);
            outcome.LogicallyRemovedGenerationIds.Should().Equal(plan.RemovedGenerationIds);
            outcome.PhysicallyCleanedGenerationIds.Should().BeEquivalentTo(plan.CleanupGenerationIds);
            outcome.PendingCleanupGenerationIds.Should().BeEmpty();
            outcome.AuxiliaryCleanupPending.Should().BeFalse();
            outcome.RequiresCleanup.Should().BeFalse();
            normal.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().ContainSingle();
        }
    }

    [Test]
    public void PruneOutcome_CancellationAfterPublicationIsTruthfulAndResumable()
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        using (CancellationTokenSource source = new())
        {
            RecoveryPrunePlan plan = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            LinuxInstallerEngine cancelling = new(new InstallerTransactionExecutor(), new CancellingPruneFaultInjector(source));

            RecoveryPruneOutcome outcome = cancelling.ExecuteRecoveryPruneWithOutcomeAsync(plan, plan.ConfirmationDigest, source.Token).GetAwaiter().GetResult();

            outcome.Status.Should().Be(RecoveryPruneOutcomeStatus.CancelledWithCleanupPending);
            outcome.LogicallyRemovedGenerationIds.Should().Equal(plan.RemovedGenerationIds);
            outcome.PhysicallyCleanedGenerationIds.Should().BeEmpty();
            RecoveryPrunePlan resume = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            resume.RemovedGenerationIds.Should().BeEmpty();
            resume.CleanupGenerationIds.Should().BeEquivalentTo(plan.CleanupGenerationIds);
        }
    }

    [Test]
    public void PruneOutcome_PreCancelledCleanupOnlyPlanDoesNotAssertPendingBeforeExactRevalidation()
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        using (CancellationTokenSource source = new())
        {
            RecoveryPrunePlan initial = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            LinuxInstallerEngine publishing = new(
                new InstallerTransactionExecutor(),
                new BoundaryTerminationFaultInjector(RecoveryPruneBoundary.AfterPointerPublish)
            );
            publishing.ExecuteRecoveryPruneWithOutcomeAsync(initial, initial.ConfirmationDigest).GetAwaiter().GetResult().Status
                .Should().Be(RecoveryPruneOutcomeStatus.Interrupted);
            RecoveryPrunePlan cleanup = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            cleanup.RemovedGenerationIds.Should().BeEmpty();
            source.Cancel();

            RecoveryPruneOutcome outcome = normal.ExecuteRecoveryPruneWithOutcomeAsync(cleanup, cleanup.ConfirmationDigest, source.Token).GetAwaiter().GetResult();

            outcome.Status.Should().Be(RecoveryPruneOutcomeStatus.CancelledBeforePublication);
            outcome.PendingCleanupGenerationIds.Should().BeEmpty();
            outcome.PhysicallyCleanedGenerationIds.Should().BeEmpty();
            outcome.RequiresCleanup.Should().BeFalse();
            outcome.SafeMessage.Should().Contain("List recoveries");
        }
    }

    [Test]
    public void PruneOutcome_CleanupOnlyPartialFirstGenerationFailureKeepsThatGenerationPending()
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        {
            RecoveryPrunePlan initial = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            LinuxInstallerEngine publishing = new(
                new InstallerTransactionExecutor(),
                new BoundaryTerminationFaultInjector(RecoveryPruneBoundary.AfterPointerPublish)
            );
            publishing.ExecuteRecoveryPruneWithOutcomeAsync(initial, initial.ConfirmationDigest).GetAwaiter().GetResult().Status
                .Should().Be(RecoveryPruneOutcomeStatus.Interrupted);
            RecoveryPrunePlan cleanup = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            EntryUnlinkTerminationFaultInjector fault = new("files/00000000");
            LinuxInstallerEngine failing = new(new InstallerTransactionExecutor(), fault);

            RecoveryPruneOutcome outcome = failing.ExecuteRecoveryPruneWithOutcomeAsync(cleanup, cleanup.ConfirmationDigest).GetAwaiter().GetResult();

            outcome.Status.Should().Be(RecoveryPruneOutcomeStatus.Interrupted);
            outcome.PhysicallyCleanedGenerationIds.Should().BeEmpty();
            outcome.PendingCleanupGenerationIds.Should().BeEquivalentTo(cleanup.CleanupGenerationIds);
            outcome.PendingCleanupGenerationIds.Should().Contain(fault.GenerationId!.Value);
            outcome.RequiresCleanup.Should().BeTrue();
        }
    }

    [Test]
    public void PruneOutcome_StaleCleanupPlanAfterSuccessfulCleanupDoesNotAssertOldPendingIds()
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        {
            RecoveryPrunePlan initial = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            LinuxInstallerEngine publishing = new(
                new InstallerTransactionExecutor(),
                new BoundaryTerminationFaultInjector(RecoveryPruneBoundary.AfterPointerPublish)
            );
            publishing.ExecuteRecoveryPruneWithOutcomeAsync(initial, initial.ConfirmationDigest).GetAwaiter().GetResult().Status
                .Should().Be(RecoveryPruneOutcomeStatus.Interrupted);
            RecoveryPrunePlan cleanup = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            normal.ExecuteRecoveryPruneWithOutcomeAsync(cleanup, cleanup.ConfirmationDigest).GetAwaiter().GetResult().Status
                .Should().Be(RecoveryPruneOutcomeStatus.Succeeded);

            RecoveryPruneOutcome stale = normal.ExecuteRecoveryPruneWithOutcomeAsync(cleanup, cleanup.ConfirmationDigest).GetAwaiter().GetResult();

            stale.Status.Should().Be(RecoveryPruneOutcomeStatus.FailedBeforePublication);
            stale.LogicallyRemovedGenerationIds.Should().BeEmpty();
            stale.PhysicallyCleanedGenerationIds.Should().BeEmpty();
            stale.PendingCleanupGenerationIds.Should().BeEmpty();
            stale.RequiresCleanup.Should().BeFalse();
            stale.SafeMessage.Should().Contain("List recoveries");
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
    public void Prune_PendingPointerPathSwapBeforePublicationIsRejected()
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        {
            RecoveryPrunePlan plan = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            PendingPointerSwapFaultInjector fault = new(game);
            LinuxInstallerEngine engine = new(new InstallerTransactionExecutor(), fault);

            Action execute = () => engine.ExecuteRecoveryPruneAsync(plan, plan.ConfirmationDigest).GetAwaiter().GetResult();

            execute.Should().Throw<OwnershipDocumentException>().WithMessage("*path changed*");
            fault.DisplacedPath.Should().NotBeNull();
            File.Exists(fault.DisplacedPath!).Should().BeTrue();
            normal.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().HaveCount(3);
        }
    }

    [Test]
    public void Prune_CurrentPointerPathSwapBeforePublicationIsRejected()
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        {
            RecoveryPrunePlan plan = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            CurrentPointerSwapFaultInjector fault = new(game);
            LinuxInstallerEngine engine = new(new InstallerTransactionExecutor(), fault);

            Action execute = () => engine.ExecuteRecoveryPruneAsync(plan, plan.ConfirmationDigest).GetAwaiter().GetResult();

            execute.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.PathChanged);
            fault.DisplacedPath.Should().NotBeNull();
            File.Exists(fault.DisplacedPath!).Should().BeTrue();
            normal.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().HaveCount(3);
        }
    }

    [Test]
    public void Prune_PendingPointerSwapBetweenVerificationAndCleanupUnlinkIsRejected()
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        {
            RecoveryPrunePlan initial = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            LinuxInstallerEngine terminating = new(
                new InstallerTransactionExecutor(),
                new BoundaryTerminationFaultInjector(RecoveryPruneBoundary.BeforePointerPublish)
            );
            Action interrupt = () => terminating.ExecuteRecoveryPruneAsync(
                initial,
                initial.ConfirmationDigest
            ).GetAwaiter().GetResult();
            interrupt.Should().Throw<SimulatedProcessTerminationException>();
            RecoveryPrunePlan resume = normal.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            resume.HasAuxiliaryCleanup.Should().BeTrue();
            PendingPointerCleanupSwapFaultInjector fault = new(game);
            LinuxInstallerEngine swapping = new(new InstallerTransactionExecutor(), fault);

            Action execute = () => swapping.ExecuteRecoveryPruneAsync(
                resume,
                resume.ConfirmationDigest
            ).GetAwaiter().GetResult();

            execute.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.PathChanged);
            fault.DisplacedPath.Should().NotBeNull();
            File.Exists(fault.DisplacedPath!).Should().BeTrue();
            normal.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().HaveCount(3);
        }
    }

    [Test]
    public void Prune_OrphanRetentionSwapBetweenVerificationAndCleanupUnlinkIsRejected()
    {
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(4);
        using (package)
        {
            RecoveryPrunePlan initial = normal.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            normal.ExecuteRecoveryPruneAsync(initial, initial.ConfirmationDigest).GetAwaiter().GetResult();
            CommittedRecoveryPointer pointer = CanonicalRecoveryPointerDocument.Parse(File.ReadAllBytes(CurrentPointerPath(game)));
            RecoveryRetentionRecord active = CanonicalRecoveryRetentionDocument.Parse(
                File.ReadAllBytes(RetentionDocumentPath(game, pointer.RetentionSha256!))
            );
            byte[] orphanBytes = CanonicalRecoveryRetentionDocument.Serialize(active with
            {
                PublicationHeadPointerSha256 = OwnershipTestData.Digest('6')
            });
            Sha256Digest orphanDigest = Sha256Digest.Hash(orphanBytes);
            WritePrivate(RetentionDocumentPath(game, orphanDigest), orphanBytes);
            RecoveryPrunePlan cleanup = normal.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            cleanup.HasAuxiliaryCleanup.Should().BeTrue();
            RetentionCleanupSwapFaultInjector fault = new(game, orphanDigest);
            LinuxInstallerEngine swapping = new(new InstallerTransactionExecutor(), fault);

            Action execute = () => swapping.ExecuteRecoveryPruneAsync(
                cleanup,
                cleanup.ConfirmationDigest
            ).GetAwaiter().GetResult();

            execute.Should().Throw<OwnershipDocumentException>().WithMessage("*changed before garbage collection*");
            fault.DisplacedPath.Should().NotBeNull();
            File.Exists(fault.DisplacedPath!).Should().BeTrue();
            normal.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().HaveCount(2);
            File.Exists(RetentionDocumentPath(game, pointer.RetentionSha256!)).Should().BeTrue();
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

    [Test]
    public void Prune_MigratesV1HeadAndCarriesAuthenticatedRetentionThroughLaterActions()
    {
        (string game, LinuxInstallerEngine engine, FilePackageAuthority package) = this.CreateRecoveryHistory(3);
        using (package)
        {
            byte[] v1Bytes = File.ReadAllBytes(CurrentPointerPath(game));
            CommittedRecoveryPointer v1 = CanonicalRecoveryPointerDocument.Parse(v1Bytes);
            v1.SchemaVersion.Should().Be(CommittedRecoveryPointer.LegacySchemaVersion);
            RecoveryPrunePlan plan = engine.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();

            engine.ExecuteRecoveryPruneAsync(plan, plan.ConfirmationDigest).GetAwaiter().GetResult().Should().Be(1);

            byte[] prunedBytes = File.ReadAllBytes(CurrentPointerPath(game));
            CommittedRecoveryPointer pruned = CanonicalRecoveryPointerDocument.Parse(prunedBytes);
            pruned.SchemaVersion.Should().Be(CommittedRecoveryPointer.CurrentSchemaVersion);
            pruned.RetentionSha256.Should().NotBeNull();
            CanonicalRecoveryPointerDocument.Serialize(
                pruned.WithRetention(null, CommittedRecoveryPointer.LegacySchemaVersion)
            ).Should().Equal(v1Bytes);
            byte[] retentionBytes = File.ReadAllBytes(RetentionDocumentPath(game, pruned.RetentionSha256!));
            RecoveryRetentionRecord retention = CanonicalRecoveryRetentionDocument.Parse(retentionBytes);
            retention.PublicationHeadPointerSha256.Should().Be(Sha256Digest.Hash(v1Bytes));
            retention.PublicationHeadPointerSchemaVersion.Should().Be(CommittedRecoveryPointer.LegacySchemaVersion);
            retention.PreviousRetentionSha256.Should().BeNull();

            Execute(engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult(), engine);

            CommittedRecoveryPointer carried = CanonicalRecoveryPointerDocument.Parse(File.ReadAllBytes(CurrentPointerPath(game)));
            carried.SchemaVersion.Should().Be(CommittedRecoveryPointer.CurrentSchemaVersion);
            carried.RetentionSha256.Should().Be(pruned.RetentionSha256);
            carried.PreviousPointerSha256.Should().Be(Sha256Digest.Hash(prunedBytes));
            string previous = Path.Combine(
                game,
                ".smapi-installer",
                "recovery",
                "generations",
                carried.GenerationId.ToString("N"),
                "previous-pointer.json"
            );
            File.ReadAllBytes(previous).Should().Equal(prunedBytes);
            engine.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().HaveCount(3);
        }
    }

    [Test]
    public void RecoveryHistory_ValidAlternateRetentionDocumentCannotChangeVisibleHistory()
    {
        (string game, LinuxInstallerEngine engine, FilePackageAuthority package) = this.CreateRecoveryHistory(4);
        using (package)
        {
            RecoveryPrunePlan prune = engine.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            engine.ExecuteRecoveryPruneAsync(prune, prune.ConfirmationDigest).GetAwaiter().GetResult();
            CommittedRecoveryPointer pointer = CanonicalRecoveryPointerDocument.Parse(File.ReadAllBytes(CurrentPointerPath(game)));
            RecoveryRetentionRecord active = CanonicalRecoveryRetentionDocument.Parse(
                File.ReadAllBytes(RetentionDocumentPath(game, pointer.RetentionSha256!))
            );
            RecoveryRetentionRecord alternate = active with
            {
                PublicationHeadPointerSha256 = OwnershipTestData.Digest('7')
            };
            byte[] alternateBytes = CanonicalRecoveryRetentionDocument.Serialize(alternate);
            Sha256Digest alternateDigest = Sha256Digest.Hash(alternateBytes);
            WritePrivate(RetentionDocumentPath(game, alternateDigest), alternateBytes);

            engine.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().HaveCount(2);
            File.WriteAllBytes(CurrentPointerPath(game), CanonicalRecoveryPointerDocument.Serialize(pointer.WithRetention(alternateDigest)));
            File.SetUnixFileMode(CurrentPointerPath(game), (UnixFileMode)0x180);

            Action list = () => engine.ListRecoveriesAsync(game).GetAwaiter().GetResult();
            list.Should().Throw<OwnershipDocumentException>().WithMessage("*isn't bound*");
        }
    }

    [Test]
    public void RecoveryPrune_GarbageCollectsOnlyUnreferencedBoundedRetentionDocuments()
    {
        (string game, LinuxInstallerEngine engine, FilePackageAuthority package) = this.CreateRecoveryHistory(4);
        using (package)
        {
            RecoveryPrunePlan first = engine.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            engine.ExecuteRecoveryPruneAsync(first, first.ConfirmationDigest).GetAwaiter().GetResult();
            RecoveryPrunePlan noWork = engine.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            noWork.RemovedGenerationIds.Should().BeEmpty();
            noWork.CleanupGenerationIds.Should().BeEmpty();
            noWork.HasAuxiliaryCleanup.Should().BeFalse();
            CommittedRecoveryPointer pointer = CanonicalRecoveryPointerDocument.Parse(File.ReadAllBytes(CurrentPointerPath(game)));
            RecoveryRetentionRecord active = CanonicalRecoveryRetentionDocument.Parse(
                File.ReadAllBytes(RetentionDocumentPath(game, pointer.RetentionSha256!))
            );
            byte[] orphanBytes = CanonicalRecoveryRetentionDocument.Serialize(active with
            {
                PublicationHeadPointerSha256 = OwnershipTestData.Digest('8')
            });
            Sha256Digest orphanDigest = Sha256Digest.Hash(orphanBytes);
            WritePrivate(RetentionDocumentPath(game, orphanDigest), orphanBytes);
            Directory.EnumerateFiles(RetentionDirectoryPath(game)).Should().HaveCount(2);

            RecoveryPrunePlan cleanup = engine.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            cleanup.RemovedGenerationIds.Should().BeEmpty();
            cleanup.CleanupGenerationIds.Should().BeEmpty();
            cleanup.HasAuxiliaryCleanup.Should().BeTrue();
            engine.ExecuteRecoveryPruneAsync(cleanup, cleanup.ConfirmationDigest).GetAwaiter().GetResult().Should().Be(0);

            Directory.EnumerateFiles(RetentionDirectoryPath(game)).Should().ContainSingle()
                .Which.Should().EndWith($"{pointer.RetentionSha256!.Value}.json");
            engine.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().HaveCount(2);

            Execute(engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult(), engine);
            RecoveryPrunePlan second = engine.InspectRecoveryPruneAsync(game, 1).GetAwaiter().GetResult();
            second.HasAuxiliaryCleanup.Should().BeTrue("publishing a replacement retention boundary also removes the prior authenticated retention document");
            engine.ExecuteRecoveryPruneAsync(second, second.ConfirmationDigest).GetAwaiter().GetResult().Should().Be(2);
            engine.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().ContainSingle();
            Directory.EnumerateFiles(RetentionDirectoryPath(game)).Should().ContainSingle();
        }
    }

    [Test]
    public void RecoveryPrune_RejectsCrossRootPlanAndInvalidatesEarlierHandle()
    {
        (string firstGame, LinuxInstallerEngine engine, FilePackageAuthority firstPackage) = this.CreateRecoveryHistory(3);
        (string secondGame, LinuxInstallerEngine _, FilePackageAuthority secondPackage) = this.CreateRecoveryHistory(3);
        using (firstPackage)
        using (secondPackage)
        using (CommittedRecoveryHandle staleHandle = engine.OpenCurrentRecoveryAsync(firstGame).GetAwaiter().GetResult())
        {
            RecoveryPrunePlan firstPlan = engine.InspectRecoveryPruneAsync(firstGame, 1).GetAwaiter().GetResult();
            using InstallerOperationLease secondLease = InstallerOperationLease.Acquire(secondGame);
            AnchoredCoreStateAuthority secondState = AnchoredCoreStateAuthority.Inspect(secondLease);
            Action crossRoot = () => CommittedRecoveryHandle.ExecutePrunePlan(secondLease, secondState, firstPlan);
            crossRoot.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.PathChanged);
            engine.ExecuteRecoveryPruneAsync(firstPlan, firstPlan.ConfirmationDigest).GetAwaiter().GetResult();

            Action stale = () => engine.InspectAsync(
                firstGame,
                InstallationAction.Rollback,
                recovery: staleHandle
            ).GetAwaiter().GetResult();
            stale.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StaleRollbackSnapshot);
        }
    }

    private string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-recovery-tests-{Guid.NewGuid():N}");
        LinuxGameTestFolder.MakeValid(path);
        this.TemporaryDirectories.Add(path);
        return path;
    }

    private static string CurrentPointerPath(string game)
        => Path.Combine(game, ".smapi-installer", "recovery", "current.json");

    private static string RetentionDirectoryPath(string game)
        => Path.Combine(game, ".smapi-installer", "recovery", "retention");

    private static string RetentionDocumentPath(string game, Sha256Digest digest)
        => Path.Combine(RetentionDirectoryPath(game), $"{digest.Value}.json");

    private static void WritePrivate(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        File.SetUnixFileMode(path, (UnixFileMode)0x180);
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

    private sealed class PendingPointerSwapFaultInjector : IRecoveryPruneFaultInjector
    {
        private readonly string Game;
        public string? DisplacedPath { get; private set; }

        public PendingPointerSwapFaultInjector(string game)
        {
            this.Game = game;
        }

        public void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null)
        {
            if (boundary != RecoveryPruneBoundary.BeforePointerPublish || this.DisplacedPath is not null)
                return;
            string pending = Path.Combine(this.Game, ".smapi-installer", "recovery", "current.pending");
            this.DisplacedPath = Path.Combine(this.Game, "displaced-current.pending");
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

    private sealed class CurrentPointerSwapFaultInjector : IRecoveryPruneFaultInjector
    {
        private readonly string Game;
        public string? DisplacedPath { get; private set; }

        public CurrentPointerSwapFaultInjector(string game)
        {
            this.Game = game;
        }

        public void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null)
        {
            if (boundary != RecoveryPruneBoundary.BeforePointerPublish || this.DisplacedPath is not null)
                return;
            string current = CurrentPointerPath(this.Game);
            this.DisplacedPath = Path.Combine(this.Game, "displaced-current.json");
            File.Move(current, this.DisplacedPath);
            File.Copy(this.DisplacedPath, current);
            File.SetUnixFileMode(current, (UnixFileMode)0x180);
        }
    }

    private sealed class BoundaryTerminationFaultInjector : IRecoveryPruneFaultInjector
    {
        private readonly RecoveryPruneBoundary Target;

        public BoundaryTerminationFaultInjector(RecoveryPruneBoundary target)
        {
            this.Target = target;
        }

        public void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null)
        {
            if (boundary == this.Target)
                throw new SimulatedProcessTerminationException($"Simulated termination at {boundary}.");
        }
    }

    private sealed class CancellingPruneFaultInjector : IRecoveryPruneFaultInjector
    {
        private readonly CancellationTokenSource Source;
        public CancellingPruneFaultInjector(CancellationTokenSource source) => this.Source = source;
        public void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null)
        {
            if (boundary == RecoveryPruneBoundary.AfterPointerPublish)
                this.Source.Cancel();
        }
    }

    private sealed class CleanupCountTerminationFaultInjector : IRecoveryPruneFaultInjector
    {
        private readonly int FailAfter;
        private int Cleaned;
        public CleanupCountTerminationFaultInjector(int failAfter) => this.FailAfter = failAfter;
        public void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null)
        {
            if (boundary == RecoveryPruneBoundary.AfterGenerationCleanup && ++this.Cleaned == this.FailAfter)
                throw new SimulatedProcessTerminationException("Simulated cleanup-only interruption.");
        }
    }

    private sealed class CleanupCountFailureFaultInjector(int failAfter, Exception failure) : IRecoveryPruneFaultInjector
    {
        private int Cleaned;
        public void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null)
        {
            if (boundary == RecoveryPruneBoundary.AfterGenerationCleanup && ++this.Cleaned == failAfter)
                throw failure;
        }
    }

    private sealed class PendingPointerCleanupSwapFaultInjector : IRecoveryPruneFaultInjector
    {
        private readonly string Game;
        public string? DisplacedPath { get; private set; }

        public PendingPointerCleanupSwapFaultInjector(string game)
        {
            this.Game = game;
        }

        public void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null) { }

        public void BeforePendingPointerCleanupUnlink()
        {
            string pending = Path.Combine(this.Game, ".smapi-installer", "recovery", "current.pending");
            this.DisplacedPath = Path.Combine(this.Game, "displaced-cleanup-current.pending");
            File.Move(pending, this.DisplacedPath);
            File.Copy(this.DisplacedPath, pending);
            File.SetUnixFileMode(pending, (UnixFileMode)0x180);
        }
    }

    private sealed class RetentionCleanupSwapFaultInjector : IRecoveryPruneFaultInjector
    {
        private readonly string Game;
        private readonly Sha256Digest Target;
        public string? DisplacedPath { get; private set; }

        public RetentionCleanupSwapFaultInjector(string game, Sha256Digest target)
        {
            this.Game = game;
            this.Target = target;
        }

        public void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null) { }

        public void BeforeRetentionDocumentCleanupUnlink(Sha256Digest digest)
        {
            if (digest != this.Target || this.DisplacedPath is not null)
                return;
            string document = RetentionDocumentPath(this.Game, digest);
            this.DisplacedPath = Path.Combine(this.Game, "displaced-retention-orphan.json");
            File.Move(document, this.DisplacedPath);
            File.Copy(this.DisplacedPath, document);
            File.SetUnixFileMode(document, (UnixFileMode)0x180);
        }
    }
}
