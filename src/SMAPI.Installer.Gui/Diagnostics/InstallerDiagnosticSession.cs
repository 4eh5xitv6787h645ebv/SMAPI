using System.Globalization;
using System.Text;
using System.Threading.Channels;
using StardewModdingAPI.Installer.Core.Privacy;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Diagnostics;

/// <summary>A fixed, privacy-reviewed event which may be persisted by the graphical installer.</summary>
internal enum InstallerDiagnosticCode
{
    SessionStarted,
    SessionCompleted,
    SessionEndedUnexpectedly,
    ReleaseCatalogLoading,
    ReleaseCatalogReady,
    ReleaseCatalogEmpty,
    ReleaseCatalogFailed,
    ReleaseVerificationStarted,
    ReleaseDownloading,
    ReleaseVerifying,
    ReleaseOpening,
    ReleaseVerified,
    ReleaseCancelled,
    ReleaseFailed,
    ReleaseNetworkUnavailable,
    ReleaseNetworkTimedOut,
    ReleaseDownloadInterrupted,
    GameDiscoveryStarted,
    GameDiscoveryReady,
    GameDiscoveryEmpty,
    GameManualValidating,
    GameManualValid,
    GameManualInvalid,
    GameDiscoveryCancelled,
    GameDiscoveryFailed,
    PlanChoosing,
    PlanInspecting,
    PlanReady,
    PlanRejected,
    PlanConfirmed,
    PlanFailed,
    ExecutionReady,
    ExecutionStarting,
    ExecutionProgress,
    ExecutionCancellationRequested,
    ExecutionRecoveryRequired,
    ExecutionRecoveryStarting,
    ExecutionRecoveryProgress,
    ExecutionTerminal,
    ExecutionRecoveryTerminal,
    ExecutionFailed,
    RecoveryPruneLoading,
    RecoveryPruneReady,
    RecoveryPruneInspecting,
    RecoveryPrunePlanReady,
    RecoveryPruneConfirmed,
    RecoveryPruneStarting,
    RecoveryPruneProgress,
    RecoveryPruneCancellationRequested,
    RecoveryPruneTerminal,
    RecoveryPruneFailed,
    DiagnosticsCoalesced,
    MutationLoggingVerified
}

/// <summary>A bounded display projection of one record which was durably accepted by the private log.</summary>
internal sealed record InstallerDiagnosticEntry(
    DateTimeOffset Timestamp,
    InstallerLogLevel Level,
    string EventCode,
    string Message,
    string? StableErrorCode
);

/// <summary>The bounded diagnostic writer state exposed to the local viewer.</summary>
internal enum InstallerDiagnosticHealth
{
    Healthy,
    BoundedWithOmissions,
    WriteFailed,
    Disposed
}

/// <summary>An immutable, path-free diagnostic viewer snapshot.</summary>
internal sealed record InstallerDiagnosticSnapshot(
    long Revision,
    InstallerDiagnosticHealth Health,
    IReadOnlyList<InstallerDiagnosticEntry> Entries,
    int DisplayOmittedEntryCount,
    int RawLogOmittedEntryCount,
    int CoalescedEventCount
);

/// <summary>One immutable sanitized viewer capture derived from a single diagnostic snapshot.</summary>
internal sealed record InstallerDiagnosticCapture(
    long Revision,
    InstallerDiagnosticHealth Health,
    string HealthLabel,
    int DisplayedEntryCount,
    int DisplayOmittedEntryCount,
    int RawLogOmittedEntryCount,
    int CoalescedEventCount,
    string Text
);

/// <summary>One GUI-owned, local-only diagnostic writer for a production desktop session.</summary>
/// <remarks>
/// Controller callbacks only enqueue fixed typed events. A single worker owns durable writes, and a separate
/// finite typed-stage progress lane coalesces duplicates without delaying the installer controller.
/// </remarks>
internal sealed class InstallerDiagnosticSession : IProductionInstallerDiagnosticSink, IAsyncDisposable
{
    internal const int MaximumDisplayEntries = 256;
    internal const int MaximumSanitizedCopyBytes = 32 * 1024;
    internal const int MaximumSanitizedCopyEntries = 128;
    private const string SanitizedCopyTruncationMarker = "[sanitized diagnostics truncated to the copy limit]\n";
    private const int NormalCapacity = 128;
    private const int ProgressCapacity = 64;
    private const int TerminalCapacity = 16;

    private readonly InstallerLog Log;
    private readonly Guid OperationId;
    private readonly Func<DateTimeOffset> GetNow;
    private readonly Channel<PendingDiagnostic> Normal;
    private readonly Channel<PendingDiagnostic> Progress;
    private readonly Channel<PendingDiagnostic> Terminal;
    private readonly SemaphoreSlim WakeSignal = new(0, 1);
    private readonly CancellationTokenSource Lifetime = new();
    private readonly object Sync = new();
    private readonly List<InstallerDiagnosticEntry> EntriesValue = [];
    private readonly HashSet<ProgressKey> ObservedProgress = [];
    private readonly Task Writer;
    private int coalescedEvents;
    private int clipboardWriteActive;
    private long revision;
    private int displayOmittedEntries;
    private int rawLogOmittedEntries;
    private bool unavailable;
    private bool completed;
    private bool disposeStarted;

    public InstallerDiagnosticSession(InstallerLog log, Guid operationId, Func<DateTimeOffset>? getNow = null)
    {
        this.Log = log ?? throw new ArgumentNullException(nameof(log));
        if (operationId == Guid.Empty)
            throw new ArgumentException("A diagnostic operation ID is required.", nameof(operationId));
        this.OperationId = operationId;
        this.GetNow = getNow ?? (() => DateTimeOffset.UtcNow);
        this.Normal = CreateChannel(NormalCapacity, BoundedChannelFullMode.Wait);
        this.Progress = CreateChannel(ProgressCapacity, BoundedChannelFullMode.Wait);
        this.Terminal = CreateChannel(TerminalCapacity, BoundedChannelFullMode.Wait);

        // This synchronous fixed record proves the private log can be created and written before the production
        // frontend starts Avalonia, networking, a backend process, game discovery, or mutation.
        if (!this.WriteCore(new(InstallerDiagnosticCode.SessionStarted, null, null)))
            throw new InstallerDiagnosticsUnavailableException();
        this.Writer = Task.Run(this.RunWriterAsync);
    }

