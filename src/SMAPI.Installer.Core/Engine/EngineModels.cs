using System.Collections.ObjectModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Engine;

/// <summary>Stable reasons why a plan can't cross the execution-preparation trust boundary.</summary>
public enum ExecutionCompilationError
{
    NonExecutablePlan,
    PlanDoesNotMatchRequest,
    StalePlan,
    StaleManifest,
    StaleInstalledReceipt,
    StaleRollbackSnapshot,
    InvalidOperationMapping,
    DuplicateDestination,
    UnsafeDestination
}

/// <summary>A rejected plan-to-execution compilation.</summary>
public sealed class ExecutionCompilationException : Exception
{
    public ExecutionCompilationError Error { get; }

    public ExecutionCompilationException(ExecutionCompilationError error, string message)
        : base(message)
    {
        this.Error = error;
    }
}

/// <summary>An immutable plan identity captured together with every state object which influenced it.</summary>
public sealed class BoundInstallationPlan
{
    public InstallationAction Action { get; }
    public GameRootIdentity GameRoot { get; }
    public ulong OperationGeneration { get; }
    public Sha256Digest PlanSha256 { get; }
    public Sha256Digest? ManifestSha256 { get; }
    public Sha256Digest? InstalledReceiptSha256 { get; }
    public Sha256Digest? InstalledManifestSha256 { get; }
    public Sha256Digest? RollbackSnapshotSha256 { get; }
    public Sha256Digest? RecoveryObservationsSha256 { get; }
    public Guid? RecoveryGenerationId { get; }
    public Sha256Digest? CurrentRecoveryPointerSha256 { get; }
    internal IVerifiedPackageContentAuthority? TargetPackageContent { get; }
    internal ICommittedRecoveryContentAuthority? RollbackContent { get; }

    internal BoundInstallationPlan(
        InstallationAction action,
        GameRootIdentity gameRoot,
        ulong operationGeneration,
        Sha256Digest planSha256,
        Sha256Digest? manifestSha256,
        Sha256Digest? installedReceiptSha256,
        Sha256Digest? installedManifestSha256,
        Sha256Digest? rollbackSnapshotSha256,
        Sha256Digest? recoveryObservationsSha256,
        Guid? recoveryGenerationId,
        Sha256Digest? currentRecoveryPointerSha256,
        IVerifiedPackageContentAuthority? targetPackageContent,
        ICommittedRecoveryContentAuthority? rollbackContent
    )
    {
        ArgumentNullException.ThrowIfNull(gameRoot);
        this.Action = action;
        this.GameRoot = gameRoot;
        this.OperationGeneration = operationGeneration;
        this.PlanSha256 = planSha256;
        this.ManifestSha256 = manifestSha256;
        this.InstalledReceiptSha256 = installedReceiptSha256;
        this.InstalledManifestSha256 = installedManifestSha256;
        this.RollbackSnapshotSha256 = rollbackSnapshotSha256;
        this.RecoveryObservationsSha256 = recoveryObservationsSha256;
        this.RecoveryGenerationId = recoveryGenerationId;
        this.CurrentRecoveryPointerSha256 = currentRecoveryPointerSha256;
        this.TargetPackageContent = targetPackageContent;
        this.RollbackContent = rollbackContent;
    }

