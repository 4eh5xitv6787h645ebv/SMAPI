using System.Diagnostics;
using System.Text;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>Owns one fail-stop JSONL session with the packaged sibling installer.</summary>
internal sealed class ProcessInstallerProtocolClient : IInstallerProtocolClient
{
    internal const string ProtocolFlag = "--linux-protocol-v1-jsonl";
    internal const string PackageVerificationCapability = "verified-local-package";
    internal const int MaximumObservedStderrBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultReapTimeout = TimeSpan.FromSeconds(5);

    private readonly string InstallerPath;
    private readonly IInstallerProtocolProcessFactory ProcessFactory;
    private readonly TimeSpan OperationTimeout;
    private readonly TimeSpan ReapTimeout;
    private readonly SemaphoreSlim CommandGate = new(1, 1);
    private readonly CancellationTokenSource Lifetime = new();
    private readonly object CleanupLock = new();
    private IInstallerProtocolProcess? Process;
    private StrictJsonLineReader? Reader;
    private Task? StderrDrain;
    private ProtocolSessionId? SessionId;
    private bool PackageOpened;
    private int CleanupStarted;
    private int DisposeStarted;
    private int ObservedStderrBytesValue;
    private Task? CleanupTask;

    internal int ObservedStderrBytes => Volatile.Read(ref this.ObservedStderrBytesValue);

    private ProcessInstallerProtocolClient(string installerPath, IInstallerProtocolProcessFactory processFactory, TimeSpan operationTimeout, TimeSpan reapTimeout)
    {
        this.InstallerPath = installerPath;
        this.ProcessFactory = processFactory;
        this.OperationTimeout = operationTimeout;
        this.ReapTimeout = reapTimeout;
    }

    /// <summary>Create a client whose executable authority comes only from the current GUI's packaged sibling.</summary>
    public static ProcessInstallerProtocolClient CreateForCurrentProcess()
    {
        string guiExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The graphical installer executable path isn't available.");
        return new(SiblingInstallerLocator.Locate(guiExecutable), new SystemInstallerProtocolProcessFactory(), DefaultOperationTimeout, DefaultReapTimeout);
    }

    internal static ProcessInstallerProtocolClient CreateForTesting(
        string installerPath,
        IInstallerProtocolProcessFactory processFactory,
        TimeSpan? operationTimeout = null,
        TimeSpan? reapTimeout = null
    ) => new(
        installerPath,
        processFactory ?? throw new ArgumentNullException(nameof(processFactory)),
        operationTimeout ?? DefaultOperationTimeout,
        reapTimeout ?? DefaultReapTimeout
    );

