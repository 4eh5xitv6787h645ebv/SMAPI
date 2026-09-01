using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Tests.Protocol.V1;

[TestFixture]
internal sealed class LinuxInstallerProtocolJsonlHostTests
{
    [Test]
    public async Task RunAsync_FragmentedStrictUtf8Handshake_EmitsOneJsonLineWithoutBomOrDiagnostics()
    {
        HandshakeRequest request = new("桌面", "1");
        byte[] input = Encoding.UTF8.GetBytes(ProtocolJsonSerializer.SerializeLine(request) + "\n");
        FakeSession session = new((value, _) => Task.FromResult<ProtocolEvent>(Response(value)));
        (int exit, byte[] output, string diagnostics) = await RunAsync(new FragmentedReadStream(input, 1), session);

        exit.Should().Be(0);
        diagnostics.Should().BeEmpty();
        output.Take(3).Should().NotEqual(new byte[] { 0xef, 0xbb, 0xbf });
        string[] lines = Encoding.UTF8.GetString(output).Split('\n');
        lines.Should().HaveCount(2);
        lines[1].Should().BeEmpty();
        ProtocolJsonSerializer.DeserializeEventLine(lines[0]).CommandId.Should().Be(request.CommandId);
        session.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task RunAsync_ManualValidationDispatchesStrictRequestAndWritesTypedCorrelatedEvent()
    {
        ValidateGameRequest request = new(Session, "/selected/game");
        FakeSession session = new((value, _) =>
        {
            ValidateGameRequest validation = value.Should().BeOfType<ValidateGameRequest>().Subject;
            validation.GamePath.Should().Be("/selected/game");
            return Task.FromResult<ProtocolEvent>(new GameValidationEvent(Session, new("/canonical/game", StardewModdingAPI.Installer.Core.Engine.LinuxGameFolderStatus.Valid, "Stardew Valley")) { CommandId = value.CommandId });
        });

        (int exit, byte[] output, string diagnostics) = await RunAsync(new MemoryStream(Encoding.UTF8.GetBytes(Lines(request))), session);

        exit.Should().Be(0);
        diagnostics.Should().BeEmpty();
        GameValidationEvent response = ParseEvents(output).Should().ContainSingle().Which.Should().BeOfType<GameValidationEvent>().Subject;
        response.CommandId.Should().Be(request.CommandId);
        response.Candidate.CanonicalPath.Should().Be("/canonical/game");
        session.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task RunAsync_NoRecoveryHistoryRemainsAMinimalCorrelatedResponseAndDoesNotStopFollowingRequests()
    {
        ListRecoveriesRequest list = new(Session, "/game");
        ValidateGameRequest validate = new(Session, "/game");
        FakeSession session = new((request, _) => Task.FromResult<ProtocolEvent>(request switch
        {
            ListRecoveriesRequest => new NoRecoveryHistoryEvent(Session) { CommandId = request.CommandId },
            ValidateGameRequest => new GameValidationEvent(Session, new("/game", StardewModdingAPI.Installer.Core.Engine.LinuxGameFolderStatus.Valid, "Stardew Valley")) { CommandId = request.CommandId },
            _ => throw new AssertionException("Unexpected request type.")
        }));

        (int exit, byte[] output, string diagnostics) = await RunAsync(new MemoryStream(Encoding.UTF8.GetBytes(Lines(list, validate))), session);

        exit.Should().Be(0);
        diagnostics.Should().BeEmpty();
        ProtocolEvent[] responses = ParseEvents(output);
        responses.Should().HaveCount(2);
        responses[0].Should().BeOfType<NoRecoveryHistoryEvent>().Which.CommandId.Should().Be(list.CommandId);
        responses[1].Should().BeOfType<GameValidationEvent>().Which.CommandId.Should().Be(validate.CommandId);
        Encoding.UTF8.GetString(output).Should().NotContain("catalogId").And.NotContain("gamePath").And.NotContain("headSha256");
        session.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task RunAsync_RejectsNoRecoveryHistoryEventInputIncludingAnExtraField()
    {
        NoRecoveryHistoryEvent response = new(Session);
        string canonical = ProtocolJsonSerializer.SerializeLine(response);
        string extra = canonical.Replace("\"sessionId\":", "\"catalogId\":\"33333333333333333333333333333333\",\"sessionId\":", StringComparison.Ordinal);

        foreach (string line in new[] { canonical, extra })
        {
            FakeSession session = new((request, _) => Task.FromResult<ProtocolEvent>(Response(request)));
            (int exit, byte[] output, string diagnostics) = await RunAsync(new MemoryStream(Encoding.UTF8.GetBytes(line + "\n")), session);

            exit.Should().Be(LinuxInstallerProtocolJsonlHost.InvalidInputExitCode);
            output.Should().BeEmpty();
            diagnostics.Should().Be("Protocol input was rejected." + Environment.NewLine);
        }
    }

    [TestCase(ProtocolJsonSerializer.MaxLineBytes - 1, true)]
    [TestCase(ProtocolJsonSerializer.MaxLineBytes, true)]
    [TestCase(ProtocolJsonSerializer.MaxLineBytes + 1, false)]
    public async Task LineReader_EnforcesExactRawByteLimit(int byteCount, bool accepted)
    {
        byte[] input = Encoding.ASCII.GetBytes(new string('a', byteCount) + "\n");
        BoundedJsonLineReader reader = new(new FragmentedReadStream(input, 997));
        if (accepted)
            (await reader.ReadLineAsync(default)).Should().HaveLength(byteCount);
        else
            await FluentActions.Awaiting(async () => await reader.ReadLineAsync(default)).Should().ThrowAsync<ProtocolFramingException>();
    }

    [Test]
    public async Task RunAsync_RejectsMalformedFramingAndJsonWithoutEchoingInput()
    {
        byte[][] rejected =
        [
            Encoding.UTF8.GetBytes("\n"),
            Encoding.UTF8.GetBytes("{}\n"),
            Encoding.UTF8.GetBytes("{\"secret-token\":true}\r\n"),
            Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"protocolVersion\":1,\"messageType\":\"handshake.request\",\"payload\":{}}\n"),
            Encoding.UTF8.GetBytes(new string('[', ProtocolJsonSerializer.MaxDepth + 2) + new string(']', ProtocolJsonSerializer.MaxDepth + 2) + "\n"),
            [0xff, (byte)'\n'],
            [(byte)'{', 0, (byte)'}', (byte)'\n'],
            Encoding.UTF8.GetBytes("{\"unterminated\":true}")
        ];

        foreach (byte[] value in rejected)
        {
            FakeSession session = new((request, _) => Task.FromResult<ProtocolEvent>(Response(request)));
            (int exit, byte[] output, string diagnostics) = await RunAsync(new FragmentedReadStream(value, 2), session);
            exit.Should().Be(LinuxInstallerProtocolJsonlHost.InvalidInputExitCode);
            output.Should().BeEmpty();
            diagnostics.Should().Be("Protocol input was rejected." + Environment.NewLine);
            diagnostics.Should().NotContain("secret-token");
        }
    }

    [Test]
    public async Task RunAsync_AllowsOnlyCancellationAsSecondInFlightRequest()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        FakeSession session = new(async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(token);
            return Response(request);
        }, dispose: () => { release.TrySetResult(); return ValueTask.CompletedTask; });
        string input = Lines(new ExecutePlanRequest(Session, Plan, Digest), new DiscoverGamesRequest(Session));

        (int exit, byte[] output, _) = await RunAsync(new MemoryStream(Encoding.UTF8.GetBytes(input)), session);

        exit.Should().Be(LinuxInstallerProtocolJsonlHost.InvalidInputExitCode);
        calls.Should().Be(1);
        output.Should().BeEmpty();
    }