    /// <summary>Create the sole production GUI diagnostic owner before any desktop or installer work starts.</summary>
    public static InstallerDiagnosticSession CreateProduction(
        Func<string, string?>? environment = null,
        string? userProfile = null,
        Func<Guid>? createOperationId = null,
        Func<DateTimeOffset>? getNow = null
    )
    {
        environment ??= Environment.GetEnvironmentVariable;
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        createOperationId ??= Guid.NewGuid;
        getNow ??= () => DateTimeOffset.UtcNow;
        Guid operationId = createOperationId();
        if (operationId == Guid.Empty)
            throw new InstallerDiagnosticsUnavailableException();

        InstallerLog? log = null;
        try
        {
            string stateRoot = InstallerStatePaths.GetStateRoot(environment, userProfile);
            log = new InstallerLog(new(stateRoot), operationId, getNow(), new[] { userProfile, stateRoot });
            InstallerDiagnosticSession result = new(log, operationId, getNow);
            log = null;
            return result;
        }
        catch (Exception ex) when (ex is not InstallerDiagnosticsUnavailableException)
        {
            log?.Dispose();
            throw new InstallerDiagnosticsUnavailableException();
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<InstallerDiagnosticEntry> Entries
    {
        get
        {
            lock (this.Sync)
                return this.EntriesValue.ToArray();
        }
    }

    public bool IsAvailable
    {
        get { lock (this.Sync) return !this.unavailable; }
    }

    public bool IsTruncated
        => this.Snapshot.Health == InstallerDiagnosticHealth.BoundedWithOmissions;

    public int CoalescedEventCount => Math.Max(0, Volatile.Read(ref this.coalescedEvents));

    public InstallerDiagnosticSnapshot Snapshot
    {
        get
        {
            lock (this.Sync)
            {
                InstallerDiagnosticHealth health = this.disposeStarted
                    ? InstallerDiagnosticHealth.Disposed
                    : this.unavailable
                        ? InstallerDiagnosticHealth.WriteFailed
                        : this.displayOmittedEntries > 0 || this.rawLogOmittedEntries > 0 || Volatile.Read(ref this.coalescedEvents) > 0
                            ? InstallerDiagnosticHealth.BoundedWithOmissions
                            : InstallerDiagnosticHealth.Healthy;
                return new(
                    this.revision,
                    health,
                    this.EntriesValue.ToArray(),
                    this.displayOmittedEntries,
                    this.rawLogOmittedEntries,
                    Math.Max(0, Volatile.Read(ref this.coalescedEvents))
                );
            }
        }
    }

    /// <summary>Capture one immutable, path-free diagnostic snapshot for both viewer labels and copied text.</summary>
    public InstallerDiagnosticCapture CreateSanitizedCapture()
    {
        InstallerDiagnosticSnapshot snapshot = this.Snapshot;
        return new(
            snapshot.Revision,
            snapshot.Health,
            GetHealthLabel(snapshot.Health),
            snapshot.Entries.Count,
            snapshot.DisplayOmittedEntryCount,
            snapshot.RawLogOmittedEntryCount,
            snapshot.CoalescedEventCount,
            CreateSanitizedCopyText(snapshot)
        );
    }

    /// <summary>Create a bounded, path-free text projection for one explicit clipboard write.</summary>
    public string CreateSanitizedCopyText()
        => this.CreateSanitizedCapture().Text;

    private static string CreateSanitizedCopyText(InstallerDiagnosticSnapshot snapshot)
    {
        StringBuilder result = new();
        AppendBounded(result, "SMAPI Linux graphical installer — sanitized diagnostics\n");
        AppendBounded(result, "Review this text before sharing it. Local paths, identifiers, and backend prose are excluded.\n");
        AppendBounded(result, $"Snapshot health: {GetHealthLabel(snapshot.Health)}\n");

        IReadOnlyList<InstallerDiagnosticEntry> entries = snapshot.Entries.Count <= MaximumSanitizedCopyEntries
            ? snapshot.Entries
            : snapshot.Entries.Skip(snapshot.Entries.Count - MaximumSanitizedCopyEntries).ToArray();
        int additionalOmissions = snapshot.Entries.Count - entries.Count;
        int displayOmitted = snapshot.DisplayOmittedEntryCount > int.MaxValue - additionalOmissions
            ? int.MaxValue
            : snapshot.DisplayOmittedEntryCount + additionalOmissions;
        AppendBounded(result, $"Displayed entries in this copy: {entries.Count.ToString(CultureInfo.InvariantCulture)}\n");
        AppendBounded(result, $"Display-window omissions: {displayOmitted.ToString(CultureInfo.InvariantCulture)}\n");
        AppendBounded(result, $"Private raw-log omissions: {snapshot.RawLogOmittedEntryCount.ToString(CultureInfo.InvariantCulture)}\n");
        AppendBounded(result, $"Coalesced intermediate events: {snapshot.CoalescedEventCount.ToString(CultureInfo.InvariantCulture)}\n");

        foreach (InstallerDiagnosticEntry entry in entries)
        {
            string line = $"{entry.Timestamp.ToUniversalTime():O} [{GetLevelLabel(entry.Level)}] {entry.EventCode}: {entry.Message}";
            if (entry.StableErrorCode is not null)
                line += $" error={entry.StableErrorCode}";
            if (!TryAppendBounded(result, line + "\n", Encoding.UTF8.GetByteCount(SanitizedCopyTruncationMarker)))
            {
                AppendBounded(result, SanitizedCopyTruncationMarker);
                break;
            }
        }
        return result.ToString();
    }

    /// <summary>Acquire the one cross-window clipboard-write authority for this production session.</summary>
    internal bool TryAcquireClipboardWriteAuthority()
        => Interlocked.CompareExchange(ref this.clipboardWriteActive, 1, 0) == 0;

    internal bool IsClipboardWriteActive => Volatile.Read(ref this.clipboardWriteActive) != 0;

    /// <summary>Release clipboard authority only after the platform provider has actually settled.</summary>
    internal void ReleaseClipboardWriteAuthority()
        => Volatile.Write(ref this.clipboardWriteActive, 0);

    /// <summary>Queue a fixed state event without blocking the publishing controller.</summary>
    public void Record(InstallerDiagnosticCode code, string? releaseTag = null)
    {
        ValidatePlainRecordCode(code);
        this.Enqueue(new(code, null, releaseTag), IsTerminal(code) ? DiagnosticLane.Terminal : DiagnosticLane.Normal);
    }

    /// <summary>Queue a fixed state event with a validated protocol error classification.</summary>
    public void Record(InstallerDiagnosticCode code, ProtocolPrePlanErrorCode? error, string? releaseTag = null)
    {
        ValidatePlainRecordCode(code);
        this.Enqueue(new(code, GetStablePrePlanCode(error), releaseTag), IsTerminal(code) ? DiagnosticLane.Terminal : DiagnosticLane.Normal);
    }

    /// <summary>Queue a fixed terminal event with a validated protocol error classification.</summary>
    public void Record(InstallerDiagnosticCode code, ProtocolTerminalErrorCode? error, string? releaseTag = null)
    {
        ValidatePlainRecordCode(code);
        this.Enqueue(new(code, GetStableTerminalCode(error), releaseTag), IsTerminal(code) ? DiagnosticLane.Terminal : DiagnosticLane.Normal);
    }

    /// <summary>Queue a latest-value progress event. Replaced intermediate values are intentionally coalesced.</summary>
    public void RecordProgress(InstallerDiagnosticCode code, string? releaseTag = null)
        => this.RecordProgress(new ProgressKey(code, null, null), releaseTag);

    void IProductionInstallerDiagnosticSink.RecordProgress(InstallerDiagnosticCode code, ReviewedReleasePreparationStage stage)
    {
        _ = GetReleaseStageMessage(stage);
        this.RecordProgress(new ProgressKey(code, stage, null), releaseTag: null);
    }

    void IProductionInstallerDiagnosticSink.RecordProgress(InstallerDiagnosticCode code, TransactionStage stage)
    {
        _ = GetTransactionStageMessage(stage);
        this.RecordProgress(new ProgressKey(code, null, stage), releaseTag: null);
    }

    void IProductionInstallerDiagnosticSink.Record(InstallerDiagnosticCode code) => this.Record(code);

    void IProductionInstallerDiagnosticSink.Record(InstallerDiagnosticCode code, ProtocolPrePlanErrorCode? error) => this.Record(code, error);

    void IProductionInstallerDiagnosticSink.Record(InstallerDiagnosticCode code, ProtocolTerminalErrorCode? error) => this.Record(code, error);

    void IProductionInstallerDiagnosticSink.RecordExecutionTerminal(
        InstallerOperation operation,
        ProtocolExecutionOutcome outcome,
        ProtocolDurableState durableState,
        ProtocolTerminalErrorCode? error,
        ProtocolNextAction nextAction
    ) => this.RecordExactTerminal(
        InstallerDiagnosticCode.ExecutionTerminal,
        GetStableTerminalCode(error),
        ProjectExecutionTerminal(operation, outcome, durableState, nextAction)
    );

    void IProductionInstallerDiagnosticSink.RecordRecoveryTerminal(
        InstallerOperation operation,
        ProtocolInterruptedRecoveryOutcome outcome,
        ProtocolDurableState durableState,
        ProtocolTerminalErrorCode? error,
        ProtocolNextAction nextAction
    ) => this.RecordExactTerminal(
        InstallerDiagnosticCode.ExecutionRecoveryTerminal,
        GetStableTerminalCode(error),
        ProjectRecoveryTerminal(operation, outcome, durableState, nextAction)
    );

    void IProductionInstallerDiagnosticSink.RecordPruneTerminal(
        ProtocolPruneOutcome outcome,
        ProtocolDurableState durableState,
        ProtocolTerminalErrorCode? error,
        ProtocolNextAction nextAction
    ) => this.RecordExactTerminal(
        InstallerDiagnosticCode.RecoveryPruneTerminal,
        GetStableTerminalCode(error),
        ProjectPruneTerminal(outcome, durableState, nextAction)
    );

    private void RecordExactTerminal(
        InstallerDiagnosticCode code,
        string? stableErrorCode,
        DiagnosticProjection projection
    ) => this.Enqueue(new(code, stableErrorCode, null, Projection: projection), DiagnosticLane.Terminal);

    private void RecordProgress(ProgressKey key, string? releaseTag)
    {
        lock (this.Sync)
        {
            if (this.disposeStarted || this.unavailable)
                return;
            if (!this.ObservedProgress.Add(key))
            {
                this.IncrementCoalescedEvents();
                return;
            }
        }
        this.Enqueue(new(key.Code, null, releaseTag, key.ReleaseStage, key.TransactionStage), DiagnosticLane.Progress);
    }

    /// <summary>
    /// Durably prove logging is still available immediately before an explicit mutating action. Failure prevents
    /// admission but never changes an operation which was already admitted.
    /// </summary>
    public void EnsureReadyForMutation()
    {
        lock (this.Sync)
        {
            if (this.disposeStarted || this.unavailable)
                throw new InstallerDiagnosticsUnavailableException();
        }

        try
        {
            if (!this.WriteCore(new(InstallerDiagnosticCode.MutationLoggingVerified, null, null)))
            {
                this.MarkUnavailable();
                throw new InstallerDiagnosticsUnavailableException();
            }
        }
        catch (Exception ex) when (ex is not InstallerDiagnosticsUnavailableException)
        {
            this.MarkUnavailable();
            throw new InstallerDiagnosticsUnavailableException();
        }
    }

    /// <summary>Mark that the desktop lifetime returned normally so disposal can persist the exact session outcome.</summary>
    public void MarkCompleted()
    {
        lock (this.Sync)
        {
            if (this.disposeStarted)
                throw new ObjectDisposedException(nameof(InstallerDiagnosticSession));
            this.completed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        bool endedNormally;
        lock (this.Sync)
        {
            if (this.disposeStarted)
                return;
            this.disposeStarted = true;
            endedNormally = this.completed;
            this.revision++;
        }

        this.Terminal.Writer.TryComplete();
        this.Normal.Writer.TryComplete();
        this.Progress.Writer.TryComplete();
        this.SignalWriter();
        try
        {
            await this.Writer.ConfigureAwait(false);
            try
            {
                if (!this.WriteCore(
                    new(endedNormally ? InstallerDiagnosticCode.SessionCompleted : InstallerDiagnosticCode.SessionEndedUnexpectedly, null, null),
                    isLogTerminal: true
                ))
                    this.MarkUnavailable();
            }
            catch
            {
                this.MarkUnavailable();
            }
        }
        finally
        {
            this.Lifetime.Cancel();
            this.Lifetime.Dispose();
            this.WakeSignal.Dispose();
            try
            {
                this.Log.Dispose();
            }
            catch
            {
                // Logging failure during settlement must not change an already-settled installer outcome.
                this.MarkUnavailable();
            }
        }
    }

    private static Channel<PendingDiagnostic> CreateChannel(int capacity, BoundedChannelFullMode fullMode)
        => Channel.CreateBounded<PendingDiagnostic>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = fullMode
        });

    private void Enqueue(PendingDiagnostic value, DiagnosticLane lane)
    {
        lock (this.Sync)
        {
            if (this.disposeStarted || this.unavailable)
                return;
        }

        ChannelWriter<PendingDiagnostic> writer = lane switch
        {
            DiagnosticLane.Terminal => this.Terminal.Writer,
            DiagnosticLane.Progress => this.Progress.Writer,
            _ => this.Normal.Writer
        };
        if (!writer.TryWrite(value))
        {
            this.IncrementCoalescedEvents();
            if (lane == DiagnosticLane.Terminal)
            {
                this.IncrementRawLogOmittedEntries();
                this.MarkUnavailable();
            }
        }
        else
            this.SignalWriter();
    }

    private async Task RunWriterAsync()
    {
        try
        {
            while (true)
            {
                bool wrote = false;
                while (this.Terminal.Reader.TryRead(out PendingDiagnostic? terminal))
                {
                    this.WriteCore(terminal);
                    wrote = true;
                }
                if (this.Normal.Reader.TryRead(out PendingDiagnostic? normal))
                {
                    this.WriteCore(normal);
                    wrote = true;
                }
                if (this.Progress.Reader.TryRead(out PendingDiagnostic? progress))
                {
                    this.WriteCore(progress);
                    wrote = true;
                }
                if (wrote)
                    continue;

                if (this.Terminal.Reader.Completion.IsCompleted
                    && this.Normal.Reader.Completion.IsCompleted
                    && this.Progress.Reader.Completion.IsCompleted)
                {
                    break;
                }

                await this.WakeSignal.WaitAsync(this.Lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (this.Lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            this.MarkUnavailable();
        }
    }

    private void SignalWriter()
    {
        try
        {
            if (this.WakeSignal.CurrentCount == 0)
            {
                try { this.WakeSignal.Release(); }
                catch (SemaphoreFullException) { }
            }
        }
        catch (ObjectDisposedException) { }
    }

    private void IncrementCoalescedEvents()
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref this.coalescedEvents);
            if (observed == int.MaxValue)
                return;
        }
        while (Interlocked.CompareExchange(ref this.coalescedEvents, observed + 1, observed) != observed);
    }

    private void IncrementRawLogOmittedEntries()
    {
        lock (this.Sync)
        {
            if (this.rawLogOmittedEntries < int.MaxValue)
                this.rawLogOmittedEntries++;
            this.revision++;
        }
    }

    private bool WriteCore(PendingDiagnostic pending, bool isLogTerminal = false)
    {
        DiagnosticProjection projection = Project(pending);
        DateTimeOffset timestamp = this.GetNow();
        InstallerLogEntry entry = new(
            timestamp,
            this.OperationId,
            projection.Level,
            projection.EventCode,
            projection.Message,
            pending.ReleaseTag,
            StableErrorCode: pending.StableErrorCode
        );
        bool written;
        try
        {
            written = isLogTerminal
                ? this.Log.WriteTerminal(entry)
                : this.Log.Write(entry);
        }
        catch
        {
            this.IncrementRawLogOmittedEntries();
            throw;
        }

        lock (this.Sync)
        {
            if (!written)
            {
                if (this.rawLogOmittedEntries < int.MaxValue)
                    this.rawLogOmittedEntries++;
                this.revision++;
            }
            else
            {
                if (this.EntriesValue.Count == MaximumDisplayEntries)
                {
                    this.EntriesValue.RemoveAt(0);
                    if (this.displayOmittedEntries < int.MaxValue)
                        this.displayOmittedEntries++;
                }
                this.EntriesValue.Add(new(timestamp, projection.Level, projection.EventCode, projection.Message, pending.StableErrorCode));
                this.revision++;
            }
        }
        this.RaiseChangedSafely();
        return written;
    }

    private void MarkUnavailable()
    {
        bool changed;
        lock (this.Sync)
        {
            changed = !this.unavailable;
            this.unavailable = true;
            if (changed)
                this.revision++;
        }
        if (changed)
            this.RaiseChangedSafely();
    }

    private void RaiseChangedSafely()
    {
        try
        {
            this.Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Diagnostic presentation is ancillary and can't change logging or installer control flow.
        }
    }

    private static string? GetStablePrePlanCode(ProtocolPrePlanErrorCode? value) => value switch
    {
        null => null,
        ProtocolPrePlanErrorCode.RequestCancelled => "protocol.preplan.request-cancelled",
        ProtocolPrePlanErrorCode.InvalidGameFolder => "protocol.preplan.invalid-game-folder",
        ProtocolPrePlanErrorCode.PackageRejected => "protocol.preplan.package-rejected",
        ProtocolPrePlanErrorCode.PackageIntegrityRejected => "protocol.preplan.package-integrity-rejected",
        ProtocolPrePlanErrorCode.PackageMetadataRejected => "protocol.preplan.package-metadata-rejected",
        ProtocolPrePlanErrorCode.PackageArchiveRejected => "protocol.preplan.package-archive-rejected",
        ProtocolPrePlanErrorCode.PackageProvenanceRejected => "protocol.preplan.package-provenance-rejected",
        ProtocolPrePlanErrorCode.PackageReleaseIdentityRejected => "protocol.preplan.package-release-identity-rejected",
        ProtocolPrePlanErrorCode.RecoveryUnavailable => "protocol.preplan.recovery-unavailable",
        ProtocolPrePlanErrorCode.InspectionFailed => "protocol.preplan.inspection-failed",
        ProtocolPrePlanErrorCode.CandidateApprovalFailed => "protocol.preplan.candidate-approval-failed",
        ProtocolPrePlanErrorCode.PermissionDenied => "protocol.preplan.permission-denied",
        ProtocolPrePlanErrorCode.InputOutputFailure => "protocol.preplan.input-output-failure",
        ProtocolPrePlanErrorCode.UnexpectedFailure => "protocol.preplan.unexpected-failure",
        ProtocolPrePlanErrorCode.NothingToPrune => "protocol.preplan.nothing-to-prune",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string? GetStableTerminalCode(ProtocolTerminalErrorCode? value) => value switch
    {
        null => null,
        ProtocolTerminalErrorCode.InvalidPlan => "protocol.terminal.invalid-plan",
        ProtocolTerminalErrorCode.UnsafePath => "protocol.terminal.unsafe-path",
        ProtocolTerminalErrorCode.PathChanged => "protocol.terminal.path-changed",
        ProtocolTerminalErrorCode.ExistingFileMismatch => "protocol.terminal.existing-file-mismatch",
        ProtocolTerminalErrorCode.PayloadMismatch => "protocol.terminal.payload-mismatch",
        ProtocolTerminalErrorCode.ConcurrentOperation => "protocol.terminal.concurrent-operation",
        ProtocolTerminalErrorCode.WorkspaceConflict => "protocol.terminal.workspace-conflict",
        ProtocolTerminalErrorCode.RecoveryFailed => "protocol.terminal.recovery-failed",
        ProtocolTerminalErrorCode.DiskFull => "protocol.terminal.disk-full",
        ProtocolTerminalErrorCode.ReadOnlyFileSystem => "protocol.terminal.read-only-filesystem",
        ProtocolTerminalErrorCode.PermissionDenied => "protocol.terminal.permission-denied",
        ProtocolTerminalErrorCode.CrossDeviceBoundary => "protocol.terminal.cross-device-boundary",
        ProtocolTerminalErrorCode.IoFailure => "protocol.terminal.io-failure",
        ProtocolTerminalErrorCode.UnexpectedCoreFailure => "protocol.terminal.unexpected-core-failure",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static bool IsTerminal(InstallerDiagnosticCode code)
        => code is InstallerDiagnosticCode.ReleaseVerified
            or InstallerDiagnosticCode.ReleaseCancelled
            or InstallerDiagnosticCode.ReleaseFailed
            or InstallerDiagnosticCode.ReleaseNetworkUnavailable
            or InstallerDiagnosticCode.ReleaseNetworkTimedOut
            or InstallerDiagnosticCode.ReleaseDownloadInterrupted
            or InstallerDiagnosticCode.GameDiscoveryCancelled
            or InstallerDiagnosticCode.GameDiscoveryFailed
            or InstallerDiagnosticCode.PlanRejected
            or InstallerDiagnosticCode.PlanConfirmed
            or InstallerDiagnosticCode.PlanFailed
            or InstallerDiagnosticCode.ExecutionTerminal
            or InstallerDiagnosticCode.ExecutionRecoveryTerminal
            or InstallerDiagnosticCode.ExecutionFailed
            or InstallerDiagnosticCode.RecoveryPruneTerminal
            or InstallerDiagnosticCode.RecoveryPruneFailed;

    private static void ValidatePlainRecordCode(InstallerDiagnosticCode code)
    {
        if (code is InstallerDiagnosticCode.ExecutionTerminal
            or InstallerDiagnosticCode.ExecutionRecoveryTerminal
            or InstallerDiagnosticCode.RecoveryPruneTerminal)
        {
            throw new ArgumentException("An exact typed terminal projection is required for this diagnostic code.", nameof(code));
        }
    }

    private static DiagnosticProjection Project(PendingDiagnostic pending)
    {
        if (pending.Projection is { } exact)
            return exact;
        DiagnosticProjection projection = Project(pending.Code);
        if (pending.ReleaseStage is { } releaseStage)
            return projection with { Message = GetReleaseStageMessage(releaseStage) };
        if (pending.TransactionStage is { } transactionStage)
            return projection with { Message = GetTransactionStageMessage(transactionStage) };
        return projection;
    }

    private static DiagnosticProjection Project(InstallerDiagnosticCode code) => code switch
    {
        InstallerDiagnosticCode.SessionStarted => new("session.started", InstallerLogLevel.Information, "The production graphical installer session started."),
        InstallerDiagnosticCode.SessionCompleted => new("session.completed", InstallerLogLevel.Information, "The production graphical installer session ended normally."),
        InstallerDiagnosticCode.SessionEndedUnexpectedly => new("session.ended-unexpectedly", InstallerLogLevel.Error, "The production graphical installer session ended unexpectedly."),
        InstallerDiagnosticCode.ReleaseCatalogLoading => new("release.catalog.loading", InstallerLogLevel.Information, "Loading the reviewed release catalog."),
        InstallerDiagnosticCode.ReleaseCatalogReady => new("release.catalog.ready", InstallerLogLevel.Information, "The reviewed release catalog is ready."),
        InstallerDiagnosticCode.ReleaseCatalogEmpty => new("release.catalog.empty", InstallerLogLevel.Warning, "No compatible graphical-installer release is available."),
        InstallerDiagnosticCode.ReleaseCatalogFailed => new("release.catalog.failed", InstallerLogLevel.Error, "The reviewed release catalog could not be loaded."),
        InstallerDiagnosticCode.ReleaseVerificationStarted => new("release.verification.started", InstallerLogLevel.Information, "Release verification started."),
        InstallerDiagnosticCode.ReleaseDownloading => new("release.download.running", InstallerLogLevel.Information, "The exact reviewed release assets are downloading."),
        InstallerDiagnosticCode.ReleaseVerifying => new("release.verification.running", InstallerLogLevel.Information, "The downloaded release assets are being verified."),
        InstallerDiagnosticCode.ReleaseOpening => new("release.package.opening", InstallerLogLevel.Information, "The verified installer package is opening."),
        InstallerDiagnosticCode.ReleaseVerified => new("release.verified", InstallerLogLevel.Information, "The selected release and package were verified."),
        InstallerDiagnosticCode.ReleaseCancelled => new("release.cancelled", InstallerLogLevel.Warning, "Release preparation was cancelled safely."),
        InstallerDiagnosticCode.ReleaseFailed => new("release.failed", InstallerLogLevel.Error, "Release preparation or verification failed safely."),
        InstallerDiagnosticCode.ReleaseNetworkUnavailable => new("release.network.unavailable", InstallerLogLevel.Error, "A required release network request became unavailable before preparation completed."),
        InstallerDiagnosticCode.ReleaseNetworkTimedOut => new("release.network.timeout", InstallerLogLevel.Error, "A required release network request exceeded its bounded time limit before preparation completed."),
        InstallerDiagnosticCode.ReleaseDownloadInterrupted => new("release.download.interrupted", InstallerLogLevel.Error, "A release file transfer did not produce one complete expected file."),
        InstallerDiagnosticCode.GameDiscoveryStarted => new("game.discovery.started", InstallerLogLevel.Information, "Read-only game discovery started."),
        InstallerDiagnosticCode.GameDiscoveryReady => new("game.discovery.ready", InstallerLogLevel.Information, "A validated game selection is ready."),
        InstallerDiagnosticCode.GameDiscoveryEmpty => new("game.discovery.empty", InstallerLogLevel.Warning, "No validated game installation was detected."),
        InstallerDiagnosticCode.GameManualValidating => new("game.manual.validating", InstallerLogLevel.Information, "The selected game folder is being validated."),
        InstallerDiagnosticCode.GameManualValid => new("game.manual.valid", InstallerLogLevel.Information, "The selected game folder was validated."),
        InstallerDiagnosticCode.GameManualInvalid => new("game.manual.invalid", InstallerLogLevel.Warning, "The selected folder is not a valid supported game installation."),
        InstallerDiagnosticCode.GameDiscoveryCancelled => new("game.discovery.cancelled", InstallerLogLevel.Warning, "Game discovery was cancelled safely."),
        InstallerDiagnosticCode.GameDiscoveryFailed => new("game.discovery.failed", InstallerLogLevel.Error, "Game discovery or validation failed safely."),
        InstallerDiagnosticCode.PlanChoosing => new("plan.choosing", InstallerLogLevel.Information, "The installer is waiting for an explicit operation choice."),
        InstallerDiagnosticCode.PlanInspecting => new("plan.inspecting", InstallerLogLevel.Information, "The selected operation is being inspected without mutation."),
        InstallerDiagnosticCode.PlanReady => new("plan.ready", InstallerLogLevel.Information, "A read-only installer plan is ready for review."),
        InstallerDiagnosticCode.PlanRejected => new("plan.rejected", InstallerLogLevel.Warning, "Plan inspection was rejected safely."),
        InstallerDiagnosticCode.PlanConfirmed => new("plan.confirmed", InstallerLogLevel.Information, "The exact reviewed plan was confirmed without starting mutation."),
        InstallerDiagnosticCode.PlanFailed => new("plan.failed", InstallerLogLevel.Error, "Plan inspection or confirmation failed safely."),
        InstallerDiagnosticCode.ExecutionReady => new("execution.ready", InstallerLogLevel.Information, "The confirmed operation is ready and has not started."),
        InstallerDiagnosticCode.ExecutionStarting => new("execution.starting", InstallerLogLevel.Information, "The exact confirmed operation is starting."),
        InstallerDiagnosticCode.ExecutionProgress => new("execution.progress", InstallerLogLevel.Information, "A bounded installer progress stage was observed."),
        InstallerDiagnosticCode.ExecutionCancellationRequested => new("execution.cancellation.requested", InstallerLogLevel.Warning, "Cancellation was requested; the operation is finishing safely."),
        InstallerDiagnosticCode.ExecutionRecoveryRequired => new("execution.recovery.required", InstallerLogLevel.Warning, "Interrupted recovery is required before another installer action."),
        InstallerDiagnosticCode.ExecutionRecoveryStarting => new("execution.recovery.starting", InstallerLogLevel.Information, "Explicit interrupted recovery is starting."),
        InstallerDiagnosticCode.ExecutionRecoveryProgress => new("execution.recovery.progress", InstallerLogLevel.Information, "A bounded recovery progress stage was observed."),
        InstallerDiagnosticCode.ExecutionTerminal => new("execution.terminal", InstallerLogLevel.Information, "The installer reported an exact durable operation result."),
        InstallerDiagnosticCode.ExecutionRecoveryTerminal => new("execution.recovery.terminal", InstallerLogLevel.Information, "The installer reported an exact durable recovery result."),
        InstallerDiagnosticCode.ExecutionFailed => new("execution.failed", InstallerLogLevel.Error, "The operation state could not be confirmed safely."),
        InstallerDiagnosticCode.RecoveryPruneLoading => new("recovery.prune.loading", InstallerLogLevel.Information, "Authenticated recovery history is loading."),
        InstallerDiagnosticCode.RecoveryPruneReady => new("recovery.prune.ready", InstallerLogLevel.Information, "Authenticated recovery history is ready for explicit selection."),
        InstallerDiagnosticCode.RecoveryPruneInspecting => new("recovery.prune.inspecting", InstallerLogLevel.Information, "Recovery cleanup is being inspected without mutation."),
        InstallerDiagnosticCode.RecoveryPrunePlanReady => new("recovery.prune.plan.ready", InstallerLogLevel.Warning, "A destructive recovery-cleanup plan is ready for review."),
        InstallerDiagnosticCode.RecoveryPruneConfirmed => new("recovery.prune.confirmed", InstallerLogLevel.Warning, "The exact cleanup plan was confirmed without starting mutation."),
        InstallerDiagnosticCode.RecoveryPruneStarting => new("recovery.prune.starting", InstallerLogLevel.Warning, "The exact confirmed recovery cleanup is starting."),
        InstallerDiagnosticCode.RecoveryPruneProgress => new("recovery.prune.progress", InstallerLogLevel.Information, "A bounded recovery-cleanup progress stage was observed."),
        InstallerDiagnosticCode.RecoveryPruneCancellationRequested => new("recovery.prune.cancellation.requested", InstallerLogLevel.Warning, "Recovery-cleanup cancellation was requested; the operation is finishing safely."),
        InstallerDiagnosticCode.RecoveryPruneTerminal => new("recovery.prune.terminal", InstallerLogLevel.Information, "The installer reported an exact durable recovery-cleanup result."),
        InstallerDiagnosticCode.RecoveryPruneFailed => new("recovery.prune.failed", InstallerLogLevel.Error, "Recovery cleanup could not be completed or confirmed safely."),
        InstallerDiagnosticCode.DiagnosticsCoalesced => new("diagnostics.coalesced", InstallerLogLevel.Warning, "Intermediate diagnostic events were coalesced to preserve bounded operation behavior."),
        InstallerDiagnosticCode.MutationLoggingVerified => new("diagnostics.mutation-ready", InstallerLogLevel.Information, "Private diagnostic logging was verified immediately before mutation admission."),
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    private static DiagnosticProjection ProjectExecutionTerminal(
        InstallerOperation operation,
        ProtocolExecutionOutcome outcome,
        ProtocolDurableState durableState,
        ProtocolNextAction nextAction
    )
    {
        (string suffix, InstallerLogLevel level, string label) = outcome switch
        {
            ProtocolExecutionOutcome.Succeeded => ("succeeded", InstallerLogLevel.Information, "succeeded"),
            ProtocolExecutionOutcome.SucceededWithCleanupWarning => ("succeeded-cleanup-warning", InstallerLogLevel.Warning, "succeeded with a cleanup warning"),
            ProtocolExecutionOutcome.FailedBeforeMutation => ("failed-before-mutation", InstallerLogLevel.Error, "failed before mutation"),
            ProtocolExecutionOutcome.CancelledBeforeMutation => ("cancelled-before-mutation", InstallerLogLevel.Warning, "was cancelled before mutation"),
            ProtocolExecutionOutcome.CancelledAndRolledBack => ("cancelled-rolled-back", InstallerLogLevel.Warning, "was cancelled and rolled back"),
            ProtocolExecutionOutcome.FailedAndRolledBack => ("failed-rolled-back", InstallerLogLevel.Error, "failed and rolled back"),
            ProtocolExecutionOutcome.InterruptedRecoveryRequired => ("interrupted-recovery-required", InstallerLogLevel.Error, "was interrupted and requires recovery"),
            ProtocolExecutionOutcome.AutomaticRecoveryCompletedFreshInspectionRequired => ("automatic-recovery-completed", InstallerLogLevel.Warning, "completed automatic recovery and requires fresh inspection"),
            ProtocolExecutionOutcome.UnexpectedCoreFailure => ("unexpected-core-failure", InstallerLogLevel.Error, "ended with an unexpected core failure"),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        return new(
            $"execution.terminal.{suffix}",
            level,
            $"{GetOperationLabel(operation)} {label}. Durable state: {GetDurableStateLabel(durableState)}. Safe next action: {GetNextActionLabel(nextAction)}."
        );
    }

    private static DiagnosticProjection ProjectRecoveryTerminal(
        InstallerOperation operation,
        ProtocolInterruptedRecoveryOutcome outcome,
        ProtocolDurableState durableState,
        ProtocolNextAction nextAction
    )
    {
        (string suffix, InstallerLogLevel level, string label) = outcome switch
        {
            ProtocolInterruptedRecoveryOutcome.RecoveryCompleted => ("completed", InstallerLogLevel.Information, "completed"),
            ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery => ("cancelled-before-start", InstallerLogLevel.Warning, "was cancelled before recovery began"),
            ProtocolInterruptedRecoveryOutcome.PartialFailure => ("partial-failure", InstallerLogLevel.Error, "completed only partially"),
            ProtocolInterruptedRecoveryOutcome.UnexpectedFailure => ("unexpected-failure", InstallerLogLevel.Error, "ended with an unexpected failure"),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        return new(
            $"execution.recovery.terminal.{suffix}",
            level,
            $"Interrupted recovery for {GetOperationLabel(operation).ToLowerInvariant()} {label}. Durable state: {GetDurableStateLabel(durableState)}. Safe next action: {GetNextActionLabel(nextAction)}."
        );
    }

    private static DiagnosticProjection ProjectPruneTerminal(
        ProtocolPruneOutcome outcome,
        ProtocolDurableState durableState,
        ProtocolNextAction nextAction
    )
    {
        (string suffix, InstallerLogLevel level, string label) = outcome switch
        {
            ProtocolPruneOutcome.Succeeded => ("succeeded", InstallerLogLevel.Information, "succeeded"),
            ProtocolPruneOutcome.FailedBeforePublication => ("failed-before-publication", InstallerLogLevel.Error, "failed before publication"),
            ProtocolPruneOutcome.CancelledBeforePublication => ("cancelled-before-publication", InstallerLogLevel.Warning, "was cancelled before publication"),
            ProtocolPruneOutcome.Interrupted => ("interrupted", InstallerLogLevel.Error, "was interrupted"),
            ProtocolPruneOutcome.CancelledWithCleanupPending => ("cancelled-cleanup-pending", InstallerLogLevel.Warning, "was cancelled with cleanup pending"),
            ProtocolPruneOutcome.FailedWithCleanupPending => ("failed-cleanup-pending", InstallerLogLevel.Error, "failed with cleanup pending"),
            ProtocolPruneOutcome.UnexpectedCoreFailure => ("unexpected-core-failure", InstallerLogLevel.Error, "ended with an unexpected core failure"),
            ProtocolPruneOutcome.CancelledAfterApply => ("cancelled-after-apply", InstallerLogLevel.Warning, "was cancelled after applying the reviewed prune"),
            ProtocolPruneOutcome.FailedAfterApply => ("failed-after-apply", InstallerLogLevel.Error, "failed after applying the reviewed prune"),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        return new(
            $"recovery.prune.terminal.{suffix}",
            level,
            $"Recovery cleanup {label}. Durable state: {GetDurableStateLabel(durableState)}. Safe next action: {GetNextActionLabel(nextAction)}."
        );
    }

    private static string GetOperationLabel(InstallerOperation operation) => operation switch
    {
        InstallerOperation.Install => "Install",
        InstallerOperation.Update => "Update",
        InstallerOperation.Repair => "Repair",
        InstallerOperation.Uninstall => "Uninstall",
        InstallerOperation.Backup => "Backup",
        InstallerOperation.Rollback => "Rollback",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static string GetDurableStateLabel(ProtocolDurableState durableState) => durableState switch
    {
        ProtocolDurableState.Committed => "committed",
        ProtocolDurableState.Unchanged => "unchanged",
        ProtocolDurableState.RolledBack => "rolled back",
        ProtocolDurableState.RecoveryRequired => "recovery required",
        ProtocolDurableState.RecoveryCompleted => "recovery completed",
        ProtocolDurableState.Unknown => "unknown; inspect before another action",
        ProtocolDurableState.PruneApplied => "recovery cleanup applied",
        _ => throw new ArgumentOutOfRangeException(nameof(durableState))
    };

    private static string GetNextActionLabel(ProtocolNextAction nextAction) => nextAction switch
    {
        ProtocolNextAction.RetryRequest => "retry the request",
        ProtocolNextAction.SelectGameFolder => "select a game folder",
        ProtocolNextAction.ReopenVerifiedPackage => "reopen the verified package",
        ProtocolNextAction.InspectAgain => "inspect again",
        ProtocolNextAction.ListRecoveries => "list authenticated recoveries",
        ProtocolNextAction.RecoverInterrupted => "recover interrupted state",
        ProtocolNextAction.StartNewSession => "start a new verified session",
        ProtocolNextAction.ReviewFilesystem => "review the filesystem",
        ProtocolNextAction.ViewPrivateLog => "review the private raw log",
        _ => throw new ArgumentOutOfRangeException(nameof(nextAction))
    };

    private static string GetHealthLabel(InstallerDiagnosticHealth health) => health switch
    {
        InstallerDiagnosticHealth.Healthy => "complete within configured bounds",
        InstallerDiagnosticHealth.BoundedWithOmissions => "bounded; some events were omitted or coalesced",
        InstallerDiagnosticHealth.WriteFailed => "private raw-log write failed; no further mutation is admitted",
        InstallerDiagnosticHealth.Disposed => "session closed; this is the last captured in-memory state",
        _ => throw new ArgumentOutOfRangeException(nameof(health))
    };

    private static string GetLevelLabel(InstallerLogLevel level) => level switch
    {
        InstallerLogLevel.Information => "information",
        InstallerLogLevel.Warning => "warning",
        InstallerLogLevel.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    private static string GetReleaseStageMessage(ReviewedReleasePreparationStage stage) => stage switch
    {
        ReviewedReleasePreparationStage.ObservingTag => "Release preparation stage: observing the selected public tag.",
        ReviewedReleasePreparationStage.Downloading => "Release preparation stage: downloading the reviewed assets.",
        ReviewedReleasePreparationStage.RefreshingTag => "Release preparation stage: refreshing the selected public tag.",
        ReviewedReleasePreparationStage.ImportingLocalPackage => "Release preparation stage: importing the selected local package.",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    private static string GetTransactionStageMessage(TransactionStage stage) => stage switch
    {
        TransactionStage.AcquiringLock => "Installer progress stage: acquiring the operation lock.",
        TransactionStage.Recovering => "Installer progress stage: recovering interrupted installer state.",
        TransactionStage.Staging => "Installer progress stage: staging reviewed changes.",
        TransactionStage.Revalidating => "Installer progress stage: revalidating the target.",
        TransactionStage.Applying => "Installer progress stage: applying reviewed changes.",
        TransactionStage.Verifying => "Installer progress stage: verifying applied changes.",
        TransactionStage.Committing => "Installer progress stage: committing durable installer state.",
        TransactionStage.RollingBack => "Installer progress stage: rolling back managed changes.",
        TransactionStage.Completed => "Installer progress stage: operation completed.",
        TransactionStage.Inspecting => "Installer progress stage: inspecting managed state.",
        TransactionStage.VerifyingRecovery => "Installer progress stage: verifying recovery state.",
        TransactionStage.PreparingRecovery => "Installer progress stage: preparing recovery.",
        TransactionStage.PreparingPayload => "Installer progress stage: preparing the verified payload.",
        TransactionStage.WritingFiles => "Installer progress stage: writing managed files.",
        TransactionStage.RemovingFiles => "Installer progress stage: removing managed files.",
        TransactionStage.UpdatingLauncher => "Installer progress stage: updating the managed launcher.",
        TransactionStage.UpdatingInstallerState => "Installer progress stage: updating private installer state.",
        TransactionStage.PublishingRecovery => "Installer progress stage: publishing recovery state.",
        TransactionStage.CleaningRecovery => "Installer progress stage: cleaning authenticated recovery state.",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    private static void AppendBounded(StringBuilder builder, string value)
    {
        if (!TryAppendBounded(builder, value))
            throw new InvalidOperationException("The fixed diagnostic copy header exceeded its reviewed bound.");
    }

    private static bool TryAppendBounded(StringBuilder builder, string value, int reservedBytes = 0)
    {
        if (Encoding.UTF8.GetByteCount(builder.ToString()) + Encoding.UTF8.GetByteCount(value) + reservedBytes > MaximumSanitizedCopyBytes)
            return false;
        builder.Append(value);
        return true;
    }

    private enum DiagnosticLane { Normal, Progress, Terminal }
    private readonly record struct ProgressKey(
        InstallerDiagnosticCode Code,
        ReviewedReleasePreparationStage? ReleaseStage,
        TransactionStage? TransactionStage
    );
    private sealed record PendingDiagnostic(
        InstallerDiagnosticCode Code,
        string? StableErrorCode,
        string? ReleaseTag,
        ReviewedReleasePreparationStage? ReleaseStage = null,
        TransactionStage? TransactionStage = null,
        DiagnosticProjection? Projection = null
    );
    private sealed record DiagnosticProjection(string EventCode, InstallerLogLevel Level, string Message);
}

/// <summary>A safe refusal raised before mutation when the required private diagnostic record is unavailable.</summary>
internal sealed class InstallerDiagnosticsUnavailableException : InvalidOperationException
{
    public InstallerDiagnosticsUnavailableException()
        : base("Private installer diagnostics are unavailable; no operation was started.") { }
}