    /// <summary>Serialize every execution-relevant plan and observed-state identity deterministically.</summary>
    public string ToCanonicalJson()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(
            stream,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false, SkipValidation = false }
        ))
        {
            writer.WriteStartObject();
            writer.WriteString("action", this.Action.ToString().ToLowerInvariant());
            writer.WriteStartObject("game_root");
            writer.WriteString("canonical_path", this.GameRoot.CanonicalPath);
            writer.WriteNumber("device_major", this.GameRoot.DeviceMajor);
            writer.WriteNumber("device_minor", this.GameRoot.DeviceMinor);
            writer.WriteNumber("inode", this.GameRoot.Inode);
            writer.WriteNumber("operation_generation", this.OperationGeneration);
            writer.WriteEndObject();
            writer.WriteString("plan_sha256", this.PlanSha256.Value);
            WriteNullableDigest(writer, "manifest_sha256", this.ManifestSha256);
            WriteNullableDigest(writer, "installed_receipt_sha256", this.InstalledReceiptSha256);
            WriteNullableDigest(writer, "installed_manifest_sha256", this.InstalledManifestSha256);
            WriteNullableDigest(writer, "rollback_snapshot_sha256", this.RollbackSnapshotSha256);
            WriteNullableDigest(writer, "recovery_observations_sha256", this.RecoveryObservationsSha256);
            if (this.RecoveryGenerationId is null)
                writer.WriteNull("recovery_generation_id");
            else
                writer.WriteString("recovery_generation_id", this.RecoveryGenerationId.Value.ToString("N"));
            WriteNullableDigest(writer, "current_recovery_pointer_sha256", this.CurrentRecoveryPointerSha256);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Get the confirmation digest for the canonical plan plus all state which influenced it.</summary>
    public Sha256Digest GetCanonicalDigest()
    {
        return Sha256Digest.Hash(Encoding.UTF8.GetBytes(this.ToCanonicalJson()));
    }

    private static void WriteNullableDigest(Utf8JsonWriter writer, string name, Sha256Digest? digest)
    {
        if (digest is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, digest.Value);
    }
}

internal interface ICommittedRecoveryContentAuthority
{
    Guid GenerationId { get; }
    InstallationAction OriginAction { get; }
    GameRootIdentity GameRoot { get; }
    RollbackSnapshot Snapshot { get; }
    Sha256Digest SnapshotSha256 { get; }
    Sha256Digest? PreviousManifestSha256 { get; }
    Sha256Digest? PreviousReceiptSha256 { get; }
    Sha256Digest AuthorizedHeadPointerSha256 { get; }
    LinuxAnchoredFile OpenGameFile(NormalizedRelativePath path, RecoveryFileIdentity expectedIdentity);
    LinuxAnchoredFile OpenPreviousReceipt(Sha256Digest expectedSha256);
    LinuxAnchoredFile OpenPreviousManifest(Sha256Digest expectedSha256);
    void AssertUsable();
}

/// <summary>The later preparation step to perform for one losslessly retained planner operation.</summary>
public enum PreparationInstructionKind
{
    WriteTransactionDestination,
    RemoveTransactionDestination,
    CaptureRecoveryFile,
    VerifyUnchanged
}

/// <summary>A closed, core-owned source description. Frontends can inspect these but can't construct them.</summary>
public abstract class PreparationSource
{
    internal PreparationSource() { }
}

/// <summary>An exact file in the already verified release package.</summary>
public sealed class VerifiedPackageFileSource : PreparationSource
{
    public NormalizedRelativePath PackagePath { get; }
    public Sha256Digest Sha256 { get; }
    public long SizeBytes { get; }
    public int UnixMode { get; }
    internal IVerifiedPackageContentAuthority Authority { get; }

    internal VerifiedPackageFileSource(PackageManifestEntry entry, IVerifiedPackageContentAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!authority.Manifest.Entries.Contains(entry))
            throw new ArgumentException("The package source entry isn't owned by its verified content authority.", nameof(entry));
        this.PackagePath = entry.Path;
        this.Sha256 = entry.Sha256;
        this.SizeBytes = entry.SizeBytes;
        this.UnixMode = entry.UnixMode;
        this.Authority = authority;
    }
}

/// <summary>The only two current-game launcher sources installation rules may consume.</summary>
public enum CurrentGameLauncherRole
{
    CurrentLauncher,
    OriginalLauncherBackup
}

/// <summary>A hash-bound current launcher selected by core launcher rules.</summary>
public sealed class CurrentGameLauncherSource : PreparationSource
{
    public CurrentGameLauncherRole Role { get; }
    public NormalizedRelativePath SourcePath { get; }
    public Sha256Digest Sha256 { get; }
    public long SizeBytes { get; }
    public int UnixMode { get; }
    public RecoveryFileType FileType { get; }

