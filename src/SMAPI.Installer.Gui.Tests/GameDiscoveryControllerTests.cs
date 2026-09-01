using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Tests;

[NonParallelizable]
internal sealed class GameDiscoveryControllerTests
{
    [Test]
    public async Task DiscoveryRepresentsZeroOneAndManyWithSafeSelectionRules()
    {
        foreach (ProtocolGameCandidate[] expected in new[]
        {
            Array.Empty<ProtocolGameCandidate>(),
            new[] { Candidate("one", LinuxGameFolderStatus.Valid) },
            new[] { Candidate("invalid", LinuxGameFolderStatus.MissingLauncher) },
            new[]
            {
                Candidate("valid", LinuxGameFolderStatus.Valid),
                Candidate("invalid", LinuxGameFolderStatus.MissingLauncher)
            }
        })
        {
            FakeVerifiedSession session = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>(expected) };
            GameDiscoveryController controller = new(session);

            await controller.DiscoverAsync();

            GameDiscoverySnapshot snapshot = controller.Snapshot;
            snapshot.State.Should().Be(expected.Length == 0 ? GameDiscoveryState.NoCandidates : GameDiscoveryState.Ready);
            snapshot.Candidates.Should().Equal(expected);
            ProtocolGameCandidate? expectedSelection = expected is [{ State: LinuxGameFolderStatus.Valid }]
                ? expected[0]
                : null;
            snapshot.SelectedCandidate.Should().BeSameAs(expectedSelection);
            snapshot.CanContinue.Should().Be(expectedSelection is not null);
            snapshot.CanBrowse.Should().BeTrue();
            await controller.DisposeAsync();
            session.DisposeCalls.Should().Be(1);
        }
    }

    [Test]
    public async Task ExactExplicitSelectionAllowsOnlyAValidCandidateToContinue()
    {
        ProtocolGameCandidate valid = Candidate("valid", LinuxGameFolderStatus.Valid);
        ProtocolGameCandidate invalid = Candidate("invalid", LinuxGameFolderStatus.UnsafeLauncher);
        FakeVerifiedSession session = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid, invalid]) };
        await using GameDiscoveryController controller = new(session);
        await controller.DiscoverAsync();

        controller.SelectCandidate(invalid);
        controller.Snapshot.SelectedCandidate.Should().BeSameAs(invalid);
        controller.Snapshot.CanContinue.Should().BeFalse();

        Action forged = () => controller.SelectCandidate(invalid with { });
        forged.Should().Throw<ArgumentException>().WithMessage("*exact current discovery result*");

        controller.SelectCandidate(valid);
        controller.Snapshot.SelectedCandidate.Should().BeSameAs(valid);
        controller.Snapshot.CanContinue.Should().BeTrue();
    }

    [Test]
    public async Task ManualValidationPublishesTypedInvalidThenValidResultWithoutMutationAuthority()
    {
        ProtocolGameCandidate invalid = Candidate("manual", LinuxGameFolderStatus.UnsupportedGameVersion);
        ProtocolGameCandidate valid = Candidate("manual", LinuxGameFolderStatus.Valid);
        Queue<ProtocolGameCandidate> results = new([invalid, valid]);
        FakeVerifiedSession session = new() { Validation = (_, _) => Task.FromResult(results.Dequeue()) };
        await using GameDiscoveryController controller = new(session);

        await controller.ValidateManualAsync("/games/manual");
        controller.Snapshot.State.Should().Be(GameDiscoveryState.ManualInvalid);
        controller.Snapshot.SelectedCandidate.Should().BeSameAs(invalid);
        controller.Snapshot.CanContinue.Should().BeFalse();

        await controller.ValidateManualAsync("/games/manual");
        controller.Snapshot.State.Should().Be(GameDiscoveryState.ManualValid);
        controller.Snapshot.Candidates.Should().ContainSingle().Which.Should().BeSameAs(valid);
        controller.Snapshot.SelectedCandidate.Should().BeSameAs(valid);
        controller.Snapshot.CanContinue.Should().BeTrue();
        session.ValidatedPaths.Should().Equal("/games/manual", "/games/manual");
    }

    [Test]
    public async Task CancellationSettlesBeforeRetryAndLateDiscoveryCannotPublish()
    {
        TaskCompletionSource started = NewCompletion();
        FakeVerifiedSession session = new()
        {
            Discovery = async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Array.Empty<ProtocolGameCandidate>();
            }
        };
        await using GameDiscoveryController controller = new(session);
        Task discovery = controller.DiscoverAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await controller.CancelAsync();
        await discovery;

        controller.Snapshot.State.Should().Be(GameDiscoveryState.Cancelled);
        controller.Snapshot.CanRetry.Should().BeTrue();
        controller.Snapshot.Candidates.Should().BeEmpty();
        session.Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("retry", LinuxGameFolderStatus.Valid)]);
        await controller.DiscoverAsync();
        controller.Snapshot.State.Should().Be(GameDiscoveryState.Ready);
        session.DiscoverCalls.Should().Be(2);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AcceptedCancellationWinsTheFinalSuccessCommitRace(bool discovery)
    {
        ProtocolGameCandidate valid = Candidate("late-valid", LinuxGameFolderStatus.Valid);
        TaskCompletionSource reachedCommit = NewCompletion();
        TaskCompletionSource releaseCommit = NewCompletion();
        FakeVerifiedSession session = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]),
            Validation = (_, _) => Task.FromResult(valid)
        };
        await using GameDiscoveryController controller = new(session);
        Action hook = () =>
        {
            reachedCommit.TrySetResult();
            releaseCommit.Task.GetAwaiter().GetResult();
        };
        if (discovery)
            controller.BeforeDiscoveryCommitForTesting = hook;
        else
            controller.BeforeManualValidationCommitForTesting = hook;

        Task operation = discovery
            ? controller.DiscoverAsync()
            : controller.ValidateManualAsync("/games/late-valid");
        await reachedCommit.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task cancellation = controller.CancelAsync();
        await WaitUntilAsync(() => controller.Snapshot.State == GameDiscoveryState.Cancelling);
        releaseCommit.TrySetResult();
        await Task.WhenAll(operation, cancellation).WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(GameDiscoveryState.Cancelled);
        controller.Snapshot.SelectedCandidate.Should().BeNull();
        controller.Snapshot.CanContinue.Should().BeFalse();
        controller.Snapshot.CanRetry.Should().BeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CallerCancellationWinsTheFinalSuccessCommitRace(bool discovery)
    {
        ProtocolGameCandidate valid = Candidate("late-valid", LinuxGameFolderStatus.Valid);
        TaskCompletionSource reachedCommit = NewCompletion();
        TaskCompletionSource releaseCommit = NewCompletion();
        FakeVerifiedSession session = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]),
            Validation = (_, _) => Task.FromResult(valid)
        };
        await using GameDiscoveryController controller = new(session);
        using CancellationTokenSource callerCancellation = new();
        Action hook = () =>
        {
            reachedCommit.TrySetResult();
            releaseCommit.Task.GetAwaiter().GetResult();
        };
        if (discovery)
            controller.BeforeDiscoveryCommitForTesting = hook;
        else
            controller.BeforeManualValidationCommitForTesting = hook;

        Task operation = discovery
            ? controller.DiscoverAsync(callerCancellation.Token)
            : controller.ValidateManualAsync("/games/late-valid", callerCancellation.Token);
        await reachedCommit.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerCancellation.Cancel();
        releaseCommit.TrySetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(GameDiscoveryState.Cancelled);
        controller.Snapshot.SelectedCandidate.Should().BeNull();
        controller.Snapshot.CanContinue.Should().BeFalse();
        controller.Snapshot.CanRetry.Should().BeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SessionFaultWinsTheFinalSuccessCommitRace(bool discovery)
    {
        ProtocolGameCandidate valid = Candidate("late-valid", LinuxGameFolderStatus.Valid);
        TaskCompletionSource reachedCommit = NewCompletion();
        TaskCompletionSource releaseCommit = NewCompletion();
        FakeVerifiedSession session = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]),
            Validation = (_, _) => Task.FromResult(valid)
        };
        await using GameDiscoveryController controller = new(session);
        Action hook = () =>
        {
            reachedCommit.TrySetResult();
            releaseCommit.Task.GetAwaiter().GetResult();
        };
        if (discovery)
            controller.BeforeDiscoveryCommitForTesting = hook;
        else
            controller.BeforeManualValidationCommitForTesting = hook;

        Task operation = discovery
            ? controller.DiscoverAsync()
            : controller.ValidateManualAsync("/games/late-valid");
        await reachedCommit.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.Fault.TrySetResult(new InstallerProtocolClientException("late fault"));
        await WaitUntilAsync(() => controller.Snapshot.State == GameDiscoveryState.SessionFaulted);
        releaseCommit.TrySetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(GameDiscoveryState.SessionFaulted);
        controller.Snapshot.SelectedCandidate.Should().BeNull();
        controller.Snapshot.CanContinue.Should().BeFalse();
        controller.Snapshot.CanRetry.Should().BeFalse();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AcceptedCancellationWinsAfterSuccessCommitBeforeFinalization(bool discovery)
    {
        ProtocolGameCandidate valid = Candidate("late-valid", LinuxGameFolderStatus.Valid);
        TaskCompletionSource reachedFinalization = NewCompletion();
        TaskCompletionSource releaseFinalization = NewCompletion();
        FakeVerifiedSession session = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]),
            Validation = (_, _) => Task.FromResult(valid)
        };
        await using GameDiscoveryController controller = new(session)
        {
            BeforeOperationCompletionForTesting = () =>
            {
                reachedFinalization.TrySetResult();
                releaseFinalization.Task.GetAwaiter().GetResult();
            }
        };

        Task operation = discovery
            ? controller.DiscoverAsync()
            : controller.ValidateManualAsync("/games/late-valid");
        await reachedFinalization.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task cancellation = controller.CancelAsync();
        await WaitUntilAsync(() => controller.Snapshot.State == GameDiscoveryState.Cancelling);
        releaseFinalization.TrySetResult();
        await Task.WhenAll(operation, cancellation).WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(GameDiscoveryState.Cancelled);
        controller.Snapshot.SelectedCandidate.Should().BeNull();
        controller.Snapshot.CanContinue.Should().BeFalse();
        controller.Snapshot.CanRetry.Should().BeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CallerCancellationWinsAfterSuccessCommitBeforeFinalization(bool discovery)
    {
        ProtocolGameCandidate valid = Candidate("late-valid", LinuxGameFolderStatus.Valid);
        TaskCompletionSource reachedFinalization = NewCompletion();
        TaskCompletionSource releaseFinalization = NewCompletion();
        FakeVerifiedSession session = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]),
            Validation = (_, _) => Task.FromResult(valid)
        };
        await using GameDiscoveryController controller = new(session)
        {
            BeforeOperationCompletionForTesting = () =>
            {
                reachedFinalization.TrySetResult();
                releaseFinalization.Task.GetAwaiter().GetResult();
            }
        };
        using CancellationTokenSource callerCancellation = new();

        Task operation = discovery
            ? controller.DiscoverAsync(callerCancellation.Token)
            : controller.ValidateManualAsync("/games/late-valid", callerCancellation.Token);
        await reachedFinalization.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerCancellation.Cancel();
        releaseFinalization.TrySetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(GameDiscoveryState.Cancelled);
        controller.Snapshot.SelectedCandidate.Should().BeNull();
        controller.Snapshot.CanContinue.Should().BeFalse();
        controller.Snapshot.CanRetry.Should().BeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SessionFaultWinsAConcurrentFailureOutcome(bool discovery)
    {
        TaskCompletionSource reachedOutcome = NewCompletion();
        TaskCompletionSource releaseOutcome = NewCompletion();
        FakeVerifiedSession session = new()
        {
            Discovery = _ => throw new InvalidOperationException("discovery failed"),
            Validation = (_, _) => throw new InvalidOperationException("validation failed")
        };
        await using GameDiscoveryController controller = new(session)
        {
            BeforeOutcomeCommitForTesting = () =>
            {
                reachedOutcome.TrySetResult();
                releaseOutcome.Task.GetAwaiter().GetResult();
            }
        };

        Task operation = discovery
            ? controller.DiscoverAsync()
            : controller.ValidateManualAsync("/games/failure");
        await reachedOutcome.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.Fault.TrySetResult(new InstallerProtocolClientException("late fault"));
        await WaitUntilAsync(() => controller.Snapshot.State == GameDiscoveryState.SessionFaulted);
        releaseOutcome.TrySetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(GameDiscoveryState.SessionFaulted);
        controller.Snapshot.SelectedCandidate.Should().BeNull();
        controller.Snapshot.CanContinue.Should().BeFalse();
        controller.Snapshot.CanRetry.Should().BeFalse();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CallerCancellationWinsAConcurrentFailureOutcome(bool discovery)
    {
        TaskCompletionSource reachedOutcome = NewCompletion();
        TaskCompletionSource releaseOutcome = NewCompletion();
        FakeVerifiedSession session = new()
        {
            Discovery = _ => throw new InvalidOperationException("discovery failed"),
            Validation = (_, _) => throw new InvalidOperationException("validation failed")
        };
        await using GameDiscoveryController controller = new(session)
        {
            BeforeOutcomeCommitForTesting = () =>
            {
                reachedOutcome.TrySetResult();
                releaseOutcome.Task.GetAwaiter().GetResult();
            }
        };
        using CancellationTokenSource callerCancellation = new();

        Task operation = discovery
            ? controller.DiscoverAsync(callerCancellation.Token)
            : controller.ValidateManualAsync("/games/failure", callerCancellation.Token);
        await reachedOutcome.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerCancellation.Cancel();
        releaseOutcome.TrySetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(GameDiscoveryState.Cancelled);
        controller.Snapshot.SelectedCandidate.Should().BeNull();
        controller.Snapshot.CanContinue.Should().BeFalse();
        controller.Snapshot.CanRetry.Should().BeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task DisposalWinsAfterSuccessCommitBeforeFinalization(bool discovery)
    {
        ProtocolGameCandidate valid = Candidate("late-valid", LinuxGameFolderStatus.Valid);
        TaskCompletionSource reachedFinalization = NewCompletion();
        TaskCompletionSource releaseFinalization = NewCompletion();
        FakeVerifiedSession session = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]),
            Validation = (_, _) => Task.FromResult(valid)
        };
        await using GameDiscoveryController controller = new(session)
        {
            BeforeOperationCompletionForTesting = () =>
            {
                reachedFinalization.TrySetResult();
                releaseFinalization.Task.GetAwaiter().GetResult();
            }
        };

        Task operation = discovery
            ? controller.DiscoverAsync()
            : controller.ValidateManualAsync("/games/late-valid");
        await reachedFinalization.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ValueTask disposal = controller.DisposeAsync();
        controller.Snapshot.State.Should().Be(GameDiscoveryState.Cancelling);
        releaseFinalization.TrySetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(2));
        await disposal.AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(GameDiscoveryState.Disposed);
        controller.Snapshot.SelectedCandidate.Should().BeNull();
        controller.Snapshot.CanContinue.Should().BeFalse();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ManualValidationKeepsOneBoundedSlotAlongsideDiscoveredCandidates()
    {
        ProtocolGameCandidate[] discovered = Enumerable.Range(0, ProtocolJsonSerializer.MaxGameCandidates)
            .Select(index => Candidate($"detected-{index:D2}", LinuxGameFolderStatus.Valid))
            .ToArray();
        FakeVerifiedSession session = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>(discovered),
            Validation = (path, _) => Task.FromResult(new ProtocolGameCandidate(
                path,
                LinuxGameFolderStatus.Valid,
                "Manually selected Stardew Valley"
            ))
        };
        await using GameDiscoveryController controller = new(session);
        await controller.DiscoverAsync();

        for (int index = 0; index < ProtocolJsonSerializer.MaxGameCandidates + 16; index++)
        {
            string path = $"/games/manual-{index:D2}";
            await controller.ValidateManualAsync(path);
            GameDiscoverySnapshot snapshot = controller.Snapshot;
            snapshot.Candidates.Should().HaveCount(ProtocolJsonSerializer.MaxGameCandidates);
            snapshot.Candidates.Should().ContainSingle(candidate => candidate.CanonicalPath == path);
            snapshot.SelectedCandidate.Should().BeSameAs(snapshot.Candidates[^1]);
            snapshot.CanContinue.Should().BeTrue();
            if (index > 0)
                snapshot.Candidates.Should().NotContain(candidate => candidate.CanonicalPath == $"/games/manual-{index - 1:D2}");
        }
    }

    [Test]
    public async Task SessionFaultWinsActiveValidationAndLaterFaultRevokesSelection()
    {
        TaskCompletionSource<ProtocolGameCandidate> validating = NewCompletion<ProtocolGameCandidate>();
        FakeVerifiedSession activeSession = new() { Validation = (_, _) => validating.Task };
        await using (GameDiscoveryController controller = new(activeSession))
        {
            Task operation = controller.ValidateManualAsync("/games/private-name");
            activeSession.Fault.TrySetResult(new InstallerProtocolClientException("private /home/alice SECRET"));
            validating.TrySetCanceled();
            await operation;

            controller.Snapshot.State.Should().Be(GameDiscoveryState.SessionFaulted);
            controller.Snapshot.SelectedCandidate.Should().BeNull();
            controller.Snapshot.CanRetry.Should().BeFalse();
            controller.Snapshot.ToString().Should().NotContain("alice").And.NotContain("SECRET");
        }

        ProtocolGameCandidate valid = Candidate("ready", LinuxGameFolderStatus.Valid);
        FakeVerifiedSession laterSession = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]) };
        await using GameDiscoveryController later = new(laterSession);
        await later.DiscoverAsync();
        later.SelectCandidate(valid);
        later.Snapshot.CanContinue.Should().BeTrue();

        laterSession.Fault.TrySetResult(new InstallerProtocolClientException("late"));
        await WaitUntilAsync(() => later.Snapshot.State == GameDiscoveryState.SessionFaulted);
        later.Snapshot.SelectedCandidate.Should().BeNull();
        later.Snapshot.CanContinue.Should().BeFalse();
    }

    [Test]
    public async Task MalformedOrDuplicateSessionResultsFailClosed()
    {
        ProtocolGameCandidate duplicate = Candidate("same", LinuxGameFolderStatus.Valid);
        FakeVerifiedSession session = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([duplicate, duplicate with { }])
        };
        await using GameDiscoveryController controller = new(session);

        await controller.DiscoverAsync();

        controller.Snapshot.State.Should().Be(GameDiscoveryState.Failed);
        controller.Snapshot.Candidates.Should().BeEmpty();
        controller.Snapshot.CanContinue.Should().BeFalse();
    }

    internal static ProtocolGameCandidate Candidate(string suffix, LinuxGameFolderStatus status)
    {
        return new($"/games/{suffix}", status, $"Stardew Valley {suffix}");
    }

    internal static ProtocolReleaseIdentity Release()
    {
        return new(
            "https://github.com/4eh5xitv6787h645ebv/SMAPI",
            "fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2",
            "4.5.4-unofficial.4eh5xitv6787h645ebv.linux.alpha.2",
            "SMAPI-linux.zip",
            new string('1', 40),
            new string('2', 40),
            new string('a', 64),
            10,
            "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2",
            "Release",
            "linux-x64"
        );
    }

    internal static TaskCompletionSource NewCompletion()
    {
        return new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal static TaskCompletionSource<T> NewCompletion<T>()
    {
        return new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected controller state was not reached.");
            await Task.Delay(10);
        }
    }

    internal sealed class FakeVerifiedSession : IVerifiedInstallerSession
    {
        public ProtocolReleaseIdentity Release { get; } = GameDiscoveryControllerTests.Release();
        public TaskCompletionSource<InstallerProtocolClientException> Fault { get; } = NewCompletion<InstallerProtocolClientException>();
        public Func<CancellationToken, Task<IReadOnlyList<ProtocolGameCandidate>>> Discovery { get; set; } =
            _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>(Array.Empty<ProtocolGameCandidate>());
        public Func<string, CancellationToken, Task<ProtocolGameCandidate>> Validation { get; set; } =
            (path, _) => Task.FromResult(Candidate(path.GetHashCode(StringComparison.Ordinal).ToString(), LinuxGameFolderStatus.Valid));
        public List<string> ValidatedPaths { get; } = [];
        public int DiscoverCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;

        public Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
        {
            this.DiscoverCalls++;
            return this.Discovery(cancellationToken);
        }

        public Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default)
        {
            this.ValidatedPaths.Add(canonicalPath);
            return this.Validation(canonicalPath, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
