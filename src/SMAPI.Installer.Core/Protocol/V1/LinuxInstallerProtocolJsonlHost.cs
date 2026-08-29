using System.Text;
using System.Threading.Channels;

namespace StardewModdingAPI.Installer.Core.Protocol.V1;

/// <summary>Runs one bounded version 1 protocol session over strict UTF-8 JSON Lines streams.</summary>
public sealed class LinuxInstallerProtocolJsonlHost
{
    /// <summary>The maximum number of admitted commands, including an active operation and its cancellation command.</summary>
    public const int MaximumInFlightRequests = 2;

    internal const int OutboundEventCapacity = 256;
    internal const int InvalidInputExitCode = 2;
    internal const int FailureExitCode = 1;
    internal const int CancelledExitCode = 130;

    private readonly Func<Action<ProtocolEvent>, ILinuxInstallerProtocolHostSession> CreateSession;

    /// <summary>Create a host backed by the production Linux installer protocol service.</summary>
    /// <param name="serverVersion">The bounded backend version returned by the handshake.</param>
    /// <param name="githubCliPath">The absolute host-owned path to the packaged GitHub CLI executable.</param>
    /// <param name="sanitizedLogPath">An optional absolute path to a sanitized local installer log.</param>
    public LinuxInstallerProtocolJsonlHost(string serverVersion, string githubCliPath, string? sanitizedLogPath = null)
        : this(sink => new LinuxInstallerProtocolHostSession(new LinuxInstallerProtocolService(serverVersion, githubCliPath, sanitizedLogPath, sink)))
    {
    }

    internal LinuxInstallerProtocolJsonlHost(Func<Action<ProtocolEvent>, ILinuxInstallerProtocolHostSession> createSession)
    {
        this.CreateSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
    }

    /// <summary>Run until clean input EOF, cancellation, or a fail-stop transport error.</summary>
    /// <remarks>
    /// Standard output is reserved exclusively for complete protocol event lines. Diagnostics are bounded, generic,
    /// and written only to <paramref name="diagnostics"/> so rejected input, paths, and tokens aren't reflected.
    /// A cancellation or fail-stop error may dispose <paramref name="input"/> to interrupt operating-system streams
    /// whose asynchronous reads don't observe cancellation until their descriptor is closed.
    /// </remarks>
    public async Task<int> RunAsync(Stream input, Stream output, TextWriter diagnostics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!input.CanRead) throw new ArgumentException("The protocol input stream must be readable.", nameof(input));
        if (!output.CanWrite) throw new ArgumentException("The protocol output stream must be writable.", nameof(output));

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenRegistration inputInterruption = lifetime.Token.Register(() =>
        {
            try { input.Dispose(); }
            catch { }
        });
        // Request cancellation is separate from transport cancellation. Controller EOF must stop admitted
        // backend work while leaving the output writer alive long enough to drain a safe terminal response.
        using CancellationTokenSource admittedRequests = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        int failureCode = 0;
        using CancellationTokenRegistration externalCancellation = cancellationToken.Register(() =>
        {
            Interlocked.CompareExchange(ref failureCode, CancelledExitCode, 0);
        });

