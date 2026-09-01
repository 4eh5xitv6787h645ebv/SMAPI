using System.Diagnostics;
using System.Text;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>Owns one fail-stop JSONL session with the packaged sibling installer.</summary>
internal sealed class ProcessInstallerProtocolClient : IInstallerProtocolClient
{
    internal const string ProtocolFlag = "--linux-protocol-v1-jsonl";
    internal const string PackageVerificationCapability = "verified-local-package";
    internal const string GameDiscoveryCapability = "linux-game-discovery";
    internal const string GameValidationCapability = "linux-game-validation";
    internal const int MaximumObservedStderrBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultReapTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultPartialFrameTimeout = TimeSpan.FromSeconds(2);
    private static readonly object ProductionQuarantineLock = new();
    private static (IInstallerProtocolProcess Process, LinuxExternalExecutableLease Executable)? ProductionQuarantine;
    private static bool ProductionLaunchDisabled;
    private static bool ProductionClientActive;

    private readonly string InstallerPath;
    private readonly IInstallerProtocolProcessFactory ProcessFactory;
    private readonly TimeSpan OperationTimeout;
    private readonly TimeSpan ReapTimeout;
    private readonly TimeSpan PartialFrameTimeout;
    private readonly LinuxExternalExecutableLease? ExecutableLease;
    private readonly bool IsProduction;
    private readonly SemaphoreSlim CommandGate = new(1, 1);
    private readonly CancellationTokenSource Lifetime = new();
    private readonly object CleanupLock = new();
    private readonly object DisposeLock = new();
    private readonly object ResponseLock = new();
    private readonly TaskCompletionSource<InstallerProtocolClientException> SessionFault = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IInstallerProtocolProcess? Process;
    private Stream? ProcessInput;
    private Stream? ProcessOutput;
    private Stream? ProcessError;
    private StrictJsonLineReader? Reader;
    private Task? ReaderPump;
    private PendingProtocolResponse? PendingResponse;
    private Task? StderrDrain;
    private ProtocolSessionId? SessionId;
    private ProtocolPackageId? VerifiedPackageId;
    private ProtocolReleaseIdentity? VerifiedRelease;
    private int CleanupStarted;
    private int DisposeStarted;
    private int ObservedStderrBytesValue;
    private int CleanupUnconfirmed;
    private Task? CleanupTask;
    private Task? DisposalTask;
    private bool SessionFaultRaised;
    internal Action? BeforePackageAuthorityCommitForTesting { get; set; }

    internal int ObservedStderrBytes => Volatile.Read(ref this.ObservedStderrBytesValue);
    internal bool CleanupConfirmed => Volatile.Read(ref this.CleanupUnconfirmed) == 0;
    internal static bool IsProductionQuarantineClearedForTesting
    {
        get
        {
            lock (ProductionQuarantineLock)
                return ProductionQuarantine is null;
        }
    }
    internal bool HasRetainedPackageAuthority
    {
        get
        {
            lock (this.ResponseLock)
                return this.VerifiedPackageId is not null && this.VerifiedRelease is not null;
        }
    }
    public Task<InstallerProtocolClientException> SessionFaulted => this.SessionFault.Task;

    private ProcessInstallerProtocolClient(string installerPath, IInstallerProtocolProcessFactory processFactory, TimeSpan operationTimeout, TimeSpan reapTimeout, TimeSpan partialFrameTimeout, LinuxExternalExecutableLease? executableLease, bool isProduction)
    {
        this.InstallerPath = installerPath;
        this.ProcessFactory = processFactory;
        this.OperationTimeout = operationTimeout;
        this.ReapTimeout = reapTimeout;
        this.PartialFrameTimeout = partialFrameTimeout;
        this.ExecutableLease = executableLease;
        this.IsProduction = isProduction;
    }