    [Test]
    public async Task RunAsync_AdmitsCancellationBesideActiveRequest_AndPreservesTypedTerminalOrderingWithSlowOutput()
    {
        TaskCompletionSource cancelSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<ProtocolEvent>? sink = null;
        FakeSession session = new(async (request, token) =>
        {
            if (request is ExecutePlanRequest)
            {
                sink!(new ProgressEvent(Session, Plan, Digest, 1, TransactionStage.Applying, 0, 1, "Applying.")
                {
                    CommandId = request.CommandId
                });
                await cancelSeen.Task;
                return new CancelledEvent(
                    Session,
                    Plan,
                    Digest,
                    ProtocolExecutionOutcome.CancelledBeforeMutation,
                    new(ProtocolDurableState.Unchanged, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain),
                    new(0, 0, 0, 0, 0, 0),
                    "Cancelled.",
                    null
                )
                {
                    CommandId = request.CommandId
                };
            }
            if (request is CancelPlanRequest)
            {
                cancelSeen.TrySetResult();
                return new CommandAcknowledgedEvent(Session, ProtocolAcknowledgementKind.PlanCancellationRequested, Plan, null)
                {
                    CommandId = request.CommandId
                };
            }
            throw new AssertionException("Unexpected request type.");
        });
        LinuxInstallerProtocolJsonlHost host = new(value => { sink = value; return session; });
        using SlowWriteStream output = new();
        using StringWriter diagnostics = new();
        ExecutePlanRequest execute = new(Session, Plan, Digest);
        CancelPlanRequest cancel = new(Session, Plan, Digest);
        string input = Lines(execute, cancel);

        int exit = await host.RunAsync(new MemoryStream(Encoding.UTF8.GetBytes(input)), output, diagnostics);

        exit.Should().Be(0);
        ProtocolEvent[] events = ParseEvents(output.ToArray());
        events.Should().HaveCount(3);
        events[0].Should().BeOfType<ProgressEvent>().Which.CommandId.Should().Be(execute.CommandId);
        events.OfType<CommandAcknowledgedEvent>().Should().ContainSingle().Which.CommandId.Should().Be(cancel.CommandId);
        events.OfType<CancelledEvent>().Should().ContainSingle().Which.CommandId.Should().Be(execute.CommandId);
        int terminalIndex = Array.FindIndex(events, value => value is CancelledEvent);
        events.Skip(terminalIndex + 1).Should().NotContain(value => value is ProgressEvent);
    }

