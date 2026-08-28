using System.Collections.ObjectModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StardewModdingAPI.Installer.Core.Ownership;

namespace StardewModdingAPI.Installer.Core.Planning;

/// <summary>A user-visible installer action.</summary>
public enum InstallationAction
{
    Install,
    Update,
    Repair,
    Uninstall,
    Backup,
    Rollback
}

/// <summary>A deterministic filesystem intent. Execution and journaling are separate concerns.</summary>
public enum PlanOperationKind
{
    Backup,
    Remove,
    Restore,
    Create,
    Replace,
    Retain,
    Preserve
}

/// <summary>A stable reason for a non-executable plan.</summary>
public enum PlanConflictCode
{
    TargetManifestRequired,
    ExistingInstallationRequiresUpdate,
    InstalledReceiptRequired,
    ReleaseDoesNotMatchReceipt,
    ReceiptDoesNotMatchManifest,
    ModifiedOwnedFile,
    UnknownCollision,
    LegacyOwnershipUnconfirmed,
    PreservedTargetCollision,
    MissingGameLauncher,
    ModifiedInstalledLauncher,
    AmbiguousLauncherBackup,
    MissingOriginalLauncherBackup,
    RollbackSnapshotRequired,
    RollbackReceiptMismatch,
    RollbackDrift,
    RecoveryCapacityReached
}

/// <summary>A bounded, core-derived summary of the selected game's installed state.</summary>
public enum ObservedInstallationState
{
    NotInstalled,
    KnownUnmodified,
    KnownModified,
    LegacyOrOfficial,
    Unknown
}

/// <summary>The exact physical recovery-generation capacity observed while an installation plan was created.</summary>
public sealed record RecoveryCapacityState
{
    /// <summary>The physical generation directories currently consuming bounded capacity, including pending authenticated cleanup.</summary>
    public int UsedGenerationCount { get; }
    /// <summary>The maximum physical generation count accepted by the core.</summary>
    public int MaximumGenerationCount { get; }
    /// <summary>The remaining physical generation slots.</summary>
    public int RemainingGenerationCount => this.MaximumGenerationCount - this.UsedGenerationCount;
    /// <summary>Whether one new recovery generation can be committed without pruning.</summary>
    public bool CanCreateGeneration => this.UsedGenerationCount < this.MaximumGenerationCount;

    internal RecoveryCapacityState(int usedGenerationCount, int maximumGenerationCount)
    {
        if (usedGenerationCount < 0 || maximumGenerationCount <= 0 || usedGenerationCount > maximumGenerationCount)
            throw new ArgumentOutOfRangeException(nameof(usedGenerationCount));
        this.UsedGenerationCount = usedGenerationCount;
        this.MaximumGenerationCount = maximumGenerationCount;
    }
}

/// <summary>One deterministically ordered planned operation.</summary>
public sealed record PlannedOperation
{
    public PlanOperationKind Kind { get; }
    public NormalizedRelativePath Path { get; }
    public Sha256Digest? ExpectedCurrentSha256 { get; }
    public Sha256Digest? ResultSha256 { get; }

    public PlannedOperation(
        PlanOperationKind kind,
        NormalizedRelativePath path,
        Sha256Digest? expectedCurrentSha256,
        Sha256Digest? resultSha256
    )
    {
        ArgumentNullException.ThrowIfNull(path);
        this.Kind = kind;
        this.Path = path;
        this.ExpectedCurrentSha256 = expectedCurrentSha256;
        this.ResultSha256 = resultSha256;
    }
}

/// <summary>The stable core-derived reason an exact file requires explicit approval.</summary>
public enum FileReplacementCandidateReason
{
    /// <summary>A receipt-owned non-launcher file differs from the exact installed identity.</summary>
    ModifiedReceiptOwned,
    /// <summary>The receipt-owned installed launcher differs from the exact installed identity.</summary>
    ModifiedInstalledLauncher,
    /// <summary>A compiled, recognized legacy SMAPI destination exists without a receipt.</summary>
    LegacyInstaller,
    /// <summary>An intended package destination contains an unowned file of unknown origin.</summary>
    UnknownCollision,
    /// <summary>The current launcher is part of a recognized official or legacy launcher/backup pair.</summary>
    OfficialOrLegacyLauncher,
    /// <summary>The existing original-launcher backup will be trusted and retained during adoption.</summary>
    OfficialLauncherBackup
}