    public async Task<HandshakeEvent> HandshakeAsync(string clientName, string clientVersion, CancellationToken cancellationToken = default)
    {
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            if (this.SessionId is not null)
                throw new InstallerProtocolClientException("The installer backend handshake was already completed.");

            HandshakeRequest request = new(clientName, clientVersion);
            HandshakeEvent response = await this.ExchangeAsync<HandshakeEvent>(request, cancellationToken).ConfigureAwait(false);
            if (!response.Capabilities.Contains(PackageVerificationCapability, StringComparer.Ordinal))
                return await this.FailProtocolAsync<HandshakeEvent>().ConfigureAwait(false);
            this.SessionId = response.SessionId;
            return response;
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    public async Task<PackageOpenedEvent> OpenPackageAsync(InstallerPackageOpenInput package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            ProtocolSessionId session = this.SessionId
                ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
            if (this.PackageOpened)
                throw new InstallerProtocolClientException("A package was already opened in this installer backend session.");

            OpenPackageRequest request = new(
                session,
                package.ReleaseTag,
                package.ExpectedSourceCommit,
                package.PackagePath,
                package.ChecksumsPath,
                package.BuildMetadataPath,
                package.InstallManifestPath,
                package.AttestationBundlePath,
                package.AttestationBundleChecksumPath
            );
            PackageOpenedEvent response = await this.ExchangeAsync<PackageOpenedEvent>(request, cancellationToken).ConfigureAwait(false);
            if (response.SessionId != session)
                await this.FailProtocolAsync().ConfigureAwait(false);
            this.PackageOpened = true;
            return response;
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.DisposeStarted, 1) != 0)
            return;
        await this.CleanupAsync(allowCleanExit: true).ConfigureAwait(false);
        await this.CommandGate.WaitAsync().ConfigureAwait(false);
        this.CommandGate.Release();
        this.Lifetime.Dispose();
        this.CommandGate.Dispose();
    }

    private async Task<TEvent> ExchangeAsync<TEvent>(ProtocolRequest request, CancellationToken callerToken)
        where TEvent : ProtocolEvent
    {
        string line;
        try
        {
            line = ProtocolJsonSerializer.SerializeLine(request);
        }
        catch
        {
            throw new InstallerProtocolClientException("The installer backend request was rejected safely.");
        }
        this.EnsureStarted();

        using CancellationTokenSource timeout = new(this.OperationTimeout);
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeout.Token, this.Lifetime.Token);
        try
        {
            byte[] bytes = StrictUtf8.GetBytes(line + "\n");
            await this.AwaitTransportAsync(this.Process!.Input.WriteAsync(bytes, operation.Token).AsTask(), operation.Token).ConfigureAwait(false);
            await this.AwaitTransportAsync(this.Process.Input.FlushAsync(operation.Token), operation.Token).ConfigureAwait(false);
            string? responseLine = await this.AwaitTransportAsync(this.Reader!.ReadLineAsync(operation.Token).AsTask(), operation.Token).ConfigureAwait(false);
            if (responseLine is null)
                return await this.FailProtocolAsync<TEvent>().ConfigureAwait(false);

            ProtocolEvent response = ProtocolJsonSerializer.DeserializeEventLine(responseLine);
            if (response is not TEvent typed || response.CommandId != request.CommandId)
                return await this.FailProtocolAsync<TEvent>().ConfigureAwait(false);
            return typed;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
            throw new InstallerProtocolClientException("The installer backend did not respond within its bounded deadline.");
        }
        catch (InstallerProtocolClientException)
        {
            await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
            throw new InstallerProtocolClientException("The installer backend transport stopped safely.");
        }
    }

    private void EnsureStarted()
    {
        if (this.Process is not null)
            return;

        ProcessStartInfo start = new()
        {
            FileName = this.InstallerPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(ProtocolFlag);
        try
        {
            this.Process = this.ProcessFactory.Start(start)
                ?? throw new InvalidOperationException("The process factory returned null.");
            this.Reader = new StrictJsonLineReader(this.Process.Output);
            this.StderrDrain = this.DrainStderrAsync(this.Process.Error, this.Lifetime.Token);
        }
        catch
        {
            throw new InstallerProtocolClientException("The packaged installer backend could not be started safely.");
        }
    }

    private async Task<T> AwaitTransportAsync<T>(Task<T> transport, CancellationToken cancellationToken)
    {
        Task cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        Task completed = await Task.WhenAny(transport, cancelled).ConfigureAwait(false);
        if (ReferenceEquals(completed, transport))
            return await transport.ConfigureAwait(false);

        await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
        ObserveAbandoned(transport);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InstallerProtocolClientException("The installer backend transport stopped safely.");
    }

    private async Task AwaitTransportAsync(Task transport, CancellationToken cancellationToken)
    {
        Task cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        Task completed = await Task.WhenAny(transport, cancelled).ConfigureAwait(false);
        if (ReferenceEquals(completed, transport))
        {
            await transport.ConfigureAwait(false);
            return;
        }

        await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
        ObserveAbandoned(transport);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task DrainStderrAsync(Stream error, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (true)
            {
                int read = await error.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return;
                int current = Volatile.Read(ref this.ObservedStderrBytesValue);
                while (current < MaximumObservedStderrBytes)
                {
                    int next = Math.Min(MaximumObservedStderrBytes, current + read);
                    int observed = Interlocked.CompareExchange(ref this.ObservedStderrBytesValue, next, current);
                    if (observed == current)
                        break;
                    current = observed;
                }
            }
        }
        catch when (cancellationToken.IsCancellationRequested) { }
        catch { }
    }

    private async Task<T> FailProtocolAsync<T>()
    {
        await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
        throw new InstallerProtocolClientException("The installer backend returned an invalid response and was stopped safely.");
    }

    private async Task FailProtocolAsync() => await this.FailProtocolAsync<object>().ConfigureAwait(false);

    private Task CleanupAsync(bool allowCleanExit)
    {
        lock (this.CleanupLock)
        {
            if (this.CleanupTask is not null)
                return this.CleanupTask;
            Volatile.Write(ref this.CleanupStarted, 1);
            return this.CleanupTask = this.CleanupCoreAsync(allowCleanExit);
        }
    }

    private async Task CleanupCoreAsync(bool allowCleanExit)
    {
        this.Lifetime.Cancel();
        IInstallerProtocolProcess? process = this.Process;
        if (process is null)
            return;

        try { process.Input.Dispose(); } catch { }
        if (allowCleanExit && await WaitBoundedAsync(process.WaitForExitAsync(), this.ReapTimeout).ConfigureAwait(false))
        {
            await this.FinishCleanupAsync(process).ConfigureAwait(false);
            return;
        }

        try { process.Terminate(); } catch { }
        Task reap = process.WaitForExitAsync();
        if (!await WaitBoundedAsync(reap, this.ReapTimeout).ConfigureAwait(false))
            ObserveAbandoned(reap);
        await this.FinishCleanupAsync(process).ConfigureAwait(false);
    }

    private async Task FinishCleanupAsync(IInstallerProtocolProcess process)
    {
        try { process.Output.Dispose(); } catch { }
        try { process.Error.Dispose(); } catch { }
        if (this.StderrDrain is { } stderr && !await WaitBoundedAsync(stderr, this.ReapTimeout).ConfigureAwait(false))
            ObserveAbandoned(stderr);
        process.Dispose();
    }

    private static async Task<bool> WaitBoundedAsync(Task operation, TimeSpan timeout)
    {
        Task completed = await Task.WhenAny(operation, Task.Delay(timeout)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, operation))
            return false;
        try { await operation.ConfigureAwait(false); }
        catch { }
        return true;
    }

    private static void ObserveAbandoned(Task operation)
    {
        _ = operation.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private void AssertUsable()
    {
        if (Volatile.Read(ref this.DisposeStarted) != 0 || Volatile.Read(ref this.CleanupStarted) != 0)
            throw new ObjectDisposedException(nameof(ProcessInstallerProtocolClient));
    }
}

