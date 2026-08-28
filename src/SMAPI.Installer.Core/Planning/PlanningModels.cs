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
    RollbackDrift
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

    internal InstallationPlan(
        InstallationAction action,
        IEnumerable<PlannedOperation> operations,
        IEnumerable<PlanConflict> conflicts
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
    public LauncherReceipt? Receipt { get; }
    public LauncherClassification Classification { get; }

    private LauncherState(Sha256Digest? current, Sha256Digest? backup, LauncherReceipt? receipt, LauncherClassification classification)
    {
        this.CurrentLauncherSha256 = current;
        this.BackupLauncherSha256 = backup;
        this.Receipt = receipt;
        this.Classification = classification;
    }

    /// <summary>Classify launcher observations without making ownership assumptions.</summary>
    public static LauncherState Assess(Sha256Digest? current, Sha256Digest? backup, LauncherReceipt? receipt)
    {
        if (receipt == null)
        {
            if (backup is not null)
                return new LauncherState(current, backup, null, LauncherClassification.AmbiguousBackup);
            return new LauncherState(
                current,
                null,
                null,
                current is not null ? LauncherClassification.FreshVanilla : LauncherClassification.MissingGameLauncher
            );
        }

        if (backup is null)
            return new LauncherState(current, null, receipt, LauncherClassification.MissingOriginalBackup);
        if (backup != receipt.OriginalLauncherSha256)
            return new LauncherState(current, backup, receipt, LauncherClassification.AmbiguousBackup);
        if (current is null)
            return new LauncherState(null, backup, receipt, LauncherClassification.InstalledLauncherMissing);
        if (current == receipt.InstalledLauncherSha256)
            return new LauncherState(current, backup, receipt, LauncherClassification.InstalledUnchanged);
        return new LauncherState(current, backup, receipt, LauncherClassification.InstalledModified);
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

    /// <summary>Whether this is a user checkpoint whose safe current-state preconditions are rebound at restore time.</summary>
    public bool IsUserBackup => this.ExpectedCurrentReceiptSha256 == this.PreviousReceiptSha256
        && this.ExpectedCurrentReceiptSha256 is not null
        && this.Entries.All(entry =>
            entry.Kind == RollbackEntryKind.Restore
            && entry.ExpectedCurrent is not null
            && entry.ExpectedCurrent == entry.Backup
        );

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
    public InstallationReceipt? InstalledReceipt { get; }
    public LauncherState Launcher { get; }
    public RollbackSnapshot? RollbackSnapshot { get; }
    public IReadOnlyList<RecoveryFileObservation> RecoveryObservations { get; }

    internal InstallationPlanningRequest(
        InstallationAction action,
        InstallationInventory inventory,
        LauncherState launcher,
        PackageManifest? targetManifest = null,
        InstallationReceipt? installedReceipt = null,
        RollbackSnapshot? rollbackSnapshot = null,
        IEnumerable<RecoveryFileObservation>? recoveryObservations = null
    )
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(launcher);
        this.Action = action;
        this.Inventory = inventory;
        this.TargetManifest = targetManifest;
        this.InstalledReceipt = installedReceipt;
        this.Launcher = launcher;
        this.RollbackSnapshot = rollbackSnapshot;
        RecoveryFileObservation[] observations = (recoveryObservations ?? Array.Empty<RecoveryFileObservation>())
            .OrderBy(observation => observation.Path.Value, StringComparer.Ordinal)
            .ToArray();
        OwnershipCollectionValidation.AssertDistinctFilePaths(observations.Select(observation => observation.Path), nameof(recoveryObservations));
        this.RecoveryObservations = new ReadOnlyCollection<RecoveryFileObservation>(observations);
    }
}
