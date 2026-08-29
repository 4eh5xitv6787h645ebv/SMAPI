using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Tests;

public sealed class ProcessInstallerProtocolClientTests
{
    private static readonly ProtocolSessionId Session = ProtocolSessionId.Parse("11111111111111111111111111111111");

    [Test]
    public async Task StartsExactAbsoluteSiblingWithoutShellOrArgumentParsing()
    {
        ScriptedProcess process = new(CorrectResponse);
        CapturingFactory factory = new(process);
        string path = "/tmp/installer folder ;$(touch nope)/SMAPI.Installer";
        await using ProcessInstallerProtocolClient client = ProcessInstallerProtocolClient.CreateForTesting(path, factory);

        HandshakeEvent response = await client.HandshakeAsync("SMAPI GUI", "1");

        response.SessionId.Should().Be(Session);
        factory.StartInfo.Should().NotBeNull();
        factory.StartInfo!.FileName.Should().Be(path);
        factory.StartInfo.UseShellExecute.Should().BeFalse();
        factory.StartInfo.RedirectStandardInput.Should().BeTrue();
        factory.StartInfo.RedirectStandardOutput.Should().BeTrue();
        factory.StartInfo.RedirectStandardError.Should().BeTrue();
        factory.StartInfo.ArgumentList.Should().Equal(ProcessInstallerProtocolClient.ProtocolFlag);
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public async Task RealProcessIntegrationUsesExactFlagAndHandlesPathMetacharacters()
    {
        string root = Path.Combine(Path.GetTempPath(), $"smapi gui process ;$[] {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string gui = Path.Combine(root, "SMAPI.Installer.Gui");
            string installer = Path.Combine(root, SiblingInstallerLocator.InstallerFileName);
            await File.WriteAllTextAsync(gui, "gui");
            await File.WriteAllTextAsync(installer, """
                #!/bin/sh
                test "$#" -eq 1 && test "$1" = "--linux-protocol-v1-jsonl" || exit 42
                IFS= read -r request || exit 43
                command_id=$(printf '%s\n' "$request" | sed -n 's/.*"commandId":"\([0-9a-f]*\)".*/\1/p')
                test "${#command_id}" -eq 32 || exit 44
                printf '%s\n' "{\"protocolVersion\":1,\"messageType\":\"handshake.event\",\"payload\":{\"commandId\":\"$command_id\",\"sessionId\":\"11111111111111111111111111111111\",\"serverVersion\":\"1\",\"capabilities\":[\"verified-local-package\"]}}"
                """);
            File.SetUnixFileMode(installer, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            string located = SiblingInstallerLocator.Locate(gui);
            await using ProcessInstallerProtocolClient client = ProcessInstallerProtocolClient.CreateForTesting(located, new SystemInstallerProtocolProcessFactory());

            HandshakeEvent response = await client.HandshakeAsync("SMAPI GUI", "1");

            response.SessionId.Should().Be(Session);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task CorrelatesHandshakeAndPackageOpenAndSendsOnlyThoseTwoRequestKinds()
    {
        ScriptedProcess process = new(CorrectResponse);
        await using ProcessInstallerProtocolClient client = Create(process);

        await client.HandshakeAsync("SMAPI GUI", "1");
        InstallerPackageOpenSuccess result = (await client.OpenPackageAsync(CreatePackage())).Should().BeOfType<InstallerPackageOpenSuccess>().Subject;
        PackageOpenedEvent opened = result.Opened;

        opened.SessionId.Should().Be(Session);
        process.Requests.Select(request => request.Kind).Should().Equal(ProtocolMessageKind.HandshakeRequest, ProtocolMessageKind.OpenPackageRequest);
        OpenPackageRequest sent = process.Requests.OfType<OpenPackageRequest>().Single();
        sent.PackagePath.Should().Be(CreatePackage().PackagePath);
        sent.AttestationBundleChecksumPath.Should().Be(CreatePackage().AttestationBundleChecksumPath);
        typeof(IInstallerProtocolClient).GetMethods().Select(method => method.Name)
            .Should().BeEquivalentTo([nameof(IInstallerProtocolClient.HandshakeAsync), nameof(IInstallerProtocolClient.OpenPackageAsync)]);
    }

    [Test]
    public async Task RejectsPackageOpenBeforeHandshakeWithoutStartingProcess()
    {
        ScriptedProcess process = new(CorrectResponse);
        CapturingFactory factory = new(process);
        await using ProcessInstallerProtocolClient client = ProcessInstallerProtocolClient.CreateForTesting("/tmp/SMAPI.Installer", factory);

        Func<Task> action = () => client.OpenPackageAsync(CreatePackage());

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        factory.StartInfo.Should().BeNull();
    }

    [Test]
    public async Task RejectsHandshakeWithoutVerifiedPackageCapability()
    {
        ScriptedProcess process = new(request => Serialize(new HandshakeEvent(Session, "1", ["linux-game-discovery"]) { CommandId = request.CommandId }));
        await using ProcessInstallerProtocolClient client = Create(process);

        Func<Task> action = () => client.HandshakeAsync("SMAPI GUI", "1");

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
    }

    [Test]
    public async Task SurfacesNormalCorrelatedPackageRejectionWithoutPrivateLogOrFailStop()
    {
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", [ProcessInstallerProtocolClient.PackageVerificationCapability]) { CommandId = request.CommandId }),
            OpenPackageRequest => Serialize(new PrePlanRejectedEvent(
                Session,
                ProtocolPrePlanErrorCode.PackageRejected,
                "The selected release asset set failed strict package verification.",
                ProtocolNextAction.ReopenVerifiedPackage,
                false,
                "/private/log/which-must-not-cross-the-interface"
            )
            {
                CommandId = request.CommandId
            }),
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");

        InstallerPackageOpenResult result = await client.OpenPackageAsync(CreatePackage());

        InstallerPackageOpenRejection rejection = result.Should().BeOfType<InstallerPackageOpenRejection>().Subject;
        rejection.ErrorCode.Should().Be(ProtocolPrePlanErrorCode.PackageRejected);
        rejection.NextAction.Should().Be(ProtocolNextAction.ReopenVerifiedPackage);
        rejection.Message.Should().Be("The selected release asset set failed strict package verification.");
        rejection.IsTerminal.Should().BeFalse();
        result.ToString().Should().NotContain("/private/log");
        process.Terminated.Should().BeFalse();
        process.Disposed.Should().BeFalse();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RejectsWrongCommandOrSessionCorrelationAndTerminates(bool wrongCommand)
    {
        ScriptedProcess process = new(request =>
        {
            ProtocolCommandId command = wrongCommand ? ProtocolCommandId.CreateRandom() : request.CommandId;
            ProtocolSessionId session = wrongCommand ? Session : ProtocolSessionId.CreateRandom();
            return Serialize(new HandshakeEvent(session, "1", [ProcessInstallerProtocolClient.PackageVerificationCapability]) { CommandId = command });
        });
        await using ProcessInstallerProtocolClient client = Create(process);

        if (!wrongCommand)
        {
            // A handshake may establish any valid session, so test mismatched session on package open instead.
            process.Responder = request => request is HandshakeRequest
                ? Serialize(new HandshakeEvent(Session, "1", [ProcessInstallerProtocolClient.PackageVerificationCapability]) { CommandId = request.CommandId })
                : Serialize(CreateOpened(ProtocolSessionId.CreateRandom(), request.CommandId));
            await client.HandshakeAsync("SMAPI GUI", "1");
            Func<Task> open = () => client.OpenPackageAsync(CreatePackage());
            await open.Should().ThrowAsync<InstallerProtocolClientException>();
        }
        else
        {
            Func<Task> handshake = () => client.HandshakeAsync("SMAPI GUI", "1");
            await handshake.Should().ThrowAsync<InstallerProtocolClientException>();
        }

        process.Terminated.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [TestCase("tag")]
    [TestCase("commit")]
    [TestCase("asset")]
    public async Task RejectsCorrelatedPackageOpenedEventWithWrongReleaseBinding(string mismatch)
    {
        InstallerPackageOpenInput package = mismatch switch
        {
            // Keep the response's asset name and commit bound to the input so this isolates the tag check.
            "tag" => CreatePackage(packageAssetName: PackageName(3)),
            "commit" => CreatePackage(),
            // Keep the response's tag and commit bound to the input so this isolates the basename check.
            "asset" => CreatePackage(packageAssetName: PackageName(3)),
            _ => throw new AssertionException("Unknown mismatch fixture.")
        };
        ScriptedProcess process = new(request => request switch
        {
            HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", [ProcessInstallerProtocolClient.PackageVerificationCapability]) { CommandId = request.CommandId }),
            OpenPackageRequest => mismatch switch
            {
                "tag" => Serialize(CreateOpened(Session, request.CommandId, alpha: 3)),
                "commit" => Serialize(CreateOpened(Session, request.CommandId, sourceCommit: new string('3', 40))),
                "asset" => Serialize(CreateOpened(Session, request.CommandId)),
                _ => throw new AssertionException("Unknown mismatch fixture.")
            },
            _ => throw new AssertionException("Unexpected protocol request.")
        });
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");

        Func<Task> action = () => client.OpenPackageAsync(package);

        await action.Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [TestCase("/tmp/package/")]
    [TestCase("/tmp/package/.")]
    [TestCase("/tmp/package/..")]
    [TestCase("/tmp/package/unsafe\\name.zip")]
    public async Task RejectsUnsafePackageBasenameBeforeSendingOpenRequest(string packagePath)
    {
        ScriptedProcess process = new(CorrectResponse);
        await using ProcessInstallerProtocolClient client = Create(process);
        await client.HandshakeAsync("SMAPI GUI", "1");
        InstallerPackageOpenInput package = CreatePackage() with { PackagePath = packagePath };

        Func<Task> action = () => client.OpenPackageAsync(package);

        await action.Should().ThrowAsync<InstallerProtocolClientException>().WithMessage("*safe absolute Linux filename*");
        process.Requests.Should().ContainSingle().Which.Should().BeOfType<HandshakeRequest>();
    }

    [Test]
    public async Task RejectsEofInvalidUtf8AndOversizedFramesWithoutLeakingTheirContents()
    {
        byte[][] responses =
        [
            [],
            [0xff, (byte)'\n'],
            [.. Enumerable.Repeat((byte)'x', ProtocolJsonSerializer.MaxLineBytes + 1), (byte)'\n']
        ];
        foreach (byte[] response in responses)
        {
            ScriptedProcess process = new(_ => response);
            await using ProcessInstallerProtocolClient client = Create(process);
            Func<Task> action = () => client.HandshakeAsync("private-client-value", "1");

            Exception exception = (await action.Should().ThrowAsync<InstallerProtocolClientException>()).Which;

            exception.Message.Should().NotContain("private-client-value");
            process.Terminated.Should().BeTrue();
        }
    }

    [Test]
    public async Task DrainsStderrFloodButRetainsOnlyBoundedGenericMetadata()
    {
        string secret = "/private/home/user/token";
        byte[] flood = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(secret, 10000)));
        ScriptedProcess process = new(CorrectResponse, error: new MemoryStream(flood));
        await using ProcessInstallerProtocolClient client = Create(process);

        await client.HandshakeAsync("SMAPI GUI", "1");
        await SpinWaitUntilAsync(() => client.ObservedStderrBytes == ProcessInstallerProtocolClient.MaximumObservedStderrBytes);

        client.ObservedStderrBytes.Should().Be(ProcessInstallerProtocolClient.MaximumObservedStderrBytes);
        client.ToString().Should().NotContain(secret);
    }

    [Test]
    public async Task CancellationStopsAndReapsWithCancellationResistantOutput()
    {
        ScriptedProcess process = new(_ => null, output: new CancellationResistantStream(), completeWaitInitially: false);
        await using ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource cancellation = new();

        Task running = client.HandshakeAsync("SMAPI GUI", "1", cancellation.Token);
        await process.RequestObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();
        process.Terminated.Should().BeTrue();
        process.WaitObserved.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task OperationDeadlineStopsABackendWithCancellationResistantIo()
    {
        ScriptedProcess process = new(_ => null, output: new CancellationResistantStream(), completeWaitInitially: false);
        await using ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));

        Func<Task> action = () => client.HandshakeAsync("SMAPI GUI", "1");

        await action.Should().ThrowAsync<InstallerProtocolClientException>().WithMessage("*bounded deadline*");
        process.Terminated.Should().BeTrue();
        process.WaitObserved.Should().BeTrue();
    }

    [Test]
    public async Task CancellationStopsAndReapsWithCancellationResistantInput()
    {
        CancellationResistantWriteStream input = new();
        ScriptedProcess process = new(CorrectResponse, input: input, completeWaitInitially: false);
        await using ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

        Func<Task> action = () => client.HandshakeAsync("SMAPI GUI", "1", cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        input.Disposed.Should().BeTrue();
        process.Terminated.Should().BeTrue();
        process.WaitObserved.Should().BeTrue();
    }

    [Test]
    public async Task DisposeClosesInputThenTerminatesAndReapsBackendWhichDoesNotExitOnEof()
    {
        ScriptedProcess process = new(CorrectResponse, completeWaitInitially: false);
        ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(100));
        await client.HandshakeAsync("SMAPI GUI", "1");

        await client.DisposeAsync();

        process.InputDisposed.Should().BeTrue();
        process.Terminated.Should().BeTrue();
        process.WaitObserved.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task ConcurrentDisposeCancelsTransportAndSettlesBeforeDisposingSynchronizationObjects()
    {
        ScriptedProcess process = new(_ => null, output: new CancellationResistantStream(), completeWaitInitially: false);
        ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(100));
        Task running = client.HandshakeAsync("SMAPI GUI", "1");
        await process.RequestObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task disposal = client.DisposeAsync().AsTask();

        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await FluentActions.Awaiting(() => running).Should().ThrowAsync<InstallerProtocolClientException>();
        process.Terminated.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task EveryConcurrentDisposeCallerAwaitsTheSameCleanupAndReap()
    {
        ScriptedProcess process = new(CorrectResponse, completeWaitInitially: false);
        ProcessInstallerProtocolClient client = Create(process, TimeSpan.FromMilliseconds(200));
        await client.HandshakeAsync("SMAPI GUI", "1");

        Task first = client.DisposeAsync().AsTask();
        Task second = client.DisposeAsync().AsTask();
        await Task.Delay(30);

        first.IsCompleted.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        process.Terminated.Should().BeTrue();
        process.WaitObserved.Should().BeTrue();
        process.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task MalformedBackendDiagnosticsAndPackagePathsAreNeverReflected()
    {
        const string privatePath = "/home/private-user/download/token-package.zip";
        ScriptedProcess process = new(_ => Encoding.UTF8.GetBytes("not-json\n"), error: new MemoryStream(Encoding.UTF8.GetBytes(privatePath)));
        await using ProcessInstallerProtocolClient client = Create(process);
        Func<Task> action = () => client.HandshakeAsync("SMAPI GUI", "1");

        Exception exception = (await action.Should().ThrowAsync<InstallerProtocolClientException>()).Which;

        exception.Message.Should().NotContain(privatePath).And.NotContain("not-json");
    }

    private static ProcessInstallerProtocolClient Create(ScriptedProcess process, TimeSpan? reap = null, TimeSpan? operation = null) =>
        ProcessInstallerProtocolClient.CreateForTesting(
            "/tmp/SMAPI.Installer",
            new CapturingFactory(process),
            operation ?? TimeSpan.FromSeconds(2),
            reap ?? TimeSpan.FromMilliseconds(250)
        );

    private static InstallerPackageOpenInput CreatePackage(string? packageAssetName = null)
    {
        string root = "/tmp/package set ;$[]";
        return new(
            "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2",
            new string('1', 40),
            Path.Combine(root, packageAssetName ?? PackageName(2)),
            Path.Combine(root, "SHA256SUMS"),
            Path.Combine(root, "build-metadata.json"),
            Path.Combine(root, "install-manifest.json"),
            Path.Combine(root, "attestation.json"),
            Path.Combine(root, "attestation.sha256")
        );
    }

    private static byte[]? CorrectResponse(ProtocolRequest request) => request switch
    {
        HandshakeRequest => Serialize(new HandshakeEvent(Session, "1", [ProcessInstallerProtocolClient.PackageVerificationCapability]) { CommandId = request.CommandId }),
        OpenPackageRequest => Serialize(CreateOpened(Session, request.CommandId)),
        _ => throw new AssertionException("The GUI bridge sent a command outside this slice.")
    };

    private static PackageOpenedEvent CreateOpened(ProtocolSessionId session, ProtocolCommandId command, int alpha = 2, string? sourceCommit = null) => new(
        session,
        ProtocolPackageId.Parse("22222222222222222222222222222222"),
        new(
            "https://github.com/4eh5xitv6787h645ebv/SMAPI",
            $"fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.{alpha}",
            $"4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.{alpha}",
            PackageName(alpha),
            sourceCommit ?? new string('1', 40),
            new string('2', 40),
            new string('a', 64),
            123,
            $"4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.{alpha}",
            "Release",
            "linux-x64"
        )
    )
    {
        CommandId = command
    };

    private static string PackageName(int alpha) => $"SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.{alpha}-linux-x64-installer.zip";

    private static byte[] Serialize(ProtocolEvent value) => Encoding.UTF8.GetBytes(ProtocolJsonSerializer.SerializeLine(value) + "\n");

    private static async Task SpinWaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class CapturingFactory(ScriptedProcess process) : IInstallerProtocolProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }
        public IInstallerProtocolProcess Start(ProcessStartInfo startInfo)
        {
            this.StartInfo = startInfo;
            return process;
        }
    }

    private sealed class ScriptedProcess : IInstallerProtocolProcess
    {
        private readonly TaskCompletionSource Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ResponseStream Responses;
        private readonly RequestStream RequestsStream;
        public Func<ProtocolRequest, byte[]?> Responder { get => this.RequestsStream.Responder; set => this.RequestsStream.Responder = value; }
        public List<ProtocolRequest> Requests => this.RequestsStream.Requests;
        public TaskCompletionSource RequestObserved => this.RequestsStream.RequestObserved;
        public Stream Input { get; }
        public Stream Output { get; }
        public Stream Error { get; }
        public bool Terminated { get; private set; }
        public bool WaitObserved { get; private set; }
        public bool Disposed { get; private set; }
        public bool InputDisposed => this.Input switch { RequestStream request => request.Disposed, CancellationResistantWriteStream resistant => resistant.Disposed, _ => false };

        public ScriptedProcess(Func<ProtocolRequest, byte[]?> responder, Stream? input = null, Stream? output = null, Stream? error = null, bool completeWaitInitially = true)
        {
            this.Responses = new ResponseStream();
            this.RequestsStream = new RequestStream(this.Responses, responder);
            this.Input = input ?? this.RequestsStream;
            this.Output = output ?? this.Responses;
            this.Error = error ?? new MemoryStream();
            if (completeWaitInitially)
                this.Exit.TrySetResult();
        }

        public Task WaitForExitAsync()
        {
            this.WaitObserved = true;
            return this.Exit.Task;
        }

        public void Terminate()
        {
            this.Terminated = true;
            this.Exit.TrySetResult();
        }

        public void Dispose() => this.Disposed = true;
    }

    private sealed class RequestStream(ResponseStream responses, Func<ProtocolRequest, byte[]?> responder) : MemoryStream
    {
        private long Consumed;
        public Func<ProtocolRequest, byte[]?> Responder { get; set; } = responder;
        public List<ProtocolRequest> Requests { get; } = [];
        public TaskCompletionSource RequestObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            byte[] all = this.ToArray();
            int length = checked((int)(all.Length - this.Consumed));
            string line = Encoding.UTF8.GetString(all, checked((int)this.Consumed), length).TrimEnd('\n');
            this.Consumed = all.Length;
            ProtocolRequest request = ProtocolJsonSerializer.DeserializeRequestLine(line);
            this.Requests.Add(request);
            this.RequestObserved.TrySetResult();
            byte[]? response = this.Responder(request);
            if (response is not null)
                responses.Set(response);
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            this.Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ResponseStream : Stream
    {
        private byte[] Current = [];
        private int Offset;
        public void Set(byte[] bytes) { this.Current = bytes; this.Offset = 0; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int copy = Math.Min(count, this.Current.Length - this.Offset);
            if (copy <= 0) return 0;
            Array.Copy(this.Current, this.Offset, buffer, offset, copy);
            this.Offset += copy;
            return copy;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int copy = Math.Min(buffer.Length, this.Current.Length - this.Offset);
            if (copy <= 0) return ValueTask.FromResult(0);
            this.Current.AsMemory(this.Offset, copy).CopyTo(buffer);
            this.Offset += copy;
            return ValueTask.FromResult(copy);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellationResistantStream : Stream
    {
        private readonly TaskCompletionSource<int> Never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(this.Never.Task);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellationResistantWriteStream : Stream
    {
        private readonly TaskCompletionSource Never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => new(this.Never.Task);
        protected override void Dispose(bool disposing) { this.Disposed = true; base.Dispose(disposing); }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
