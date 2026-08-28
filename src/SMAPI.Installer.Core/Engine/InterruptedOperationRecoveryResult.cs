using System.Collections.ObjectModel;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Engine;

/// <summary>The bounded result of explicitly recovering interrupted installer work under one anchored game-root lease.</summary>
public sealed class InterruptedOperationRecoveryResult
{
    /// <summary>The exact game root which was recovered and revalidated.</summary>
    public GameRootIdentity GameRoot { get; }

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
        IEnumerable<TransactionResult> recoveredTransactions
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
        this.PreviousOperationGeneration = previousOperationGeneration;
        this.CurrentOperationGeneration = currentOperationGeneration;
        this.RecoveredTransactions = new ReadOnlyCollection<TransactionResult>(recovered);
    }
}