/// <summary>The exact operation proposed for a core-minted file candidate.</summary>
public enum FileReplacementCandidateDisposition
{
    /// <summary>Replace the observed file with the selected package result.</summary>
    Replace,
    /// <summary>Remove the observed receipt-owned file.</summary>
    Remove,
    /// <summary>Restore the observed installed launcher from its receipt-authenticated backup.</summary>
    Restore,
    /// <summary>Trust and retain the exact observed official-launcher backup.</summary>
    TrustRetained
}

/// <summary>
/// A core-minted, reviewable candidate for replacing, removing, restoring, or retaining one exact installer-relevant file.
/// This candidate can only be selected through the still-usable inspection which issued it.
/// </summary>
public sealed class ModifiedFileReplacementCandidate
{
    /// <summary>The canonical installer-relevant path observed by the core.</summary>
    public NormalizedRelativePath Path { get; }
    /// <summary>The observed content digest.</summary>
    public Sha256Digest ObservedSha256 { get; }
    /// <summary>The observed byte length.</summary>
    public long ObservedSizeBytes { get; }
    /// <summary>The observed Unix permission bits.</summary>
    public int ObservedUnixMode { get; }
    /// <summary>The observed bounded file type.</summary>
    public RecoveryFileType ObservedFileType { get; }
    /// <summary>The core-derived reason this exact path requires explicit approval.</summary>
    public FileReplacementCandidateReason Reason { get; }
    /// <summary>The core-derived operation which approval permits for this exact path.</summary>
    public FileReplacementCandidateDisposition Disposition { get; }
    /// <summary>The exact proposed result digest for replacement, restoration, or retained content; null for removal.</summary>
    public Sha256Digest? ProposedResultSha256 { get; }
    internal object SourceAuthority { get; }
    internal RecoveryFileIdentity ObservedIdentity { get; }

    internal ModifiedFileReplacementCandidate(
        object sourceAuthority,
        NormalizedRelativePath path,
        RecoveryFileIdentity observedIdentity,
        FileReplacementCandidateReason reason,
        FileReplacementCandidateDisposition disposition,
        Sha256Digest? proposedResultSha256
    )
    {
        ArgumentNullException.ThrowIfNull(sourceAuthority);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(observedIdentity);
        this.SourceAuthority = sourceAuthority;
        this.Path = path;
        this.ObservedIdentity = observedIdentity;
        this.ObservedSha256 = observedIdentity.Sha256;
        this.ObservedSizeBytes = observedIdentity.SizeBytes;
        this.ObservedUnixMode = observedIdentity.UnixMode;
        this.ObservedFileType = observedIdentity.FileType;
        this.Reason = reason;
        this.Disposition = disposition;
        this.ProposedResultSha256 = proposedResultSha256;
    }

    internal ModifiedFileReplacementCandidate(
        object sourceAuthority,
        NormalizedRelativePath path,
        RecoveryFileIdentity observedIdentity
    )
        : this(
            sourceAuthority,
            path,
            observedIdentity,
            FileReplacementCandidateReason.ModifiedReceiptOwned,
            FileReplacementCandidateDisposition.Replace,
            proposedResultSha256: null
        )
    {
    }
}

/// <summary>An internal full-identity replacement or removal authorization derived only from a core-minted candidate.</summary>
internal sealed record ModifiedFileReplacementApproval
{
    public NormalizedRelativePath Path { get; }
    public RecoveryFileIdentity ObservedIdentity { get; }
    public Sha256Digest ObservedSha256 => this.ObservedIdentity.Sha256;
    public int ObservedUnixMode => this.ObservedIdentity.UnixMode;

    internal ModifiedFileReplacementApproval(
        NormalizedRelativePath path,
        RecoveryFileIdentity observedIdentity
    )
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(observedIdentity);
        this.Path = path;
        this.ObservedIdentity = observedIdentity;
    }
}

/// <summary>One stable conflict which must be resolved before execution.</summary>
public sealed record PlanConflict
{
    public PlanConflictCode Code { get; }
    public NormalizedRelativePath? Path { get; }