    internal CurrentGameLauncherSource(CurrentGameLauncherRole role, NormalizedRelativePath sourcePath, RecoveryFileIdentity identity)
    {
        this.Role = role;
        this.SourcePath = sourcePath;
        this.Sha256 = identity.Sha256;
        this.SizeBytes = identity.SizeBytes;
        this.UnixMode = identity.UnixMode;
        this.FileType = identity.FileType;
    }
}

/// <summary>A hash-bound ordinary game file captured by the explicit user-backup action.</summary>
public sealed class CurrentGameFileSource : PreparationSource
{
    public NormalizedRelativePath SourcePath { get; }
    public Sha256Digest Sha256 { get; }
    public long SizeBytes { get; }
    public int UnixMode { get; }
    public RecoveryFileType FileType { get; }

    internal CurrentGameFileSource(NormalizedRelativePath sourcePath, RecoveryFileIdentity identity)
    {
        this.SourcePath = sourcePath;
        this.Sha256 = identity.Sha256;
        this.SizeBytes = identity.SizeBytes;
        this.UnixMode = identity.UnixMode;
        this.FileType = identity.FileType;
    }
}

/// <summary>The logical object selected from an identity-bound recovery snapshot.</summary>
public enum RecoverySnapshotContent
{
    GameFile,
    InstalledReceipt,
    InstalledManifest
}

/// <summary>A source which may only be resolved from the exact canonical recovery snapshot.</summary>
public sealed class RecoverySnapshotSource : PreparationSource
{
    public Sha256Digest SnapshotSha256 { get; }
    public RecoverySnapshotContent Content { get; }
    public NormalizedRelativePath? EntryPath { get; }
    public Sha256Digest? ExpectedContentSha256 { get; }
    public long? ExpectedSizeBytes { get; }
    public int? ExpectedUnixMode { get; }
    public RecoveryFileType? ExpectedFileType { get; }
    internal ICommittedRecoveryContentAuthority Authority { get; }

    internal RecoverySnapshotSource(
        ICommittedRecoveryContentAuthority authority,
        RecoverySnapshotContent content,
        NormalizedRelativePath? entryPath,
        RecoveryFileIdentity? expectedIdentity,
        Sha256Digest? expectedReceiptSha256 = null
    )
    {
        ArgumentNullException.ThrowIfNull(authority);
        authority.AssertUsable();
        this.Authority = authority;
        this.SnapshotSha256 = authority.SnapshotSha256;
        this.Content = content;
        this.EntryPath = entryPath;
        this.ExpectedContentSha256 = expectedIdentity?.Sha256 ?? expectedReceiptSha256;
        this.ExpectedSizeBytes = expectedIdentity?.SizeBytes;
        this.ExpectedUnixMode = expectedIdentity?.UnixMode;
        this.ExpectedFileType = expectedIdentity?.FileType;
    }
}

/// <summary>A new canonical receipt generated exclusively from the selected manifest and transaction identity.</summary>
public sealed class GeneratedCanonicalReceiptSource : PreparationSource
{
    public InstallationReceipt Receipt { get; }
    public Sha256Digest Sha256 { get; }
    private readonly byte[] Bytes;

    internal GeneratedCanonicalReceiptSource(InstallationReceipt receipt, byte[] bytes)
    {
        this.Receipt = receipt;
        this.Bytes = bytes.ToArray();
        this.Sha256 = receipt.GetCanonicalDigest();
    }

    public byte[] GetCanonicalBytes()
    {
        return this.Bytes.ToArray();
    }
}

/// <summary>The canonical manifest retained by the exact verified target package authority.</summary>
public sealed class VerifiedCanonicalManifestSource : PreparationSource
{
    public PackageManifest Manifest { get; }
    public Sha256Digest Sha256 { get; }
    private readonly byte[] Bytes;
    internal IVerifiedPackageContentAuthority Authority { get; }

    internal VerifiedCanonicalManifestSource(IVerifiedPackageContentAuthority authority, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(authority);
        authority.AssertUsable();
        this.Authority = authority;
        this.Manifest = authority.Manifest;
        this.Bytes = bytes.ToArray();
        this.Sha256 = Sha256Digest.Hash(this.Bytes);
        if (this.Sha256 != authority.ManifestSha256)
            throw new ArgumentException("The canonical manifest bytes don't match their verified package authority.", nameof(bytes));
    }

