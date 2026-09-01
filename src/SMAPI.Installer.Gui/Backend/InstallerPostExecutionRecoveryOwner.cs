using System.Threading.Channels;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>
/// Sealed ownership for explicit post-execution recovery. It retains only a private exact game path and a fresh
/// client factory plus a sanitized release re-verification hint; it never retains package, plan, confirmation, or
/// execution authority.
/// </summary>
internal sealed class InstallerPostExecutionRecoveryOwner : IAsyncDisposable
{
    private enum OwnerState
    {
        Available,
        Preparing,
        Admitted,
        Completed,
        Disposing,
        Disposed
    }

    private readonly object Sync = new();
    private readonly Func<IInstallerProtocolClient> ClientFactory;
    private readonly string ExactCanonicalPath;
    /// <summary>
    /// Sanitized identity hint for a future release re-verification/fresh-inspection flow. This is data only and is
    /// never treated as package authority or sent during interrupted recovery.
    /// </summary>
    public ProtocolReleaseIdentity Release { get; }
    private readonly CancellationTokenSource Lifetime = new();
    private Task PriorBackendSettlement = Task.CompletedTask;
    private bool PriorBackendSettlementAttached;
    private TaskCompletionSource? AttemptSettled;
    private Task? DisposalTask;
    private OwnerState State;

    internal InstallerPostExecutionRecoveryOwner(
        Func<IInstallerProtocolClient> clientFactory,
        string exactCanonicalPath,
        ProtocolReleaseIdentity release
    )
    {
        this.ClientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.ExactCanonicalPath = exactCanonicalPath ?? throw new ArgumentNullException(nameof(exactCanonicalPath));
        this.Release = release ?? throw new ArgumentNullException(nameof(release));
    }

    /// <summary>Attach cleanup of the execution backend before this owner is published to its caller.</summary>
    internal void AttachPriorBackendSettlement(Task settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        lock (this.Sync)
        {
            if (this.PriorBackendSettlementAttached || this.State != OwnerState.Available)
                throw new InvalidOperationException("The prior backend settlement was already attached.");
            this.PriorBackendSettlementAttached = true;
            this.PriorBackendSettlement = ObserveCleanupAsync(settlement);
        }
    }

    /// <summary>
    /// Explicitly start one fresh recovery attempt. Cancellation can stop only fresh-session preparation; once the
    /// recovery client admits or returns an operation, cleanup awaits its terminal without cancellation.
    /// </summary>
    public async Task<InstallerRecoveryOperation> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        Task priorSettlement;
        TaskCompletionSource attemptSettled;
        CancellationToken lifetime;
        lock (this.Sync)
        {
            if (this.State is OwnerState.Disposing or OwnerState.Disposed)
                throw new ObjectDisposedException(nameof(InstallerPostExecutionRecoveryOwner));
            if (this.State == OwnerState.Completed)
                throw new InvalidOperationException("Interrupted recovery completed and requires the indicated fresh next step.");
            if (this.State != OwnerState.Available)
                throw new InvalidOperationException("An interrupted-recovery attempt is already active.");
            if (!this.PriorBackendSettlementAttached)
                throw new InvalidOperationException("The prior installer backend must be settled before interrupted recovery can start.");
            cancellationToken.ThrowIfCancellationRequested();
            this.State = OwnerState.Preparing;
            this.AttemptSettled = attemptSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            priorSettlement = this.PriorBackendSettlement;
            lifetime = this.Lifetime.Token;
        }