internal interface IInstallerProtocolProcessFactory
{
    IInstallerProtocolProcess Start(ProcessStartInfo startInfo);
}

internal interface IInstallerProtocolProcess : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    Stream Error { get; }
    Task WaitForExitAsync();
    void Terminate();
}

internal sealed class SystemInstallerProtocolProcessFactory : IInstallerProtocolProcessFactory
{
    public IInstallerProtocolProcess Start(ProcessStartInfo startInfo)
    {
        Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The packaged installer backend did not start.");
        }
        return new SystemInstallerProtocolProcess(process);
    }
}

internal sealed class SystemInstallerProtocolProcess(Process process) : IInstallerProtocolProcess
{
    public Stream Input => process.StandardInput.BaseStream;
    public Stream Output => process.StandardOutput.BaseStream;
    public Stream Error => process.StandardError.BaseStream;
    public Task WaitForExitAsync() => process.WaitForExitAsync();
    public void Terminate()
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
    }
    public void Dispose() => process.Dispose();
}

internal sealed class StrictJsonLineReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream Input;
    private readonly byte[] ReadBuffer = new byte[4096];
    private readonly byte[] LineBuffer = new byte[ProtocolJsonSerializer.MaxLineBytes];
    private int ReadOffset;
    private int ReadLength;
    private int LineLength;

    public StrictJsonLineReader(Stream input) => this.Input = input;

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int newline = Array.IndexOf(this.ReadBuffer, (byte)'\n', this.ReadOffset, this.ReadLength - this.ReadOffset);
            if (newline >= 0)
            {
                this.Append(this.ReadBuffer.AsSpan(this.ReadOffset, newline - this.ReadOffset));
                this.ReadOffset = newline + 1;
                string result = this.Decode();
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
                throw new InstallerProtocolClientException("The installer backend returned an incomplete response and was stopped safely.");
            return null;
        }
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > this.LineBuffer.Length - this.LineLength)
            throw new InstallerProtocolClientException("The installer backend response exceeded its bounded framing limit.");
        bytes.CopyTo(this.LineBuffer.AsSpan(this.LineLength));
        this.LineLength += bytes.Length;
    }

    private string Decode()
    {
        try { return StrictUtf8.GetString(this.LineBuffer, 0, this.LineLength); }
        catch (DecoderFallbackException) { throw new InstallerProtocolClientException("The installer backend response wasn't valid UTF-8 and was stopped safely."); }
    }
}