    public PlanConflict(PlanConflictCode code, NormalizedRelativePath? path = null)
    {
        this.Code = code;
        this.Path = path;
    }
}

/// <summary>A complete immutable plan. Any conflict makes it non-executable.</summary>
public sealed class InstallationPlan
{
    public InstallationAction Action { get; }
    public IReadOnlyList<PlannedOperation> Operations { get; }
    public IReadOnlyList<PlanConflict> Conflicts { get; }
    public bool CanExecute => this.Conflicts.Count == 0;
    public ObservedInstallationState ObservedState { get; }
    public RecoveryCapacityState RecoveryCapacity { get; }

    internal InstallationPlan(
        InstallationAction action,
        IEnumerable<PlannedOperation> operations,
        IEnumerable<PlanConflict> conflicts,
        ObservedInstallationState observedState = ObservedInstallationState.Unknown,
        RecoveryCapacityState? recoveryCapacity = null
    )
    {
        PlannedOperation[] orderedOperations = operations
            .OrderBy(operation => operation.Path.Value, StringComparer.Ordinal)
            .ThenBy(operation => GetOperationOrder(operation.Kind))
            .ThenBy(operation => operation.ResultSha256?.Value, StringComparer.Ordinal)
            .ToArray();
        PlanConflict[] orderedConflicts = conflicts
            .DistinctBy(conflict => (conflict.Code, conflict.Path?.Value))
            .OrderBy(conflict => conflict.Path?.Value ?? "", StringComparer.Ordinal)
            .ThenBy(conflict => conflict.Code)
            .ToArray();

        this.Action = action;
        this.Operations = new ReadOnlyCollection<PlannedOperation>(orderedOperations);
        this.Conflicts = new ReadOnlyCollection<PlanConflict>(orderedConflicts);
        this.ObservedState = observedState;
        this.RecoveryCapacity = recoveryCapacity ?? new RecoveryCapacityState(0, int.MaxValue);
    }

