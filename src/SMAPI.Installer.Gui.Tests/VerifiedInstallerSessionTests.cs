using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Tests;

[NonParallelizable]
internal sealed class VerifiedInstallerSessionTests
{
    [Test]
    public async Task BindAcceptsOnlyTheExactValidReferenceIssuedByTheBackend()
    {
        ProtocolGameCandidate valid = Candidate("exact", LinuxGameFolderStatus.Valid);
        RecordingClient client = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]) };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate issued = (await session.DiscoverGamesAsync()).Single();

        Action forged = () => session.BindToGame(issued with { });
        forged.Should().Throw<ArgumentException>().WithMessage("*exact valid result*");

        await using IPlanInspectionSession bound = session.BindToGame(issued);
        bound.Game.CanonicalPath.Should().Be(valid.CanonicalPath);
        client.DisposeCalls.Should().Be(0);
    }

    [Test]
    public async Task BindRejectsAnExactIssuedInvalidCandidate()
    {
        ProtocolGameCandidate invalid = Candidate("invalid", LinuxGameFolderStatus.UnsafeLauncher);
        RecordingClient client = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([invalid]) };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate issued = (await session.DiscoverGamesAsync()).Single();

        Action bind = () => session.BindToGame(issued);

        bind.Should().Throw<ArgumentException>().WithMessage("*exact valid result*");
    }

    [Test]
    public async Task BindRejectsWhileADiscoveryCommandIsActive()
    {
        ProtocolGameCandidate valid = Candidate("existing", LinuxGameFolderStatus.Valid);
        TaskCompletionSource discoveryStarted = NewCompletion();
        TaskCompletionSource<IReadOnlyList<ProtocolGameCandidate>> releaseDiscovery = NewCompletion<IReadOnlyList<ProtocolGameCandidate>>();
        int calls = 0;
        RecordingClient client = new()
        {
            Discovery = _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    return Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]);
                discoveryStarted.TrySetResult();
                return releaseDiscovery.Task;
            }
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate issued = (await session.DiscoverGamesAsync()).Single();
        Task active = session.DiscoverGamesAsync();
        await discoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Action bind = () => session.BindToGame(issued);
        bind.Should().Throw<InvalidOperationException>().WithMessage("*still active*");

        releaseDiscovery.SetResult([]);
        await active.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task BindRejectsACompletedSessionFault()
    {
        ProtocolGameCandidate valid = Candidate("faulted", LinuxGameFolderStatus.Valid);
        RecordingClient client = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]) };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate issued = (await session.DiscoverGamesAsync()).Single();
        client.Fault.SetResult(new InstallerProtocolClientException("synthetic fault"));

        Action bind = () => session.BindToGame(issued);

        bind.Should().Throw<InvalidOperationException>().WithMessage("*already faulted*");
    }

    [Test]
    public async Task BindIsOneTimeAndRevokesDiscoveryAndValidation()
    {
        ProtocolGameCandidate valid = Candidate("bound", LinuxGameFolderStatus.Valid);
        RecordingClient client = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]) };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate issued = (await session.DiscoverGamesAsync()).Single();
        await using IPlanInspectionSession bound = session.BindToGame(issued);

        Action secondBind = () => session.BindToGame(issued);
        secondBind.Should().Throw<InvalidOperationException>().WithMessage("*already bound*");
        Func<Task> discover = () => session.DiscoverGamesAsync();
        await discover.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already bound*");
        Func<Task> validate = () => session.ValidateGameAsync(valid.CanonicalPath);
        await validate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already bound*");
        client.DiscoverCalls.Should().Be(1);
        client.ValidateCalls.Should().Be(0);
    }

    [Test]
    public async Task BoundOwnerPreservesReleaseAndGamePresentationAndFixesTheInspectPath()
    {
        ProtocolReleaseIdentity release = CreateRelease();
        ProtocolGameCandidate valid = Candidate("selected", LinuxGameFolderStatus.Valid);
        InstallerReadOnlyPlanResult expected = Plan(InstallerOperation.Backup);
        RecordingClient client = new()
        {
            Validation = (_, _) => Task.FromResult(valid),
            Inspection = (_, _, _) => Task.FromResult(expected)
        };
        VerifiedInstallerSession session = new(release, client);
        ProtocolGameCandidate issued = await session.ValidateGameAsync(valid.CanonicalPath);
        await using IPlanInspectionSession bound = session.BindToGame(issued);

        InstallerReadOnlyPlanResult actual = await bound.InspectPlanAsync(InstallerOperation.Backup);

        actual.Should().BeSameAs(expected);
        bound.Release.Should().BeSameAs(release);
        bound.Game.CanonicalPath.Should().Be(valid.CanonicalPath);
        bound.Game.DisplayName.Should().Be(valid.DisplayName);
        client.InspectedPaths.Should().Equal(valid.CanonicalPath);
        client.InspectedOperations.Should().Equal(InstallerOperation.Backup);
    }

    [Test]
    public async Task ParentDisposalIsInertAfterTransferAndChildCleanupIsIdempotent()
    {
        ProtocolGameCandidate valid = Candidate("ownership", LinuxGameFolderStatus.Valid);
        RecordingClient client = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]) };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate issued = (await session.DiscoverGamesAsync()).Single();
        IPlanInspectionSession bound = session.BindToGame(issued);

        await session.DisposeAsync();
        await session.DisposeAsync();
        client.DisposeCalls.Should().Be(0, "only the transferred child owns backend cleanup");

        await bound.DisposeAsync();
        await bound.DisposeAsync();
        await session.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task TerminalPlanRejectionCleansUpBeforePublishingAndRevokesTheChild()
    {
        ProtocolGameCandidate valid = Candidate("terminal", LinuxGameFolderStatus.Valid);
        InstallerReadOnlyPlanRejection terminal = new(
            ProtocolPrePlanErrorCode.InspectionFailed,
            ProtocolNextAction.StartNewSession,
            true
        );
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]),
            Inspection = (_, _, _) => Task.FromResult<InstallerReadOnlyPlanResult>(terminal)
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());

        InstallerReadOnlyPlanResult result = await bound.InspectPlanAsync(InstallerOperation.Backup);

        result.Should().BeSameAs(terminal);
        client.DisposeCalls.Should().Be(1, "terminal cleanup must complete before the result is returned");
        Func<Task> inspectAgain = () => bound.InspectPlanAsync(InstallerOperation.Backup);
        await inspectAgain.Should().ThrowAsync<ObjectDisposedException>();
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task CallerCancellationPreventsACompletedPlanResultFromPublishing()
    {
        (VerifiedInstallerSession session, IPlanInspectionSession bound, RecordingClient client, TaskCompletionSource resultReady) =
            await CreateBlockedPlanSessionAsync("cancelled");
        using CancellationTokenSource cancellation = new();
        Task<InstallerReadOnlyPlanResult> inspection = bound.InspectPlanAsync(InstallerOperation.Backup, cancellation.Token);
        await client.InspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        resultReady.SetResult();

        await FluentActions.Awaiting(() => inspection).Should().ThrowAsync<OperationCanceledException>();
        client.DisposeCalls.Should().Be(0);
        await session.DisposeAsync();
        client.DisposeCalls.Should().Be(0);
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task SessionFaultTakesPrecedenceOverCallerCancellationBeforeResultPublication()
    {
        (_, IPlanInspectionSession bound, RecordingClient client, TaskCompletionSource resultReady) =
            await CreateBlockedPlanSessionAsync("fault-over-cancel");
        using CancellationTokenSource cancellation = new();
        Task<InstallerReadOnlyPlanResult> inspection = bound.InspectPlanAsync(InstallerOperation.Backup, cancellation.Token);
        await client.InspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        client.Fault.SetResult(new InstallerProtocolClientException("synthetic fault"));
        resultReady.SetResult();

        await FluentActions.Awaiting(() => inspection)
            .Should().ThrowAsync<InstallerProtocolClientException>()
            .WithMessage("*faulted before the plan result*");
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ChildDisposalTakesPrecedenceOverFaultAndCancellationBeforeResultPublication()
    {
        (_, IPlanInspectionSession bound, RecordingClient client, TaskCompletionSource resultReady) =
            await CreateBlockedPlanSessionAsync("dispose-over-terminal-signals");
        using CancellationTokenSource cancellation = new();
        Task<InstallerReadOnlyPlanResult> inspection = bound.InspectPlanAsync(InstallerOperation.Backup, cancellation.Token);
        await client.InspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task disposal = bound.DisposeAsync().AsTask();
        cancellation.Cancel();
        client.Fault.SetResult(new InstallerProtocolClientException("synthetic fault"));
        resultReady.SetResult();

        await FluentActions.Awaiting(() => inspection).Should().ThrowAsync<ObjectDisposedException>();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        client.DisposeCalls.Should().Be(1);
    }

    private static async Task<(
        VerifiedInstallerSession Session,
        IPlanInspectionSession Bound,
        RecordingClient Client,
        TaskCompletionSource ResultReady
    )> CreateBlockedPlanSessionAsync(string suffix)
    {
        ProtocolGameCandidate valid = Candidate(suffix, LinuxGameFolderStatus.Valid);
        TaskCompletionSource resultReady = NewCompletion();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]),
            Inspection = async (_, _, _) =>
            {
                await resultReady.Task;
                return Plan(InstallerOperation.Backup);
            }
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        return (session, bound, client, resultReady);
    }

    private static InstallerReadOnlyPlanSuccess Plan(InstallerOperation operation)
    {
        InstallerPlanRelease release = new("test-tag", "test-version");
        return new(
            operation,
            ObservedInstallState.KnownUnmodified,
            release,
            release,
            true,
            [],
            ProtocolRecommendedDefault.Cancel,
            true,
            [],
            [],
            [],
            0
        );
    }

    private static ProtocolGameCandidate Candidate(string suffix, LinuxGameFolderStatus status)
        => new($"/games/{suffix}", status, $"Stardew Valley {suffix}");

    private static ProtocolReleaseIdentity CreateRelease() => GameDiscoveryControllerTests.Release();

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewCompletion<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RecordingClient : IInstallerProtocolClient
    {
        public TaskCompletionSource<InstallerProtocolClientException> Fault { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InspectionStarted { get; } = NewCompletion();
        public Func<CancellationToken, Task<IReadOnlyList<ProtocolGameCandidate>>> Discovery { get; init; } =
            _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([]);
        public Func<string, CancellationToken, Task<ProtocolGameCandidate>> Validation { get; init; } =
            (path, _) => Task.FromResult(Candidate(path.GetHashCode(StringComparison.Ordinal).ToString(), LinuxGameFolderStatus.Valid));
        public Func<string, InstallerOperation, CancellationToken, Task<InstallerReadOnlyPlanResult>> Inspection { get; init; } =
            (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation));
        public List<string> InspectedPaths { get; } = [];
        public List<InstallerOperation> InspectedOperations { get; } = [];
        public int DiscoverCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;

        public Task<HandshakeEvent> HandshakeAsync(
            string clientName,
            string clientVersion,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("A verified-session test must not handshake again.");

        public Task<InstallerPackageOpenResult> OpenPackageAsync(
            InstallerPackageOpenInput package,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("A verified-session test must not open another package.");

        public Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
        {
            this.DiscoverCalls++;
            return this.Discovery(cancellationToken);
        }

        public Task<ProtocolGameCandidate> ValidateGameAsync(
            string canonicalPath,
            CancellationToken cancellationToken = default
        )
        {
            this.ValidateCalls++;
            return this.Validation(canonicalPath, cancellationToken);
        }

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(
            string canonicalGamePath,
            InstallerOperation operation,
            CancellationToken cancellationToken = default
        )
        {
            this.InspectedPaths.Add(canonicalGamePath);
            this.InspectedOperations.Add(operation);
            this.InspectionStarted.TrySetResult();
            return this.Inspection(canonicalGamePath, operation, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