        Channel<ProtocolEvent> outbound = Channel.CreateBounded<ProtocolEvent>(new BoundedChannelOptions(OutboundEventCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        void Fail(int exitCode)
        {
            Interlocked.CompareExchange(ref failureCode, exitCode, 0);
            lifetime.Cancel();
        }

        void PublishProgress(ProtocolEvent value)
        {
            if (!outbound.Writer.TryWrite(value))
                Fail(FailureExitCode);
        }

        ILinuxInstallerProtocolHostSession? session = null;
        Task writer = WriteEventsAsync(output, outbound.Reader, Fail, lifetime.Token);
        List<Task> requests = new(MaximumInFlightRequests);
        Task<string?>? read = null;
        Task cancellationSignal = Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
        bool sessionDisposed = false;
        try
        {
            session = this.CreateSession(PublishProgress) ?? throw new InvalidOperationException("The protocol session factory returned null.");
            BoundedJsonLineReader reader = new(input);
            read = reader.ReadLineAsync(lifetime.Token).AsTask();
            bool eof = false;

            while (!eof && Volatile.Read(ref failureCode) == 0)
            {
                if (requests.Count == MaximumInFlightRequests)
                {
                    Task completed = await Task.WhenAny(requests.Append(cancellationSignal)).ConfigureAwait(false);
                    if (ReferenceEquals(completed, cancellationSignal))
                        await cancellationSignal.ConfigureAwait(false);
                    requests.Remove(completed);
                    await completed.ConfigureAwait(false);
                    continue;
                }

                Task completedWork = await Task.WhenAny(requests.Append(read!).Append(cancellationSignal)).ConfigureAwait(false);
                if (ReferenceEquals(completedWork, cancellationSignal))
                    await cancellationSignal.ConfigureAwait(false);
                if (!ReferenceEquals(completedWork, read))
                {
                    requests.Remove(completedWork);
                    await completedWork.ConfigureAwait(false);
                    continue;
                }

                string? line = await read!.ConfigureAwait(false);
                if (line is null)
                {
                    read = null;
                    eof = true;
                    break;
                }

                ProtocolRequest request = ProtocolJsonSerializer.DeserializeRequestLine(line);
                foreach (Task completed in requests.Where(value => value.IsCompleted).ToArray())
                {
                    requests.Remove(completed);
                    await completed.ConfigureAwait(false);
                }
                if (requests.Count != 0 && request is not CancelPlanRequest and not CancelPruneRequest)
                    throw new ProtocolException("Only a cancellation command may overlap an active command.");
                requests.Add(ProcessRequestAsync(session, request, outbound.Writer, admittedRequests.Token, lifetime.Token));
                read = reader.ReadLineAsync(lifetime.Token).AsTask();
            }

            // EOF means the controller has disappeared. Cancel every admitted caller token first, then
            // dispose the session so both CommandGate-held calls and tracked long operations durably settle.
            // Keep the separate transport token live so any resulting safe terminal response can drain.
            if (eof && Volatile.Read(ref failureCode) == 0)
            {
                admittedRequests.Cancel();
                await session.DisposeAsync().ConfigureAwait(false);
                sessionDisposed = true;
            }
            if (Volatile.Read(ref failureCode) == 0)
                await Task.WhenAll(requests).ConfigureAwait(false);
        }
        catch (ProtocolFramingException)
        {
            Fail(InvalidInputExitCode);
            await WriteDiagnosticAsync(diagnostics, "Protocol input was rejected.").ConfigureAwait(false);
        }
        catch (ProtocolException)
        {
            Fail(InvalidInputExitCode);
            await WriteDiagnosticAsync(diagnostics, "Protocol input was rejected.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            if (Volatile.Read(ref failureCode) == 0)
                Fail(cancellationToken.IsCancellationRequested ? CancelledExitCode : FailureExitCode);
        }
        catch (Exception) when (lifetime.IsCancellationRequested)
        {
            if (Volatile.Read(ref failureCode) == 0)
                Fail(cancellationToken.IsCancellationRequested ? CancelledExitCode : FailureExitCode);
        }
        catch
        {
            Fail(FailureExitCode);
            await WriteDiagnosticAsync(diagnostics, "Protocol host failed.").ConfigureAwait(false);
        }
        finally
        {
            if (Volatile.Read(ref failureCode) != 0)
                lifetime.Cancel();

            if (session is not null && !sessionDisposed)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch { Fail(FailureExitCode); }
            }

            try { await Task.WhenAll(requests).ConfigureAwait(false); }
            catch { if (Volatile.Read(ref failureCode) == 0) Fail(FailureExitCode); }

            if (read is not null)
                ObserveAbandonedRead(read);

            outbound.Writer.TryComplete();
            try { await writer.ConfigureAwait(false); }
            catch { Fail(FailureExitCode); }

            // Release the infinite cancellation race task without changing the established exit result.
            // On clean EOF the read has completed, so don't dispose the caller's input just for this cleanup.
            inputInterruption.Dispose();
            lifetime.Cancel();
        }

        int result = Volatile.Read(ref failureCode);
        if (result == FailureExitCode)
            await WriteDiagnosticAsync(diagnostics, "Protocol transport stopped safely.").ConfigureAwait(false);
        else if (result == CancelledExitCode)
            await WriteDiagnosticAsync(diagnostics, "Protocol host was cancelled.").ConfigureAwait(false);
        return result;
    }

    private static async Task ProcessRequestAsync(
        ILinuxInstallerProtocolHostSession session,
        ProtocolRequest request,
        ChannelWriter<ProtocolEvent> outbound,
        CancellationToken requestCancellationToken,
        CancellationToken transportCancellationToken
    )
    {
        ProtocolEvent response;
        try
        {
            response = await session.HandleAsync(request, requestCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            requestCancellationToken.IsCancellationRequested
            && !transportCancellationToken.IsCancellationRequested
        )
        {
            // Clean controller EOF owns the admitted request token. A call cancelled before entering the
            // service CommandGate has no typed terminal response, but it is safely settled and must not
            // turn clean EOF into a transport failure.
            return;
        }
        await outbound.WriteAsync(response, transportCancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteEventsAsync(Stream output, ChannelReader<ProtocolEvent> outbound, Action<int> fail, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ProtocolEvent value in outbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                string line = ProtocolJsonSerializer.SerializeLine(value);
                byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            fail(FailureExitCode);
            throw;
        }
    }

    private static async Task WriteDiagnosticAsync(TextWriter diagnostics, string message)
    {
        try { await diagnostics.WriteLineAsync(message).ConfigureAwait(false); }
        catch { }
    }

    private static void ObserveAbandonedRead(Task<string?> read)
    {
        _ = read.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }
}

internal interface ILinuxInstallerProtocolHostSession : IAsyncDisposable
{
    Task<ProtocolEvent> HandleAsync(ProtocolRequest request, CancellationToken cancellationToken);
}

internal sealed class LinuxInstallerProtocolHostSession(LinuxInstallerProtocolService service) : ILinuxInstallerProtocolHostSession
{
    public Task<ProtocolEvent> HandleAsync(ProtocolRequest request, CancellationToken cancellationToken) => service.HandleAsync(request, cancellationToken);
    public ValueTask DisposeAsync() => service.DisposeAsync();
}

internal sealed class BoundedJsonLineReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream Input;
    private readonly byte[] ReadBuffer = new byte[4096];
    private readonly byte[] LineBuffer = new byte[ProtocolJsonSerializer.MaxLineBytes];
    private int ReadOffset;
    private int ReadLength;
    private int LineLength;

    public BoundedJsonLineReader(Stream input)
    {
        this.Input = input;
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int newline = Array.IndexOf(this.ReadBuffer, (byte)'\n', this.ReadOffset, this.ReadLength - this.ReadOffset);
            if (newline >= 0)
            {
                this.Append(this.ReadBuffer.AsSpan(this.ReadOffset, newline - this.ReadOffset));
                this.ReadOffset = newline + 1;
                string result = this.DecodeLine();
                this.LineLength = 0;
                return result;
            }

            if (this.ReadOffset < this.ReadLength)
                this.Append(this.ReadBuffer.AsSpan(this.ReadOffset, this.ReadLength - this.ReadOffset));
            this.ReadOffset = 0;
            this.ReadLength = await this.Input.ReadAsync(this.ReadBuffer, cancellationToken).ConfigureAwait(false);
            if (this.ReadLength != 0)
                continue;
            if (this.LineLength != 0)
                throw new ProtocolFramingException("The final protocol line wasn't terminated.");
            return null;
        }
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > this.LineBuffer.Length - this.LineLength)
            throw new ProtocolFramingException("The protocol line exceeded its byte limit.");
        bytes.CopyTo(this.LineBuffer.AsSpan(this.LineLength));
        this.LineLength += bytes.Length;
    }

    private string DecodeLine()
    {
        try { return StrictUtf8.GetString(this.LineBuffer, 0, this.LineLength); }
        catch (DecoderFallbackException exception) { throw new ProtocolFramingException("The protocol line wasn't valid UTF-8.", exception); }
    }
}

internal sealed class ProtocolFramingException : Exception
{
    public ProtocolFramingException(string message) : base(message) { }
    public ProtocolFramingException(string message, Exception innerException) : base(message, innerException) { }
}