    /// <summary>Serialize the plan with canonical property and item ordering.</summary>
    public string ToCanonicalJson()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(
            stream,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false, SkipValidation = false }
        ))
        {
            writer.WriteStartObject();
            writer.WriteString("action", GetActionName(this.Action));
            writer.WriteBoolean("can_execute", this.CanExecute);
            writer.WriteString("observed_state", this.ObservedState.ToString().ToLowerInvariant());
            writer.WriteStartObject("recovery_capacity");
            writer.WriteNumber("used_generation_count", this.RecoveryCapacity.UsedGenerationCount);
            writer.WriteNumber("maximum_generation_count", this.RecoveryCapacity.MaximumGenerationCount);
            writer.WriteEndObject();
            writer.WriteStartArray("operations");
            foreach (PlannedOperation operation in this.Operations)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", GetOperationName(operation.Kind));
                writer.WriteString("path", operation.Path.Value);
                if (operation.ExpectedCurrentSha256 is not null)
                    writer.WriteString("expected_current_sha256", operation.ExpectedCurrentSha256.Value);
                else
                    writer.WriteNull("expected_current_sha256");
                if (operation.ResultSha256 is not null)
                    writer.WriteString("result_sha256", operation.ResultSha256.Value);
                else
                    writer.WriteNull("result_sha256");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("conflicts");
            foreach (PlanConflict conflict in this.Conflicts)
            {
                writer.WriteStartObject();
                writer.WriteString("code", GetConflictName(conflict.Code));
                if (conflict.Path != null)
                    writer.WriteString("path", conflict.Path.Value);
                else
                    writer.WriteNull("path");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Get the digest of the canonical plan bytes.</summary>
    public Sha256Digest GetCanonicalDigest()
    {
        return Sha256Digest.Hash(Encoding.UTF8.GetBytes(this.ToCanonicalJson()));
    }

    private static int GetOperationOrder(PlanOperationKind kind)
    {
        return kind switch
        {
            PlanOperationKind.Backup => 0,
            PlanOperationKind.Remove => 1,
            PlanOperationKind.Restore => 2,
            PlanOperationKind.Create => 3,
            PlanOperationKind.Replace => 4,
            PlanOperationKind.Retain => 5,
            PlanOperationKind.Preserve => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static string GetActionName(InstallationAction action) => action.ToString().ToLowerInvariant();
    private static string GetOperationName(PlanOperationKind operation) => operation.ToString().ToLowerInvariant();

    private static string GetConflictName(PlanConflictCode code)
    {
        StringBuilder result = new();
        string value = code.ToString();
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (index > 0 && char.IsUpper(character))
                result.Append('_');
            result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }
}

/// <summary>The safe classification of the installed and backed-up Linux launchers.</summary>
public enum LauncherClassification
{
    FreshVanilla,
    MissingGameLauncher,
    InstalledUnchanged,
    InstalledLauncherMissing,
    InstalledModified,
    AmbiguousBackup,
    MissingOriginalBackup
}

/// <summary>Immutable launcher observations used to prevent ambiguous replacement or restoration.</summary>
public sealed class LauncherState
{
    public Sha256Digest? CurrentLauncherSha256 { get; }
    public Sha256Digest? BackupLauncherSha256 { get; }
    public int? CurrentLauncherUnixMode { get; }
    public int? BackupLauncherUnixMode { get; }
    public LauncherReceipt? Receipt { get; }
    public LauncherClassification Classification { get; }

    private LauncherState(
        Sha256Digest? current,
        Sha256Digest? backup,
        int? currentUnixMode,
        int? backupUnixMode,
        LauncherReceipt? receipt,
        LauncherClassification classification
    )
    {
        this.CurrentLauncherSha256 = current;
        this.BackupLauncherSha256 = backup;
        this.CurrentLauncherUnixMode = currentUnixMode;
        this.BackupLauncherUnixMode = backupUnixMode;
        this.Receipt = receipt;
        this.Classification = classification;
    }

    /// <summary>Classify launcher observations without making ownership assumptions.</summary>
    public static LauncherState Assess(
        Sha256Digest? current,
        Sha256Digest? backup,
        LauncherReceipt? receipt,
        int? currentUnixMode = null,
        int? backupUnixMode = null
    )
    {
        if (receipt == null)
        {
            if (backup is not null)
                return new LauncherState(current, backup, currentUnixMode, backupUnixMode, null, LauncherClassification.AmbiguousBackup);
            return new LauncherState(
                current,
                null,
                currentUnixMode,
                null,
                null,
                current is not null ? LauncherClassification.FreshVanilla : LauncherClassification.MissingGameLauncher
            );
        }

        if (backup is null)
            return new LauncherState(current, null, currentUnixMode, null, receipt, LauncherClassification.MissingOriginalBackup);
        if (
            backup != receipt.OriginalLauncherSha256
            || (backupUnixMode is not null && backupUnixMode != receipt.OriginalLauncherUnixMode)
        )
            return new LauncherState(current, backup, currentUnixMode, backupUnixMode, receipt, LauncherClassification.AmbiguousBackup);
        if (current is null)
            return new LauncherState(null, backup, null, backupUnixMode, receipt, LauncherClassification.InstalledLauncherMissing);
        if (
            current == receipt.InstalledLauncherSha256
            && (currentUnixMode is null || currentUnixMode == receipt.InstalledLauncherUnixMode)
        )
            return new LauncherState(current, backup, currentUnixMode, backupUnixMode, receipt, LauncherClassification.InstalledUnchanged);
        return new LauncherState(current, backup, currentUnixMode, backupUnixMode, receipt, LauncherClassification.InstalledModified);
    }
}

/// <summary>The rollback operation captured by a committed transaction snapshot.</summary>
public enum RollbackEntryKind
{
    Restore,
    Remove
}

/// <summary>The bounded filesystem object types a recovery snapshot can authenticate.</summary>
public enum RecoveryFileType
{
    /// <summary>An ordinary non-linked file.</summary>
    RegularFile
}

/// <summary>The complete identity of one ordinary file at a recovery boundary.</summary>
public sealed record RecoveryFileIdentity
{
    public Sha256Digest Sha256 { get; }
    public long SizeBytes { get; }
    public int UnixMode { get; }
    public RecoveryFileType FileType { get; }

    internal RecoveryFileIdentity(Sha256Digest sha256, long sizeBytes, int unixMode, RecoveryFileType fileType = RecoveryFileType.RegularFile)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        if (sizeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        if (unixMode is < 0 or > 511)
            throw new ArgumentOutOfRangeException(nameof(unixMode), "Only ordinary 0000-0777 Unix permission bits are allowed.");
        if (fileType != RecoveryFileType.RegularFile)
            throw new ArgumentOutOfRangeException(nameof(fileType));

        this.Sha256 = sha256;
        this.SizeBytes = sizeBytes;
        this.UnixMode = unixMode;
        this.FileType = fileType;
    }
}

/// <summary>An explicit present-or-absent observation used to build or authenticate durable recovery state.</summary>
public sealed record RecoveryFileObservation
{
    public NormalizedRelativePath Path { get; }
    public RecoveryFileIdentity? Identity { get; }

    internal RecoveryFileObservation(NormalizedRelativePath path, RecoveryFileIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(path);
        this.Path = path;
        this.Identity = identity;
    }
}

/// <summary>One ownership-constrained rollback intent.</summary>
public sealed class RollbackSnapshotEntry
{
    public NormalizedRelativePath Path { get; }
    public OwnedEntryKind OwnedKind { get; }
    public RollbackEntryKind Kind { get; }
    public RecoveryFileIdentity? ExpectedCurrent { get; }
    public RecoveryFileIdentity? Backup { get; }

    public Sha256Digest? ExpectedCurrentSha256 => this.ExpectedCurrent?.Sha256;
    public Sha256Digest? BackupSha256 => this.Backup?.Sha256;

    internal RollbackSnapshotEntry(
        NormalizedRelativePath path,
        OwnedEntryKind ownedKind,
        RollbackEntryKind kind,
        RecoveryFileIdentity? expectedCurrent,
        RecoveryFileIdentity? backup
    )
    {
        ArgumentNullException.ThrowIfNull(path);
        OwnedNamespacePolicy.AssertRecoveryAllowed(path, ownedKind);
        if (kind == RollbackEntryKind.Restore && backup is null)
            throw new ArgumentException("A restore rollback entry requires a complete backup identity.", nameof(backup));
        if (kind == RollbackEntryKind.Remove && (expectedCurrent is null || backup is not null))
            throw new ArgumentException("A remove rollback entry requires only a complete expected-current identity.");

        this.Path = path;
        this.OwnedKind = ownedKind;
        this.Kind = kind;
        this.ExpectedCurrent = expectedCurrent;
        this.Backup = backup;
    }
}

/// <summary>A bounded immutable rollback snapshot tied to the receipt transition it reverses.</summary>
public sealed class RollbackSnapshot
{
    /// <summary>The receipt expected after the completed operation, or <c>null</c> when that operation removed it.</summary>
    public Sha256Digest? ExpectedCurrentReceiptSha256 { get; }

    /// <summary>The prior receipt to restore, or <c>null</c> when the prior state had no receipt.</summary>
    public Sha256Digest? PreviousReceiptSha256 { get; }

    public IReadOnlyList<RollbackSnapshotEntry> Entries { get; }

    internal RollbackSnapshot(
        Sha256Digest? expectedCurrentReceiptSha256,
        Sha256Digest? previousReceiptSha256,
        IEnumerable<RollbackSnapshotEntry> entries
    )
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (expectedCurrentReceiptSha256 is null && previousReceiptSha256 is null)
            throw new ArgumentException("A rollback snapshot must bind at least one side of its receipt transition.");
        RollbackSnapshotEntry[] ordered = entries.OrderBy(entry => entry.Path.Value, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 && expectedCurrentReceiptSha256 == previousReceiptSha256)
            throw new ArgumentException("A receipt-only rollback snapshot must describe a real receipt transition.", nameof(entries));
        OwnershipCollectionValidation.AssertDistinctFilePaths(ordered.Select(entry => entry.Path), nameof(entries));

        this.ExpectedCurrentReceiptSha256 = expectedCurrentReceiptSha256;
        this.PreviousReceiptSha256 = previousReceiptSha256;
        this.Entries = new ReadOnlyCollection<RollbackSnapshotEntry>(ordered);
    }
}

/// <summary>All immutable inputs needed for one pure planning operation.</summary>
internal sealed class InstallationPlanningRequest
{
    public InstallationAction Action { get; }
    public InstallationInventory Inventory { get; }
    public PackageManifest? TargetManifest { get; }
    public PackageManifest? InstalledManifest { get; }
    public InstallationReceipt? InstalledReceipt { get; }
    public PackageManifest? PersistedInstalledManifest { get; }
    public InstallationReceipt? PersistedInstalledReceipt { get; }
    public Sha256Digest? PersistedManifestSha256 { get; }
    public Sha256Digest? PersistedReceiptSha256 { get; }
    public bool HasGeneratedOwnershipEvolution =>
        this.InstalledManifest is not null
        && this.PersistedManifestSha256 is not null
        && this.InstalledManifest.GetCanonicalDigest() != this.PersistedManifestSha256;
    public LauncherState Launcher { get; }
    public RollbackSnapshot? RollbackSnapshot { get; }
    public InstallationAction? RollbackOriginAction { get; }
    public IReadOnlyList<ModifiedFileReplacementApproval> ModifiedFileReplacementApprovals { get; }
    public IReadOnlyList<RecoveryFileObservation> RecoveryObservations { get; }
    public RecoveryCapacityState RecoveryCapacity { get; }
    public ObservedInstallationState ObservedState { get; }

    internal InstallationPlanningRequest(
        InstallationAction action,
        InstallationInventory inventory,
        LauncherState launcher,
        PackageManifest? targetManifest = null,
        InstallationReceipt? installedReceipt = null,
        RollbackSnapshot? rollbackSnapshot = null,
        IEnumerable<RecoveryFileObservation>? recoveryObservations = null,
        InstallationAction? rollbackOriginAction = null,
        IEnumerable<ModifiedFileReplacementApproval>? modifiedFileReplacementApprovals = null,
        RecoveryCapacityState? recoveryCapacity = null,
        ObservedInstallationState observedState = ObservedInstallationState.Unknown,
        PackageManifest? installedManifest = null,
        Sha256Digest? persistedManifestSha256 = null,
        Sha256Digest? persistedReceiptSha256 = null,
        PackageManifest? persistedInstalledManifest = null,
        InstallationReceipt? persistedInstalledReceipt = null
    )
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(launcher);
        this.Action = action;
        this.Inventory = inventory;
        this.TargetManifest = targetManifest;
        this.InstalledManifest = installedManifest;
        this.InstalledReceipt = installedReceipt;
        this.PersistedInstalledManifest = persistedInstalledManifest ?? installedManifest;
        this.PersistedInstalledReceipt = persistedInstalledReceipt ?? installedReceipt;
        this.PersistedManifestSha256 = persistedManifestSha256 ?? installedReceipt?.ManifestSha256;
        this.PersistedReceiptSha256 = persistedReceiptSha256 ?? installedReceipt?.GetCanonicalDigest();
        this.Launcher = launcher;
        this.RollbackSnapshot = rollbackSnapshot;
        this.RollbackOriginAction = rollbackOriginAction;
        this.RecoveryCapacity = recoveryCapacity ?? new RecoveryCapacityState(0, int.MaxValue);
        this.ObservedState = observedState;
        ModifiedFileReplacementApproval[] approvals = (modifiedFileReplacementApprovals ?? Array.Empty<ModifiedFileReplacementApproval>())
            .OrderBy(approval => approval.Path.Value, StringComparer.Ordinal)
            .ToArray();
        OwnershipCollectionValidation.AssertDistinctFilePaths(approvals.Select(approval => approval.Path), nameof(modifiedFileReplacementApprovals));
        this.ModifiedFileReplacementApprovals = new ReadOnlyCollection<ModifiedFileReplacementApproval>(approvals);
        RecoveryFileObservation[] observations = (recoveryObservations ?? Array.Empty<RecoveryFileObservation>())
            .OrderBy(observation => observation.Path.Value, StringComparer.Ordinal)
            .ToArray();
        OwnershipCollectionValidation.AssertDistinctFilePaths(observations.Select(observation => observation.Path), nameof(recoveryObservations));
        this.RecoveryObservations = new ReadOnlyCollection<RecoveryFileObservation>(observations);
    }
}