        IInstallerProtocolClient? client = null;
        using CancellationTokenSource preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        try
        {
            await priorSettlement.WaitAsync(preparation.Token).ConfigureAwait(false);
            preparation.Token.ThrowIfCancellationRequested();
            client = this.ClientFactory()
                ?? throw new InstallerProtocolClientException("A fresh interrupted-recovery session isn't available.");

            HandshakeEvent handshake = await client.HandshakeAsync(
                InstallerProtocolClientIdentity.Name,
                InstallerProtocolClientIdentity.Version,
                preparation.Token
            ).ConfigureAwait(false);
            if (
                handshake is null
                || !handshake.Capabilities.Contains(ProcessInstallerProtocolClient.GameValidationCapability, StringComparer.Ordinal)
                || !handshake.Capabilities.Contains(ProcessInstallerProtocolClient.InterruptedRecoveryCapability, StringComparer.Ordinal)
            )
            {
                throw new InstallerProtocolClientException("The fresh installer backend doesn't support interrupted recovery.");
            }

            ProtocolGameCandidate candidate = await client.ValidateGameAsync(this.ExactCanonicalPath, preparation.Token).ConfigureAwait(false);
            if (
                candidate is null
                || candidate.State != LinuxGameFolderStatus.Valid
                || !StringComparer.Ordinal.Equals(candidate.CanonicalPath, this.ExactCanonicalPath)
            )
            {
                throw new InstallerProtocolClientException("The selected game folder is no longer the exact valid recovery target.");
            }

            preparation.Token.ThrowIfCancellationRequested();
            InstallerRecoveryOperation? operation;
            try
            {
                operation = await client.RecoverInterruptedAsync(candidate, preparation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (preparation.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                lock (this.Sync)
                {
                    if (this.State == OwnerState.Preparing)
                        this.State = OwnerState.Admitted;
                }
                return this.CreateUnknownOperation(client, attemptSettled);
            }
            lock (this.Sync)
            {
                // A returned operation is the process client's admission acknowledgement. If disposal raced with
                // that boundary, publish and settle the operation without disposing its client early.
                if (this.State == OwnerState.Preparing)
                    this.State = OwnerState.Admitted;
                else if (this.State != OwnerState.Disposing)
                    throw new ObjectDisposedException(nameof(InstallerPostExecutionRecoveryOwner));
            }
            if (operation?.Progress is null || operation.Completion is null)
                return this.CreateUnknownOperation(client, attemptSettled);

            Task<InstallerRecoveryResult> completion = this.CompleteAdmittedAttemptAsync(operation.Completion, client, attemptSettled);
            return new InstallerRecoveryOperation(operation.Progress, completion);
        }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested)
        {
            await DisposeClientAsync(client).ConfigureAwait(false);
            this.ResetAfterPreparation(attemptSettled);
            CancellationToken cancelled = cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(canceled: true);
            throw new OperationCanceledException("Interrupted-recovery preparation was cancelled safely.", cancelled);
        }
        catch
        {
            await DisposeClientAsync(client).ConfigureAwait(false);
            this.ResetAfterPreparation(attemptSettled);
            throw new InstallerProtocolClientException("A fresh interrupted-recovery session could not be prepared safely.");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (this.Sync)
        {
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            if (this.State == OwnerState.Disposed)
                return ValueTask.CompletedTask;
            OwnerState previous = this.State;
            this.State = OwnerState.Disposing;
            if (previous == OwnerState.Preparing)
            {
                try { this.Lifetime.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            return new ValueTask(this.DisposalTask = this.DisposeCoreAsync(this.AttemptSettled?.Task));
        }
    }

    private InstallerRecoveryOperation CreateUnknownOperation(IInstallerProtocolClient client, TaskCompletionSource attemptSettled)
    {
        Channel<InstallerRecoveryProgress> progress = Channel.CreateBounded<InstallerRecoveryProgress>(1);
        progress.Writer.TryComplete();
        Task<InstallerRecoveryResult> completion = this.CompleteAdmittedAttemptAsync(
            Task.FromResult<InstallerRecoveryResult>(new InstallerRecoveryStateUnknownResult()),
            client,
            attemptSettled
        );
        return new(progress.Reader, completion);
    }

    private async Task<InstallerRecoveryResult> CompleteAdmittedAttemptAsync(
        Task<InstallerRecoveryResult> completion,
        IInstallerProtocolClient client,
        TaskCompletionSource attemptSettled
    )
    {
        InstallerRecoveryResult result;
        try { result = NormalizeResult(await completion.ConfigureAwait(false)); }
        catch { result = new InstallerRecoveryStateUnknownResult(); }
        await DisposeClientAsync(client).ConfigureAwait(false);
        lock (this.Sync)
        {
            if (this.State == OwnerState.Admitted)
                this.State = RequiresRetry(result) ? OwnerState.Available : OwnerState.Completed;
            if (ReferenceEquals(this.AttemptSettled, attemptSettled))
                this.AttemptSettled = null;
        }
        attemptSettled.TrySetResult();
        return result;
    }

    private void ResetAfterPreparation(TaskCompletionSource attemptSettled)
    {
        lock (this.Sync)
        {
            if (this.State == OwnerState.Preparing)
                this.State = OwnerState.Available;
            if (ReferenceEquals(this.AttemptSettled, attemptSettled))
                this.AttemptSettled = null;
        }
        attemptSettled.TrySetResult();
    }

    private async Task DisposeCoreAsync(Task? attempt)
    {
        try
        {
            await ObserveCleanupAsync(this.PriorBackendSettlement).ConfigureAwait(false);
            if (attempt is not null)
                await attempt.ConfigureAwait(false);
        }
        finally
        {
            this.Lifetime.Dispose();
            lock (this.Sync)
                this.State = OwnerState.Disposed;
        }
    }

    private static InstallerRecoveryResult NormalizeResult(InstallerRecoveryResult? result)
    {
        if (result is InstallerRecoveryStateUnknownResult)
            return result;
        if (result is not InstallerRecoveryTerminalResult terminal)
            return new InstallerRecoveryStateUnknownResult();
        if (
            !Enum.IsDefined(terminal.BackendSettlement)
            || terminal.ErrorCode is { } error && !Enum.IsDefined(error)
            || terminal.Attempt is { } attempt
                && (attempt.RecoveredTransactionCount is < 0 or > ProcessInstallerProtocolClient.MaximumRecoveryTransactions
                    || attempt.RecoveredPathCount is < 0 or > ProcessInstallerProtocolClient.MaximumExecutionProgressUnits)
        )
        {
            return new InstallerRecoveryStateUnknownResult();
        }

        bool valid = terminal switch
        {
            {
                Outcome: ProtocolInterruptedRecoveryOutcome.RecoveryCompleted,
                DurableState: ProtocolDurableState.RecoveryCompleted,
                ErrorCode: null,
                RecoveryDisposition: ProtocolRecoveryDisposition.Completed,
                Attempt: { OperationGenerationAdvanced: true, NamedRootStillSelected: true },
                NextAction: ProtocolNextAction.InspectAgain
            } => true,
            {
                Outcome: ProtocolInterruptedRecoveryOutcome.RecoveryCompleted,
                DurableState: ProtocolDurableState.RecoveryCompleted,
                ErrorCode: null,
                RecoveryDisposition: ProtocolRecoveryDisposition.Completed,
                Attempt: { OperationGenerationAdvanced: true, NamedRootStillSelected: false },
                NextAction: ProtocolNextAction.SelectGameFolder
            } => true,
            {
                Outcome: ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery,
                DurableState: ProtocolDurableState.Unchanged,
                ErrorCode: null,
                RecoveryDisposition: ProtocolRecoveryDisposition.InterruptedRecoveryRequired,
                Attempt: null,
                NextAction: ProtocolNextAction.RecoverInterrupted
            } => true,
            {
                Outcome: ProtocolInterruptedRecoveryOutcome.PartialFailure,
                DurableState: ProtocolDurableState.RecoveryRequired,
                ErrorCode: not null and not ProtocolTerminalErrorCode.UnexpectedCoreFailure,
                RecoveryDisposition: ProtocolRecoveryDisposition.InterruptedRecoveryRequired,
                Attempt: not null,
                NextAction: ProtocolNextAction.RecoverInterrupted
            } => true,
            {
                Outcome: ProtocolInterruptedRecoveryOutcome.UnexpectedFailure,
                DurableState: ProtocolDurableState.Unknown,
                ErrorCode: ProtocolTerminalErrorCode.UnexpectedCoreFailure,
                RecoveryDisposition: ProtocolRecoveryDisposition.InterruptedRecoveryRequired,
                Attempt: null,
                NextAction: ProtocolNextAction.RecoverInterrupted
            } => true,
            _ => false
        };
        return valid ? terminal : new InstallerRecoveryStateUnknownResult();
    }

    private static bool RequiresRetry(InstallerRecoveryResult result)
        => result is InstallerRecoveryStateUnknownResult
            || result is InstallerRecoveryTerminalResult { NextAction: ProtocolNextAction.RecoverInterrupted };

    private static async Task ObserveCleanupAsync(Task cleanup)
    {
        try { await cleanup.ConfigureAwait(false); }
        catch { }
    }

    private static async Task DisposeClientAsync(IInstallerProtocolClient? client)
    {
        if (client is null)
            return;
        try { await client.DisposeAsync().ConfigureAwait(false); }
        catch { }
    }
}
