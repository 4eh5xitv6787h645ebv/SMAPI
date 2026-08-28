using System.Collections.ObjectModel;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Engine;

/// <summary>The bounded result of explicitly recovering interrupted installer work under one anchored game-root lease.</summary>
public sealed class InterruptedOperationRecoveryResult
{
    /// <summary>The exact anchored game root which was recovered; its original named path may no longer select it.</summary>
    public GameRootIdentity GameRoot { get; }

    /// <summary>Whether the originally selected named path still resolves to the exact recovered root.</summary>
    public bool NamedRootStillSelected { get; }

    /// <summary>Whether the named path was removed or replaced while the anchored recovery completed.</summary>
    public bool NamedRootSelectionChanged => !this.NamedRootStillSelected;

    /// <summary>The operation generation observed after the exclusive lease was acquired and before recovery began.</summary>
    public ulong PreviousOperationGeneration { get; }

    /// <summary>The newly published operation generation after recovery completed.</summary>
    public ulong CurrentOperationGeneration { get; }

    /// <summary>The durable transactions which were found incomplete and restored, in bounded store order.</summary>
    public IReadOnlyList<TransactionResult> RecoveredTransactions { get; }

    /// <summary>Whether at least one published transaction required exact rollback.</summary>
    public bool RecoveredAny => this.RecoveredTransactions.Count > 0;

    /// <summary>Whether the caller must discard every prior inspection and create a fresh one.</summary>
    public bool RequiresFreshInspection => true;

    internal InterruptedOperationRecoveryResult(
        GameRootIdentity gameRoot,
        ulong previousOperationGeneration,
        ulong currentOperationGeneration,
        IEnumerable<TransactionResult> recoveredTransactions,
        bool namedRootStillSelected = true
    )
    {
        ArgumentNullException.ThrowIfNull(gameRoot);
        ArgumentNullException.ThrowIfNull(recoveredTransactions);
        if (currentOperationGeneration <= previousOperationGeneration)
            throw new ArgumentOutOfRangeException(nameof(currentOperationGeneration), "Recovery must publish a newer operation generation.");

        TransactionResult[] recovered = recoveredTransactions.ToArray();
        if (recovered.Length > InstallerTransactionExecutor.MaximumTransactionStoreEntries)
            throw new ArgumentException("Interrupted-operation recovery exceeded the bounded transaction store.", nameof(recoveredTransactions));
        if (recovered.Any(result => result is null))
            throw new ArgumentException("Interrupted-operation recovery results can't contain null entries.", nameof(recoveredTransactions));
        if (recovered.Any(result => result.Status != TransactionStatus.Recovered))
            throw new ArgumentException("Interrupted-operation recovery can report only recovered transactions.", nameof(recoveredTransactions));

        this.GameRoot = gameRoot;
        this.NamedRootStillSelected = namedRootStillSelected;
        this.PreviousOperationGeneration = previousOperationGeneration;
        this.CurrentOperationGeneration = currentOperationGeneration;
        this.RecoveredTransactions = new ReadOnlyCollection<TransactionResult>(recovered);
    }
}

/// <summary>
/// A typed interrupted-operation recovery failure which preserves every exact transaction already restored before a
/// later transaction, cleanup, or generation-publication boundary failed.
/// </summary>
public sealed class InterruptedOperationRecoveryException : Exception
{
    /// <summary>The exact anchored game root on which recovery ran.</summary>
    public GameRootIdentity GameRoot { get; }

    /// <summary>The operation generation observed before recovery began.</summary>
    public ulong PreviousOperationGeneration { get; }

    /// <summary>The operation generation durably observed when the failed attempt ended, or <see langword="null"/> when it couldn't be read safely.</summary>
    public ulong? CurrentOperationGeneration { get; }

    /// <summary>Whether a newer operation generation was durably published, or <see langword="null"/> when it couldn't be verified.</summary>
    public bool? OperationGenerationAdvanced => this.CurrentOperationGeneration is { } current
        ? current > this.PreviousOperationGeneration
        : null;

    /// <summary>Whether the originally selected path still named the anchored root, or <see langword="null"/> when that couldn't be verified.</summary>
    public bool? NamedRootStillSelected { get; }

    /// <summary>Whether the originally selected path changed, or <see langword="null"/> when that couldn't be verified.</summary>
    public bool? NamedRootSelectionChanged => this.NamedRootStillSelected is { } selected ? !selected : null;

    /// <summary>Every transaction which reached a durable rolled-back event before the later failure.</summary>
    public IReadOnlyList<TransactionResult> RecoveredTransactions { get; }

    /// <summary>Whether at least one transaction was completely restored.</summary>
    public bool RecoveredAny => this.RecoveredTransactions.Count > 0;

    /// <summary>Whether every prior inspection must be discarded.</summary>
    public bool RequiresFreshInspection => true;

    /// <summary>Whether interrupted-operation recovery must be retried.</summary>
    public bool RequiresRecovery => true;

    /// <summary>The stable failure code.</summary>
    public TransactionErrorCode ErrorCode { get; }

    /// <summary>A bounded path-free explanation suitable for display.</summary>
    public string SafeMessage { get; }

    internal InterruptedOperationRecoveryException(
        GameRootIdentity gameRoot,
        ulong previousOperationGeneration,
        ulong? currentOperationGeneration,
        bool? namedRootStillSelected,
        IEnumerable<TransactionResult> recoveredTransactions,
        TransactionErrorCode errorCode,
        string safeMessage,
        Exception failure
    )
        : base(safeMessage, failure)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);
        ArgumentNullException.ThrowIfNull(recoveredTransactions);
        if (string.IsNullOrEmpty(safeMessage))
            throw new ArgumentException("A safe recovery failure message is required.", nameof(safeMessage));
        if (currentOperationGeneration is { } current && current < previousOperationGeneration)
            throw new ArgumentOutOfRangeException(nameof(currentOperationGeneration));
        TransactionResult[] recovered = recoveredTransactions.ToArray();
        if (recovered.Length > InstallerTransactionExecutor.MaximumTransactionStoreEntries)
            throw new ArgumentException("Interrupted-operation recovery exceeded the bounded transaction store.", nameof(recoveredTransactions));
        if (recovered.Any(result => result is null || result.Status != TransactionStatus.Recovered))
            throw new ArgumentException("Interrupted-operation recovery failures can report only exact completed recoveries.", nameof(recoveredTransactions));
        this.GameRoot = gameRoot;
        this.PreviousOperationGeneration = previousOperationGeneration;
        this.CurrentOperationGeneration = currentOperationGeneration;
        this.NamedRootStillSelected = namedRootStillSelected;
        this.RecoveredTransactions = new ReadOnlyCollection<TransactionResult>(recovered);
        this.ErrorCode = errorCode;
        this.SafeMessage = safeMessage;
    }
}
