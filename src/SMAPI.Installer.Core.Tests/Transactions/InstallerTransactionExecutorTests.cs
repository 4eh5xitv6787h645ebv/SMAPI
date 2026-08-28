using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Tests.Transactions;

[TestFixture]
public sealed class InstallerTransactionExecutorTests
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
                // Best effort test cleanup only.
            }
        }
    }

    [Test]
    public void Apply_CreatesReplacesAndRemovesFilesThenCommits()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "old");
        Write(game, "steam_appid.txt", "remove me");
        Write(game, "unrelated.txt", "preserve me");
        Write(payload, "new/create.txt", "created");
        Write(payload, "new/replace.txt", "new");

        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("smapi-internal/created/deep/file.txt", null, "new/create.txt", Hash("created"), 0x1a4),
            WriteOperation("StardewModdingAPI.dll", Hash("old"), "new/replace.txt", Hash("new"), 0x1ed),
            RemoveOperation("steam_appid.txt", Hash("remove me"))
        });

        TransactionResult result = new InstallerTransactionExecutor().Apply(game, payload, plan);

        result.Should().Be(new TransactionResult(plan.TransactionId, TransactionStatus.Committed, 3));
        File.ReadAllText(Path.Combine(game, "smapi-internal/created/deep/file.txt")).Should().Be("created");
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("new");
        File.Exists(Path.Combine(game, "steam_appid.txt")).Should().BeFalse();
        File.ReadAllText(Path.Combine(game, "unrelated.txt")).Should().Be("preserve me");
        File.ReadAllText(Path.Combine(game, ".smapi-installer/transactions", plan.TransactionId.ToString("N"), "events.jsonl"))
            .Should().Contain("\"kind\":\"Committed\"");
    }

    [Test]
    public void Apply_WhenExpectedFileChanged_RefusesWithoutChangingAnything()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "user changed");
        Write(payload, "managed.txt", "release");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("expected old"), "managed.txt", Hash("release"))
        });

        Action action = () => new InstallerTransactionExecutor().Apply(game, payload, plan);

        action.Should().Throw<InstallerTransactionException>()
            .Which.Code.Should().Be(TransactionErrorCode.ExistingFileMismatch);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("user changed");
    }

    [Test]
    public void Apply_WhenUnknownDestinationExists_RefusesWithoutBackingItUpOrReplacingIt()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.xml", "unknown");
        Write(payload, "collision.txt", "release");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.xml", null, "collision.txt", Hash("release"))
        });

        Action action = () => new InstallerTransactionExecutor().Apply(game, payload, plan);

        action.Should().Throw<InstallerTransactionException>()
            .Which.Code.Should().Be(TransactionErrorCode.ExistingFileMismatch);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.xml")).Should().Be("unknown");
    }

    [Test]
    public void Apply_WhenFaultOccursAfterMutation_RollsBackEveryChangedFile()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "first old");
        Write(game, "StardewModdingAPI.xml", "second old");
        Write(payload, "first.txt", "first new");
        Write(payload, "second.txt", "second new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("first old"), "first.txt", Hash("first new")),
            WriteOperation("StardewModdingAPI.xml", Hash("second old"), "second.txt", Hash("second new"))
        });

        InstallerTransactionExecutor executor = new(faultInjector: new ThrowingFaultInjector(afterOperation: 0, simulateTermination: false));
        Action action = () => executor.Apply(game, payload, plan);

        action.Should().Throw<InvalidOperationException>();
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("first old");
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.xml")).Should().Be("second old");
        File.ReadAllText(Path.Combine(game, ".smapi-installer/transactions", plan.TransactionId.ToString("N"), "events.jsonl"))
            .Should().Contain("\"kind\":\"RolledBack\"");
    }

    [Test]
    public void Recover_WhenProcessStopsAfterIntentButBeforeMutation_LeavesOriginalUntouched()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "old");
        Write(payload, "managed.txt", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("old"), "managed.txt", Hash("new"))
        });
        InstallerTransactionExecutor crashing = new(faultInjector: new ThrowingFaultInjector(beforeOperation: 0, simulateTermination: true));

        Action interrupted = () => crashing.Apply(game, payload, plan);
        interrupted.Should().Throw<SimulatedProcessTerminationException>();

        IReadOnlyList<TransactionResult> recovered = new InstallerTransactionExecutor().RecoverIncompleteTransactions(game);

        recovered.Should().ContainSingle().Which.Status.Should().Be(TransactionStatus.Recovered);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("old");
    }

    [Test]
    public void Recover_WhenProcessStopsAfterMutation_RestoresOriginalExactly()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "old bytes");
        Write(payload, "managed.txt", "new bytes");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("old bytes"), "managed.txt", Hash("new bytes"))
        });
        InstallerTransactionExecutor crashing = new(faultInjector: new ThrowingFaultInjector(afterOperation: 0, simulateTermination: true));

        Action interrupted = () => crashing.Apply(game, payload, plan);
        interrupted.Should().Throw<SimulatedProcessTerminationException>();
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("new bytes");

        IReadOnlyList<TransactionResult> recovered = new InstallerTransactionExecutor().RecoverIncompleteTransactions(game);

        recovered.Should().ContainSingle().Which.Status.Should().Be(TransactionStatus.Recovered);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("old bytes");
        new InstallerTransactionExecutor().RecoverIncompleteTransactions(game).Should().BeEmpty();
    }

    [Test]
    public void Recover_LegacySchema2InterruptedJournal_RestoresOriginalAndAllowsUpgrade()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "legacy old");
        Write(payload, "managed", "legacy new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("legacy old"), "managed", Hash("legacy new"))
        });
        Action interrupted = () => new InstallerTransactionExecutor(
            faultInjector: new ThrowingFaultInjector(afterOperation: 0, simulateTermination: true)
        ).Apply(game, payload, plan);
        interrupted.Should().Throw<SimulatedProcessTerminationException>();
        DowngradeJournalToSchema2(game, plan.TransactionId);

        new InstallerTransactionExecutor().RecoverIncompleteTransactions(game).Should().ContainSingle();

        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("legacy old");
    }

    [Test]
    public void Apply_LegacySchema2FinalJournal_DoesNotBlockNextTransaction()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(payload, "first", "first");
        Write(payload, "second", "second");
        TransactionPlan first = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", null, "first", Hash("first"))
        });
        new InstallerTransactionExecutor().Apply(game, payload, first);
        DowngradeJournalToSchema2(game, first.TransactionId);
        TransactionPlan second = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.xml", null, "second", Hash("second"))
        });

        new InstallerTransactionExecutor().Apply(game, payload, second).Status.Should().Be(TransactionStatus.Committed);

        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("first");
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.xml")).Should().Be("second");
    }

    [Test]
    public void Recover_WhenResultChangedAfterInterruption_PreservesChangeAndRefusesRollback()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "old");
        Write(payload, "managed.txt", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("old"), "managed.txt", Hash("new"))
        });
        InstallerTransactionExecutor crashing = new(faultInjector: new ThrowingFaultInjector(afterOperation: 0, simulateTermination: true));
        Action interrupted = () => crashing.Apply(game, payload, plan);
        interrupted.Should().Throw<SimulatedProcessTerminationException>();
        File.WriteAllText(Path.Combine(game, "StardewModdingAPI.dll"), "user edit after interruption");

        Action recover = () => new InstallerTransactionExecutor().RecoverIncompleteTransactions(game);

        recover.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.RecoveryFailed);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("user edit after interruption");
    }

    [Test]
    public void Recover_WhenBackupWasTampered_PreservesResultAndRefusesRollback()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "old");
        Write(payload, "managed.txt", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("old"), "managed.txt", Hash("new"))
        });
        InstallerTransactionExecutor crashing = new(faultInjector: new ThrowingFaultInjector(afterOperation: 0, simulateTermination: true));
        Action interrupted = () => crashing.Apply(game, payload, plan);
        interrupted.Should().Throw<SimulatedProcessTerminationException>();
        string transaction = Path.Combine(game, ".smapi-installer/transactions", plan.TransactionId.ToString("N"));
        File.WriteAllText(Path.Combine(transaction, "backups/00000000"), "tampered backup");

        Action recover = () => new InstallerTransactionExecutor().RecoverIncompleteTransactions(game);

        recover.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.RecoveryFailed);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("new");
    }

    [Test]
    public void Recover_WhenJournalDestinationWasTampered_PreservesUnrelatedFile()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "old");
        Write(game, "unrelated.txt", "preserve");
        Write(payload, "managed.txt", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("old"), "managed.txt", Hash("new"))
        });
        InstallerTransactionExecutor crashing = new(faultInjector: new ThrowingFaultInjector(afterOperation: 0, simulateTermination: true));
        Action interrupted = () => crashing.Apply(game, payload, plan);
        interrupted.Should().Throw<SimulatedProcessTerminationException>();
        string journalPath = Path.Combine(game, ".smapi-installer/transactions", plan.TransactionId.ToString("N"), "journal.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(journalPath))!;
        root["entries"]![0]!["relativePath"] = "unrelated.txt";
        File.WriteAllText(journalPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Action recover = () => new InstallerTransactionExecutor().RecoverIncompleteTransactions(game);

        recover.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.RecoveryFailed);
        File.ReadAllText(Path.Combine(game, "unrelated.txt")).Should().Be("preserve");
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("new");
    }

    [TestCase("{}")]
    [TestCase("[]")]
    public void Recover_WhenJournalRootShapeIsMalformed_FailsWithControlledRecoveryError(string journalJson)
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "old");
        Write(payload, "managed", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("old"), "managed", Hash("new"))
        });
        Action interrupted = () => new InstallerTransactionExecutor(
            faultInjector: new ThrowingFaultInjector(afterOperation: 0, simulateTermination: true)
        ).Apply(game, payload, plan);
        interrupted.Should().Throw<SimulatedProcessTerminationException>();
        string journalPath = Path.Combine(game, ".smapi-installer/transactions", plan.TransactionId.ToString("N"), "journal.json");
        File.WriteAllText(journalPath, journalJson);

        Action recover = () => new InstallerTransactionExecutor().RecoverIncompleteTransactions(game);

        recover.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.RecoveryFailed);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("new");
    }

    [Test]
    public void Apply_WhenRollbackRuns_RemovesDirectoriesCreatedByTransaction()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(payload, "managed.txt", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("smapi-internal/new/deep/managed.txt", null, "managed.txt", Hash("new"))
        });
        InstallerTransactionExecutor executor = new(faultInjector: new ThrowingFaultInjector(afterOperation: 0, simulateTermination: false));

        Action action = () => executor.Apply(game, payload, plan);

        action.Should().Throw<InvalidOperationException>();
        Directory.Exists(Path.Combine(game, "smapi-internal")).Should().BeFalse();
    }

    [Test]
    public void Apply_WhenUnexpectedBackupCollisionAppears_PreservesOriginal()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "old");
        Write(payload, "managed.txt", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("old"), "managed.txt", Hash("new"))
        });
        CallbackFaultInjector collision = new(before: (transactionId, operationIndex) =>
        {
            string path = Path.Combine(game, ".smapi-installer/transactions", transactionId.ToString("N"), "backups", operationIndex.ToString("D8"));
            File.WriteAllText(path, "unexpected");
        });

        Action action = () => new InstallerTransactionExecutor(faultInjector: collision).Apply(game, payload, plan);

        action.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.RecoveryFailed);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("old");
    }

    [Test]
    public void Apply_RejectsSymlinkedDestinationParentWithoutTouchingTarget()
    {
        Assume.That(OperatingSystem.IsLinux(), Is.True);
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        string outside = this.CreateDirectory();
        Write(payload, "managed.txt", "new");
        Directory.CreateSymbolicLink(Path.Combine(game, "smapi-internal"), outside);
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("smapi-internal/managed.txt", null, "managed.txt", Hash("new"))
        });

        Action action = () => new InstallerTransactionExecutor().Apply(game, payload, plan);

        action.Should().Throw<InstallerTransactionException>()
            .Which.Code.Should().Be(TransactionErrorCode.UnsafePath);
        File.Exists(Path.Combine(outside, "managed.txt")).Should().BeFalse();
    }

    [Test]
    public void Apply_RejectsDuplicateAndCaseCollidingDestinations()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(payload, "one.txt", "one");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("smapi-internal/managed.txt", null, "one.txt", Hash("one")),
            WriteOperation("SMAPI-INTERNAL/managed.txt", null, "one.txt", Hash("one"))
        });

        Action action = () => new InstallerTransactionExecutor().Apply(game, payload, plan);

        action.Should().Throw<InstallerTransactionException>()
            .Which.Code.Should().Be(TransactionErrorCode.InvalidPlan);
    }

    [Test]
    public void Apply_RejectsUnknownWorkspaceCollision()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Directory.CreateDirectory(Path.Combine(game, ".smapi-installer"));
        Write(payload, "managed.txt", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", null, "managed.txt", Hash("new"))
        });

        Action action = () => new InstallerTransactionExecutor().Apply(game, payload, plan);

        action.Should().Throw<InstallerTransactionException>()
            .Which.Code.Should().Be(TransactionErrorCode.WorkspaceConflict);
        File.Exists(Path.Combine(game, "StardewModdingAPI.dll")).Should().BeFalse();
    }

    [Test]
    public void Apply_ReportsMonotonicBoundedProgress()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(payload, "managed.txt", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", null, "managed.txt", Hash("new"))
        });
        RecordingProgress progress = new();

        new InstallerTransactionExecutor(progress).Apply(game, payload, plan);

        progress.Items.Should().NotBeEmpty();
        progress.Items.Should().OnlyContain(item => item.CompletedOperations >= 0 && item.CompletedOperations <= item.TotalOperations && item.TotalOperations == 1);
        progress.Items.Last().Should().Be(new TransactionProgress(TransactionStage.Completed, 1, 1));
    }

    [Test]
    public void Apply_MultipleFilesWithSharedMissingParents_CommitsWithoutDirectoryOwnershipCollision()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(payload, "one", "one");
        Write(payload, "two", "two");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("smapi-internal/shared/deep/one", null, "one", Hash("one")),
            WriteOperation("smapi-internal/shared/deep/two", null, "two", Hash("two"))
        });

        new InstallerTransactionExecutor().Apply(game, payload, plan);

        File.ReadAllText(Path.Combine(game, "smapi-internal/shared/deep/one")).Should().Be("one");
        File.ReadAllText(Path.Combine(game, "smapi-internal/shared/deep/two")).Should().Be("two");
    }

    [Test]
    public void Recover_TornTrailingEvent_TruncatesUncommittedRecordAndRestoresOriginal()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "old");
        Write(payload, "managed", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("old"), "managed", Hash("new"))
        });
        Action interrupted = () => new InstallerTransactionExecutor(
            faultInjector: new ThrowingFaultInjector(afterOperation: 0, simulateTermination: true)
        ).Apply(game, payload, plan);
        interrupted.Should().Throw<SimulatedProcessTerminationException>();
        string events = Path.Combine(game, ".smapi-installer/transactions", plan.TransactionId.ToString("N"), "events.jsonl");
        File.AppendAllText(events, "{\"partial\":");

        new InstallerTransactionExecutor().RecoverIncompleteTransactions(game).Should().ContainSingle();

        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("old");
        File.ReadAllText(events).Should().EndWith("\n").And.NotContain("partial");
    }

    [Test]
    public void Recover_TamperedHashChainedEvent_FailsClosedAndPreservesResult()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "old");
        Write(payload, "managed", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("old"), "managed", Hash("new"))
        });
        Action interrupted = () => new InstallerTransactionExecutor(
            faultInjector: new ThrowingFaultInjector(afterOperation: 0, simulateTermination: true)
        ).Apply(game, payload, plan);
        interrupted.Should().Throw<SimulatedProcessTerminationException>();
        string events = Path.Combine(game, ".smapi-installer/transactions", plan.TransactionId.ToString("N"), "events.jsonl");
        string text = File.ReadAllText(events);
        File.WriteAllText(events, text.Replace("\"kind\":\"Intent\"", "\"kind\":\"Applied\"", StringComparison.Ordinal));

        Action recover = () => new InstallerTransactionExecutor().RecoverIncompleteTransactions(game);

        recover.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.RecoveryFailed);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("new");
    }

    [Test]
    public void Apply_PayloadHardlink_RejectsWithoutCreatingDestination()
    {
        Assume.That(OperatingSystem.IsLinux(), Is.True);
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(payload, "managed", "new");
        link(Path.Combine(payload, "managed"), Path.Combine(payload, "linked"))
            .Should().Be(0, $"link(2) failed with errno {Marshal.GetLastWin32Error()}");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", null, "managed", Hash("new"))
        });

        Action apply = () => new InstallerTransactionExecutor().Apply(game, payload, plan);

        apply.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.PayloadMismatch);
        File.Exists(Path.Combine(game, "StardewModdingAPI.dll")).Should().BeFalse();
    }

    [Test]
    public void Apply_GameRootPathSwappedDuringIntent_RemainsAnchoredToSelectedDirectory()
    {
        Assume.That(OperatingSystem.IsLinux(), Is.True);
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        string movedGame = Path.Combine(Path.GetDirectoryName(game)!, $"moved-{Guid.NewGuid():N}");
        string outside = this.CreateDirectory();
        Write(payload, "managed", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", null, "managed", Hash("new"))
        });
        CallbackFaultInjector swap = new(before: (_, _) =>
        {
            Directory.Move(game, movedGame);
            Directory.CreateSymbolicLink(game, outside);
            this.TemporaryDirectories.Add(movedGame);
        });

        new InstallerTransactionExecutor(faultInjector: swap).Apply(game, payload, plan);

        File.ReadAllText(Path.Combine(movedGame, "StardewModdingAPI.dll")).Should().Be("new");
        File.Exists(Path.Combine(outside, "StardewModdingAPI.dll")).Should().BeFalse();
    }

    [Test]
    public void Apply_ConcurrentExecutor_UsesKernelLockAndFailsBeforeSecondMutation()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(payload, "first", "first");
        Write(payload, "second", "second");
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        TransactionPlan firstPlan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", null, "first", Hash("first"))
        });
        TransactionPlan secondPlan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.xml", null, "second", Hash("second"))
        });
        CallbackFaultInjector blocking = new(before: (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        });
        Task<TransactionResult> first = Task.Run(() => new InstallerTransactionExecutor(faultInjector: blocking).Apply(game, payload, firstPlan));
        entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        Action concurrent = () => new InstallerTransactionExecutor().Apply(game, payload, secondPlan);
        concurrent.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.ConcurrentOperation);
        File.Exists(Path.Combine(game, "StardewModdingAPI.xml")).Should().BeFalse();
        release.Set();
        first.GetAwaiter().GetResult().Status.Should().Be(TransactionStatus.Committed);
    }

    [Test]
    public void Apply_PublicPlanCannotForgeReservedReceiptMutation()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(payload, "receipt", "attacker selected bytes");
        TransactionPlan injected = new(Guid.NewGuid(), new[]
        {
            WriteOperation(TransactionPlan.CoreReceiptRelativePath, null, "receipt", Hash("attacker selected bytes"))
        });

        Action apply = () => new InstallerTransactionExecutor().Apply(game, payload, injected);

        apply.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.InvalidPlan);
        Directory.Exists(Path.Combine(game, ".smapi-installer")).Should().BeFalse("plan validation must precede every side effect");
    }

    [Test]
    public void Apply_CoreStateCommitsRecoveryContentAndOwnershipTupleAtomically()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        new InstallerTransactionExecutor().RecoverIncompleteTransactions(game).Should().BeEmpty();
        Write(game, "StardewModdingAPI.dll", "old runtime");
        Write(game, TransactionPlan.CoreManifestRelativePath, "old manifest");
        Write(game, TransactionPlan.CoreReceiptRelativePath, "old receipt");
        Write(game, TransactionPlan.CoreRecoveryPointerRelativePath, "old pointer");
        foreach ((string path, string contents) in new[]
        {
            ("recovery/snapshot", "snapshot"),
            ("recovery/receipt", "old receipt"),
            ("recovery/manifest", "old manifest"),
            ("recovery/pointer", "old pointer"),
            ("recovery/file", "old runtime"),
            ("runtime", "new runtime"),
            ("manifest", "new manifest"),
            ("receipt", "new receipt"),
            ("pointer", "new pointer")
        })
        {
            Write(payload, path, contents);
        }
        Guid transactionId = Guid.NewGuid();
        string prefix = $".smapi-installer/recovery/generations/{transactionId:N}";
        TransactionPlan plan = TransactionPlan.CreateWithCoreState(
            transactionId,
            new[]
            {
                WriteOperation($"{prefix}/snapshot.json", null, "recovery/snapshot", Hash("snapshot"), 0x180),
                WriteOperation($"{prefix}/previous-receipt.json", null, "recovery/receipt", Hash("old receipt"), 0x180),
                WriteOperation($"{prefix}/previous-manifest.json", null, "recovery/manifest", Hash("old manifest"), 0x180),
                WriteOperation($"{prefix}/previous-pointer.json", null, "recovery/pointer", Hash("old pointer"), 0x180),
                WriteOperation($"{prefix}/files/00000000", null, "recovery/file", Hash("old runtime"), 0x180)
            },
            new[] { WriteOperation("StardewModdingAPI.dll", Hash("old runtime"), "runtime", Hash("new runtime"), 0x1ed) },
            WriteOperation(TransactionPlan.CoreManifestRelativePath, Hash("old manifest"), "manifest", Hash("new manifest"), 0x180),
            WriteOperation(TransactionPlan.CoreReceiptRelativePath, Hash("old receipt"), "receipt", Hash("new receipt"), 0x180),
            WriteOperation(TransactionPlan.CoreRecoveryPointerRelativePath, Hash("old pointer"), "pointer", Hash("new pointer"), 0x180)
        );

        new InstallerTransactionExecutor().Apply(game, payload, plan).Status.Should().Be(TransactionStatus.Committed);

        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("new runtime");
        File.ReadAllText(Path.Combine(game, TransactionPlan.CoreManifestRelativePath)).Should().Be("new manifest");
        File.ReadAllText(Path.Combine(game, TransactionPlan.CoreReceiptRelativePath)).Should().Be("new receipt");
        File.ReadAllText(Path.Combine(game, TransactionPlan.CoreRecoveryPointerRelativePath)).Should().Be("new pointer");
        File.ReadAllText(Path.Combine(game, $"{prefix}/files/00000000")).Should().Be("old runtime");
    }

    [Test]
    public void Apply_CoreStateFailureAfterPointer_RestoresPreviousTupleAndRemovesGeneration()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        new InstallerTransactionExecutor().RecoverIncompleteTransactions(game).Should().BeEmpty();
        Write(game, "StardewModdingAPI.dll", "old runtime");
        Write(game, TransactionPlan.CoreManifestRelativePath, "old manifest");
        Write(game, TransactionPlan.CoreReceiptRelativePath, "old receipt");
        Write(game, TransactionPlan.CoreRecoveryPointerRelativePath, "old pointer");
        Write(payload, "snapshot", "snapshot");
        Write(payload, "runtime", "new runtime");
        Write(payload, "manifest", "new manifest");
        Write(payload, "receipt", "new receipt");
        Write(payload, "pointer", "new pointer");
        Guid transactionId = Guid.NewGuid();
        string prefix = $".smapi-installer/recovery/generations/{transactionId:N}";
        TransactionPlan plan = TransactionPlan.CreateWithCoreState(
            transactionId,
            new[] { WriteOperation($"{prefix}/snapshot.json", null, "snapshot", Hash("snapshot"), 0x180) },
            new[] { WriteOperation("StardewModdingAPI.dll", Hash("old runtime"), "runtime", Hash("new runtime")) },
            WriteOperation(TransactionPlan.CoreManifestRelativePath, Hash("old manifest"), "manifest", Hash("new manifest"), 0x180),
            WriteOperation(TransactionPlan.CoreReceiptRelativePath, Hash("old receipt"), "receipt", Hash("new receipt"), 0x180),
            WriteOperation(TransactionPlan.CoreRecoveryPointerRelativePath, Hash("old pointer"), "pointer", Hash("new pointer"), 0x180)
        );
        InstallerTransactionExecutor executor = new(faultInjector: new ThrowingFaultInjector(afterOperation: plan.Operations.Count - 1));

        Action apply = () => executor.Apply(game, payload, plan);

        apply.Should().Throw<InvalidOperationException>();
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("old runtime");
        File.ReadAllText(Path.Combine(game, TransactionPlan.CoreManifestRelativePath)).Should().Be("old manifest");
        File.ReadAllText(Path.Combine(game, TransactionPlan.CoreReceiptRelativePath)).Should().Be("old receipt");
        File.ReadAllText(Path.Combine(game, TransactionPlan.CoreRecoveryPointerRelativePath)).Should().Be("old pointer");
        Directory.Exists(Path.Combine(game, prefix)).Should().BeFalse();
    }

    [TestCase(TransactionPlan.CoreManifestRelativePath)]
    [TestCase(TransactionPlan.CoreReceiptRelativePath)]
    [TestCase(TransactionPlan.CoreRecoveryPointerRelativePath)]
    [TestCase(".smapi-installer/recovery/generations/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/snapshot.json")]
    public void Apply_OrdinaryPlanCannotForgeReservedCoreState(string destination)
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(payload, "state", "forged");
        TransactionPlan plan = new(Guid.NewGuid(), new[] { WriteOperation(destination, null, "state", Hash("forged")) });

        Action apply = () => new InstallerTransactionExecutor().Apply(game, payload, plan);

        apply.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.InvalidPlan);
        Directory.Exists(Path.Combine(game, ".smapi-installer")).Should().BeFalse();
    }

    [Test]
    public void CreateWithCoreState_RejectsNonCanonicalRecoveryPublicationOrder()
    {
        Guid transactionId = Guid.NewGuid();
        string prefix = $".smapi-installer/recovery/generations/{transactionId:N}";

        Action create = () => TransactionPlan.CreateWithCoreState(
            transactionId,
            new[]
            {
                WriteOperation($"{prefix}/files/00000000", null, "file", Hash("file")),
                WriteOperation($"{prefix}/snapshot.json", null, "snapshot", Hash("snapshot"))
            },
            Array.Empty<TransactionFileOperation>(),
            null,
            null,
            WriteOperation(TransactionPlan.CoreRecoveryPointerRelativePath, null, "pointer", Hash("pointer"))
        );

        create.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Apply_RetainsAtMostBoundedFinalTransactionRecords()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(payload, "managed", "same");
        for (int index = 0; index < 19; index++)
        {
            TransactionPlan plan = new(Guid.NewGuid(), new[]
            {
                WriteOperation($"smapi-internal/retention/file-{index:D2}", null, "managed", Hash("same"))
            });
            new InstallerTransactionExecutor().Apply(game, payload, plan);
        }

        string transactions = Path.Combine(game, ".smapi-installer/transactions");
        Directory.EnumerateDirectories(transactions).Should().HaveCount(16);
        Directory.EnumerateDirectories(transactions).Should().OnlyContain(path =>
            Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).OrderBy(name => name)
                .SequenceEqual(new[] { "events.jsonl", "journal.json" })
        );
    }

    [TestCase(TransactionSetupBoundary.PreparationDirectoryCreated, 0)]
    [TestCase(TransactionSetupBoundary.PayloadDirectoriesCreated, 0)]
    [TestCase(TransactionSetupBoundary.ImmutablePlanCreated, 0)]
    [TestCase(TransactionSetupBoundary.CreationEventCreated, 0)]
    [TestCase(TransactionSetupBoundary.TransactionPublished, 1)]
    public void Recover_ProcessStopsAtEverySetupBoundary_CleansOrRollsBackWithoutBlocking(
        TransactionSetupBoundary boundary,
        int expectedRecoveryCount
    )
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(payload, "managed", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", null, "managed", Hash("new"))
        });
        InstallerTransactionExecutor crashing = new(faultInjector: new SetupTerminationFaultInjector(boundary));

        Action interrupted = () => crashing.Apply(game, payload, plan);
        interrupted.Should().Throw<SimulatedProcessTerminationException>();

        new InstallerTransactionExecutor().RecoverIncompleteTransactions(game).Should().HaveCount(expectedRecoveryCount);
        Directory.EnumerateDirectories(Path.Combine(game, ".smapi-installer/transactions"))
            .Select(Path.GetFileName)
            .Should().NotContain(name => name!.StartsWith("preparing-", StringComparison.Ordinal));
        File.Exists(Path.Combine(game, "StardewModdingAPI.dll")).Should().BeFalse();
    }

    private string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-installer-core-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        this.TemporaryDirectories.Add(path);
        return path;
    }

    private static void Write(string root, string relativePath, string contents)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string Hash(string text)
    {
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static void DowngradeJournalToSchema2(string gameRoot, Guid transactionId)
    {
        string transaction = Path.Combine(gameRoot, ".smapi-installer/transactions", transactionId.ToString("N"));
        string journalPath = Path.Combine(transaction, "journal.json");
        JsonObject journal = JsonNode.Parse(File.ReadAllText(journalPath))!.AsObject();
        journal["schemaVersion"] = 2;
        journal.Remove("coreGenerationId");
        journal.Remove("coreRecoveryOperationCount");
        journal.Remove("coreRecoveryContentCount");
        journal.Remove("hasCoreAuthorizedManifestMutation");
        journal.Remove("hasCoreAuthorizedRecoveryPointerMutation");
        string journalText = journal.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(journalPath, journalText);
        string planSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(journalText))).ToLowerInvariant();

        string eventsPath = Path.Combine(transaction, "events.jsonl");
        string? previous = null;
        List<string> rewritten = new();
        foreach (string line in File.ReadAllLines(eventsPath).Where(line => line.Length > 0))
        {
            JsonObject prior = JsonNode.Parse(line)!.AsObject();
            JsonObject unsigned = new()
            {
                ["schemaVersion"] = 1,
                ["sequence"] = prior["sequence"]!.GetValue<int>(),
                ["kind"] = prior["kind"]!.GetValue<string>(),
                ["operationIndex"] = prior["operationIndex"]?.DeepClone(),
                ["planSha256"] = planSha256,
                ["previousEventSha256"] = previous
            };
            string eventSha256 = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(unsigned.ToJsonString()))
            ).ToLowerInvariant();
            unsigned["eventSha256"] = eventSha256;
            rewritten.Add(unsigned.ToJsonString());
            previous = eventSha256;
        }
        File.WriteAllText(eventsPath, string.Join('\n', rewritten) + "\n");
    }

    private static TransactionFileOperation WriteOperation(string destination, string? existingHash, string source, string resultHash, int? mode = null)
    {
        return new(TransactionOperationKind.WriteFile, destination, existingHash, source, resultHash, mode);
    }

    private static TransactionFileOperation RemoveOperation(string destination, string existingHash)
    {
        return new(TransactionOperationKind.RemoveFile, destination, existingHash);
    }

    private sealed class RecordingProgress : ITransactionProgressSink
    {
        public List<TransactionProgress> Items { get; } = new();

        public void Report(TransactionProgress progress) => this.Items.Add(progress);
    }

    private sealed class ThrowingFaultInjector : ITransactionFaultInjector
    {
        private readonly int? BeforeOperation;
        private readonly int? AfterOperation;
        private readonly bool SimulateTermination;

        public ThrowingFaultInjector(int? beforeOperation = null, int? afterOperation = null, bool simulateTermination = false)
        {
            this.BeforeOperation = beforeOperation;
            this.AfterOperation = afterOperation;
            this.SimulateTermination = simulateTermination;
        }

        public void BeforeMutation(Guid transactionId, int operationIndex)
        {
            if (this.BeforeOperation == operationIndex)
                this.Throw();
        }

        public void AfterMutation(Guid transactionId, int operationIndex)
        {
            if (this.AfterOperation == operationIndex)
                this.Throw();
        }

        private void Throw()
        {
            if (this.SimulateTermination)
                throw new SimulatedProcessTerminationException("Injected process termination.");
            throw new InvalidOperationException("Injected transaction failure.");
        }
    }

    private sealed class CallbackFaultInjector : ITransactionFaultInjector
    {
        private readonly Action<Guid, int>? Before;
        private readonly Action<Guid, int>? After;

        public CallbackFaultInjector(Action<Guid, int>? before = null, Action<Guid, int>? after = null)
        {
            this.Before = before;
            this.After = after;
        }

        public void BeforeMutation(Guid transactionId, int operationIndex) => this.Before?.Invoke(transactionId, operationIndex);
        public void AfterMutation(Guid transactionId, int operationIndex) => this.After?.Invoke(transactionId, operationIndex);
    }

    private sealed class SetupTerminationFaultInjector : ITransactionFaultInjector
    {
        private readonly TransactionSetupBoundary Boundary;

        public SetupTerminationFaultInjector(TransactionSetupBoundary boundary)
        {
            this.Boundary = boundary;
        }

        public void AtSetupBoundary(Guid transactionId, TransactionSetupBoundary boundary)
        {
            if (boundary == this.Boundary)
                throw new SimulatedProcessTerminationException($"Injected setup termination at {boundary}.");
        }

        public void BeforeMutation(Guid transactionId, int operationIndex) { }

        public void AfterMutation(Guid transactionId, int operationIndex) { }
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int link(string oldPath, string newPath);
}
