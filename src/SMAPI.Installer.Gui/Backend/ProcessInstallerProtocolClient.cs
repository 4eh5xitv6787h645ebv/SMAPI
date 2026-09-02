using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>Owns one fail-stop JSONL session with the packaged sibling installer.</summary>
internal sealed partial class ProcessInstallerProtocolClient : IInstallerProtocolClient
{
    internal const string ProtocolFlag = "--linux-protocol-v1-jsonl";
    internal const string PackageVerificationCapability = "verified-local-package";
    internal const string GameDiscoveryCapability = "linux-game-discovery";
    internal const string GameValidationCapability = "linux-game-validation";
    internal const string PlanInspectionCapability = "install-update-repair-uninstall-backup-rollback";
    internal const string CandidateApprovalCapability = "candidate-approval";
    internal const string ExactCoreProgressCapability = "exact-core-progress";
    internal const string CancellationCapability = "cancellation";
    internal const string InterruptedRecoveryCapability = "interrupted-operation-recovery";
    internal const string RecoveryPruningCapability = "recovery-pruning";
    internal const int MaximumObservedStderrBytes = 64 * 1024;
    internal const int MaximumPlanPageCount = 512;
    internal const int MaximumPlanAggregateUtf8Bytes = 16 * 1024 * 1024;
    // One million events exceeds the 640,000-unit legal maximum-capacity recovery envelope while still bounding a
    // hostile packaged sibling. The aggregate byte gate is independent, includes newlines, and is tested at N/N+1.
    internal const int MaximumExecutionProgressEvents = 1_000_000;
    internal const int MaximumExecutionProgressUnits = 640_000;
    internal const int MaximumRecoveryTransactions = 32;
    internal const int MaximumRecoveryPathsPerTransaction = 20_000;
    internal const int MaximumPruneProgressEvents = 256;
    internal const int MaximumPruneProgressUnits = ProtocolJsonSerializer.MaxRecoveryGenerations;
    internal const long MaximumPruneProgressUtf8Bytes = 4L * 1024 * 1024;
    internal const long MaximumExecutionProgressUtf8Bytes = 256L * 1024 * 1024;
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
    private ActiveExecutionRoute? ActiveExecution;
    private ActiveExecutionRoute? SettlingExecution;
    private ActiveRecoveryRoute? ActiveRecovery;
    private ActiveRecoveryRoute? SettlingRecovery;
    private Task? StderrDrain;
    private ProtocolSessionId? SessionId;
    private ProtocolPackageId? VerifiedPackageId;
    private ProtocolReleaseIdentity? VerifiedRelease;
    private RetainedRecoveryCatalogBinding? CurrentRecoveryCatalogBinding;
    private RetainedPlanBinding? CurrentPlanBinding;
    private RetainedConfirmedPlanBinding? CurrentConfirmedPlanBinding;
    private readonly HashSet<ProtocolCandidateId> IssuedCandidateIds = [];
    private readonly HashSet<ProtocolGameCandidate> DiscoveredGameCandidates = new(ReferenceEqualityComparer.Instance);
    private ProtocolGameCandidate? LatestValidatedGameCandidate;
    private int CleanupStarted;
    private int DisposeStarted;
    private int ExecutionAdmitted;
    private int RecoveryAdmitted;
    private int RecoveryEligibilityLost;
    private int ObservedStderrBytesValue;
    private int CleanupUnconfirmed;
    private Task? CleanupTask;
    private Task? DisposalTask;
    private bool SessionFaultRaised;
    internal Action? BeforePackageAuthorityCommitForTesting { get; set; }
    internal Action? BeforeRecoveryCatalogCommitForTesting { get; set; }
    internal Action? BeforePlanBindingCommitForTesting { get; set; }
    internal Action? BeforeConfirmationAuthorityCommitForTesting { get; set; }
    internal Action? BeforeExecutionWriteForTesting { get; set; }
    internal Func<Task>? BeforeExecuteWrittenCommitForTesting { get; set; }
    internal Action? ExecutionTerminalRoutedForTesting { get; set; }
    internal Func<Task>? BeforeExecutionSettlementForTesting { get; set; }
    internal Func<Task>? BeforePostCancellationDeadlineForTesting { get; set; }
    internal int IssuedCandidateCapacityForTesting { get; set; } = InstallerCandidateSelection.MaximumIssuedCandidatesPerSession;
    internal int ExecutionProgressCapacityForTesting { get; set; } = MaximumExecutionProgressEvents;
    internal long ExecutionProgressByteCapacityForTesting { get; set; } = MaximumExecutionProgressUtf8Bytes;
    internal TimeSpan ExecutionHardTimeoutForTesting { get; set; } = TimeSpan.FromMinutes(30);
    internal TimeSpan ExecutionIdleTimeoutForTesting { get; set; } = TimeSpan.FromMinutes(5);
    internal TimeSpan ExecutionCancellationAcknowledgementTimeoutForTesting { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan ExecutionPostCancellationTimeoutForTesting { get; set; } = TimeSpan.FromMinutes(30);
    internal Action? BeforeRecoveryWriteForTesting { get; set; }
    internal Action? BeforeRecoveryAdmissionForTesting { get; set; }
    internal Func<Task>? BeforeRecoveryWrittenCommitForTesting { get; set; }
    internal Action? RecoveryTerminalRoutedForTesting { get; set; }
    internal Func<Task>? BeforeRecoverySettlementForTesting { get; set; }
    internal int RecoveryProgressCapacityForTesting { get; set; } = MaximumExecutionProgressEvents;
    internal long RecoveryProgressByteCapacityForTesting { get; set; } = MaximumExecutionProgressUtf8Bytes;
    internal TimeSpan RecoveryHardTimeoutForTesting { get; set; } = TimeSpan.FromMinutes(30);
    internal TimeSpan RecoveryIdleTimeoutForTesting { get; set; } = TimeSpan.FromMinutes(5);

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
    internal bool HasRetainedRecoveryCatalogForTesting
    {
        get
        {
            lock (this.ResponseLock)
                return this.CurrentRecoveryCatalogBinding is not null;
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
                || !response.Capabilities.Contains(PlanInspectionCapability, StringComparer.Ordinal)
                || !response.Capabilities.Contains(CandidateApprovalCapability, StringComparer.Ordinal)
                || !response.Capabilities.Contains(ExactCoreProgressCapability, StringComparer.Ordinal)
                || !response.Capabilities.Contains(CancellationCapability, StringComparer.Ordinal)
                || !response.Capabilities.Contains(InterruptedRecoveryCapability, StringComparer.Ordinal)
                || !response.Capabilities.Contains(RecoveryPruningCapability, StringComparer.Ordinal)
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
            Volatile.Write(ref this.RecoveryEligibilityLost, 1);
            lock (this.ResponseLock)
            {
                this.RequireReadyClientState();
                this.CurrentRecoveryCatalogBinding = null;
            }
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
            lock (this.ResponseLock)
                this.RequireReadyClientState();
            ProtocolSessionId session = this.SessionId
                ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
            GameDiscoveryEvent response = await this.ExchangeAsync<GameDiscoveryEvent>(
                new DiscoverGamesRequest(session),
                cancellationToken
            ).ConfigureAwait(false);
            if (response.SessionId != session)
                return await this.FailProtocolAsync<IReadOnlyList<ProtocolGameCandidate>>().ConfigureAwait(false);
            ProtocolGameCandidate[] candidates = response.Candidates;
            bool retained;
            lock (this.ResponseLock)
            {
                retained = !this.SessionFaultRaised && Volatile.Read(ref this.CleanupStarted) == 0;
                if (retained)
                {
                    this.DiscoveredGameCandidates.Clear();
                    foreach (ProtocolGameCandidate candidate in candidates)
                        this.DiscoveredGameCandidates.Add(candidate);
                }
            }
            if (!retained)
                return await this.FailProtocolAsync<IReadOnlyList<ProtocolGameCandidate>>().ConfigureAwait(false);
            return Array.AsReadOnly(candidates);
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
            lock (this.ResponseLock)
                this.RequireReadyClientState();
            ProtocolSessionId session = this.SessionId
                ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
            GameValidationEvent response = await this.ExchangeAsync<GameValidationEvent>(
                new ValidateGameRequest(session, canonicalPath),
                cancellationToken
            ).ConfigureAwait(false);
            if (response.SessionId != session)
                return await this.FailProtocolAsync<ProtocolGameCandidate>().ConfigureAwait(false);
            bool retained;
            lock (this.ResponseLock)
            {
                retained = !this.SessionFaultRaised && Volatile.Read(ref this.CleanupStarted) == 0;
                if (retained)
                    this.LatestValidatedGameCandidate = response.Candidate;
            }
            if (!retained)
                return await this.FailProtocolAsync<ProtocolGameCandidate>().ConfigureAwait(false);
            return response.Candidate;
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    public async Task<InstallerRecoveryCatalogResult> ListRecoveriesAsync(string canonicalGamePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalGamePath);
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            Volatile.Write(ref this.RecoveryEligibilityLost, 1);
            ProtocolSessionId session;
            lock (this.ResponseLock)
            {
                this.RequireReadyClientState();
                session = this.SessionId
                    ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");

                // Refresh revokes every previously issued point before any request byte can be written. A failed,
                // cancelled, or rejected refresh can never make an older exact-reference capability current again.
                this.CurrentRecoveryCatalogBinding = null;
            }

            try
            {
                ProtocolEvent response = await this.ExchangeAsync<ProtocolEvent>(
                    new ListRecoveriesRequest(session, canonicalGamePath),
                    cancellationToken
                ).ConfigureAwait(false);

                InstallerRecoveryCatalogResult projected;
                RetainedRecoveryCatalogBinding? binding = null;
                switch (response)
                {
                    case RecoveryCatalogEvent catalog when catalog.SessionId == session:
                        try { (projected, binding) = ProjectRecoveryCatalog(canonicalGamePath, catalog); }
                        catch { return await this.FailProtocolAsync<InstallerRecoveryCatalogResult>().ConfigureAwait(false); }
                        break;

                    case NoRecoveryHistoryEvent missing when missing.SessionId == session:
                        projected = new InstallerNoRecoveryHistory();
                        break;

                    case PrePlanRejectedEvent rejected when rejected.SessionId == session && IsReachableRecoveryCatalogRejection(rejected):
                        projected = new InstallerRecoveryCatalogRejection(rejected.ErrorCode, rejected.NextAction, rejected.IsTerminal);
                        break;

                    default:
                        return await this.FailProtocolAsync<InstallerRecoveryCatalogResult>().ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);
                this.BeforeRecoveryCatalogCommitForTesting?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);
                if (!this.TryCommitRecoveryCatalog(binding))
                    return await this.FailProtocolAsync<InstallerRecoveryCatalogResult>().ConfigureAwait(false);
                if (projected is InstallerRecoveryCatalogRejection { IsTerminal: true })
                    await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                return projected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    /// <summary>Internal test seam proving that only an exact current projected point retains selection authority.</summary>
    internal void AssertCurrentRecoveryPointForTesting(InstallerRecoveryPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        lock (this.ResponseLock)
        {
            if (this.CurrentRecoveryCatalogBinding is null || !this.CurrentRecoveryCatalogBinding.Points.ContainsKey(point))
                throw new ArgumentException("The recovery point must be an exact current capability issued by this client.", nameof(point));
        }
    }

    public async Task<InstallerReadOnlyPlanResult> InspectRollbackAsync(
        string canonicalGamePath,
        InstallerRecoveryPoint recoveryPoint,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(canonicalGamePath);
        ArgumentNullException.ThrowIfNull(recoveryPoint);

        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            Volatile.Write(ref this.RecoveryEligibilityLost, 1);
            ProtocolSessionId session;
            ProtocolReleaseIdentity verifiedRelease;
            RetainedRecoveryCatalogBinding catalog;
            ProtocolRecoveryGeneration selectedGeneration;
            lock (this.ResponseLock)
            {
                session = this.SessionId
                    ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
                if (this.CurrentConfirmedPlanBinding is not null || this.CurrentConfirmedPruneBinding is not null)
                    throw new InvalidOperationException("The confirmed backend session can no longer inspect a plan.");
                catalog = this.CurrentRecoveryCatalogBinding
                    ?? throw new InvalidOperationException("A current recovery catalog is required before inspecting rollback.");
                if (!string.Equals(canonicalGamePath, catalog.CanonicalGamePath, StringComparison.Ordinal))
                    throw new ArgumentException("The rollback game path must match the exact current recovery catalog.", nameof(canonicalGamePath));
                if (!catalog.Points.TryGetValue(recoveryPoint, out selectedGeneration!))
                    throw new ArgumentException("The recovery point must be an exact current capability issued by this client.", nameof(recoveryPoint));
                verifiedRelease = this.VerifiedRelease
                    ?? throw new InstallerProtocolClientException("A verified package session is required before inspecting an operation.");

                // Consuming the point revokes the entire catalog before any request byte can be written. A failed,
                // cancelled, or rejected inspection can never make this or another point current again.
                this.CurrentRecoveryCatalogBinding = null;
                this.CurrentPlanBinding = null;
                this.CurrentPrunePlanBinding = null;
            }

            using CancellationTokenSource aggregateTimeout = new(this.OperationTimeout);
            using CancellationTokenSource aggregate = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, aggregateTimeout.Token);
            try
            {
                ProtocolEvent response = await this.ExchangeAsync<ProtocolEvent>(
                    new InspectPlanRequest(session, canonicalGamePath, InstallerOperation.Rollback, null, selectedGeneration.SelectionId),
                    aggregate.Token
                ).ConfigureAwait(false);

                if (response is PrePlanRejectedEvent rejected && rejected.SessionId == session)
                {
                    if (!IsReachableRollbackInspectionRejection(rejected))
                        return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false);
                    InstallerReadOnlyPlanRejection result = new(rejected.ErrorCode, rejected.NextAction, rejected.IsTerminal);
                    if (rejected.IsTerminal)
                        await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                    return result;
                }
                if (
                    response is not PlanEvent plan
                    || !ValidateRollbackPlanHeader(plan, session, catalog, selectedGeneration)
                )
                    return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false);

                PlanCollections collections = await this.FetchAllPlanPagesAsync(plan, session, aggregate.Token).ConfigureAwait(false);
                if (!ValidateCompletePlan(plan, collections))
                    return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false);

                (InstallerReadOnlyPlanSuccess projected, Dictionary<InstallerReadOnlyPlanCandidate, ProtocolPlanCandidate> candidates) projection;
                try { projection = ProjectPlan(plan, collections); }
                catch { return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false); }
                (InstallerReadOnlyPlanSuccess projected, Dictionary<InstallerReadOnlyPlanCandidate, ProtocolPlanCandidate> candidates) = projection;
                aggregate.Token.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);
                this.BeforePlanBindingCommitForTesting?.Invoke();
                aggregate.Token.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);
                if (!this.TryRetainPlanBinding(new(canonicalGamePath, InstallerOperation.Rollback, null, verifiedRelease, plan.GameRoot, plan.PlanId, plan.PlanDigest, plan.OperationCount, candidates, projected.Confirmation)))
                    return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false);
                return projected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && aggregateTimeout.IsCancellationRequested)
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw new InstallerProtocolClientException(this.CleanupConfirmed
                    ? "The installer backend rollback inspection exceeded its bounded deadline and was stopped."
                    : "The installer backend rollback inspection exceeded its bounded deadline, and termination could not be confirmed.");
            }
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    public async Task<InstallerReadOnlyPlanResult> InspectPlanAsync(string canonicalGamePath, InstallerOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalGamePath);
        if (operation is not (InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair or InstallerOperation.Uninstall or InstallerOperation.Backup))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Only non-rollback read-only plan inspection is available.");

        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            Volatile.Write(ref this.RecoveryEligibilityLost, 1);
            ProtocolSessionId session;
            ProtocolPackageId? packageId;
            ProtocolReleaseIdentity? verifiedRelease;
            lock (this.ResponseLock)
            {
                session = this.SessionId
                    ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
                if (this.CurrentConfirmedPlanBinding is not null || this.CurrentConfirmedPruneBinding is not null)
                    throw new InvalidOperationException("The confirmed backend session can no longer inspect a plan.");
                this.CurrentRecoveryCatalogBinding = null;
                packageId = this.VerifiedPackageId;
                verifiedRelease = this.VerifiedRelease;
                this.CurrentPlanBinding = null;
                this.CurrentPrunePlanBinding = null;
            }

            if (packageId is null || verifiedRelease is null)
                throw new InstallerProtocolClientException("A verified package session is required before inspecting an operation.");
            bool requiresPackage = operation is InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair;
            ProtocolPackageId? requestPackageId = requiresPackage ? packageId : null;

            using CancellationTokenSource aggregateTimeout = new(this.OperationTimeout);
            using CancellationTokenSource aggregate = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, aggregateTimeout.Token);
            try
            {
                ProtocolEvent response = await this.ExchangeAsync<ProtocolEvent>(
                    new InspectPlanRequest(session, canonicalGamePath, operation, requestPackageId, null),
                    aggregate.Token
                ).ConfigureAwait(false);

                if (response is PrePlanRejectedEvent rejected && rejected.SessionId == session)
                {
                    if (!IsReachableInspectPlanRejection(rejected.ErrorCode))
                        return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false);
                    InstallerReadOnlyPlanRejection result = new(rejected.ErrorCode, rejected.NextAction, rejected.IsTerminal);
                    if (rejected.IsTerminal)
                        await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                    return result;
                }
                if (response is not PlanEvent plan || !ValidatePlanHeader(plan, session, canonicalGamePath, operation, requestPackageId, verifiedRelease))
                    return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false);

                PlanCollections collections = await this.FetchAllPlanPagesAsync(plan, session, aggregate.Token).ConfigureAwait(false);
                if (!ValidateCompletePlan(plan, collections))
                    return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false);

                (InstallerReadOnlyPlanSuccess projected, Dictionary<InstallerReadOnlyPlanCandidate, ProtocolPlanCandidate> candidates) projection;
                try { projection = ProjectPlan(plan, collections); }
                catch { return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false); }
                (InstallerReadOnlyPlanSuccess projected, Dictionary<InstallerReadOnlyPlanCandidate, ProtocolPlanCandidate> candidates) = projection;
                aggregate.Token.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);
                this.BeforePlanBindingCommitForTesting?.Invoke();
                aggregate.Token.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);
                if (!this.TryRetainPlanBinding(new(canonicalGamePath, operation, requestPackageId, verifiedRelease, plan.GameRoot, plan.PlanId, plan.PlanDigest, plan.OperationCount, candidates, projected.Confirmation)))
                    return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false);
                return projected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && aggregateTimeout.IsCancellationRequested)
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw new InstallerProtocolClientException(this.CleanupConfirmed
                    ? "The installer backend plan inspection exceeded its bounded deadline and was stopped."
                    : "The installer backend plan inspection exceeded its bounded deadline, and termination could not be confirmed.");
            }
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    public async Task<InstallerReadOnlyPlanResult> ApprovePlanCandidatesAsync(IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates, CancellationToken cancellationToken = default)
    {
        InstallerReadOnlyPlanCandidate[] requested = InstallerCandidateSelection.Snapshot(candidates, nameof(candidates));

        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            Volatile.Write(ref this.RecoveryEligibilityLost, 1);
            RetainedPlanBinding binding;
            ProtocolCandidateId[] selectedIds;
            ProtocolPlanCandidate[] selectedCandidates;
            lock (this.ResponseLock)
            {
                this.CurrentRecoveryCatalogBinding = null;
                this.CurrentPrunePlanBinding = null;
                binding = this.CurrentPlanBinding
                    ?? throw new InvalidOperationException("A current inspected plan is required before approving candidates.");
                if (binding.Operation is not (InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair or InstallerOperation.Uninstall))
                    throw new InvalidOperationException("Candidate approval isn't supported for this operation.");

                HashSet<InstallerReadOnlyPlanCandidate> unique = new(ReferenceEqualityComparer.Instance);
                selectedIds = new ProtocolCandidateId[requested.Length];
                selectedCandidates = new ProtocolPlanCandidate[requested.Length];
                for (int index = 0; index < requested.Length; index++)
                {
                    InstallerReadOnlyPlanCandidate candidate = requested[index];
                    if (!unique.Add(candidate))
                        throw new ArgumentException("The candidate selection contains a duplicate capability.", nameof(candidates));
                    if (!binding.Candidates.TryGetValue(candidate, out ProtocolPlanCandidate? retainedCandidate))
                        throw new ArgumentException("Every candidate must be an exact current capability issued by this plan.", nameof(candidates));
                    selectedCandidates[index] = retainedCandidate;
                    selectedIds[index] = retainedCandidate.CandidateId;
                }
                this.CurrentPlanBinding = null;
            }

            using CancellationTokenSource aggregateTimeout = new(this.OperationTimeout);
            using CancellationTokenSource aggregate = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, aggregateTimeout.Token);
            try
            {
                ProtocolSessionId session = this.SessionId
                    ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
                ProtocolEvent response = await this.ExchangeAsync<ProtocolEvent>(
                    new SelectPlanCandidatesRequest(session, binding.PlanId, binding.PlanDigest, selectedIds),
                    aggregate.Token
                ).ConfigureAwait(false);
                if (response is PrePlanRejectedEvent rejected && rejected.SessionId == session)
                {
                    if (!IsReachableCandidateApprovalRejection(rejected))
                        return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false);
                    InstallerReadOnlyPlanRejection result = new(rejected.ErrorCode, rejected.NextAction, rejected.IsTerminal);
                    if (rejected.IsTerminal)
                        await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                    return result;
                }
                if (
                    response is not PlanEvent plan
                    || plan.PlanId == binding.PlanId
                    || plan.PlanDigest == binding.PlanDigest
                    || plan.GameRoot != binding.GameRoot
                    || !ValidatePlanHeader(plan, session, binding.CanonicalGamePath, binding.Operation, binding.PackageId, binding.VerifiedRelease)
                )
                    return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false);

                PlanCollections collections = await this.FetchAllPlanPagesAsync(plan, session, aggregate.Token).ConfigureAwait(false);
                if (!ValidateCompletePlan(plan, collections) || !ValidateCandidateReplacement(binding, selectedCandidates, collections.Candidates))
                    return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false);
                (InstallerReadOnlyPlanSuccess projected, Dictionary<InstallerReadOnlyPlanCandidate, ProtocolPlanCandidate> replacementCandidates) projection;
                try { projection = ProjectPlan(plan, collections); }
                catch { return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false); }
                (InstallerReadOnlyPlanSuccess projected, Dictionary<InstallerReadOnlyPlanCandidate, ProtocolPlanCandidate> replacementCandidates) = projection;
                aggregate.Token.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);
                this.BeforePlanBindingCommitForTesting?.Invoke();
                aggregate.Token.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);
                if (!this.TryRetainPlanBinding(new(binding.CanonicalGamePath, binding.Operation, binding.PackageId, binding.VerifiedRelease, plan.GameRoot, plan.PlanId, plan.PlanDigest, plan.OperationCount, replacementCandidates, projected.Confirmation)))
                    return await this.FailProtocolAsync<InstallerReadOnlyPlanResult>().ConfigureAwait(false);
                return projected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && aggregateTimeout.IsCancellationRequested)
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw new InstallerProtocolClientException(this.CleanupConfirmed
                    ? "The installer backend candidate approval exceeded its bounded deadline and was stopped."
                    : "The installer backend candidate approval exceeded its bounded deadline, and termination could not be confirmed.");
            }
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    public async Task<InstallerConfirmedPlanAuthority> ConfirmPlanAsync(
        InstallerPlanConfirmation confirmation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            Volatile.Write(ref this.RecoveryEligibilityLost, 1);
            RetainedPlanBinding binding;
            lock (this.ResponseLock)
            {
                this.CurrentRecoveryCatalogBinding = null;
                if (this.CurrentPrunePlanBinding is not null || this.CurrentConfirmedPruneBinding is not null)
                    throw new InvalidOperationException("An ordinary plan can't be confirmed while recovery cleanup authority is current.");
                binding = this.CurrentPlanBinding
                    ?? throw new InvalidOperationException("A current executable inspected plan is required before confirmation.");
                if (binding.Confirmation is null || !ReferenceEquals(binding.Confirmation, confirmation))
                    throw new ArgumentException("The confirmation must be the exact current capability issued by this plan.", nameof(confirmation));
                if (this.CurrentConfirmedPlanBinding is not null)
                    throw new InvalidOperationException("A plan was already confirmed in this backend session.");

                // Confirmation authority is consumed before any wire operation. An admitted failure is fail-stop and
                // can never make this exact reference current again.
                this.CurrentPlanBinding = null;
            }

            ProtocolSessionId session = this.SessionId
                ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
            ConfirmPlanRequest request = new(session, binding.PlanId, binding.PlanDigest);
            try
            {
                CommandAcknowledgedEvent acknowledged = await this.ExchangeAsync<CommandAcknowledgedEvent>(request, cancellationToken).ConfigureAwait(false);
                if (
                    acknowledged.SessionId != session
                    || acknowledged.Acknowledgement != ProtocolAcknowledgementKind.PlanConfirmed
                    || acknowledged.PlanId != binding.PlanId
                    || acknowledged.PrunePlanId is not null
                )
                {
                    return await this.FailProtocolAsync<InstallerConfirmedPlanAuthority>().ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);
                this.BeforeConfirmationAuthorityCommitForTesting?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);

                InstallerConfirmedPlanAuthority authority = new();
                bool committed;
                lock (this.ResponseLock)
                {
                    committed = !this.SessionFaultRaised
                        && Volatile.Read(ref this.CleanupStarted) == 0
                        && !cancellationToken.IsCancellationRequested
                        && this.CurrentConfirmedPlanBinding is null;
                    if (committed)
                        this.CurrentConfirmedPlanBinding = new(
                            binding.Operation,
                            binding.GameRoot,
                            binding.PlanId,
                            binding.PlanDigest,
                            binding.OperationCount,
                            authority
                        );
                }
                if (!committed)
                    return await this.FailProtocolAsync<InstallerConfirmedPlanAuthority>().ConfigureAwait(false);
                return authority;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw;
            }
            catch (InstallerProtocolClientException)
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw;
            }
            catch
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw new InstallerProtocolClientException(this.CleanupConfirmed
                    ? "The installer backend confirmation stopped safely."
                    : "The installer backend confirmation stopped, and termination could not be confirmed.");
            }
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    public async Task<InstallerExecutionOperation> ExecutePlanAsync(
        InstallerConfirmedPlanAuthority authority,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(authority);
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            Volatile.Write(ref this.RecoveryEligibilityLost, 1);
            RetainedConfirmedPlanBinding binding;
            ActiveExecutionRoute route;
            ExecutePlanRequest request;
            lock (this.ResponseLock)
            {
                binding = this.CurrentConfirmedPlanBinding
                    ?? throw new InvalidOperationException("A current confirmed plan is required before execution.");
                if (!ReferenceEquals(binding.Authority, authority))
                    throw new ArgumentException("Execution requires the exact current confirmed-plan authority.", nameof(authority));
                if (
                    this.PendingResponse is not null
                    || this.ActiveExecution is not null
                    || this.SettlingExecution is not null
                    || this.ActiveRecovery is not null
                    || this.SettlingRecovery is not null
                    || this.ActivePrune is not null
                    || this.SettlingPrune is not null
                )
                    throw new InvalidOperationException("The confirmed plan is already executing.");
                if (
                    this.ExecutionProgressCapacityForTesting is < 1 or > MaximumExecutionProgressEvents
                    || this.ExecutionProgressByteCapacityForTesting is < ProtocolJsonSerializer.MaxLineBytes or > MaximumExecutionProgressUtf8Bytes
                )
                    throw new InvalidOperationException("The execution progress bounds are invalid.");

                ProtocolSessionId session = this.SessionId
                    ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
                request = new(session, binding.PlanId, binding.PlanDigest);
                route = new(
                    session,
                    binding,
                    request.CommandId,
                    this.ExecutionProgressCapacityForTesting,
                    this.ExecutionProgressByteCapacityForTesting
                );

                // The exact authority is consumed and the route is installed before the first execute byte can be
                // written. Every post-admission failure is therefore reported conservatively.
                this.CurrentRecoveryCatalogBinding = null;
                this.CurrentConfirmedPlanBinding = null;
                this.ActiveExecution = route;
                Volatile.Write(ref this.ExecutionAdmitted, 1);
            }

            Task<InstallerExecutionResult> completion = this.CompleteExecutionAsync(route);
            InstallerExecutionOperation operation = new(route.Progress.Reader, completion, () => this.RequestExecutionCancellationAsync(route));
            route.AttachCallerCancellation(cancellationToken, () => ObserveAbandoned(this.RequestExecutionCancellationAsync(route)));
            try
            {
                this.BeforeExecutionWriteForTesting?.Invoke();
                await this.WriteExecutionRequestAsync(request).ConfigureAwait(false);
                if (this.BeforeExecuteWrittenCommitForTesting is { } beforeCommit)
                    await beforeCommit().ConfigureAwait(false);
                route.MarkExecuteWritten();
            }
            catch
            {
                route.MarkExecuteWriteFailed();
                await this.TryCleanupAfterExecutionAsync(route, allowCleanExit: false).ConfigureAwait(false);
            }
            return operation;
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    private async Task WriteExecutionRequestAsync(ExecutePlanRequest request)
    {
        string line;
        try { line = ProtocolJsonSerializer.SerializeLine(request); }
        catch { throw new InstallerProtocolClientException("The installer backend execute request was rejected safely."); }
        await this.EnsureStartedAsync().ConfigureAwait(false);
        using CancellationTokenSource timeout = new(this.OperationTimeout);
        using CancellationTokenSource write = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, this.Lifetime.Token);
        byte[] bytes = StrictUtf8.GetBytes(line + "\n");
        await this.AwaitTransportAsync(this.ProcessInput!.WriteAsync(bytes, write.Token).AsTask(), write.Token).ConfigureAwait(false);
        await this.AwaitTransportAsync(this.ProcessInput.FlushAsync(write.Token), write.Token).ConfigureAwait(false);
    }

    private Task RequestExecutionCancellationAsync(ActiveExecutionRoute route)
    {
        TaskCompletionSource? publication = null;
        Task result;
        lock (this.ResponseLock)
        {
            if (route.CancellationTask is not null)
                return route.CancellationTask;
            if (!ReferenceEquals(this.ActiveExecution, route) || route.Terminal.Task.IsCompleted)
                return Task.CompletedTask;
            publication = new(TaskCreationOptions.RunContinuationsAsynchronously);
            result = route.CancellationTask = publication.Task;
        }
        _ = this.PublishExecutionCancellationAsync(route, publication);
        return result;
    }

    private async Task PublishExecutionCancellationAsync(ActiveExecutionRoute route, TaskCompletionSource publication)
    {
        try
        {
            await this.SendExecutionCancellationAsync(route).ConfigureAwait(false);
            publication.TrySetResult();
        }
        catch (Exception error)
        {
            publication.TrySetException(error);
        }
    }

    private async Task SendExecutionCancellationAsync(ActiveExecutionRoute route)
    {
        if (!await route.ExecuteWritten.Task.ConfigureAwait(false))
        {
            route.MarkSettlementUnconfirmed();
            throw new InstallerProtocolClientException("The installer backend could not confirm the cancellation request.");
        }
        using CancellationTokenSource timeout = new(this.ExecutionCancellationAcknowledgementTimeoutForTesting);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, this.Lifetime.Token);
        try
        {
            CancelPlanRequest request;
            Task<CommandAcknowledgedEvent> acknowledgement;
            lock (this.ResponseLock)
            {
                if (!ReferenceEquals(this.ActiveExecution, route) || route.Terminal.Task.IsCompleted)
                    return;
                request = new(route.SessionId, route.Binding.PlanId, route.Binding.PlanDigest);
                acknowledgement = route.InstallCancellationLane(request.CommandId);
                route.MarkCancellationRequested();
            }
            await this.WriteCancellationRequestAsync(request, cancellation.Token).ConfigureAwait(false);
            await this.AwaitTransportAsync(acknowledgement, cancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            route.MarkSettlementUnconfirmed();
            await this.TryCleanupAfterExecutionAsync(route, allowCleanExit: false).ConfigureAwait(false);
            throw new InstallerProtocolClientException(this.CleanupConfirmed
                ? "The installer backend could not confirm the cancellation request and was stopped."
                : "The installer backend could not confirm the cancellation request, and termination could not be confirmed.");
        }
    }

    private async Task WriteCancellationRequestAsync(CancelPlanRequest request, CancellationToken cancellationToken)
    {
        string line;
        try { line = ProtocolJsonSerializer.SerializeLine(request); }
        catch { throw new InstallerProtocolClientException("The installer backend cancellation request was rejected safely."); }
        byte[] bytes = StrictUtf8.GetBytes(line + "\n");
        await this.AwaitTransportAsync(this.ProcessInput!.WriteAsync(bytes, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);
        await this.AwaitTransportAsync(this.ProcessInput.FlushAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<InstallerExecutionResult> CompleteExecutionAsync(ActiveExecutionRoute route)
    {
        using CancellationTokenSource hard = new(this.ExecutionHardTimeoutForTesting);
        Task hardDeadline = Task.Delay(Timeout.InfiniteTimeSpan, hard.Token);
        Task postCancellationDeadline = Task.Delay(Timeout.InfiniteTimeSpan);
        Task cancellationRequested = route.CancellationRequested.Task;
        try
        {
            while (true)
            {
                using CancellationTokenSource idle = new(this.ExecutionIdleTimeoutForTesting);
                Task idleDeadline = Task.Delay(Timeout.InfiniteTimeSpan, idle.Token);
                Task<bool> activity = route.Activity.Reader.WaitToReadAsync().AsTask();
                Task completed = await Task.WhenAny(route.Terminal.Task, activity, cancellationRequested, hardDeadline, idleDeadline, postCancellationDeadline).ConfigureAwait(false);
                if (ReferenceEquals(completed, route.Terminal.Task))
                    break;
                if (ReferenceEquals(completed, activity))
                {
                    while (route.Activity.Reader.TryRead(out _)) { }
                    continue;
                }
                if (ReferenceEquals(completed, cancellationRequested))
                {
                    if (this.BeforePostCancellationDeadlineForTesting is { } beforeDeadline)
                        await beforeDeadline().ConfigureAwait(false);
                    postCancellationDeadline = Task.Delay(this.ExecutionPostCancellationTimeoutForTesting);
                    cancellationRequested = Task.Delay(Timeout.InfiniteTimeSpan);
                    continue;
                }
                return await this.CompleteExecutionUnknownAsync(route).ConfigureAwait(false);
            }

            ProtocolEvent terminal = await route.Terminal.Task.ConfigureAwait(false);
            InstallerExecutionTerminalResult result = ProjectExecutionTerminal(route, terminal);
            if (this.BeforeExecutionSettlementForTesting is { } beforeSettlement)
                await beforeSettlement().ConfigureAwait(false);
            bool acknowledgementConfirmed = true;
            Task? cancellation = route.CancellationTask;
            if (cancellation is not null)
            {
                try { await cancellation.ConfigureAwait(false); }
                catch
                {
                    acknowledgementConfirmed = false;
                    route.MarkSettlementUnconfirmed();
                    // An exact terminal is stronger evidence than a missing or late cancellation acknowledgement.
                }
            }
            route.CompleteProgress();
            bool cleanupConfirmed = await this.TryCleanupAfterExecutionAsync(route, allowCleanExit: true).ConfigureAwait(false);
            return result with
            {
                BackendSettlement = acknowledgementConfirmed && cleanupConfirmed && !route.SettlementUnconfirmed
                    ? InstallerBackendSettlement.ConfirmedClosed
                    : InstallerBackendSettlement.Unconfirmed
            };
        }
        catch
        {
            return await this.CompleteExecutionUnknownAsync(route).ConfigureAwait(false);
        }
        finally
        {
            route.DisposeCallerCancellation();
        }
    }

    private async Task<InstallerExecutionResult> CompleteExecutionUnknownAsync(ActiveExecutionRoute route)
    {
        route.CompleteProgress();
        lock (this.ResponseLock)
        {
            if (ReferenceEquals(this.ActiveExecution, route))
                this.ActiveExecution = null;
        }
        await this.TryCleanupAfterExecutionAsync(route, allowCleanExit: false).ConfigureAwait(false);
        return new InstallerExecutionStateUnknownResult();
    }

    private async Task<bool> TryCleanupAfterExecutionAsync(ActiveExecutionRoute route, bool allowCleanExit)
    {
        try
        {
            await this.CleanupAsync(allowCleanExit).ConfigureAwait(false);
            return this.CleanupConfirmed;
        }
        catch
        {
            route.MarkSettlementUnconfirmed();
            Volatile.Write(ref this.CleanupUnconfirmed, 1);
            return false;
        }
    }

    private static InstallerExecutionTerminalResult ProjectExecutionTerminal(ActiveExecutionRoute route, ProtocolEvent terminal)
    {
        ProtocolExecutionOutcome outcome;
        ProtocolTerminalState state;
        ProtocolExecutionSummary summary;
        switch (terminal)
        {
            case SuccessEvent value when value.Operation == route.Binding.Operation:
                (outcome, state, summary) = (value.Outcome, value.TerminalState, value.ExecutionSummary);
                break;
            case RolledBackFailureEvent value:
                (outcome, state, summary) = (value.Outcome, value.TerminalState, value.ExecutionSummary);
                break;
            case RecoverableInterruptionEvent value:
                (outcome, state, summary) = (value.Outcome, value.TerminalState, value.ExecutionSummary);
                break;
            case CancelledEvent value when route.CancellationRequested.Task.IsCompletedSuccessfully:
                (outcome, state, summary) = (value.Outcome, value.TerminalState, value.ExecutionSummary);
                break;
            default:
                throw new InstallerProtocolClientException("The installer backend returned an invalid execution terminal and was stopped.");
        }
        if (summary.ManagedFileChangeCount is { } changed && changed > route.Binding.OperationCount)
            throw new InstallerProtocolClientException("The installer backend returned impossible execution counters and was stopped.");
        return new(
            outcome,
            state.DurableState,
            state.ErrorCode,
            state.RecoveryDisposition,
            state.NextAction,
            new(
                summary.ManagedFileChangeCount,
                summary.RolledBackManagedFileCount,
                summary.InternalStateChangeCount,
                summary.RolledBackInternalStateCount,
                summary.RecoveredTransactionCount,
                summary.RecoveredPathCount
            ),
            InstallerBackendSettlement.Unconfirmed
        );
    }

    public async Task<InstallerRecoveryOperation> RecoverInterruptedAsync(
        ProtocolGameCandidate candidate,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            ActiveRecoveryRoute route;
            RecoverInterruptedRequest request;
            this.BeforeRecoveryAdmissionForTesting?.Invoke();
            lock (this.ResponseLock)
            {
                if (Volatile.Read(ref this.DisposeStarted) != 0 || Volatile.Read(ref this.CleanupStarted) != 0 || this.SessionFaultRaised)
                    throw new ObjectDisposedException(nameof(ProcessInstallerProtocolClient));
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    candidate.State != Core.Engine.LinuxGameFolderStatus.Valid
                    || !this.DiscoveredGameCandidates.Contains(candidate) && !ReferenceEquals(this.LatestValidatedGameCandidate, candidate)
                )
                    throw new ArgumentException("Recovery requires the exact current valid game-folder candidate issued by this client.", nameof(candidate));
                if (Volatile.Read(ref this.RecoveryEligibilityLost) != 0)
                    throw new InvalidOperationException("Recovery requires a fresh backend used only to discover or validate its exact game folder.");
                if (
                    this.PendingResponse is not null
                    || this.ActiveExecution is not null
                    || this.SettlingExecution is not null
                    || this.ActiveRecovery is not null
                    || this.SettlingRecovery is not null
                    || this.ActivePrune is not null
                    || this.SettlingPrune is not null
                )
                    throw new InvalidOperationException("The installer backend already has an active or settling command.");
                if (
                    this.RecoveryProgressCapacityForTesting is < 1 or > MaximumExecutionProgressEvents
                    || this.RecoveryProgressByteCapacityForTesting is < ProtocolJsonSerializer.MaxLineBytes or > MaximumExecutionProgressUtf8Bytes
                )
                    throw new InvalidOperationException("The recovery progress bounds are invalid.");

                ProtocolSessionId session = this.SessionId
                    ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
                request = new(session, candidate.CanonicalPath);
                route = new(
                    session,
                    request.CommandId,
                    candidate.CanonicalPath,
                    this.RecoveryProgressCapacityForTesting,
                    this.RecoveryProgressByteCapacityForTesting
                );

                // Recovery consumes every session-scoped authority and installs its route before any request byte
                // can be written. The client is one-shot from this point, even after an exact terminal.
                this.VerifiedPackageId = null;
                this.VerifiedRelease = null;
                this.CurrentRecoveryCatalogBinding = null;
                this.CurrentPlanBinding = null;
                this.CurrentConfirmedPlanBinding = null;
                this.CurrentPrunePlanBinding = null;
                this.CurrentConfirmedPruneBinding = null;
                this.IssuedCandidateIds.Clear();
                this.DiscoveredGameCandidates.Clear();
                this.LatestValidatedGameCandidate = null;
                this.ActiveRecovery = route;
                Volatile.Write(ref this.RecoveryAdmitted, 1);
            }

            Task<InstallerRecoveryResult> completion = this.CompleteRecoveryAsync(route);
            InstallerRecoveryOperation operation = new(route.Progress.Reader, completion);
            try
            {
                this.BeforeRecoveryWriteForTesting?.Invoke();
                await this.WriteRecoveryRequestAsync(request).ConfigureAwait(false);
                if (this.BeforeRecoveryWrittenCommitForTesting is { } beforeCommit)
                    await beforeCommit().ConfigureAwait(false);
            }
            catch
            {
                await this.TryCleanupAfterRecoveryAsync(route, allowCleanExit: false).ConfigureAwait(false);
            }
            return operation;
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    private async Task WriteRecoveryRequestAsync(RecoverInterruptedRequest request)
    {
        string line;
        try { line = ProtocolJsonSerializer.SerializeLine(request); }
        catch { throw new InstallerProtocolClientException("The installer backend recovery request was rejected safely."); }
        await this.EnsureStartedAsync().ConfigureAwait(false);
        using CancellationTokenSource timeout = new(this.OperationTimeout);
        using CancellationTokenSource write = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, this.Lifetime.Token);
        byte[] bytes = StrictUtf8.GetBytes(line + "\n");
        await this.AwaitTransportAsync(this.ProcessInput!.WriteAsync(bytes, write.Token).AsTask(), write.Token).ConfigureAwait(false);
        await this.AwaitTransportAsync(this.ProcessInput.FlushAsync(write.Token), write.Token).ConfigureAwait(false);
    }

    private async Task<InstallerRecoveryResult> CompleteRecoveryAsync(ActiveRecoveryRoute route)
    {
        using CancellationTokenSource hard = new(this.RecoveryHardTimeoutForTesting);
        Task hardDeadline = Task.Delay(Timeout.InfiniteTimeSpan, hard.Token);
        try
        {
            while (true)
            {
                using CancellationTokenSource idle = new(this.RecoveryIdleTimeoutForTesting);
                Task idleDeadline = Task.Delay(Timeout.InfiniteTimeSpan, idle.Token);
                Task<bool> activity = route.Activity.Reader.WaitToReadAsync().AsTask();
                Task completed = await Task.WhenAny(route.Terminal.Task, activity, hardDeadline, idleDeadline).ConfigureAwait(false);
                if (ReferenceEquals(completed, route.Terminal.Task))
                    break;
                if (ReferenceEquals(completed, activity))
                {
                    while (route.Activity.Reader.TryRead(out _)) { }
                    continue;
                }
                return await this.CompleteRecoveryUnknownAsync(route).ConfigureAwait(false);
            }

            ProtocolEvent terminal = await route.Terminal.Task.ConfigureAwait(false);
            InstallerRecoveryTerminalResult result;
            try { result = ProjectRecoveryTerminal(route, terminal); }
            catch (InstallerProtocolClientException)
            {
                route.MarkSettlementUnconfirmed();
                this.FaultSession("The installer backend returned an invalid recovery terminal.");
                return await this.CompleteRecoveryUnknownAsync(route).ConfigureAwait(false);
            }
            if (this.BeforeRecoverySettlementForTesting is { } beforeSettlement)
                await beforeSettlement().ConfigureAwait(false);
            route.CompleteProgress();
            bool cleanupConfirmed = await this.TryCleanupAfterRecoveryAsync(route, allowCleanExit: true).ConfigureAwait(false);
            return result with
            {
                BackendSettlement = cleanupConfirmed && !route.SettlementUnconfirmed
                    ? InstallerBackendSettlement.ConfirmedClosed
                    : InstallerBackendSettlement.Unconfirmed
            };
        }
        catch
        {
            return await this.CompleteRecoveryUnknownAsync(route).ConfigureAwait(false);
        }
    }

    private async Task<InstallerRecoveryResult> CompleteRecoveryUnknownAsync(ActiveRecoveryRoute route)
    {
        route.CompleteProgress();
        lock (this.ResponseLock)
        {
            if (ReferenceEquals(this.ActiveRecovery, route))
                this.ActiveRecovery = null;
        }
        await this.TryCleanupAfterRecoveryAsync(route, allowCleanExit: false).ConfigureAwait(false);
        return new InstallerRecoveryStateUnknownResult();
    }

    private async Task<bool> TryCleanupAfterRecoveryAsync(ActiveRecoveryRoute route, bool allowCleanExit)
    {
        try
        {
            await this.CleanupAsync(allowCleanExit).ConfigureAwait(false);
            return this.CleanupConfirmed;
        }
        catch
        {
            route.MarkSettlementUnconfirmed();
            Volatile.Write(ref this.CleanupUnconfirmed, 1);
            return false;
        }
    }

    private static InstallerRecoveryTerminalResult ProjectRecoveryTerminal(ActiveRecoveryRoute route, ProtocolEvent terminal)
    {
        ProtocolInterruptedRecoveryOutcome outcome;
        ProtocolTerminalState state;
        ProtocolInterruptedRecoveryAttempt? attempt;
        switch (terminal)
        {
            case RecoveryCompletedEvent value:
                (outcome, state, attempt) = (value.Outcome, value.TerminalState, value.Attempt);
                break;
            case RecoveryFailureEvent value:
                (outcome, state, attempt) = (value.Outcome, value.TerminalState, value.Attempt);
                break;
            default:
                throw new InstallerProtocolClientException("The installer backend returned an invalid recovery terminal and was stopped.");
        }

        InstallerRecoveryAttemptSummary? summary = null;
        if (attempt is not null)
        {
            if (!StringComparer.Ordinal.Equals(attempt.GameRoot.CanonicalPath, route.CanonicalGamePath))
                throw new InstallerProtocolClientException("The installer backend returned recovery state for a different game folder and was stopped.");
            ProtocolRecoveredTransactionResult[] recovered = attempt.RecoveredTransactions;
            if (recovered.Length > MaximumRecoveryTransactions || recovered.Any(value => value.ChangedPathCount is < 0 or > MaximumRecoveryPathsPerTransaction))
                throw new InstallerProtocolClientException("The installer backend returned invalid recovery transaction counters and was stopped.");
            int recoveredPaths;
            try { recoveredPaths = recovered.Sum(value => checked(value.ChangedPathCount)); }
            catch { throw new InstallerProtocolClientException("The installer backend returned impossible recovery counters and was stopped."); }
            if (recoveredPaths is < 0 or > MaximumExecutionProgressUnits)
                throw new InstallerProtocolClientException("The installer backend returned impossible recovery counters and was stopped.");
            summary = new(attempt.OperationGenerationAdvanced, attempt.NamedRootStillSelected, recovered.Length, recoveredPaths);
        }

        bool exact = outcome switch
        {
            ProtocolInterruptedRecoveryOutcome.RecoveryCompleted => terminal is RecoveryCompletedEvent
                && state.DurableState == ProtocolDurableState.RecoveryCompleted
                && state.ErrorCode is null
                && state.RecoveryDisposition == ProtocolRecoveryDisposition.Completed
                && summary is { OperationGenerationAdvanced: true, NamedRootStillSelected: not null }
                && state.NextAction == (summary.NamedRootStillSelected.Value ? ProtocolNextAction.InspectAgain : ProtocolNextAction.SelectGameFolder),
            ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery => terminal is RecoveryFailureEvent
                && state.DurableState == ProtocolDurableState.Unchanged
                && state.ErrorCode is null
                && state.RecoveryDisposition == ProtocolRecoveryDisposition.InterruptedRecoveryRequired
                && state.NextAction == ProtocolNextAction.RecoverInterrupted
                && summary is null,
            ProtocolInterruptedRecoveryOutcome.PartialFailure => terminal is RecoveryFailureEvent
                && state.DurableState == ProtocolDurableState.RecoveryRequired
                && state.ErrorCode is not null and not ProtocolTerminalErrorCode.UnexpectedCoreFailure
                && state.RecoveryDisposition == ProtocolRecoveryDisposition.InterruptedRecoveryRequired
                && state.NextAction == ProtocolNextAction.RecoverInterrupted
                && summary is not null,
            ProtocolInterruptedRecoveryOutcome.UnexpectedFailure => terminal is RecoveryFailureEvent
                && state.DurableState == ProtocolDurableState.Unknown
                && state.ErrorCode == ProtocolTerminalErrorCode.UnexpectedCoreFailure
                && state.RecoveryDisposition == ProtocolRecoveryDisposition.InterruptedRecoveryRequired
                && state.NextAction == ProtocolNextAction.RecoverInterrupted
                && summary is null,
            _ => false
        };
        if (!exact)
            throw new InstallerProtocolClientException("The installer backend returned an inconsistent recovery terminal and was stopped.");

        return new(outcome, state.DurableState, state.ErrorCode, state.RecoveryDisposition, state.NextAction, summary, InstallerBackendSettlement.Unconfirmed);
    }

    private async Task<PlanCollections> FetchAllPlanPagesAsync(PlanEvent plan, ProtocolSessionId session, CancellationToken cancellationToken)
    {
        PlanCollections result = new(plan.OperationCount, plan.ConflictCount, plan.CandidateCount, plan.WarningCount);
        int pageCount = 0;
        long aggregateBytes;
        try
        {
            aggregateBytes = StrictUtf8.GetByteCount(ProtocolJsonSerializer.SerializeLine(plan));
        }
        catch
        {
            return await this.FailProtocolAsync<PlanCollections>().ConfigureAwait(false);
        }

        foreach ((ProtocolPlanPageKind kind, int totalCount) in new[]
        {
            (ProtocolPlanPageKind.Operations, plan.OperationCount),
            (ProtocolPlanPageKind.Conflicts, plan.ConflictCount),
            (ProtocolPlanPageKind.Candidates, plan.CandidateCount),
            (ProtocolPlanPageKind.Warnings, plan.WarningCount)
        })
        {
            int offset = 0;
            while (offset < totalCount)
            {
                if (++pageCount > MaximumPlanPageCount)
                    return await this.FailProtocolAsync<PlanCollections>().ConfigureAwait(false);

                PlanPageEvent page = await this.ExchangeAsync<PlanPageEvent>(
                    new GetPlanPageRequest(session, plan.PlanId, plan.PlanDigest, kind, offset),
                    cancellationToken
                ).ConfigureAwait(false);
                if (
                    page.SessionId != session
                    || page.PlanId != plan.PlanId
                    || page.PlanDigest != plan.PlanDigest
                    || page.PageKind != kind
                    || page.Offset != offset
                    || page.TotalCount != totalCount
                )
                    return await this.FailProtocolAsync<PlanCollections>().ConfigureAwait(false);

                int populated = page.Operations.Length + page.Conflicts.Length + page.Candidates.Length + page.Warnings.Length;
                int expectedNext = checked(offset + populated);
                if (populated <= 0 || page.NextOffset != (expectedNext < totalCount ? expectedNext : null))
                    return await this.FailProtocolAsync<PlanCollections>().ConfigureAwait(false);

                try
                {
                    aggregateBytes = checked(aggregateBytes + StrictUtf8.GetByteCount(ProtocolJsonSerializer.SerializeLine(page)));
                }
                catch
                {
                    return await this.FailProtocolAsync<PlanCollections>().ConfigureAwait(false);
                }
                if (aggregateBytes > MaximumPlanAggregateUtf8Bytes)
                    return await this.FailProtocolAsync<PlanCollections>().ConfigureAwait(false);

                switch (kind)
                {
                    case ProtocolPlanPageKind.Operations:
                        result.Operations.AddRange(page.Operations);
                        break;
                    case ProtocolPlanPageKind.Conflicts:
                        result.Conflicts.AddRange(page.Conflicts);
                        break;
                    case ProtocolPlanPageKind.Candidates:
                        result.Candidates.AddRange(page.Candidates);
                        break;
                    case ProtocolPlanPageKind.Warnings:
                        result.Warnings.AddRange(page.Warnings);
                        break;
                    default:
                        return await this.FailProtocolAsync<PlanCollections>().ConfigureAwait(false);
                }
                offset = expectedNext;
            }
        }
        return result;
    }

    private static bool ValidatePlanHeader(PlanEvent plan, ProtocolSessionId session, string canonicalGamePath, InstallerOperation operation, ProtocolPackageId? packageId, ProtocolReleaseIdentity verifiedRelease)
    {
        if (
            plan.SessionId != session
            || plan.Operation != operation
            || plan.PackageId != packageId
            || plan.RecoveryAuthority is not null
            || !string.Equals(plan.GameRoot.CanonicalPath, canonicalGamePath, StringComparison.Ordinal)
            || plan.RecommendedDefault != ProtocolRecommendedDefault.Cancel
            || !plan.RequiresConfirmation
            || plan.CanExecute != (plan.ConflictCount == 0)
        )
            return false;

        bool knownReceipt = plan.ObservedState is ObservedInstallState.KnownUnmodified or ObservedInstallState.KnownModified;
        if (knownReceipt != (plan.CurrentRelease is not null))
            return false;

        if (operation is InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair)
        {
            if (plan.TargetRelease != verifiedRelease)
                return false;
        }
        else if (operation == InstallerOperation.Backup)
        {
            if ((plan.CurrentRelease is null) != (plan.TargetRelease is null) || plan.CurrentRelease is not null && plan.CurrentRelease != plan.TargetRelease)
                return false;
        }
        else if (plan.TargetRelease is not null)
            return false;

        return operation != InstallerOperation.Install || plan.CurrentRelease is null;
    }

    private static bool ValidateRollbackPlanHeader(
        PlanEvent plan,
        ProtocolSessionId session,
        RetainedRecoveryCatalogBinding catalog,
        ProtocolRecoveryGeneration selectedGeneration
    )
    {
        ProtocolRecoveryAuthority? authority = plan.RecoveryAuthority;
        if (
            plan.SessionId != session
            || plan.Operation != InstallerOperation.Rollback
            || plan.PackageId is not null
            || authority is null
            || authority.CatalogId != catalog.CatalogId
            || authority.SelectionId != selectedGeneration.SelectionId
            || !string.Equals(authority.HeadSha256, catalog.HeadSha256, StringComparison.Ordinal)
            || authority.Generation != selectedGeneration
            || plan.GameRoot != authority.GameRoot
            || !HasSameAnchoredRoot(authority.GameRoot, catalog.GameRoot)
            || plan.RecommendedDefault != ProtocolRecommendedDefault.Cancel
            || !plan.RequiresConfirmation
            || plan.CanExecute != (plan.ConflictCount == 0)
        )
            return false;

        bool knownReceipt = plan.ObservedState is ObservedInstallState.KnownUnmodified or ObservedInstallState.KnownModified;
        if (knownReceipt != (plan.CurrentRelease is not null))
            return false;

        ProtocolReleaseIdentity? expectedTarget = selectedGeneration.RestoresUninstalledState
            ? null
            : selectedGeneration.RestoreRelease;
        return plan.TargetRelease == expectedTarget;
    }

    /// <summary>
    /// Compare the durable anchored identity without comparing operation generation. Recovery catalogs are issued
    /// at generation zero, while rollback inspection reports the independently observed current generation.
    /// </summary>
    private static bool HasSameAnchoredRoot(ProtocolGameRootIdentity inspected, ProtocolGameRootIdentity catalog) =>
        string.Equals(inspected.CanonicalPath, catalog.CanonicalPath, StringComparison.Ordinal)
        && inspected.DeviceMajor == catalog.DeviceMajor
        && inspected.DeviceMinor == catalog.DeviceMinor
        && inspected.Inode == catalog.Inode;

    private static bool ValidateCompletePlan(PlanEvent plan, PlanCollections collections)
    {
        if (
            collections.Operations.Count != plan.OperationCount
            || collections.Conflicts.Count != plan.ConflictCount
            || collections.Candidates.Count != plan.CandidateCount
            || collections.Warnings.Count != plan.WarningCount
            || plan.Operation == InstallerOperation.Rollback && collections.Candidates.Count != 0
            || !IsCanonicalOperationSequence(collections.Operations)
            || !IsCanonicalConflictSequence(collections.Conflicts)
            || collections.Candidates.Select(item => item.CandidateId).Distinct().Count() != collections.Candidates.Count
            || collections.Candidates.Select(item => item.Path).Distinct(StringComparer.Ordinal).Count() != collections.Candidates.Count
            || collections.Warnings.Distinct(StringComparer.Ordinal).Count() != collections.Warnings.Count
        )
            return false;

        ProtocolPlanDigest recomputed;
        try
        {
            recomputed = ProtocolPlanDigest.Compute(
                plan.ExecutionBindingDigest,
                plan.Operation,
                plan.PackageId,
                plan.RecoveryAuthority,
                plan.GameRoot,
                plan.CurrentRelease,
                plan.TargetRelease,
                plan.ObservedState,
                collections.Operations,
                collections.Conflicts,
                collections.Candidates,
                plan.Summary,
                collections.Warnings,
                plan.RequiresConfirmation
            );
        }
        catch
        {
            return false;
        }
        if (recomputed != plan.PlanDigest)
            return false;

        if (plan.Operation == InstallerOperation.Backup && plan.CurrentRelease is null && !ValidateReceiptlessBackup(plan, collections))
            return false;

        ProtocolPlanRisk[] expectedRisks;
        try
        {
            expectedRisks = GetExpectedRisks(plan.Operation, plan.CurrentRelease, plan.TargetRelease, collections.Candidates.Count);
        }
        catch
        {
            return false;
        }
        return plan.Risks.SequenceEqual(expectedRisks);
    }

    private static bool ValidateReceiptlessBackup(PlanEvent plan, PlanCollections collections)
    {
        ProtocolPlanConflict[] expectedConflicts = collections.Conflicts.Count switch
        {
            1 => [new(PlanConflictCode.InstalledReceiptRequired, null)],
            2 =>
            [
                new(PlanConflictCode.InstalledReceiptRequired, null),
                new(PlanConflictCode.RecoveryCapacityReached, null)
            ],
            _ => []
        };
        string[] expectedWarnings = expectedConflicts
            .Select(conflict => $"{conflict.Code}.")
            .ToArray();
        return !plan.CanExecute
            && collections.Operations.Count == 0
            && collections.Candidates.Count == 0
            && collections.Conflicts.SequenceEqual(expectedConflicts)
            && collections.Warnings.SequenceEqual(expectedWarnings, StringComparer.Ordinal);
    }

    private static bool IsCanonicalOperationSequence(IReadOnlyList<ProtocolPlanOperation> operations)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        string? previous = null;
        foreach (ProtocolPlanOperation operation in operations)
        {
            string key = $"{operation.Path}\0{(int)operation.Kind:D3}\0{operation.ResultSha256}";
            if (!seen.Add(key) || previous is not null && StringComparer.Ordinal.Compare(previous, key) > 0)
                return false;
            previous = key;
        }
        return true;
    }

    private static bool IsCanonicalConflictSequence(IReadOnlyList<ProtocolPlanConflict> conflicts)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        string? previous = null;
        foreach (ProtocolPlanConflict conflict in conflicts)
        {
            string key = $"{conflict.Path}\0{(int)conflict.Code:D3}";
            if (!seen.Add(key) || previous is not null && StringComparer.Ordinal.Compare(previous, key) > 0)
                return false;
            previous = key;
        }
        return true;
    }

    private static ProtocolPlanRisk[] GetExpectedRisks(InstallerOperation operation, ProtocolReleaseIdentity? currentRelease, ProtocolReleaseIdentity? targetRelease, int candidateCount)
    {
        List<ProtocolPlanRisk> risks = [];
        if (operation == InstallerOperation.Uninstall)
            risks.Add(ProtocolPlanRisk.Uninstall);
        if (operation == InstallerOperation.Rollback)
            risks.Add(ProtocolPlanRisk.Rollback);
        if (
            currentRelease is not null
            && targetRelease is not null
            && ForkReleaseIdentity.Compare(
                ForkReleaseIdentity.Parse(targetRelease.Tag),
                ForkReleaseIdentity.Parse(currentRelease.Tag)
            ) < 0
        )
            risks.Add(ProtocolPlanRisk.Downgrade);
        if (candidateCount > 0)
            risks.Add(ProtocolPlanRisk.ModifiedOrUnknownFileApproval);
        return risks.ToArray();
    }

    private static (InstallerRecoveryCatalogSuccess Result, RetainedRecoveryCatalogBinding Binding) ProjectRecoveryCatalog(
        string canonicalGamePath,
        RecoveryCatalogEvent catalog
    )
    {
        ProtocolRecoveryGeneration[] generations = catalog.Generations;
        if (
            catalog.GameRoot.CanonicalPath != canonicalGamePath
            || catalog.GameRoot.OperationGeneration != 0
            || generations.Length is <= 0 or > ProtocolJsonSerializer.MaxRecoveryGenerations
        )
        {
            throw new InstallerProtocolClientException("The recovery catalog doesn't match the exact requested game root and bounded lookup contract.");
        }

        InstallerRecoveryPoint[] points = new InstallerRecoveryPoint[generations.Length];
        Dictionary<InstallerRecoveryPoint, ProtocolRecoveryGeneration> bindings = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < generations.Length; index++)
        {
            ProtocolRecoveryGeneration generation = generations[index];
            if (
                generation.IsCurrent != (index == 0)
                || generation.IsUserCheckpoint != (generation.OriginOperation == InstallerOperation.Backup)
                || (generation.RestoreRelease is null) != generation.RestoresUninstalledState
            )
            {
                throw new InstallerProtocolClientException("The recovery catalog generation semantics are inconsistent.");
            }

            InstallerRecoveryRestoreTarget target = generation.RestoreRelease is { } release
                ? new InstallerRecoveryReleaseTarget(release.Tag, release.EmbeddedVersion)
                : new InstallerRecoveryUninstalledTarget();
            InstallerRecoveryPoint point = new(index + 1, generation.IsCurrent, generation.IsUserCheckpoint, generation.OriginOperation, target);
            points[index] = point;
            bindings.Add(point, generation);
        }

        InstallerRecoveryCatalogSuccess result = new(Array.AsReadOnly(points));
        return (result, new(canonicalGamePath, catalog.CatalogId, catalog.GameRoot, catalog.HeadSha256, bindings));
    }

    private static bool IsReachableRecoveryCatalogRejection(PrePlanRejectedEvent rejection) => rejection switch
    {
        { ErrorCode: ProtocolPrePlanErrorCode.RequestCancelled, NextAction: ProtocolNextAction.RetryRequest, IsTerminal: false } => true,
        { ErrorCode: ProtocolPrePlanErrorCode.InvalidGameFolder, NextAction: ProtocolNextAction.SelectGameFolder, IsTerminal: false } => true,
        { ErrorCode: ProtocolPrePlanErrorCode.RecoveryUnavailable, NextAction: ProtocolNextAction.ListRecoveries, IsTerminal: false } => true,
        { ErrorCode: ProtocolPrePlanErrorCode.PermissionDenied, NextAction: ProtocolNextAction.ReviewFilesystem, IsTerminal: false } => true,
        { ErrorCode: ProtocolPrePlanErrorCode.UnexpectedFailure, NextAction: ProtocolNextAction.StartNewSession or ProtocolNextAction.ViewPrivateLog, IsTerminal: true } => true,
        _ => false
    };

    private static bool IsReachableRollbackInspectionRejection(PrePlanRejectedEvent rejection) => rejection switch
    {
        { ErrorCode: ProtocolPrePlanErrorCode.RequestCancelled, NextAction: ProtocolNextAction.RetryRequest, IsTerminal: false } => true,
        { ErrorCode: ProtocolPrePlanErrorCode.InvalidGameFolder, NextAction: ProtocolNextAction.SelectGameFolder, IsTerminal: false } => true,
        { ErrorCode: ProtocolPrePlanErrorCode.InspectionFailed, NextAction: ProtocolNextAction.InspectAgain, IsTerminal: false } => true,
        { ErrorCode: ProtocolPrePlanErrorCode.PermissionDenied, NextAction: ProtocolNextAction.ReviewFilesystem, IsTerminal: false } => true,
        { ErrorCode: ProtocolPrePlanErrorCode.UnexpectedFailure, NextAction: ProtocolNextAction.StartNewSession or ProtocolNextAction.ViewPrivateLog, IsTerminal: true } => true,
        _ => false
    };

    private static bool IsReachableInspectPlanRejection(ProtocolPrePlanErrorCode errorCode) => errorCode is
        ProtocolPrePlanErrorCode.RequestCancelled
        or ProtocolPrePlanErrorCode.InvalidGameFolder
        or ProtocolPrePlanErrorCode.PackageRejected
        or ProtocolPrePlanErrorCode.InspectionFailed
        or ProtocolPrePlanErrorCode.PermissionDenied
        or ProtocolPrePlanErrorCode.UnexpectedFailure;

    private static bool IsReachableCandidateApprovalRejection(PrePlanRejectedEvent rejection) =>
        rejection.ErrorCode == ProtocolPrePlanErrorCode.CandidateApprovalFailed
        && rejection.NextAction == ProtocolNextAction.InspectAgain
        && !rejection.IsTerminal;

    private bool TryRetainPlanBinding(RetainedPlanBinding binding)
    {
        lock (this.ResponseLock)
        {
            if (
                this.SessionFaultRaised
                || Volatile.Read(ref this.CleanupStarted) != 0
                || this.CurrentPrunePlanBinding is not null
                || this.CurrentConfirmedPruneBinding is not null
            )
                return false;
            int capacity = this.IssuedCandidateCapacityForTesting;
            ProtocolCandidateId[] issued = binding.Candidates.Values.Select(candidate => candidate.CandidateId).ToArray();
            if (
                capacity is < ProtocolJsonSerializer.MaxPlanCandidates or > InstallerCandidateSelection.MaximumIssuedCandidatesPerSession
                || this.IssuedCandidateIds.Count > capacity - issued.Length
                || issued.Any(this.IssuedCandidateIds.Contains)
            )
                return false;
            foreach (ProtocolCandidateId candidateId in issued)
                this.IssuedCandidateIds.Add(candidateId);
            this.CurrentPlanBinding = binding;
            return true;
        }
    }

    private bool TryCommitRecoveryCatalog(RetainedRecoveryCatalogBinding? binding)
    {
        lock (this.ResponseLock)
        {
            if (this.SessionFaultRaised || Volatile.Read(ref this.CleanupStarted) != 0)
                return false;
            this.CurrentRecoveryCatalogBinding = binding;
            return true;
        }
    }

    private static bool ValidateCandidateReplacement(RetainedPlanBinding binding, IReadOnlyList<ProtocolPlanCandidate> selected, IReadOnlyList<ProtocolPlanCandidate> replacement)
    {
        HashSet<string> selectedPaths = selected.Select(candidate => candidate.Path).ToHashSet(StringComparer.Ordinal);
        ProtocolPlanCandidate[] retained = binding.Candidates.Values
            .Where(candidate => !selectedPaths.Contains(candidate.Path))
            .OrderBy(candidate => candidate.Path, StringComparer.Ordinal)
            .ToArray();
        ProtocolPlanCandidate[] remaining = replacement.OrderBy(candidate => candidate.Path, StringComparer.Ordinal).ToArray();
        if (retained.Length != remaining.Length || replacement.Any(candidate => selectedPaths.Contains(candidate.Path)))
            return false;
        for (int index = 0; index < retained.Length; index++)
        {
            ProtocolPlanCandidate old = retained[index];
            ProtocolPlanCandidate current = remaining[index];
            if (
                old.CandidateId == current.CandidateId
                || old.Reason != current.Reason
                || old.Disposition != current.Disposition
                || !string.Equals(old.Path, current.Path, StringComparison.Ordinal)
                || !string.Equals(old.ObservedSha256, current.ObservedSha256, StringComparison.Ordinal)
                || old.ObservedSizeBytes != current.ObservedSizeBytes
                || old.ObservedUnixMode != current.ObservedUnixMode
                || !string.Equals(old.ProposedResultSha256, current.ProposedResultSha256, StringComparison.Ordinal)
                || old.Selected != current.Selected
            )
                return false;
        }
        return true;
    }

    private static (InstallerReadOnlyPlanSuccess Plan, Dictionary<InstallerReadOnlyPlanCandidate, ProtocolPlanCandidate> Candidates) ProjectPlan(PlanEvent plan, PlanCollections collections)
    {
        InstallerPlanOperationCount[] operations = collections.Operations
            .GroupBy(item => item.Kind)
            .OrderBy(group => group.Key)
            .Select(group => new InstallerPlanOperationCount(group.Key, group.Count()))
            .ToArray();
        InstallerPlanConflictCount[] conflicts = collections.Conflicts
            .GroupBy(item => item.Code)
            .OrderBy(group => group.Key)
            .Select(group => new InstallerPlanConflictCount(group.Key, group.Count()))
            .ToArray();
        InstallerPlanCandidateCount[] candidates = collections.Candidates
            .GroupBy(item => (item.Reason, item.Disposition, item.Selected))
            .OrderBy(group => group.Key.Reason)
            .ThenBy(group => group.Key.Disposition)
            .ThenBy(group => group.Key.Selected)
            .Select(group => new InstallerPlanCandidateCount(group.Key.Reason, group.Key.Disposition, group.Key.Selected, group.Count()))
            .ToArray();

        InstallerReadOnlyPlanCandidate[] projectedCandidates = collections.Candidates
            .Select(candidate => new InstallerReadOnlyPlanCandidate(candidate))
            .ToArray();
        Dictionary<InstallerReadOnlyPlanCandidate, ProtocolPlanCandidate> candidateIds = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < projectedCandidates.Length; index++)
            candidateIds.Add(projectedCandidates[index], collections.Candidates[index]);

        InstallerReadOnlyPlanSuccess result = new(
            plan.Operation,
            plan.ObservedState,
            ProjectRelease(plan.CurrentRelease),
            ProjectRelease(plan.TargetRelease),
            plan.ConflictCount > 0,
            Array.AsReadOnly(plan.Risks),
            plan.RecommendedDefault,
            plan.RequiresConfirmation,
            Array.AsReadOnly(operations),
            Array.AsReadOnly(conflicts),
            Array.AsReadOnly(candidates),
            collections.Warnings.Count
        )
        {
            Candidates = Array.AsReadOnly(projectedCandidates),
            Confirmation = plan.CanExecute ? new InstallerPlanConfirmation() : null
        };
        return (result, candidateIds);
    }

    private static InstallerPlanRelease? ProjectRelease(ProtocolReleaseIdentity? release) =>
        release is null ? null : new(release.Tag, release.EmbeddedVersion);

    private sealed class PlanCollections
    {
        public List<ProtocolPlanOperation> Operations { get; }
        public List<ProtocolPlanConflict> Conflicts { get; }
        public List<ProtocolPlanCandidate> Candidates { get; }
        public List<string> Warnings { get; }

        public PlanCollections(int operationCount, int conflictCount, int candidateCount, int warningCount)
        {
            this.Operations = new(operationCount);
            this.Conflicts = new(conflictCount);
            this.Candidates = new(candidateCount);
            this.Warnings = new(warningCount);
        }
    }

    private sealed record RetainedRecoveryCatalogBinding(
        string CanonicalGamePath,
        ProtocolRecoveryCatalogId CatalogId,
        ProtocolGameRootIdentity GameRoot,
        string HeadSha256,
        Dictionary<InstallerRecoveryPoint, ProtocolRecoveryGeneration> Points
    );

    private sealed class RetainedPlanBinding
    {
        public string CanonicalGamePath { get; }
        public InstallerOperation Operation { get; }
        public ProtocolPackageId? PackageId { get; }
        public ProtocolReleaseIdentity VerifiedRelease { get; }
        public ProtocolGameRootIdentity GameRoot { get; }
        public ProtocolPlanId PlanId { get; }
        public ProtocolPlanDigest PlanDigest { get; }
        public int OperationCount { get; }
        public Dictionary<InstallerReadOnlyPlanCandidate, ProtocolPlanCandidate> Candidates { get; }
        public InstallerPlanConfirmation? Confirmation { get; }

        public RetainedPlanBinding(
            string canonicalGamePath,
            InstallerOperation operation,
            ProtocolPackageId? packageId,
            ProtocolReleaseIdentity verifiedRelease,
            ProtocolGameRootIdentity gameRoot,
            ProtocolPlanId planId,
            ProtocolPlanDigest planDigest,
            int operationCount,
            Dictionary<InstallerReadOnlyPlanCandidate, ProtocolPlanCandidate> candidates,
            InstallerPlanConfirmation? confirmation
        )
        {
            this.CanonicalGamePath = canonicalGamePath;
            this.Operation = operation;
            this.PackageId = packageId;
            this.VerifiedRelease = verifiedRelease;
            this.GameRoot = gameRoot;
            this.PlanId = planId;
            this.PlanDigest = planDigest;
            this.OperationCount = operationCount;
            this.Candidates = candidates;
            this.Confirmation = confirmation;
        }
    }

    private sealed record RetainedConfirmedPlanBinding(
        InstallerOperation Operation,
        ProtocolGameRootIdentity GameRoot,
        ProtocolPlanId PlanId,
        ProtocolPlanDigest PlanDigest,
        int OperationCount,
        InstallerConfirmedPlanAuthority Authority
    );

    private sealed class ActiveExecutionRoute
    {
        private long LastSequence = -1;
        private int ProgressEventCount;
        private long ProgressUtf8Bytes;
        private CancellationTokenRegistration CallerCancellation;
        private int SettlementUnconfirmedValue;
        private ProtocolCommandId? CancellationCommandId;
        private TaskCompletionSource<CommandAcknowledgedEvent>? CancellationAcknowledgement;

        public ProtocolSessionId SessionId { get; }
        public RetainedConfirmedPlanBinding Binding { get; }
        public ProtocolCommandId CommandId { get; }
        public int MaximumProgressEvents { get; }
        public long MaximumProgressUtf8Bytes { get; }
        public Channel<InstallerExecutionProgress> Progress { get; } = Channel.CreateBounded<InstallerExecutionProgress>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            }
        );
        public Channel<bool> Activity { get; } = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            }
        );
        public TaskCompletionSource<ProtocolEvent> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ExecuteWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? CancellationTask { get; set; }
        public bool SettlementUnconfirmed => Volatile.Read(ref this.SettlementUnconfirmedValue) != 0;
        public bool HasPendingCancellation => this.CancellationAcknowledgement is not null && !this.CancellationAcknowledgement.Task.IsCompleted;

        public ActiveExecutionRoute(
            ProtocolSessionId sessionId,
            RetainedConfirmedPlanBinding binding,
            ProtocolCommandId commandId,
            int maximumProgressEvents,
            long maximumProgressUtf8Bytes
        )
        {
            this.SessionId = sessionId;
            this.Binding = binding;
            this.CommandId = commandId;
            this.MaximumProgressEvents = maximumProgressEvents;
            this.MaximumProgressUtf8Bytes = maximumProgressUtf8Bytes;
        }

        public void AttachCallerCancellation(CancellationToken token, Action request)
        {
            if (token.CanBeCanceled)
                this.CallerCancellation = token.Register(request);
        }

        public void DisposeCallerCancellation() => this.CallerCancellation.Dispose();
        public void MarkExecuteWritten() => this.ExecuteWritten.TrySetResult(true);
        public void MarkExecuteWriteFailed() => this.ExecuteWritten.TrySetResult(false);
        public void MarkCancellationRequested() => this.CancellationRequested.TrySetResult();
        public void SignalActivity() => this.Activity.Writer.TryWrite(true);
        public void MarkSettlementUnconfirmed() => Volatile.Write(ref this.SettlementUnconfirmedValue, 1);

        public Task<CommandAcknowledgedEvent> InstallCancellationLane(ProtocolCommandId commandId)
        {
            if (this.CancellationCommandId is not null || this.CancellationAcknowledgement is not null)
                throw new InstallerProtocolClientException("The execution already has a cancellation command.");
            this.CancellationCommandId = commandId;
            this.CancellationAcknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return this.CancellationAcknowledgement.Task;
        }

        public bool IsCancellationCommand(ProtocolCommandId commandId) => this.CancellationCommandId == commandId;

        public bool TryAcceptCancellationAcknowledgement(CommandAcknowledgedEvent value, int utf8Bytes)
        {
            if (
                this.CancellationAcknowledgement is null
                || value.CommandId != this.CancellationCommandId
                || value.SessionId != this.SessionId
                || value.Acknowledgement != ProtocolAcknowledgementKind.PlanCancellationRequested
                || value.PlanId != this.Binding.PlanId
                || value.PrunePlanId is not null
                || !this.TryCountFrameBytes(utf8Bytes)
            )
                return false;
            this.SignalActivity();
            return this.CancellationAcknowledgement.TrySetResult(value);
        }

        public bool TryAcceptProgress(ProgressEvent value, int utf8Bytes)
        {
            if (
                value.SessionId != this.SessionId
                || value.PlanId != this.Binding.PlanId
                || value.PlanDigest != this.Binding.PlanDigest
                || value.CommandId != this.CommandId
                || value.Sequence <= this.LastSequence
                || value.CompletedUnits is < 0 or > MaximumExecutionProgressUnits
                || value.TotalUnits is < 0 or > MaximumExecutionProgressUnits
                || value.TotalUnits is { } total && value.CompletedUnits > total
                || utf8Bytes < 1
            )
                return false;
            try
            {
                this.LastSequence = value.Sequence;
                this.ProgressEventCount = checked(this.ProgressEventCount + 1);
                if (!this.TryCountFrameBytes(utf8Bytes))
                    return false;
            }
            catch
            {
                return false;
            }
            if (this.ProgressEventCount > this.MaximumProgressEvents)
                return false;
            this.Progress.Writer.TryWrite(new(value.Stage, checked((int)value.CompletedUnits), value.TotalUnits is { } bounded ? checked((int)bounded) : null));
            this.SignalActivity();
            return true;
        }

        public bool TryCountFrameBytes(int utf8Bytes)
        {
            if (utf8Bytes < 1)
                return false;
            try { this.ProgressUtf8Bytes = checked(this.ProgressUtf8Bytes + utf8Bytes); }
            catch { return false; }
            return this.ProgressUtf8Bytes <= this.MaximumProgressUtf8Bytes;
        }

        public bool IsExactTerminal(ProtocolEvent value) => value switch
        {
            SuccessEvent terminal => this.IsExact(terminal.SessionId, terminal.PlanId, terminal.PlanDigest, terminal.CommandId),
            RolledBackFailureEvent terminal => this.IsExact(terminal.SessionId, terminal.PlanId, terminal.PlanDigest, terminal.CommandId),
            RecoverableInterruptionEvent terminal => this.IsExact(terminal.SessionId, terminal.PlanId, terminal.PlanDigest, terminal.CommandId),
            CancelledEvent terminal => this.IsExact(terminal.SessionId, terminal.PlanId, terminal.PlanDigest, terminal.CommandId),
            _ => false
        };

        private bool IsExact(ProtocolSessionId session, ProtocolPlanId plan, ProtocolPlanDigest digest, ProtocolCommandId command) =>
            session == this.SessionId && plan == this.Binding.PlanId && digest == this.Binding.PlanDigest && command == this.CommandId;

        public void CompleteTerminal(ProtocolEvent terminal)
        {
            this.Progress.Writer.TryComplete();
            this.Activity.Writer.TryComplete();
            this.Terminal.TrySetResult(terminal);
        }

        public void Fail(Exception error)
        {
            this.MarkExecuteWriteFailed();
            this.Progress.Writer.TryComplete();
            this.Activity.Writer.TryComplete();
            this.Terminal.TrySetException(error);
            this.CancellationAcknowledgement?.TrySetException(error);
        }

        public void CompleteProgress()
        {
            this.Progress.Writer.TryComplete();
            this.Activity.Writer.TryComplete();
        }
    }

    private sealed class ActiveRecoveryRoute
    {
        private long LastSequence = -1;
        private int ProgressEventCount;
        private long ProgressUtf8Bytes;
        private int SettlementUnconfirmedValue;

        public ProtocolSessionId SessionId { get; }
        public ProtocolCommandId CommandId { get; }
        public string CanonicalGamePath { get; }
        public int MaximumProgressEvents { get; }
        public long MaximumProgressUtf8Bytes { get; }
        public Channel<InstallerRecoveryProgress> Progress { get; } = Channel.CreateBounded<InstallerRecoveryProgress>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            }
        );
        public Channel<bool> Activity { get; } = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            }
        );
        public TaskCompletionSource<ProtocolEvent> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SettlementUnconfirmed => Volatile.Read(ref this.SettlementUnconfirmedValue) != 0;

        public ActiveRecoveryRoute(
            ProtocolSessionId sessionId,
            ProtocolCommandId commandId,
            string canonicalGamePath,
            int maximumProgressEvents,
            long maximumProgressUtf8Bytes
        )
        {
            this.SessionId = sessionId;
            this.CommandId = commandId;
            this.CanonicalGamePath = canonicalGamePath;
            this.MaximumProgressEvents = maximumProgressEvents;
            this.MaximumProgressUtf8Bytes = maximumProgressUtf8Bytes;
        }

        public void SignalActivity() => this.Activity.Writer.TryWrite(true);
        public void MarkSettlementUnconfirmed() => Volatile.Write(ref this.SettlementUnconfirmedValue, 1);

        public bool TryAcceptProgress(RecoveryProgressEvent value, int utf8Bytes)
        {
            if (
                value.SessionId != this.SessionId
                || value.CommandId != this.CommandId
                || value.Sequence <= this.LastSequence
                || value.CompletedUnits is < 0 or > MaximumExecutionProgressUnits
                || value.TotalUnits is < 0 or > MaximumExecutionProgressUnits
                || value.TotalUnits is { } total && value.CompletedUnits > total
                || utf8Bytes < 1
            )
                return false;
            try
            {
                this.LastSequence = value.Sequence;
                this.ProgressEventCount = checked(this.ProgressEventCount + 1);
                if (!this.TryCountFrameBytes(utf8Bytes))
                    return false;
            }
            catch
            {
                return false;
            }
            if (this.ProgressEventCount > this.MaximumProgressEvents)
                return false;
            this.Progress.Writer.TryWrite(new(value.Stage, value.CompletedUnits, value.TotalUnits));
            this.SignalActivity();
            return true;
        }

        public bool TryCountFrameBytes(int utf8Bytes)
        {
            if (utf8Bytes < 1)
                return false;
            try { this.ProgressUtf8Bytes = checked(this.ProgressUtf8Bytes + utf8Bytes); }
            catch { return false; }
            return this.ProgressUtf8Bytes <= this.MaximumProgressUtf8Bytes;
        }

        public bool IsExactTerminal(ProtocolEvent value) => value switch
        {
            RecoveryCompletedEvent terminal => terminal.SessionId == this.SessionId && terminal.CommandId == this.CommandId,
            RecoveryFailureEvent terminal => terminal.SessionId == this.SessionId && terminal.CommandId == this.CommandId,
            _ => false
        };

        public void CompleteTerminal(ProtocolEvent terminal)
        {
            this.Progress.Writer.TryComplete();
            this.Activity.Writer.TryComplete();
            this.Terminal.TrySetResult(terminal);
        }

        public void Fail(Exception error)
        {
            this.Progress.Writer.TryComplete();
            this.Activity.Writer.TryComplete();
            this.Terminal.TrySetException(error);
        }

        public void CompleteProgress()
        {
            this.Progress.Writer.TryComplete();
            this.Activity.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (this.DisposeLock)
        {
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            Volatile.Write(ref this.DisposeStarted, 1);
            lock (this.ResponseLock)
            {
                this.ActiveExecution?.MarkSettlementUnconfirmed();
                this.SettlingExecution?.MarkSettlementUnconfirmed();
                this.ActiveRecovery?.MarkSettlementUnconfirmed();
                this.SettlingRecovery?.MarkSettlementUnconfirmed();
                this.ActivePrune?.MarkSettlementUnconfirmed();
                this.SettlingPrune?.MarkSettlementUnconfirmed();
            }
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
                int frameUtf8Bytes;
                try { frameUtf8Bytes = checked(StrictUtf8.GetByteCount(line) + 1); }
                catch { frameUtf8Bytes = -1; }
                PendingProtocolResponse? pending = null;
                ActiveExecutionRoute? terminalRoute = null;
                ActiveRecoveryRoute? recoveryTerminalRoute = null;
                ActivePruneRoute? pruneTerminalRoute = null;
                bool progressFrame = false;
                bool recoveryProgressFrame = false;
                bool pruneProgressFrame = false;
                bool cancellationAcknowledgement = false;
                bool pruneCancellationAcknowledgement = false;
                bool invalid = false;
                lock (this.ResponseLock)
                {
                    ActiveRecoveryRoute? recovery = this.ActiveRecovery;
                    if (recovery is not null && response.CommandId == recovery.CommandId)
                    {
                        if (recovery.Terminal.Task.IsCompleted)
                            invalid = true;
                        else if (response is RecoveryProgressEvent progress)
                        {
                            recoveryProgressFrame = recovery.TryAcceptProgress(progress, frameUtf8Bytes);
                            invalid = !recoveryProgressFrame;
                        }
                        else if (recovery.IsExactTerminal(response) && recovery.TryCountFrameBytes(frameUtf8Bytes))
                        {
                            recoveryTerminalRoute = recovery;
                            this.SettlingRecovery = recovery;
                            this.ActiveRecovery = null;
                        }
                        else
                            invalid = true;
                    }
                    else
                    {
                        ActivePruneRoute? prune = this.ActivePrune;
                        if (prune is not null && response.CommandId == prune.CommandId)
                        {
                            if (prune.Terminal.Task.IsCompleted)
                                invalid = true;
                            else if (response is PruneProgressEvent progress)
                            {
                                pruneProgressFrame = prune.TryAcceptProgress(progress, frameUtf8Bytes);
                                invalid = !pruneProgressFrame;
                            }
                            else if (prune.IsExactTerminal(response) && prune.TryCountFrameBytes(frameUtf8Bytes))
                            {
                                pruneTerminalRoute = prune;
                                this.SettlingPrune = prune;
                                if (!prune.HasPendingCancellation)
                                    this.ActivePrune = null;
                            }
                            else
                                invalid = true;
                        }
                        else if (prune is not null && prune.IsCancellationCommand(response.CommandId))
                        {
                            pruneCancellationAcknowledgement = response is CommandAcknowledgedEvent acknowledged
                                && prune.TryAcceptCancellationAcknowledgement(acknowledged, frameUtf8Bytes);
                            invalid = !pruneCancellationAcknowledgement;
                            if (pruneCancellationAcknowledgement && prune.Terminal.Task.IsCompleted)
                                this.ActivePrune = null;
                        }
                        else
                        {
                            ActiveExecutionRoute? active = this.ActiveExecution;
                            if (active is not null && response.CommandId == active.CommandId)
                            {
                                if (active.Terminal.Task.IsCompleted)
                                    invalid = true;
                                else if (response is ProgressEvent progress)
                                {
                                    progressFrame = active.TryAcceptProgress(progress, frameUtf8Bytes);
                                    invalid = !progressFrame;
                                }
                                else if (active.IsExactTerminal(response) && active.TryCountFrameBytes(frameUtf8Bytes))
                                {
                                    terminalRoute = active;
                                    this.SettlingExecution = active;
                                    if (!active.HasPendingCancellation)
                                        this.ActiveExecution = null;
                                }
                                else
                                    invalid = true;
                            }
                            else if (active is not null && active.IsCancellationCommand(response.CommandId))
                            {
                                cancellationAcknowledgement = response is CommandAcknowledgedEvent acknowledged
                                    && active.TryAcceptCancellationAcknowledgement(acknowledged, frameUtf8Bytes);
                                invalid = !cancellationAcknowledgement;
                                if (cancellationAcknowledgement && active.Terminal.Task.IsCompleted)
                                    this.ActiveExecution = null;
                            }
                            else if (this.PendingResponse is { } current && response.CommandId == current.CommandId)
                            {
                                pending = current;
                                this.PendingResponse = null;
                            }
                            else
                                invalid = true;
                        }
                    }
                }
                if (invalid)
                {
                    this.FaultSession("The installer backend emitted an unsolicited or incorrectly correlated response.");
                    return;
                }
                if (progressFrame || recoveryProgressFrame || pruneProgressFrame || cancellationAcknowledgement || pruneCancellationAcknowledgement)
                    continue;
                if (recoveryTerminalRoute is not null)
                {
                    if (this.Reader.HasBufferedFrameData)
                    {
                        recoveryTerminalRoute.MarkSettlementUnconfirmed();
                        recoveryTerminalRoute.CompleteTerminal(response);
                        this.RecoveryTerminalRoutedForTesting?.Invoke();
                        this.FaultSession("The installer backend emitted output after a recovery terminal.");
                        return;
                    }
                    recoveryTerminalRoute.CompleteTerminal(response);
                    this.RecoveryTerminalRoutedForTesting?.Invoke();
                    continue;
                }
                if (pruneTerminalRoute is not null)
                {
                    bool cancellationAckPending = pruneTerminalRoute.HasPendingCancellation;
                    if (this.Reader.HasBufferedFrameData && !cancellationAckPending)
                    {
                        pruneTerminalRoute.MarkSettlementUnconfirmed();
                        pruneTerminalRoute.CompleteTerminal(response);
                        this.PruneTerminalRoutedForTesting?.Invoke();
                        this.FaultSession("The installer backend emitted output after a recovery-cleanup terminal.");
                        return;
                    }
                    pruneTerminalRoute.CompleteTerminal(response);
                    this.PruneTerminalRoutedForTesting?.Invoke();
                    continue;
                }
                if (terminalRoute is not null)
                {
                    bool cancellationAckPending = terminalRoute.HasPendingCancellation;
                    if (this.Reader.HasBufferedFrameData && !cancellationAckPending)
                    {
                        terminalRoute.MarkSettlementUnconfirmed();
                        terminalRoute.CompleteTerminal(response);
                        this.ExecutionTerminalRoutedForTesting?.Invoke();
                        this.FaultSession("The installer backend emitted output after an execution terminal.");
                        return;
                    }
                    terminalRoute.CompleteTerminal(response);
                    this.ExecutionTerminalRoutedForTesting?.Invoke();
                    continue;
                }
                if (pending is null)
                {
                    this.FaultSession("The installer backend response router entered an invalid state.");
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
        ActiveExecutionRoute? execution;
        ActiveRecoveryRoute? recovery;
        ActivePruneRoute? prune;
        lock (this.ResponseLock)
        {
            if (this.SessionFaultRaised)
                return;
            this.SessionFaultRaised = true;
            this.VerifiedPackageId = null;
            this.VerifiedRelease = null;
            this.CurrentRecoveryCatalogBinding = null;
            this.CurrentPlanBinding = null;
            this.CurrentConfirmedPlanBinding = null;
            this.CurrentPrunePlanBinding = null;
            this.CurrentConfirmedPruneBinding = null;
            this.IssuedCandidateIds.Clear();
            this.DiscoveredGameCandidates.Clear();
            this.LatestValidatedGameCandidate = null;
            pending = this.PendingResponse;
            this.PendingResponse = null;
            execution = this.ActiveExecution ?? this.SettlingExecution;
            this.ActiveExecution = null;
            this.SettlingExecution = null;
            recovery = this.ActiveRecovery ?? this.SettlingRecovery;
            this.ActiveRecovery = null;
            this.SettlingRecovery = null;
            prune = this.ActivePrune ?? this.SettlingPrune;
            this.ActivePrune = null;
            this.SettlingPrune = null;
        }
        pending?.Completion.TrySetException(fault);
        execution?.MarkSettlementUnconfirmed();
        execution?.Fail(fault);
        recovery?.MarkSettlementUnconfirmed();
        recovery?.Fail(fault);
        prune?.MarkSettlementUnconfirmed();
        prune?.Fail(fault);
        this.SessionFault.TrySetResult(fault);
        _ = this.CleanupAsync(allowCleanExit: false);
    }

    private bool TryCommitPackageAuthority(PackageOpenedEvent opened)
    {
        lock (this.ResponseLock)
        {
            if (this.SessionFaultRaised || Volatile.Read(ref this.CleanupStarted) != 0)
                return false;
            this.CurrentRecoveryCatalogBinding = null;
            this.CurrentPlanBinding = null;
            this.CurrentPrunePlanBinding = null;
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
            // For a clean terminal settlement, keep the response pump alive until stdin is closed and the backend
            // reaches EOF/exit. This lets a late protocol frame mark settlement unconfirmed instead of racing past
            // an eagerly cancelled reader. Fail-stop cleanup still cancels transport immediately.
            if (!allowCleanExit)
                this.CancelLifetimeWithoutThrowing();
            ActiveExecutionRoute? execution;
            ActiveExecutionRoute? settling;
            ActiveRecoveryRoute? recovery;
            ActiveRecoveryRoute? settlingRecovery;
            ActivePruneRoute? prune;
            ActivePruneRoute? settlingPrune;
            lock (this.ResponseLock)
            {
                this.VerifiedPackageId = null;
                this.VerifiedRelease = null;
                this.CurrentRecoveryCatalogBinding = null;
                this.CurrentPlanBinding = null;
                this.CurrentConfirmedPlanBinding = null;
                this.CurrentPrunePlanBinding = null;
                this.CurrentConfirmedPruneBinding = null;
                this.IssuedCandidateIds.Clear();
                this.DiscoveredGameCandidates.Clear();
                this.LatestValidatedGameCandidate = null;
                execution = this.ActiveExecution;
                this.ActiveExecution = null;
                settling = this.SettlingExecution;
                if (!allowCleanExit)
                    this.SettlingExecution = null;
                recovery = this.ActiveRecovery;
                this.ActiveRecovery = null;
                settlingRecovery = this.SettlingRecovery;
                if (!allowCleanExit)
                    this.SettlingRecovery = null;
                prune = this.ActivePrune;
                this.ActivePrune = null;
                settlingPrune = this.SettlingPrune;
                if (!allowCleanExit)
                    this.SettlingPrune = null;
            }
            execution?.Fail(new InstallerProtocolClientException("The installer backend execution transport stopped."));
            recovery?.Fail(new InstallerProtocolClientException("The installer backend recovery transport stopped."));
            prune?.Fail(new InstallerProtocolClientException("The installer backend recovery-cleanup transport stopped."));
            if (!allowCleanExit)
            {
                settling?.MarkSettlementUnconfirmed();
                settlingRecovery?.MarkSettlementUnconfirmed();
                settlingPrune?.MarkSettlementUnconfirmed();
                settling?.CompleteProgress();
                settlingRecovery?.CompleteProgress();
                settlingPrune?.CompleteProgress();
            }
            IInstallerProtocolProcess? process = this.Process;
            if (process is null)
            {
                this.ClearSettlingExecution(settling);
                this.ClearSettlingRecovery(settlingRecovery);
                this.ClearSettlingPrune(settlingPrune);
                return;
            }

            try { (this.ProcessInput ?? process.Input).Dispose(); } catch { }
            if (allowCleanExit && await WaitBoundedAsync(GetWaitTask(process), this.ReapTimeout).ConfigureAwait(false))
            {
                if (this.ReaderPump is { } reader && !await WaitBoundedAsync(reader, this.ReapTimeout).ConfigureAwait(false))
                {
                    settling?.MarkSettlementUnconfirmed();
                    settlingRecovery?.MarkSettlementUnconfirmed();
                    settlingPrune?.MarkSettlementUnconfirmed();
                    Volatile.Write(ref this.CleanupUnconfirmed, 1);
                }
                this.ClearSettlingExecution(settling);
                this.ClearSettlingRecovery(settlingRecovery);
                this.ClearSettlingPrune(settlingPrune);
                this.CancelLifetimeWithoutThrowing();
                await this.FinishCleanupAsync(process).ConfigureAwait(false);
                return;
            }

            if (allowCleanExit)
            {
                settling?.MarkSettlementUnconfirmed();
                settlingRecovery?.MarkSettlementUnconfirmed();
                settlingPrune?.MarkSettlementUnconfirmed();
            }
            this.ClearSettlingExecution(settling);
            this.ClearSettlingRecovery(settlingRecovery);
            this.ClearSettlingPrune(settlingPrune);
            this.CancelLifetimeWithoutThrowing();
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
                try { this.ExecutableLease?.Dispose(); }
                catch { Volatile.Write(ref this.CleanupUnconfirmed, 1); }
                finally
                {
                    if (this.IsProduction)
                    {
                        lock (ProductionQuarantineLock)
                            ProductionClientActive = false;
                    }
                }
            }
        }
    }

    private void ClearSettlingExecution(ActiveExecutionRoute? route)
    {
        if (route is null)
            return;
        lock (this.ResponseLock)
        {
            if (ReferenceEquals(this.SettlingExecution, route))
                this.SettlingExecution = null;
        }
        route.CompleteProgress();
    }

    private void ClearSettlingRecovery(ActiveRecoveryRoute? route)
    {
        if (route is null)
            return;
        lock (this.ResponseLock)
        {
            if (ReferenceEquals(this.SettlingRecovery, route))
                this.SettlingRecovery = null;
        }
        route.CompleteProgress();
    }

    private void ClearSettlingPrune(ActivePruneRoute? route)
    {
        if (route is null)
            return;
        lock (this.ResponseLock)
        {
            if (ReferenceEquals(this.SettlingPrune, route))
                this.SettlingPrune = null;
        }
        route.CompleteProgress();
    }

    private void CancelLifetimeWithoutThrowing()
    {
        try { this.Lifetime.Cancel(); }
        catch { Volatile.Write(ref this.CleanupUnconfirmed, 1); }
    }

    private async Task FinishCleanupAsync(IInstallerProtocolProcess process)
    {
        await this.FinishStreamCleanupAsync(process).ConfigureAwait(false);
        try { process.Dispose(); }
        catch { Volatile.Write(ref this.CleanupUnconfirmed, 1); }
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
                    try { process.Dispose(); }
                    catch { }
                    try { executableLease?.Dispose(); }
                    catch { }
                    finally
                    {
                        if (production)
                        {
                            lock (ProductionQuarantineLock)
                                ProductionQuarantine = null;
                        }
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
        if (Volatile.Read(ref this.ExecutionAdmitted) != 0)
            throw new InvalidOperationException("No ordinary command can be admitted after plan execution begins.");
        if (Volatile.Read(ref this.RecoveryAdmitted) != 0)
            throw new InvalidOperationException("No ordinary command can be admitted after interrupted recovery begins.");
        if (Volatile.Read(ref this.PruneAdmitted) != 0)
            throw new InvalidOperationException("No ordinary command can be admitted after recovery cleanup begins.");
    }

    /// <summary>Require a backend state in which protocol lookups and package opening are legal.</summary>
    /// <remarks>The caller must hold <see cref="ResponseLock"/>.</remarks>
    private void RequireReadyClientState()
    {
        if (
            this.CurrentPlanBinding is not null
            || this.CurrentConfirmedPlanBinding is not null
            || this.CurrentPrunePlanBinding is not null
            || this.CurrentConfirmedPruneBinding is not null
        )
            throw new InvalidOperationException("The installer backend must be in its ready state for this command.");
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

internal sealed record PendingProtocolResponse(
    ProtocolCommandId CommandId,
    TaskCompletionSource<ProtocolEvent> Completion
);

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