    [Test]
    public async Task RunAsync_EofWithActiveRequest_CancelsRequestBeforeDisposalAndFlushesTerminalResponse()
    {
        TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancellationObservedAtDispose = false;
        CancellationToken admittedToken = default;
        FakeSession session = new(async (request, token) =>
        {
            admittedToken = token;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                settled.TrySetResult();
            }
            return Response(request);
        }, dispose: () =>
        {
            cancellationObservedAtDispose = admittedToken.IsCancellationRequested;
            return new ValueTask(settled.Task);
        });
        ExecutePlanRequest request = new(Session, Plan, Digest);

        (int exit, byte[] output, string diagnostics) = await RunAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(ProtocolJsonSerializer.SerializeLine(request) + "\n")),
            session
        );

        exit.Should().Be(0);
        diagnostics.Should().BeEmpty();
        session.Disposed.Should().BeTrue();
        cancellationObservedAtDispose.Should().BeTrue();
        ParseEvents(output).Should().ContainSingle().Which.CommandId.Should().Be(request.CommandId);
    }

    [Test]
    public async Task RunAsync_EofBeforeRequestAdmission_ObservesPropagatedTokenCancellationAsCleanSettlement()
    {
        CancellationToken admittedToken = default;
        FakeSession session = new(async (_, token) =>
        {
            admittedToken = token;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new AssertionException("The admitted request should only finish through cancellation.");
        }, dispose: () =>
        {
            admittedToken.IsCancellationRequested.Should().BeTrue();
            return ValueTask.CompletedTask;
        });
        DiscoverGamesRequest request = new(Session);

        (int exit, byte[] output, string diagnostics) = await RunAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(ProtocolJsonSerializer.SerializeLine(request) + "\n")),
            session
        );

        exit.Should().Be(0);
        output.Should().BeEmpty();
        diagnostics.Should().BeEmpty();
        session.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task RunAsync_OutputFailure_CancelsAndDisposesWithoutWritingDiagnosticsToStdout()
    {
        HandshakeRequest request = new("gui", "1");
        Action<ProtocolEvent>? sink = null;
        TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool requestCancelled = false;
        FakeSession session = new(async (value, token) =>
        {
            sink!(new RecoveryProgressEvent(Session, 1, TransactionStage.Recovering, 0, 1, "Recovering."));
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                requestCancelled = true;
                settled.TrySetResult();
            }
            return Response(value);
        }, dispose: () => new ValueTask(settled.Task));
        LinuxInstallerProtocolJsonlHost host = new(value => { sink = value; return session; });
        using StringWriter diagnostics = new();

        int exit = await host.RunAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(ProtocolJsonSerializer.SerializeLine(request) + "\n")),
            new ThrowingWriteStream(),
            diagnostics
        );

        exit.Should().Be(LinuxInstallerProtocolJsonlHost.FailureExitCode);
        requestCancelled.Should().BeTrue();
        session.Disposed.Should().BeTrue();
        diagnostics.ToString().Should().Be("Protocol transport stopped safely." + Environment.NewLine);
    }

    [Test]
    public async Task RunAsync_ExternalCancellation_BoundsWriterWhichIgnoresCancellationAndDisposal()
    {
        HandshakeRequest request = new("gui", "1");
        byte[] line = Encoding.UTF8.GetBytes(ProtocolJsonSerializer.SerializeLine(request) + "\n");
        Action<ProtocolEvent>? sink = null;
        TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool requestCancelled = false;
        FakeSession session = new(async (value, token) =>
        {
            sink!(new RecoveryProgressEvent(Session, 1, TransactionStage.Recovering, 0, 1, "Recovering."));
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                requestCancelled = true;
                settled.TrySetResult();
            }
            return Response(value);
        }, dispose: () => new ValueTask(settled.Task));
        LinuxInstallerProtocolJsonlHost host = new(value => { sink = value; return session; });
        IgnoringWriteStream output = new();
        using CancellationTokenSource cancellation = new();
        using StringWriter diagnostics = new();
        Task<int> running = host.RunAsync(new PrefixThenBlockingReadStream(line), output, diagnostics, cancellation.Token);
        await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        (await running.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(LinuxInstallerProtocolJsonlHost.CancelledExitCode);
        requestCancelled.Should().BeTrue();
        session.Disposed.Should().BeTrue();
        output.Disposed.Should().BeTrue();
        diagnostics.ToString().Should().Be("Protocol host was cancelled." + Environment.NewLine);

        // Complete the abandoned write with a fault after RunAsync returned. The host-owned fault observer
        // must consume the single writer failure without changing the already published exit result.
        output.FailPendingWrite();
        await Task.Delay(50);
    }

    [Test]
    public async Task RunAsync_ProgressOverflow_FailsStopAndCancelsInsteadOfGrowingUnbounded()
    {
        Action<ProtocolEvent>? sink = null;
        TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool requestCancelled = false;
        FakeSession session = new(async (request, token) =>
        {
            int total = LinuxInstallerProtocolJsonlHost.OutboundEventCapacity + 2;
            for (int index = 1; index <= total; index++)
                sink!(new RecoveryProgressEvent(Session, index, TransactionStage.Recovering, index, total, "Recovering."));
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                requestCancelled = true;
                settled.TrySetResult();
            }
            return Response(request);
        }, dispose: () => new ValueTask(settled.Task));
        LinuxInstallerProtocolJsonlHost host = new(value => { sink = value; return session; });
        using StringWriter diagnostics = new();
        string input = ProtocolJsonSerializer.SerializeLine(new HandshakeRequest("gui", "1")) + "\n";

        int exit = await host.RunAsync(new MemoryStream(Encoding.UTF8.GetBytes(input)), new BlockingWriteStream(), diagnostics);

        exit.Should().Be(LinuxInstallerProtocolJsonlHost.FailureExitCode);
        requestCancelled.Should().BeTrue();
        session.Disposed.Should().BeTrue();
        diagnostics.ToString().Should().Be("Protocol transport stopped safely." + Environment.NewLine);
    }

    [Test]
    public async Task RunAsync_ExternalCancellation_StopsBlockedReadAndDisposes()
    {
        FakeSession session = new((request, _) => Task.FromResult<ProtocolEvent>(Response(request)));
        LinuxInstallerProtocolJsonlHost host = new(_ => session);
        using CancellationTokenSource cancellation = new();
        using StringWriter diagnostics = new();
        Task<int> running = host.RunAsync(new BlockingReadStream(), new MemoryStream(), diagnostics, cancellation.Token);

        cancellation.Cancel();

        (await running.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(LinuxInstallerProtocolJsonlHost.CancelledExitCode);
        session.Disposed.Should().BeTrue();
        diagnostics.ToString().Should().Be("Protocol host was cancelled." + Environment.NewLine);
    }

    private static readonly ProtocolSessionId Session = ProtocolSessionId.CreateRandom();
    private static readonly ProtocolPlanId Plan = ProtocolPlanId.CreateRandom();
    private static readonly ProtocolPlanDigest Digest = ProtocolPlanDigest.Parse(new string('a', 64));

    private static HandshakeEvent Response(ProtocolRequest request) => new(Session, "test", ["test"])
    {
        CommandId = request.CommandId
    };

    private static string Lines(params ProtocolRequest[] requests) => string.Concat(requests.Select(value => ProtocolJsonSerializer.SerializeLine(value) + "\n"));

    private static ProtocolEvent[] ParseEvents(byte[] bytes) => Encoding.UTF8.GetString(bytes)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(ProtocolJsonSerializer.DeserializeEventLine)
        .ToArray();

    private static async Task<(int Exit, byte[] Output, string Diagnostics)> RunAsync(Stream input, FakeSession session)
    {
        LinuxInstallerProtocolJsonlHost host = new(_ => session);
        using MemoryStream output = new();
        using StringWriter diagnostics = new();
        int exit = await host.RunAsync(input, output, diagnostics);
        return (exit, output.ToArray(), diagnostics.ToString());
    }

    private sealed class FakeSession : ILinuxInstallerProtocolHostSession
    {
        private readonly Func<ProtocolRequest, CancellationToken, Task<ProtocolEvent>> Handle;
        private readonly Func<ValueTask>? Dispose;
        public bool Disposed { get; private set; }

        public FakeSession(Func<ProtocolRequest, CancellationToken, Task<ProtocolEvent>> handle, Func<ValueTask>? dispose = null)
        {
            this.Handle = handle;
            this.Dispose = dispose;
        }

        public Task<ProtocolEvent> HandleAsync(ProtocolRequest request, CancellationToken cancellationToken) => this.Handle(request, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            this.Disposed = true;
            if (this.Dispose is not null)
                await this.Dispose();
        }
    }

    private sealed class FragmentedReadStream(byte[] bytes, int maximumChunk) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunk)], cancellationToken);
    }

    private sealed class PrefixThenBlockingReadStream(byte[] prefix) : Stream
    {
        private bool ReturnedPrefix;
        private readonly TaskCompletionSource<int> Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (this.ReturnedPrefix)
                return new(this.Blocked.Task);
            this.ReturnedPrefix = true;
            prefix.CopyTo(buffer);
            return ValueTask.FromResult(prefix.Length);
        }
        protected override void Dispose(bool disposing) { }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource<int> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(this.Completion.Task);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                this.Completion.TrySetException(new ObjectDisposedException(nameof(BlockingReadStream)));
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new IOException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException());
    }

    private sealed class BlockingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
    }

    private sealed class IgnoringWriteStream : Stream
    {
        private readonly TaskCompletionSource WriteCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public void FailPendingWrite() => this.WriteCompletion.TrySetException(new IOException("Late ignored write failure."));
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            this.WriteStarted.TrySetResult();
            return new(this.WriteCompletion.Task);
        }
        protected override void Dispose(bool disposing)
        {
            this.Disposed = true;
        }
    }

    private sealed class SlowWriteStream : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(5, cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