    public byte[] GetCanonicalBytes() => this.Bytes.ToArray();
}

/// <summary>One typed preparation instruction corresponding one-to-one with a planner operation.</summary>
public sealed class FilePreparationInstruction
{
    public PlanOperationKind PlanKind { get; }
    public PreparationInstructionKind Kind { get; }
    public NormalizedRelativePath Path { get; }
    public Sha256Digest? ExpectedCurrentSha256 { get; }
    public RecoveryFileIdentity? ExpectedCurrentIdentity { get; }
    public Sha256Digest? ExpectedResultSha256 { get; }
    public int? ResultUnixMode { get; }
    public long? ResultSizeBytes { get; }
    public RecoveryFileType? ResultFileType { get; }
    public PreparationSource? Source { get; }

    /// <summary>Whether this instruction becomes the sole transaction mutation for its path.</summary>
    public bool IsTransactionDestination => this.Kind is PreparationInstructionKind.WriteTransactionDestination
        or PreparationInstructionKind.RemoveTransactionDestination;

    internal FilePreparationInstruction(
        PlannedOperation operation,
        PreparationInstructionKind kind,
        PreparationSource? source,
        int? resultUnixMode,
        long? resultSizeBytes = null,
        RecoveryFileType? resultFileType = null,
        RecoveryFileIdentity? expectedCurrentIdentity = null
    )
    {
        this.PlanKind = operation.Kind;
        this.Kind = kind;
        this.Path = operation.Path;
        this.ExpectedCurrentSha256 = operation.ExpectedCurrentSha256;
        if (expectedCurrentIdentity?.Sha256 != operation.ExpectedCurrentSha256)
            throw new ArgumentException("The complete current identity doesn't match the planned current digest.", nameof(expectedCurrentIdentity));
        this.ExpectedCurrentIdentity = expectedCurrentIdentity;
        this.ExpectedResultSha256 = operation.ResultSha256;
        this.Source = source;
        this.ResultUnixMode = resultUnixMode;
        this.ResultSizeBytes = resultSizeBytes;
        this.ResultFileType = resultFileType;
    }
}

/// <summary>The atomic installed-receipt state change which accompanies the game-file transaction.</summary>
public enum ReceiptPreparationKind
{
    None,
    WriteAtomically,
    RemoveAtomically
}

/// <summary>The installed-manifest state change committed with the transaction.</summary>
public sealed class ManifestPreparationInstruction
{
    public ReceiptPreparationKind Kind { get; }
    public Sha256Digest? ExpectedExistingManifestSha256 { get; }
    public PreparationSource? Source { get; }

    internal ManifestPreparationInstruction(
        ReceiptPreparationKind kind,
        Sha256Digest? expectedExistingManifestSha256,
        PreparationSource? source
    )
    {
        this.Kind = kind;
        this.ExpectedExistingManifestSha256 = expectedExistingManifestSha256;
        this.Source = source;
    }
}

/// <summary>A core-owned receipt commit instruction. Receipt state is never represented as an arbitrary game destination.</summary>
public sealed class ReceiptPreparationInstruction
{
    public ReceiptPreparationKind Kind { get; }
    public Sha256Digest? ExpectedExistingReceiptSha256 { get; }
    public PreparationSource? Source { get; }

    internal ReceiptPreparationInstruction(
        ReceiptPreparationKind kind,
        Sha256Digest? expectedExistingReceiptSha256,
        PreparationSource? source
    )
    {
        this.Kind = kind;
        this.ExpectedExistingReceiptSha256 = expectedExistingReceiptSha256;
        this.Source = source;
    }
}

/// <summary>One exact pre-execution path state durably bound into recovery preparation.</summary>
public sealed class RecoveryPathBinding
{
    public NormalizedRelativePath Path { get; }
    public OwnedEntryKind OwnedKind { get; }
    public RecoveryFileIdentity? PriorIdentity { get; }
    public bool RequiresContentCapture { get; }

