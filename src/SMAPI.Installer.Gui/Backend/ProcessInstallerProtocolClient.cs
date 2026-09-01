using System.Diagnostics;
using System.Text;
using StardewModdingAPI.Installer.Core.Planning;
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
    internal const string PlanInspectionCapability = "install-update-repair-uninstall-backup-rollback";
    internal const string CandidateApprovalCapability = "candidate-approval";
    internal const int MaximumObservedStderrBytes = 64 * 1024;
    internal const int MaximumPlanPageCount = 512;
    internal const int MaximumPlanAggregateUtf8Bytes = 16 * 1024 * 1024;
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
    private RetainedPlanBinding? CurrentPlanBinding;
    private RetainedConfirmedPlanBinding? CurrentConfirmedPlanBinding;
    private readonly HashSet<ProtocolCandidateId> IssuedCandidateIds = [];
    private int CleanupStarted;
    private int DisposeStarted;
    private int ObservedStderrBytesValue;
    private int CleanupUnconfirmed;
    private Task? CleanupTask;
    private Task? DisposalTask;
    private bool SessionFaultRaised;
    internal Action? BeforePackageAuthorityCommitForTesting { get; set; }
    internal Action? BeforePlanBindingCommitForTesting { get; set; }
    internal Action? BeforeConfirmationAuthorityCommitForTesting { get; set; }
    internal int IssuedCandidateCapacityForTesting { get; set; } = InstallerCandidateSelection.MaximumIssuedCandidatesPerSession;

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
                || !response.Capabilities.Contains(PlanInspectionCapability, StringComparer.Ordinal)
                || !response.Capabilities.Contains(CandidateApprovalCapability, StringComparer.Ordinal)
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

    public async Task<InstallerReadOnlyPlanResult> InspectPlanAsync(string canonicalGamePath, InstallerOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalGamePath);
        if (operation is not (InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair or InstallerOperation.Uninstall or InstallerOperation.Backup))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Only non-rollback read-only plan inspection is available.");

        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            ProtocolSessionId session;
            ProtocolPackageId? packageId;
            ProtocolReleaseIdentity? verifiedRelease;
            lock (this.ResponseLock)
            {
                session = this.SessionId
                    ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
                if (this.CurrentConfirmedPlanBinding is not null)
                    throw new InvalidOperationException("The confirmed backend session can no longer inspect a plan.");
                packageId = this.VerifiedPackageId;
                verifiedRelease = this.VerifiedRelease;
                this.CurrentPlanBinding = null;
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
                if (!this.TryRetainPlanBinding(new(canonicalGamePath, operation, requestPackageId, verifiedRelease, plan.GameRoot, plan.PlanId, plan.PlanDigest, candidates, projected.Confirmation)))
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
            RetainedPlanBinding binding;
            ProtocolCandidateId[] selectedIds;
            ProtocolPlanCandidate[] selectedCandidates;
            lock (this.ResponseLock)
            {
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
                if (!this.TryRetainPlanBinding(new(binding.CanonicalGamePath, binding.Operation, binding.PackageId, binding.VerifiedRelease, plan.GameRoot, plan.PlanId, plan.PlanDigest, replacementCandidates, projected.Confirmation)))
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
            RetainedPlanBinding binding;
            lock (this.ResponseLock)
            {
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

    private static bool ValidateCompletePlan(PlanEvent plan, PlanCollections collections)
    {
        if (
            collections.Operations.Count != plan.OperationCount
            || collections.Conflicts.Count != plan.ConflictCount
            || collections.Candidates.Count != plan.CandidateCount
            || collections.Warnings.Count != plan.WarningCount
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
        if (currentRelease is not null && targetRelease is not null && IsEarlierRelease(targetRelease.Tag, currentRelease.Tag))
            risks.Add(ProtocolPlanRisk.Downgrade);
        if (candidateCount > 0)
            risks.Add(ProtocolPlanRisk.ModifiedOrUnknownFileApproval);
        return risks.ToArray();
    }

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
            if (this.SessionFaultRaised || Volatile.Read(ref this.CleanupStarted) != 0)
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

    private static bool IsEarlierRelease(string targetTag, string currentTag)
    {
        static (Version Version, int Alpha) Parse(string tag)
        {
            const string prefix = "fork-4eh5xitv6787h645ebv-linux-v";
            int separator = tag.LastIndexOf("-alpha.", StringComparison.Ordinal);
            if (
                !tag.StartsWith(prefix, StringComparison.Ordinal)
                || separator <= prefix.Length
                || !Version.TryParse(tag[prefix.Length..separator], out Version? version)
                || !int.TryParse(tag[(separator + "-alpha.".Length)..], out int alpha)
                || alpha < 1
            )
                throw new InstallerProtocolClientException("A release tag couldn't be compared safely.");
            return (version, alpha);
        }

        (Version targetVersion, int targetAlpha) = Parse(targetTag);
        (Version currentVersion, int currentAlpha) = Parse(currentTag);
        int comparison = targetVersion.CompareTo(currentVersion);
        return comparison < 0 || comparison == 0 && targetAlpha < currentAlpha;
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

    private sealed class RetainedPlanBinding
    {
        public string CanonicalGamePath { get; }
        public InstallerOperation Operation { get; }
        public ProtocolPackageId? PackageId { get; }
        public ProtocolReleaseIdentity VerifiedRelease { get; }
        public ProtocolGameRootIdentity GameRoot { get; }
        public ProtocolPlanId PlanId { get; }
        public ProtocolPlanDigest PlanDigest { get; }
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
            this.Candidates = candidates;
            this.Confirmation = confirmation;
        }
    }

    private sealed record RetainedConfirmedPlanBinding(
        InstallerOperation Operation,
        ProtocolGameRootIdentity GameRoot,
        ProtocolPlanId PlanId,
        ProtocolPlanDigest PlanDigest,
        InstallerConfirmedPlanAuthority Authority
    );

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
            this.CurrentPlanBinding = null;
            this.CurrentConfirmedPlanBinding = null;
            this.IssuedCandidateIds.Clear();
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
                this.CurrentPlanBinding = null;
                this.CurrentConfirmedPlanBinding = null;
                this.IssuedCandidateIds.Clear();
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