    /// <summary>Create a client whose executable authority comes only from the current GUI's packaged sibling.</summary>
    public static ProcessInstallerProtocolClient CreateForCurrentProcess()
    {
        return CreateProduction(
            SiblingInstallerLocator.OpenForCurrentProcess,
            new SystemInstallerProtocolProcessFactory(),
            DefaultOperationTimeout,
            DefaultReapTimeout,
            DefaultPartialFrameTimeout
        );
    }

    internal static ProcessInstallerProtocolClient CreateProductionForTesting(
        Func<LinuxExternalExecutableLease> executableFactory,
        IInstallerProtocolProcessFactory processFactory,
        TimeSpan? operationTimeout = null,
        TimeSpan? reapTimeout = null
    ) => CreateProduction(
        executableFactory,
        processFactory,
        operationTimeout ?? DefaultOperationTimeout,
        reapTimeout ?? DefaultReapTimeout,
        DefaultPartialFrameTimeout
    );

    internal static void ResetProductionGateForTesting()
    {
        lock (ProductionQuarantineLock)
        {
            if (ProductionQuarantine is not null)
                throw new InvalidOperationException("A quarantined process is still awaiting confirmed reap.");
            ProductionLaunchDisabled = false;
            ProductionClientActive = false;
        }
    }

    internal static ProcessInstallerProtocolClient CreateForTesting(
        string installerPath,
        IInstallerProtocolProcessFactory processFactory,
        TimeSpan? operationTimeout = null,
        TimeSpan? reapTimeout = null,
        LinuxExternalExecutableLease? executableLease = null,
        TimeSpan? partialFrameTimeout = null
    ) => new(
        installerPath,
        processFactory ?? throw new ArgumentNullException(nameof(processFactory)),
        operationTimeout ?? DefaultOperationTimeout,
        reapTimeout ?? DefaultReapTimeout,
        partialFrameTimeout ?? DefaultPartialFrameTimeout,
        executableLease,
        false
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
            if (
                !response.Capabilities.Contains(PackageVerificationCapability, StringComparer.Ordinal)
                || !response.Capabilities.Contains(GameDiscoveryCapability, StringComparer.Ordinal)
                || !response.Capabilities.Contains(GameValidationCapability, StringComparer.Ordinal)
            )
                return await this.FailProtocolAsync<HandshakeEvent>().ConfigureAwait(false);
            this.SessionId = response.SessionId;
            return response;
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    public async Task<InstallerPackageOpenResult> OpenPackageAsync(InstallerPackageOpenInput package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            ProtocolSessionId session = this.SessionId
                ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
            if (this.HasRetainedPackageAuthority)
                throw new InstallerProtocolClientException("A package was already opened in this installer backend session.");
            string packageAssetName = GetSafeLinuxFileName(package.PackagePath);

            OpenPackageRequest request = new(
                session,
                package.ReleaseTag,
                package.ExpectedSourceCommit,
                package.PackagePath,
                package.ChecksumsPath,
                package.BuildMetadataPath,
                package.InstallManifestPath,
                package.AttestationBundlePath,
                package.AttestationBundleChecksumPath,
                package.ProcWorkspaceIdentity
            );
            ProtocolEvent response = await this.ExchangeAsync<ProtocolEvent>(request, cancellationToken).ConfigureAwait(false);
            switch (response)
            {
                case PackageOpenedEvent opened when
                    opened.SessionId == session
                    && string.Equals(opened.Release.Tag, package.ReleaseTag, StringComparison.Ordinal)
                    && string.Equals(opened.Release.SourceCommit, package.ExpectedSourceCommit, StringComparison.Ordinal)
                    && string.Equals(opened.Release.PackageAssetName, packageAssetName, StringComparison.Ordinal):
                    this.BeforePackageAuthorityCommitForTesting?.Invoke();
                    if (!this.TryCommitPackageAuthority(opened))
                        return await this.FailProtocolAsync<InstallerPackageOpenResult>().ConfigureAwait(false);
                    return new InstallerPackageOpenSuccess(opened.Release);

                case PrePlanRejectedEvent rejected when rejected.SessionId == session:
                    return new InstallerPackageOpenRejection(rejected.ErrorCode, rejected.NextAction, rejected.Message, rejected.IsTerminal);

                default:
                    return await this.FailProtocolAsync<InstallerPackageOpenResult>().ConfigureAwait(false);
            }
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    public async Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
    {
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            ProtocolSessionId session = this.SessionId
                ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
            GameDiscoveryEvent response = await this.ExchangeAsync<GameDiscoveryEvent>(
                new DiscoverGamesRequest(session),
                cancellationToken
            ).ConfigureAwait(false);
            if (response.SessionId != session)
                return await this.FailProtocolAsync<IReadOnlyList<ProtocolGameCandidate>>().ConfigureAwait(false);
            return Array.AsReadOnly(response.Candidates);
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    public async Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            ProtocolSessionId session = this.SessionId
                ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
            GameValidationEvent response = await this.ExchangeAsync<GameValidationEvent>(
                new ValidateGameRequest(session, canonicalPath),
                cancellationToken
            ).ConfigureAwait(false);
            if (response.SessionId != session)
                return await this.FailProtocolAsync<ProtocolGameCandidate>().ConfigureAwait(false);
            return response.Candidate;
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (this.DisposeLock)
        {
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            Volatile.Write(ref this.DisposeStarted, 1);
            return new ValueTask(this.DisposalTask = this.DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await this.CleanupAsync(allowCleanExit: true).ConfigureAwait(false);
        await this.CommandGate.WaitAsync().ConfigureAwait(false);
        this.CommandGate.Release();
        this.Lifetime.Dispose();
        this.CommandGate.Dispose();
    }

    private static ProcessInstallerProtocolClient CreateProduction(
        Func<LinuxExternalExecutableLease> executableFactory,
        IInstallerProtocolProcessFactory processFactory,
        TimeSpan operationTimeout,
        TimeSpan reapTimeout,
        TimeSpan partialFrameTimeout
    )
    {
        ArgumentNullException.ThrowIfNull(executableFactory);
        ArgumentNullException.ThrowIfNull(processFactory);
        lock (ProductionQuarantineLock)
        {
            if (ProductionLaunchDisabled)
                throw new InvalidOperationException("A prior backend process could not be reaped; production launch is disabled until restart.");
            if (ProductionClientActive)
                throw new InvalidOperationException("A production installer backend client is already active.");
            ProductionClientActive = true;
        }

        LinuxExternalExecutableLease? executable = null;
        try
        {
            executable = executableFactory();
            return new(executable.ProcPath, processFactory, operationTimeout, reapTimeout, partialFrameTimeout, executable, true);
        }
        catch
        {
            executable?.Dispose();
            lock (ProductionQuarantineLock)
                ProductionClientActive = false;
            throw;
        }
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
        await this.EnsureStartedAsync().ConfigureAwait(false);
        TaskCompletionSource<ProtocolEvent> responseCompletion = this.RegisterPendingResponse(request.CommandId);

        using CancellationTokenSource timeout = new(this.OperationTimeout);
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeout.Token, this.Lifetime.Token);
        try
        {
            byte[] bytes = StrictUtf8.GetBytes(line + "\n");
            await this.AwaitTransportAsync(this.ProcessInput!.WriteAsync(bytes, operation.Token).AsTask(), operation.Token).ConfigureAwait(false);
            await this.AwaitTransportAsync(this.ProcessInput.FlushAsync(operation.Token), operation.Token).ConfigureAwait(false);
            ProtocolEvent response = await this.AwaitTransportAsync(responseCompletion.Task, operation.Token).ConfigureAwait(false);
            if (this.SessionFault.Task.IsCompletedSuccessfully)
                throw await this.SessionFault.Task.ConfigureAwait(false);
            if (response is not TEvent typed)
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
            throw new InstallerProtocolClientException(this.CleanupConfirmed
                ? "The installer backend did not respond within its bounded deadline and was stopped."
                : "The installer backend did not respond within its bounded deadline, and termination could not be confirmed.");
        }
        catch (InstallerProtocolClientException)
        {
            await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
            if (!this.CleanupConfirmed)
                throw new InstallerProtocolClientException("The installer backend failed, and termination could not be confirmed.");
            throw;
        }
        catch
        {
            await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
            throw new InstallerProtocolClientException(this.CleanupConfirmed
                ? "The installer backend transport stopped."
                : "The installer backend transport failed, and termination could not be confirmed.");
        }
        finally
        {
            this.ClearPendingResponse(responseCompletion);
        }
    }

    private static string GetSafeLinuxFileName(string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
            throw new InstallerProtocolClientException("The local installer package path isn't a safe absolute Linux filename.");
        string fileName = Path.GetFileName(path);
        if (
            string.IsNullOrEmpty(fileName)
            || fileName is "." or ".."
            || fileName.IndexOfAny(['/', '\\']) >= 0
            || fileName.Any(char.IsControl)
        )
        {
            throw new InstallerProtocolClientException("The local installer package path isn't a safe absolute Linux filename.");
        }
        return fileName;
    }

    private async Task EnsureStartedAsync()
    {
        if (this.Process is not null)
            return;

        ProcessStartInfo start = new()
        {
            FileName = this.ExecutableLease?.ProcPath ?? this.InstallerPath,
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
            this.ProcessInput = this.Process.Input;
            this.ProcessOutput = this.Process.Output;
            this.ProcessError = this.Process.Error;
            this.Reader = new StrictJsonLineReader(this.ProcessOutput, this.PartialFrameTimeout);
            this.StderrDrain = this.DrainStderrAsync(this.ProcessError, this.Lifetime.Token);
            this.ReaderPump = this.PumpResponsesAsync(this.Lifetime.Token);
        }
        catch
        {
            await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
            throw new InstallerProtocolClientException(this.CleanupConfirmed
                ? "The packaged installer backend could not be started."
                : "The packaged installer backend could not be started, and termination could not be confirmed.");
        }
    }

    private TaskCompletionSource<ProtocolEvent> RegisterPendingResponse(ProtocolCommandId commandId)
    {
        TaskCompletionSource<ProtocolEvent> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (this.ResponseLock)
        {
            if (this.PendingResponse is not null)
                throw new InstallerProtocolClientException("The installer backend already has an active command.");
            this.PendingResponse = new(commandId, completion);
        }
        return completion;
    }

    private void ClearPendingResponse(TaskCompletionSource<ProtocolEvent> completion)
    {
        lock (this.ResponseLock)
        {
            if (ReferenceEquals(this.PendingResponse?.Completion, completion))
                this.PendingResponse = null;
        }
    }

    private async Task PumpResponsesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                string? line = await this.Reader!.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    if (Volatile.Read(ref this.CleanupStarted) == 0)
                        this.FaultSession("The installer backend closed its response stream unexpectedly.");
                    return;
                }

                ProtocolEvent response = ProtocolJsonSerializer.DeserializeEventLine(line);
                PendingProtocolResponse? pending;
                lock (this.ResponseLock)
                {
                    pending = this.PendingResponse;
                    this.PendingResponse = null;
                }
                if (pending is null || response.CommandId != pending.CommandId)
                {
                    this.FaultSession("The installer backend emitted an unsolicited or incorrectly correlated response.");
                    return;
                }
                if (this.Reader.HasBufferedFrameData)
                {
                    InstallerProtocolClientException duplicate = new("The installer backend emitted duplicate buffered output.");
                    pending.Completion.TrySetException(duplicate);
                    this.FaultSession(duplicate.Message);
                    return;
                }
                pending.Completion.TrySetResult(response);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (InstallerProtocolClientException exception)
        {
            this.FaultSession(exception.Message);
        }
        catch
        {
            this.FaultSession("The installer backend response transport failed.");
        }
    }

    private void FaultSession(string message)
    {
        InstallerProtocolClientException fault = new(message);
        PendingProtocolResponse? pending;
        lock (this.ResponseLock)
        {
            if (this.SessionFaultRaised)
                return;
            this.SessionFaultRaised = true;
            this.VerifiedPackageId = null;
            this.VerifiedRelease = null;
            pending = this.PendingResponse;
            this.PendingResponse = null;
        }
        pending?.Completion.TrySetException(fault);
        this.SessionFault.TrySetResult(fault);
        _ = this.CleanupAsync(allowCleanExit: false);
    }

    private bool TryCommitPackageAuthority(PackageOpenedEvent opened)
    {
        lock (this.ResponseLock)
        {
            if (this.SessionFaultRaised || Volatile.Read(ref this.CleanupStarted) != 0)
                return false;
            this.VerifiedPackageId = opened.PackageId;
            this.VerifiedRelease = opened.Release;
            return true;
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
        throw new InstallerProtocolClientException(this.CleanupConfirmed
            ? "The installer backend transport stopped."
            : "The installer backend transport failed, and termination could not be confirmed.");
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
        throw new InstallerProtocolClientException(this.CleanupConfirmed
            ? "The installer backend returned an invalid response and was stopped."
            : "The installer backend returned an invalid response, and termination could not be confirmed.");
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
        bool leaseTransferred = false;
        try
        {
            this.Lifetime.Cancel();
            lock (this.ResponseLock)
            {
                this.VerifiedPackageId = null;
                this.VerifiedRelease = null;
            }
            IInstallerProtocolProcess? process = this.Process;
            if (process is null)
                return;

            try { (this.ProcessInput ?? process.Input).Dispose(); } catch { }
            if (allowCleanExit && await WaitBoundedAsync(GetWaitTask(process), this.ReapTimeout).ConfigureAwait(false))
            {
                await this.FinishCleanupAsync(process).ConfigureAwait(false);
                return;
            }

            try { process.Terminate(); } catch { }
            Task reap = GetWaitTask(process);
            if (!await WaitBoundedAsync(reap, this.ReapTimeout).ConfigureAwait(false))
            {
                Volatile.Write(ref this.CleanupUnconfirmed, 1);
                await this.FinishStreamCleanupAsync(process).ConfigureAwait(false);
                ReapAndDisposeLater(process, reap, this.ExecutableLease, this.IsProduction);
                leaseTransferred = this.ExecutableLease is not null;
                return;
            }
            await this.FinishCleanupAsync(process).ConfigureAwait(false);
        }
        finally
        {
            if (!leaseTransferred)
            {
                this.ExecutableLease?.Dispose();
                if (this.IsProduction)
                {
                    lock (ProductionQuarantineLock)
                        ProductionClientActive = false;
                }
            }
        }
    }

    private async Task FinishCleanupAsync(IInstallerProtocolProcess process)
    {
        await this.FinishStreamCleanupAsync(process).ConfigureAwait(false);
        process.Dispose();
    }

    private async Task FinishStreamCleanupAsync(IInstallerProtocolProcess process)
    {
        try { (this.ProcessOutput ?? process.Output).Dispose(); } catch { }
        try { (this.ProcessError ?? process.Error).Dispose(); } catch { }
        if (this.StderrDrain is { } stderr && !await WaitBoundedAsync(stderr, this.ReapTimeout).ConfigureAwait(false))
            ObserveAbandoned(stderr);
        if (this.ReaderPump is { } reader && !await WaitBoundedAsync(reader, this.ReapTimeout).ConfigureAwait(false))
            ObserveAbandoned(reader);
    }

    private static void ReapAndDisposeLater(IInstallerProtocolProcess process, Task reap, LinuxExternalExecutableLease? executableLease, bool production)
    {
        if (production)
        {
            LinuxExternalExecutableLease retained = executableLease
                ?? throw new InvalidOperationException("Production process quarantine requires retained executable authority.");
            lock (ProductionQuarantineLock)
            {
                ProductionLaunchDisabled = true;
                ProductionQuarantine ??= (process, retained);
            }
        }
        _ = reap.ContinueWith(
            completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    process.Dispose();
                    executableLease?.Dispose();
                    if (production)
                    {
                        lock (ProductionQuarantineLock)
                            ProductionQuarantine = null;
                    }
                }
                else
                {
                    _ = completed.Exception;
                    // Production retains one catastrophic quarantine slot until restart. A test-injected
                    // process remains retained by this continuation task and its owning test client.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private static async Task<bool> WaitBoundedAsync(Task operation, TimeSpan timeout)
    {
        Task completed = await Task.WhenAny(operation, Task.Delay(timeout)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, operation))
            return false;
        if (operation.Status != TaskStatus.RanToCompletion)
        {
            _ = operation.Exception;
            return false;
        }
        await operation.ConfigureAwait(false);
        return true;
    }

    private static Task GetWaitTask(IInstallerProtocolProcess process)
    {
        try
        {
            return process.WaitForExitAsync();
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
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
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The packaged installer backend did not start.");
            return new SystemInstallerProtocolProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
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

internal sealed record PendingProtocolResponse(ProtocolCommandId CommandId, TaskCompletionSource<ProtocolEvent> Completion);

internal sealed class StrictJsonLineReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream Input;
    private readonly TimeSpan PartialFrameTimeout;
    private readonly byte[] ReadBuffer = new byte[4096];
    private readonly byte[] LineBuffer = new byte[ProtocolJsonSerializer.MaxLineBytes];
    private int ReadOffset;
    private int ReadLength;
    private int LineLength;
    private long PartialFrameStarted;

    public StrictJsonLineReader(Stream input, TimeSpan partialFrameTimeout)
    {
        this.Input = input;
        this.PartialFrameTimeout = partialFrameTimeout;
    }

    public bool HasBufferedFrameData => this.LineLength != 0 || this.ReadOffset < this.ReadLength;

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
                this.PartialFrameStarted = 0;
                return result;
            }
            if (this.ReadOffset < this.ReadLength)
                this.Append(this.ReadBuffer.AsSpan(this.ReadOffset, this.ReadLength - this.ReadOffset));
            this.ReadOffset = 0;
            this.ReadLength = await this.ReadMoreAsync(cancellationToken).ConfigureAwait(false);
            if (this.ReadLength != 0)
                continue;
            if (this.LineLength != 0)
                throw new InstallerProtocolClientException("The installer backend returned an incomplete response.");
            return null;
        }
    }

    private async ValueTask<int> ReadMoreAsync(CancellationToken cancellationToken)
    {
        if (this.LineLength == 0)
            return await this.Input.ReadAsync(this.ReadBuffer, cancellationToken).ConfigureAwait(false);

        TimeSpan remaining = this.PartialFrameTimeout - Stopwatch.GetElapsedTime(this.PartialFrameStarted);
        if (remaining <= TimeSpan.Zero)
            throw new InstallerProtocolClientException("The installer backend left a partial response incomplete beyond its bounded deadline.");
        Task<int> read = this.Input.ReadAsync(this.ReadBuffer, cancellationToken).AsTask();
        Task deadline = Task.Delay(remaining, cancellationToken);
        Task completed = await Task.WhenAny(read, deadline).ConfigureAwait(false);
        if (ReferenceEquals(completed, read))
            return await read.ConfigureAwait(false);

        ObserveFault(read);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InstallerProtocolClientException("The installer backend left a partial response incomplete beyond its bounded deadline.");
    }

    private static void ObserveFault(Task operation)
    {
        _ = operation.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > this.LineBuffer.Length - this.LineLength)
            throw new InstallerProtocolClientException("The installer backend response exceeded its bounded framing limit.");
        if (this.LineLength == 0 && bytes.Length != 0)
            this.PartialFrameStarted = Stopwatch.GetTimestamp();
        bytes.CopyTo(this.LineBuffer.AsSpan(this.LineLength));
        this.LineLength += bytes.Length;
    }

    private string Decode()
    {
        try { return StrictUtf8.GetString(this.LineBuffer, 0, this.LineLength); }
        catch (DecoderFallbackException) { throw new InstallerProtocolClientException("The installer backend response wasn't valid UTF-8."); }
    }
}