    internal RecoveryPathBinding(
        NormalizedRelativePath path,
        OwnedEntryKind ownedKind,
        RecoveryFileIdentity? priorIdentity,
        bool requiresContentCapture
    )
    {
        OwnedNamespacePolicy.AssertRecoveryAllowed(path, ownedKind);
        this.Path = path;
        this.OwnedKind = ownedKind;
        this.PriorIdentity = priorIdentity;
        this.RequiresContentCapture = requiresContentCapture;
    }
}

/// <summary>
/// Canonical recovery state which must be persisted and fsynced before the associated game-file transaction begins.
/// </summary>
public sealed class RecoverySnapshotPreparation
{
    private readonly byte[] SnapshotBytes;
    private readonly byte[]? PreviousReceiptBytes;

    public RollbackSnapshot Snapshot { get; }
    public Sha256Digest SnapshotSha256 { get; }
    public IReadOnlyList<RecoveryPathBinding> PathBindings { get; }
    public Sha256Digest? PreviousReceiptSha256 { get; }

    internal RecoverySnapshotPreparation(
        RollbackSnapshot snapshot,
        byte[] snapshotBytes,
        IEnumerable<RecoveryPathBinding> pathBindings,
        byte[]? previousReceiptBytes
    )
    {
        RecoveryPathBinding[] bindings = pathBindings.OrderBy(binding => binding.Path.Value, StringComparer.Ordinal).ToArray();
        OwnershipCollectionValidation.AssertDistinctFilePaths(bindings.Select(binding => binding.Path), nameof(pathBindings));
        this.Snapshot = snapshot;
        this.SnapshotBytes = snapshotBytes.ToArray();
        this.SnapshotSha256 = Sha256Digest.Hash(this.SnapshotBytes);
        this.PathBindings = new ReadOnlyCollection<RecoveryPathBinding>(bindings);
        this.PreviousReceiptBytes = previousReceiptBytes?.ToArray();
        this.PreviousReceiptSha256 = this.PreviousReceiptBytes is null ? null : Sha256Digest.Hash(this.PreviousReceiptBytes);
        if (this.PreviousReceiptSha256 != snapshot.PreviousReceiptSha256)
            throw new ArgumentException("The captured previous receipt doesn't match the rollback transition.", nameof(previousReceiptBytes));
    }

    public byte[] GetCanonicalSnapshotBytes() => this.SnapshotBytes.ToArray();
    public byte[]? GetPreviousReceiptBytes() => this.PreviousReceiptBytes?.ToArray();
}

/// <summary>A complete side-effect-free preparation recipe for one exact plan and state identity.</summary>
public sealed class InstallationExecutionPreparation
{
    public Guid TransactionId { get; }
    public InstallationAction Action { get; }
    public BoundInstallationPlan Binding { get; }
    public IReadOnlyList<FilePreparationInstruction> Instructions { get; }
    public IReadOnlyList<FilePreparationInstruction> TransactionDestinations { get; }
    public ReceiptPreparationInstruction Receipt { get; }
    public ManifestPreparationInstruction Manifest { get; }
    public RecoverySnapshotPreparation? RecoverySnapshot { get; }

    internal InstallationExecutionPreparation(
        Guid transactionId,
        BoundInstallationPlan binding,
        IEnumerable<FilePreparationInstruction> instructions,
        ManifestPreparationInstruction manifest,
        ReceiptPreparationInstruction receipt,
        RecoverySnapshotPreparation? recoverySnapshot
    )
    {
        FilePreparationInstruction[] all = instructions.ToArray();
        this.TransactionId = transactionId;
        this.Action = binding.Action;
        this.Binding = binding;
        this.Instructions = new ReadOnlyCollection<FilePreparationInstruction>(all);
        this.TransactionDestinations = new ReadOnlyCollection<FilePreparationInstruction>(
            all.Where(instruction => instruction.IsTransactionDestination).ToArray()
        );
        this.Receipt = receipt;
        this.Manifest = manifest;
        this.RecoverySnapshot = recoverySnapshot;
    }
}
