using System.Security.Cryptography;
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
        File.ReadAllText(Path.Combine(game, ".smapi-installer/transactions", plan.TransactionId.ToString("N"), "journal.json"))
            .Should().Contain("\"status\": \"Committed\"");
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
        File.ReadAllText(Path.Combine(game, ".smapi-installer/transactions", plan.TransactionId.ToString("N"), "journal.json"))
            .Should().Contain("\"status\": \"RolledBack\"");
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
}
