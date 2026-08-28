using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Recovery;

internal sealed record CommittedRecoveryPointer
{
    public const int CurrentSchemaVersion = 1;
    public Guid GenerationId { get; }
    public InstallationAction Action { get; }
    public Sha256Digest SnapshotSha256 { get; }
    public Sha256Digest? ResultManifestSha256 { get; }
    public Sha256Digest? ResultReceiptSha256 { get; }
    public Sha256Digest? PreviousManifestSha256 { get; }
    public Sha256Digest? PreviousReceiptSha256 { get; }
    public Guid? PreviousGenerationId { get; }
    public Sha256Digest? PreviousPointerSha256 { get; }

    public CommittedRecoveryPointer(
        Guid generationId,
        InstallationAction action,
        Sha256Digest snapshotSha256,
        Sha256Digest? resultManifestSha256,
        Sha256Digest? resultReceiptSha256,
        Sha256Digest? previousManifestSha256,
        Sha256Digest? previousReceiptSha256,
        Guid? previousGenerationId,
        Sha256Digest? previousPointerSha256
    )
    {
        if (generationId == Guid.Empty)
            throw new ArgumentException("A recovery generation ID is required.", nameof(generationId));
        if (!Enum.IsDefined(typeof(InstallationAction), action))
            throw new ArgumentOutOfRangeException(nameof(action));
        ArgumentNullException.ThrowIfNull(snapshotSha256);
        if ((resultManifestSha256 is null) != (resultReceiptSha256 is null))
            throw new ArgumentException("The resulting manifest and receipt must be present or absent as one tuple.");
        if ((previousManifestSha256 is null) != (previousReceiptSha256 is null))
            throw new ArgumentException("The previous manifest and receipt must be present or absent as one tuple.");
        if ((previousGenerationId is null) != (previousPointerSha256 is null))
            throw new ArgumentException("The previous generation and pointer digest must be present or absent together.");
        if (previousGenerationId == Guid.Empty || previousGenerationId == generationId)
            throw new ArgumentException("The previous recovery generation ID is invalid.", nameof(previousGenerationId));
        bool hasResult = resultManifestSha256 is not null;
        bool hasPrevious = previousManifestSha256 is not null;
        bool validTransition = action switch
        {
            InstallationAction.Install => hasResult && !hasPrevious,
            InstallationAction.Update or InstallationAction.Repair => hasResult && hasPrevious,
            InstallationAction.Uninstall => !hasResult && hasPrevious,
            InstallationAction.Backup => hasResult
                && hasPrevious
                && resultManifestSha256 == previousManifestSha256
                && resultReceiptSha256 == previousReceiptSha256,
            InstallationAction.Rollback => hasResult || hasPrevious,
            _ => false
        };
        if (!validTransition)
            throw new ArgumentException("The recovery action doesn't match its ownership-tuple transition.", nameof(action));

        this.GenerationId = generationId;
        this.Action = action;
        this.SnapshotSha256 = snapshotSha256;
        this.ResultManifestSha256 = resultManifestSha256;
        this.ResultReceiptSha256 = resultReceiptSha256;
        this.PreviousManifestSha256 = previousManifestSha256;
        this.PreviousReceiptSha256 = previousReceiptSha256;
        this.PreviousGenerationId = previousGenerationId;
        this.PreviousPointerSha256 = previousPointerSha256;
    }
}

internal static class CanonicalRecoveryPointerDocument
{
    public const int MaximumBytes = 4096;

    public static byte[] Serialize(CommittedRecoveryPointer pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.Default,
            Indented = false,
            SkipValidation = false
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", CommittedRecoveryPointer.CurrentSchemaVersion);
            writer.WriteString("generation_id", pointer.GenerationId.ToString("N"));
            writer.WriteString("action", pointer.Action.ToString().ToLowerInvariant());
            writer.WriteString("snapshot_sha256", pointer.SnapshotSha256.Value);
            WriteDigest(writer, "result_manifest_sha256", pointer.ResultManifestSha256);
            WriteDigest(writer, "result_receipt_sha256", pointer.ResultReceiptSha256);
            WriteDigest(writer, "previous_manifest_sha256", pointer.PreviousManifestSha256);
            WriteDigest(writer, "previous_receipt_sha256", pointer.PreviousReceiptSha256);
            if (pointer.PreviousGenerationId is null)
                writer.WriteNull("previous_generation_id");
            else
                writer.WriteString("previous_generation_id", pointer.PreviousGenerationId.Value.ToString("N"));
            WriteDigest(writer, "previous_pointer_sha256", pointer.PreviousPointerSha256);
            writer.WriteEndObject();
        }
        byte[] bytes = stream.ToArray();
        if (bytes.Length > MaximumBytes)
            throw new OwnershipDocumentException("The canonical recovery pointer exceeds its byte limit.");
        return bytes;
    }

    public static CommittedRecoveryPointer Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes)
            throw new OwnershipDocumentException("The recovery pointer has an invalid byte length.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            JsonElement root = document.RootElement;
            AssertExactProperties(
                root,
                "schema_version",
                "generation_id",
                "action",
                "snapshot_sha256",
                "result_manifest_sha256",
                "result_receipt_sha256",
                "previous_manifest_sha256",
                "previous_receipt_sha256",
                "previous_generation_id",
                "previous_pointer_sha256"
            );
            if (root.GetProperty("schema_version").GetInt32() != CommittedRecoveryPointer.CurrentSchemaVersion)
                throw new OwnershipDocumentException("The recovery pointer schema isn't supported.");
            string generationText = GetString(root, "generation_id");
            if (!Guid.TryParseExact(generationText, "N", out Guid generationId))
                throw new OwnershipDocumentException("The recovery pointer generation ID isn't canonical.");
            InstallationAction action = GetString(root, "action") switch
            {
                "install" => InstallationAction.Install,
                "update" => InstallationAction.Update,
                "repair" => InstallationAction.Repair,
                "uninstall" => InstallationAction.Uninstall,
                "backup" => InstallationAction.Backup,
                "rollback" => InstallationAction.Rollback,
                _ => throw new OwnershipDocumentException("The recovery pointer action isn't supported.")
            };
            Guid? previousGeneration = ParseNullableGuid(root.GetProperty("previous_generation_id"));
            CommittedRecoveryPointer pointer = new(
                generationId,
                action,
                ParseDigest(root.GetProperty("snapshot_sha256")),
                ParseNullableDigest(root.GetProperty("result_manifest_sha256")),
                ParseNullableDigest(root.GetProperty("result_receipt_sha256")),
                ParseNullableDigest(root.GetProperty("previous_manifest_sha256")),
                ParseNullableDigest(root.GetProperty("previous_receipt_sha256")),
                previousGeneration,
                ParseNullableDigest(root.GetProperty("previous_pointer_sha256"))
            );
            if (!bytes.Span.SequenceEqual(Serialize(pointer)))
                throw new OwnershipDocumentException("The recovery pointer isn't in its unique canonical representation.");
            return pointer;
        }
        catch (OwnershipDocumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            throw new OwnershipDocumentException("The recovery pointer is invalid.", exception);
        }
    }

    private static void WriteDigest(Utf8JsonWriter writer, string name, Sha256Digest? digest)
    {
        if (digest is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, digest.Value);
    }

    private static Sha256Digest ParseDigest(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new OwnershipDocumentException("A recovery pointer digest isn't a string.");
        return Sha256Digest.Parse(element.GetString()!);
    }

    private static Sha256Digest? ParseNullableDigest(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? null : ParseDigest(element);

    private static Guid? ParseNullableGuid(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.String || !Guid.TryParseExact(element.GetString(), "N", out Guid value))
            throw new OwnershipDocumentException("A recovery pointer generation reference isn't canonical.");
        return value;
    }

    private static string GetString(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new OwnershipDocumentException($"Recovery pointer property '{name}' isn't a non-empty string.");
        return value.GetString()!;
    }

    private static void AssertExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new OwnershipDocumentException("The recovery pointer root isn't an object.");
        HashSet<string> remaining = new(expected, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
                throw new OwnershipDocumentException("The recovery pointer contains an unknown or duplicate property.");
        }
        if (remaining.Count != 0)
            throw new OwnershipDocumentException("The recovery pointer is missing a required property.");
    }
}

internal sealed class AnchoredCoreStateAuthority
{
    private const int PrivateFileMode = 0x180;
    private readonly LinuxFileIdentity? ManifestIdentity;
    private readonly LinuxFileIdentity? ReceiptIdentity;
    private readonly LinuxFileIdentity? PointerIdentity;

    public GameRootIdentity GameRoot { get; }
    public PackageManifest? Manifest { get; }
    public InstallationReceipt? Receipt { get; }
    public CommittedRecoveryPointer? Pointer { get; }
    public byte[]? ManifestBytes { get; }
    public byte[]? ReceiptBytes { get; }
    public byte[]? PointerBytes { get; }
    public Sha256Digest? ManifestSha256 => this.ManifestBytes is null ? null : Sha256Digest.Hash(this.ManifestBytes);
    public Sha256Digest? ReceiptSha256 => this.ReceiptBytes is null ? null : Sha256Digest.Hash(this.ReceiptBytes);
    public Sha256Digest? PointerSha256 => this.PointerBytes is null ? null : Sha256Digest.Hash(this.PointerBytes);

    private AnchoredCoreStateAuthority(
        GameRootIdentity gameRoot,
        PackageManifest? manifest,
        InstallationReceipt? receipt,
        CommittedRecoveryPointer? pointer,
        byte[]? manifestBytes,
        byte[]? receiptBytes,
        byte[]? pointerBytes,
        LinuxFileIdentity? manifestIdentity,
        LinuxFileIdentity? receiptIdentity,
        LinuxFileIdentity? pointerIdentity
    )
    {
        this.GameRoot = gameRoot;
        this.Manifest = manifest;
        this.Receipt = receipt;
        this.Pointer = pointer;
        this.ManifestBytes = manifestBytes;
        this.ReceiptBytes = receiptBytes;
        this.PointerBytes = pointerBytes;
        this.ManifestIdentity = manifestIdentity;
        this.ReceiptIdentity = receiptIdentity;
        this.PointerIdentity = pointerIdentity;
    }

    public static AnchoredCoreStateAuthority Inspect(InstallerOperationLease lease)
        => Inspect(lease.Game, lease.RootIdentity);

    public static AnchoredCoreStateAuthority Inspect(
        LinuxAnchoredFileSystem game,
        GameRootIdentity gameRoot
    )
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(gameRoot);
        (byte[]? manifestBytes, LinuxFileIdentity? manifestIdentity) = ReadOptional(
            game,
            TransactionPlan.CoreManifestRelativePath,
            OwnershipPersistenceLimits.Default.MaxDocumentBytes
        );
        (byte[]? receiptBytes, LinuxFileIdentity? receiptIdentity) = ReadOptional(
            game,
            TransactionPlan.CoreReceiptRelativePath,
            OwnershipPersistenceLimits.Default.MaxDocumentBytes
        );
        if ((manifestBytes is null) != (receiptBytes is null))
            throw new OwnershipDocumentException("The installed manifest and receipt aren't present as one ownership tuple.");
        PackageManifest? manifest = manifestBytes is null ? null : CanonicalOwnershipDocuments.ParseManifest(manifestBytes);
        InstallationReceipt? receipt = receiptBytes is null ? null : CanonicalOwnershipDocuments.ParseReceipt(receiptBytes, manifest!);

        (byte[]? pointerBytes, LinuxFileIdentity? pointerIdentity) = ReadOptional(
            game,
            TransactionPlan.CoreRecoveryPointerRelativePath,
            CanonicalRecoveryPointerDocument.MaximumBytes
        );
        CommittedRecoveryPointer? pointer = pointerBytes is null ? null : CanonicalRecoveryPointerDocument.Parse(pointerBytes);
        if (pointer is not null && (
            pointer.ResultManifestSha256 != (manifestBytes is null ? null : Sha256Digest.Hash(manifestBytes))
            || pointer.ResultReceiptSha256 != (receiptBytes is null ? null : Sha256Digest.Hash(receiptBytes))
        ))
        {
            throw new OwnershipDocumentException("The current recovery pointer doesn't describe the installed ownership tuple.");
        }
        return new AnchoredCoreStateAuthority(
            gameRoot,
            manifest,
            receipt,
            pointer,
            manifestBytes,
            receiptBytes,
            pointerBytes,
            manifestIdentity,
            receiptIdentity,
            pointerIdentity
        );
    }

    public void AssertUsable(InstallerOperationLease lease)
        => this.AssertUsable(lease.Game, lease.RootIdentity);

    public void AssertUsable(LinuxAnchoredFileSystem game, GameRootIdentity gameRoot)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(gameRoot);
        if (gameRoot != this.GameRoot)
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The inspected core state belongs to another game root.");
        AssertUnchanged(game, TransactionPlan.CoreManifestRelativePath, this.ManifestIdentity, this.ManifestSha256);
        AssertUnchanged(game, TransactionPlan.CoreReceiptRelativePath, this.ReceiptIdentity, this.ReceiptSha256);
        AssertUnchanged(game, TransactionPlan.CoreRecoveryPointerRelativePath, this.PointerIdentity, this.PointerSha256);
    }

    private static (byte[]? Bytes, LinuxFileIdentity? Identity) ReadOptional(LinuxAnchoredFileSystem game, string path, int maxBytes)
    {
        LinuxFileIdentity? identity = game.Stat(path);
        if (identity is null)
            return (null, null);
        if (identity.Kind != LinuxAnchoredEntryKind.RegularFile || identity.LinkCount != 1 || identity.UnixMode != PrivateFileMode || identity.Size is <= 0 or > int.MaxValue)
            throw new OwnershipDocumentException("An installed core-state document has unsafe metadata.");
        using LinuxAnchoredFile file = game.OpenRegularFileForRead(path);
        if (file.Identity != identity)
            throw new OwnershipDocumentException("An installed core-state document changed during inspection.");
        return (game.ReadAllBytes(file, maxBytes), identity);
    }

    private static void AssertUnchanged(
        LinuxAnchoredFileSystem game,
        string path,
        LinuxFileIdentity? expectedIdentity,
        Sha256Digest? expectedSha256
    )
    {
        LinuxFileIdentity? current = game.Stat(path);
        if (current != expectedIdentity)
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The installed core-state tuple changed after inspection.");
        if (current is null)
            return;
        using LinuxAnchoredFile file = game.OpenRegularFileForRead(path);
        if (file.Identity != current || Sha256Digest.Parse(game.ComputeSha256(file)) != expectedSha256)
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The installed core-state bytes changed after inspection.");
    }
}

/// <summary>Authenticated, non-sensitive metadata for one retained recovery generation.</summary>
public sealed record RecoveryGenerationInfo(
    Guid GenerationId,
    InstallationAction Action,
    bool IsCurrent,
    bool IsUserCheckpoint
);

/// <summary>A bounded recovery catalog tied to the exact current pointer the user reviewed.</summary>
public sealed class RecoveryHistory
{
    public Sha256Digest HeadConfirmationDigest { get; }
    public IReadOnlyList<RecoveryGenerationInfo> Generations { get; }

    internal RecoveryHistory(
        Sha256Digest headConfirmationDigest,
        IEnumerable<RecoveryGenerationInfo> generations
    )
    {
        this.HeadConfirmationDigest = headConfirmationDigest;
        this.Generations = generations.ToArray();
    }
}

/// <summary>An opaque descriptor-anchored authority for one committed recovery generation.</summary>
public sealed class CommittedRecoveryHandle : IDisposable, ICommittedRecoveryContentAuthority
{
    private const int MaximumRecoveryChainDepth = 64;
    private const long MaximumGenerationContentBytes = 8L * 1024 * 1024 * 1024;
    private const int PrivateFileMode = 0x180;
    private const int PrivateDirectoryMode = 0x1c0;
    private readonly LinuxAnchoredFileSystem NamedGameRoot;
    private readonly LinuxAnchoredFileSystem Generation;
    private readonly string GenerationPath;
    private readonly LinuxFileIdentity GenerationIdentity;
    private readonly LinuxFileIdentity SnapshotIdentity;
    private readonly Dictionary<string, RecoveryContentBinding> GameFiles;
    private readonly LinuxFileIdentity? PreviousReceiptIdentity;
    private readonly LinuxFileIdentity? PreviousManifestIdentity;
    private readonly Sha256Digest AuthorizedHeadPointerSha256;
    private bool Disposed;

    /// <summary>The immutable recovery generation ID.</summary>
    public Guid GenerationId => this.Pointer.GenerationId;

    /// <summary>The action whose prior state this generation can restore.</summary>
    public InstallationAction Action => this.Pointer.Action;

    /// <summary>The canonical rollback snapshot authenticated by this generation.</summary>
    public RollbackSnapshot Snapshot { get; }

    /// <summary>The canonical rollback snapshot digest.</summary>
    public Sha256Digest SnapshotSha256 => this.Pointer.SnapshotSha256;
    internal Sha256Digest? PreviousManifestSha256 => this.Pointer.PreviousManifestSha256;
    internal Sha256Digest? PreviousReceiptSha256 => this.Pointer.PreviousReceiptSha256;

    internal GameRootIdentity GameRoot { get; }
    internal CommittedRecoveryPointer Pointer { get; }
    GameRootIdentity ICommittedRecoveryContentAuthority.GameRoot => this.GameRoot;
    InstallationAction ICommittedRecoveryContentAuthority.OriginAction => this.Action;
    Sha256Digest? ICommittedRecoveryContentAuthority.PreviousManifestSha256 => this.PreviousManifestSha256;
    Sha256Digest? ICommittedRecoveryContentAuthority.PreviousReceiptSha256 => this.PreviousReceiptSha256;
    Sha256Digest ICommittedRecoveryContentAuthority.AuthorizedHeadPointerSha256 => this.AuthorizedHeadPointerSha256;

    private CommittedRecoveryHandle(
        GameRootIdentity gameRoot,
        CommittedRecoveryPointer pointer,
        RollbackSnapshot snapshot,
        LinuxAnchoredFileSystem namedGameRoot,
        LinuxAnchoredFileSystem generation,
        string generationPath,
        LinuxFileIdentity snapshotIdentity,
        Dictionary<string, RecoveryContentBinding> gameFiles,
        LinuxFileIdentity? previousReceiptIdentity,
        LinuxFileIdentity? previousManifestIdentity,
        Sha256Digest authorizedHeadPointerSha256
    )
    {
        this.GameRoot = gameRoot;
        this.Pointer = pointer;
        this.Snapshot = snapshot;
        this.NamedGameRoot = namedGameRoot;
        this.Generation = generation;
        this.GenerationPath = generationPath;
        this.GenerationIdentity = generation.Identity;
        this.SnapshotIdentity = snapshotIdentity;
        this.GameFiles = gameFiles;
        this.PreviousReceiptIdentity = previousReceiptIdentity;
        this.PreviousManifestIdentity = previousManifestIdentity;
        this.AuthorizedHeadPointerSha256 = authorizedHeadPointerSha256;
    }

    internal static CommittedRecoveryHandle OpenCurrent(
        InstallerOperationLease lease,
        AnchoredCoreStateAuthority currentState
    )
        => OpenCurrent(lease.Game, lease.CanonicalGameRoot, lease.RootIdentity, currentState);

    internal static CommittedRecoveryHandle OpenCurrent(
        LinuxAnchoredFileSystem game,
        string canonicalGameRoot,
        GameRootIdentity gameRoot,
        AnchoredCoreStateAuthority currentState
    )
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(canonicalGameRoot))
            throw new ArgumentException("A canonical game root is required.", nameof(canonicalGameRoot));
        ArgumentNullException.ThrowIfNull(gameRoot);
        ArgumentNullException.ThrowIfNull(currentState);
        currentState.AssertUsable(game, gameRoot);
        CommittedRecoveryPointer pointer = currentState.Pointer
            ?? throw new OwnershipDocumentException("There is no committed recovery generation to open.");
        return Open(game, canonicalGameRoot, gameRoot, pointer, currentState.PointerSha256
            ?? throw new OwnershipDocumentException("The current recovery pointer digest is unavailable."));
    }

    internal static CommittedRecoveryHandle OpenSelected(
        InstallerOperationLease lease,
        AnchoredCoreStateAuthority currentState,
        Guid generationId
    )
        => OpenSelected(lease.Game, lease.CanonicalGameRoot, lease.RootIdentity, currentState, generationId);

    internal static CommittedRecoveryHandle OpenSelected(
        LinuxAnchoredFileSystem game,
        string canonicalGameRoot,
        GameRootIdentity gameRoot,
        AnchoredCoreStateAuthority currentState,
        Guid generationId
    )
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(canonicalGameRoot))
            throw new ArgumentException("A canonical game root is required.", nameof(canonicalGameRoot));
        ArgumentNullException.ThrowIfNull(gameRoot);
        ArgumentNullException.ThrowIfNull(currentState);
        if (generationId == Guid.Empty)
            throw new ArgumentException("A recovery generation ID is required.", nameof(generationId));
        currentState.AssertUsable(game, gameRoot);
        CommittedRecoveryPointer pointer = currentState.Pointer
            ?? throw new OwnershipDocumentException("There is no committed recovery generation to select.");
        Sha256Digest headDigest = currentState.PointerSha256
            ?? throw new OwnershipDocumentException("The current recovery pointer digest is unavailable.");
        HashSet<Guid> visited = new();
        for (int depth = 0; depth < MaximumRecoveryChainDepth; depth++)
        {
            if (!visited.Add(pointer.GenerationId))
                throw new OwnershipDocumentException("The committed recovery chain contains a cycle.");
            if (pointer.GenerationId == generationId)
                return Open(game, canonicalGameRoot, gameRoot, pointer, headDigest);
            pointer = ReadPreviousPointer(game, pointer);
        }
        throw new OwnershipDocumentException("The selected recovery generation isn't present in the bounded committed chain.");
    }

    internal static RecoveryHistory ListHistory(
        LinuxAnchoredFileSystem game,
        string canonicalGameRoot,
        GameRootIdentity gameRoot,
        AnchoredCoreStateAuthority currentState
    )
    {
        currentState.AssertUsable(game, gameRoot);
        Sha256Digest headDigest = currentState.PointerSha256
            ?? throw new OwnershipDocumentException("There is no committed recovery history to list.");
        IReadOnlyList<CommittedRecoveryPointer> chain = ReadAuthenticatedChain(game, currentState);
        List<RecoveryGenerationInfo> items = new(chain.Count);
        for (int index = 0; index < chain.Count; index++)
        {
            CommittedRecoveryPointer pointer = chain[index];
            using CommittedRecoveryHandle verified = Open(game, canonicalGameRoot, gameRoot, pointer, headDigest);
            items.Add(new RecoveryGenerationInfo(
                pointer.GenerationId,
                pointer.Action,
                index == 0,
                pointer.Action == InstallationAction.Backup
            ));
        }
        return new RecoveryHistory(headDigest, items);
    }

    internal static int PruneHistoryTail(
        InstallerOperationLease lease,
        AnchoredCoreStateAuthority currentState,
        int retainNewest,
        Sha256Digest confirmedHeadPointer
    )
    {
        if (retainNewest < 1)
            throw new ArgumentOutOfRangeException(nameof(retainNewest), "The current recovery generation must always be retained.");
        currentState.AssertUsable(lease);
        if (currentState.PointerSha256 != confirmedHeadPointer)
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The recovery history changed after prune confirmation.");
        IReadOnlyList<CommittedRecoveryPointer> chain = ReadAuthenticatedChain(lease.Game, currentState);
        CommittedRecoveryPointer[] remove = chain.Skip(retainNewest).Reverse().ToArray();
        if (remove.Length == 0)
            return 0;

        foreach (CommittedRecoveryPointer pointer in remove)
        {
            using CommittedRecoveryHandle ignored = Open(
                lease.Game,
                lease.CanonicalGameRoot,
                lease.RootIdentity,
                pointer,
                confirmedHeadPointer
            );
        }
        lease.ReserveNextGeneration(lease.Generation);
        foreach (CommittedRecoveryPointer pointer in remove)
            DeleteGeneration(lease.Game, pointer.GenerationId);
        return remove.Length;
    }

    private static IReadOnlyList<CommittedRecoveryPointer> ReadAuthenticatedChain(
        LinuxAnchoredFileSystem game,
        AnchoredCoreStateAuthority currentState
    )
    {
        CommittedRecoveryPointer pointer = currentState.Pointer
            ?? throw new OwnershipDocumentException("There is no committed recovery history.");
        List<CommittedRecoveryPointer> chain = new();
        HashSet<Guid> visited = new();
        for (int depth = 0; depth < MaximumRecoveryChainDepth; depth++)
        {
            if (!visited.Add(pointer.GenerationId))
                throw new OwnershipDocumentException("The committed recovery chain contains a cycle.");
            string generationPath = $".smapi-installer/recovery/generations/{pointer.GenerationId:N}";
            if (game.Stat(generationPath) is null)
            {
                if (depth == 0)
                    throw new OwnershipDocumentException("The current committed recovery generation is missing.");
                return chain;
            }
            chain.Add(pointer);
            if (pointer.PreviousGenerationId is null)
                return chain;
            pointer = ReadPreviousPointer(game, pointer);
        }
        if (pointer.PreviousGenerationId is not null)
            throw new OwnershipDocumentException("The committed recovery chain exceeds its depth limit.");
        return chain;
    }

    private static void DeleteGeneration(LinuxAnchoredFileSystem game, Guid generationId)
    {
        string path = $".smapi-installer/recovery/generations/{generationId:N}";
        LinuxFileIdentity generationIdentity = game.Stat(path)
            ?? throw new OwnershipDocumentException("A pruned recovery generation disappeared.");
        using (LinuxAnchoredFileSystem generation = game.OpenSubdirectory(path))
        {
            IReadOnlyList<string> rootEntries = generation.EnumerateEntryNames(maximumEntries: 5);
            HashSet<string> allowed = new(StringComparer.Ordinal)
            {
                "snapshot.json",
                "previous-receipt.json",
                "previous-manifest.json",
                "previous-pointer.json",
                "files"
            };
            if (rootEntries.Any(name => !allowed.Contains(name)))
                throw new OwnershipDocumentException("A recovery generation contains unknown state and wasn't pruned.");
            if (rootEntries.Contains("files", StringComparer.Ordinal))
            {
                LinuxFileIdentity filesIdentity = generation.Stat("files")
                    ?? throw new OwnershipDocumentException("Recovery content disappeared during pruning.");
                using (LinuxAnchoredFileSystem files = generation.OpenSubdirectory("files"))
                {
                    IReadOnlyList<string> names = files.EnumerateEntryNames(maximumEntries: TransactionPlan.MaximumOperationCount);
                    for (int index = 0; index < names.Count; index++)
                    {
                        string name = names[index];
                        if (name != index.ToString("D8"))
                            throw new OwnershipDocumentException("Recovery content indices aren't canonical during pruning.");
                        LinuxFileIdentity identity = files.Stat(name)
                            ?? throw new OwnershipDocumentException("Recovery content disappeared during pruning.");
                        if (identity.Kind != LinuxAnchoredEntryKind.RegularFile || identity.LinkCount != 1 || identity.UnixMode != PrivateFileMode)
                            throw new OwnershipDocumentException("Recovery content has unsafe metadata and wasn't pruned.");
                        files.UnlinkFile(name, identity);
                    }
                }
                LinuxFileIdentity emptiedFiles = generation.Stat("files")
                    ?? throw new OwnershipDocumentException("The recovery content directory disappeared during pruning.");
                if (!emptiedFiles.IsSameObject(filesIdentity))
                    throw new OwnershipDocumentException("The recovery content directory changed during pruning.");
                generation.RemoveEmptyDirectory("files", emptiedFiles);
            }
            foreach (string name in rootEntries.Where(name => name != "files"))
            {
                LinuxFileIdentity identity = generation.Stat(name)
                    ?? throw new OwnershipDocumentException("A recovery document disappeared during pruning.");
                if (identity.Kind != LinuxAnchoredEntryKind.RegularFile || identity.LinkCount != 1 || identity.UnixMode != PrivateFileMode)
                    throw new OwnershipDocumentException("A recovery document has unsafe metadata and wasn't pruned.");
                generation.UnlinkFile(name, identity);
            }
        }
        LinuxFileIdentity emptiedGeneration = game.Stat(path)
            ?? throw new OwnershipDocumentException("The recovery generation disappeared during pruning.");
        if (!emptiedGeneration.IsSameObject(generationIdentity))
            throw new OwnershipDocumentException("The recovery generation changed during pruning.");
        game.RemoveEmptyDirectory(path, emptiedGeneration);
    }

    private static CommittedRecoveryHandle Open(
        LinuxAnchoredFileSystem game,
        string canonicalGameRoot,
        GameRootIdentity gameRoot,
        CommittedRecoveryPointer pointer,
        Sha256Digest authorizedHeadPointerSha256
    )
    {
        string generationPath = $".smapi-installer/recovery/generations/{pointer.GenerationId:N}";
        LinuxAnchoredFileSystem namedGameRoot = new(canonicalGameRoot);
        LinuxAnchoredFileSystem? generation = null;
        try
        {
            if (!gameRoot.Matches(game.GetCurrentRootIdentity()) || !gameRoot.Matches(namedGameRoot.Identity))
                throw new OwnershipDocumentException("The named game root changed while opening committed recovery state.");
            generation = namedGameRoot.OpenSubdirectory(generationPath);
            if (generation.Identity.UnixMode != PrivateDirectoryMode)
                throw new OwnershipDocumentException("The committed recovery generation directory isn't private.");
            (byte[] snapshotBytes, LinuxFileIdentity snapshotIdentity) = ReadRequired(
                generation,
                "snapshot.json",
                OwnershipPersistenceLimits.Default.MaxDocumentBytes
            );
            if (Sha256Digest.Hash(snapshotBytes) != pointer.SnapshotSha256)
                throw new OwnershipDocumentException("The committed recovery snapshot doesn't match its pointer.");
            RollbackSnapshot snapshot = CanonicalOwnershipDocuments.ParseRollbackSnapshotUnbound(snapshotBytes);
            if (
                snapshot.ExpectedCurrentReceiptSha256 != pointer.ResultReceiptSha256
                || snapshot.PreviousReceiptSha256 != pointer.PreviousReceiptSha256
            )
            {
                throw new OwnershipDocumentException("The committed recovery snapshot doesn't match its receipt transition.");
            }

            LinuxFileIdentity? previousReceiptIdentity = null;
            LinuxFileIdentity? previousManifestIdentity = null;
            InstallationReceipt? previousReceiptModel = null;
            if (pointer.PreviousReceiptSha256 is not null)
            {
                (byte[] previousReceipt, LinuxFileIdentity receiptIdentity) = ReadRequired(
                    generation,
                    "previous-receipt.json",
                    OwnershipPersistenceLimits.Default.MaxDocumentBytes
                );
                (byte[] previousManifest, LinuxFileIdentity manifestIdentity) = ReadRequired(
                    generation,
                    "previous-manifest.json",
                    OwnershipPersistenceLimits.Default.MaxDocumentBytes
                );
                if (
                    Sha256Digest.Hash(previousReceipt) != pointer.PreviousReceiptSha256
                    || Sha256Digest.Hash(previousManifest) != pointer.PreviousManifestSha256
                )
                {
                    throw new OwnershipDocumentException("The committed recovery ownership tuple doesn't match its pointer.");
                }
                PackageManifest manifest = CanonicalOwnershipDocuments.ParseManifest(previousManifest);
                InstallationReceipt receipt = CanonicalOwnershipDocuments.ParseReceipt(previousReceipt, manifest);
                if (receipt.GetCanonicalDigest() != snapshot.PreviousReceiptSha256)
                    throw new OwnershipDocumentException("The committed recovery receipt doesn't match its snapshot.");
                previousReceiptModel = receipt;
                previousReceiptIdentity = receiptIdentity;
                previousManifestIdentity = manifestIdentity;
            }
            if (pointer.Action == InstallationAction.Backup)
                AssertCompleteUserBackup(snapshot, previousReceiptModel);

            if (pointer.PreviousPointerSha256 is not null)
            {
                (byte[] priorPointerBytes, _) = ReadRequired(
                    generation,
                    "previous-pointer.json",
                    CanonicalRecoveryPointerDocument.MaximumBytes
                );
                if (Sha256Digest.Hash(priorPointerBytes) != pointer.PreviousPointerSha256)
                    throw new OwnershipDocumentException("The previous recovery pointer bytes don't match the committed pointer.");
                CommittedRecoveryPointer priorPointer = CanonicalRecoveryPointerDocument.Parse(priorPointerBytes);
                if (priorPointer.GenerationId != pointer.PreviousGenerationId)
                    throw new OwnershipDocumentException("The previous recovery generation reference doesn't match its pointer bytes.");
            }

            Dictionary<string, RecoveryContentBinding> gameFiles = new(StringComparer.Ordinal);
            int contentIndex = 0;
            long contentBytes = 0;
            foreach (RollbackSnapshotEntry entry in snapshot.Entries.Where(entry => entry.Kind == RollbackEntryKind.Restore))
            {
                try
                {
                    contentBytes = checked(contentBytes + entry.Backup!.SizeBytes);
                }
                catch (OverflowException exception)
                {
                    throw new OwnershipDocumentException("The committed recovery content size overflows its bound.", exception);
                }
                if (contentBytes > MaximumGenerationContentBytes)
                    throw new OwnershipDocumentException("The committed recovery generation exceeds its aggregate content limit.");
                string name = $"files/{contentIndex:D8}";
                using LinuxAnchoredFile file = generation.OpenRegularFileForRead(name);
                AssertContentIdentity(generation, file, entry.Backup);
                gameFiles.Add(entry.Path.Value, new RecoveryContentBinding(name, file.Identity, entry.Backup!));
                contentIndex++;
            }

            HashSet<string> expectedNames = new(StringComparer.Ordinal) { "snapshot.json" };
            if (previousReceiptIdentity is not null)
            {
                expectedNames.Add("previous-receipt.json");
                expectedNames.Add("previous-manifest.json");
            }
            if (pointer.PreviousPointerSha256 is not null)
                expectedNames.Add("previous-pointer.json");
            if (contentIndex > 0)
                expectedNames.Add("files");
            if (!generation.EnumerateEntryNames(maximumEntries: expectedNames.Count).ToHashSet(StringComparer.Ordinal).SetEquals(expectedNames))
                throw new OwnershipDocumentException("The committed recovery generation contains an unexpected entry.");
            if (contentIndex > 0)
            {
                string[] expectedFiles = Enumerable.Range(0, contentIndex).Select(index => index.ToString("D8")).ToArray();
                if (!generation.EnumerateEntryNames("files", contentIndex).SequenceEqual(expectedFiles, StringComparer.Ordinal))
                    throw new OwnershipDocumentException("The committed recovery generation content indices aren't exact and contiguous.");
            }

            CommittedRecoveryHandle result = new(
                gameRoot,
                pointer,
                snapshot,
                namedGameRoot,
                generation,
                generationPath,
                snapshotIdentity,
                gameFiles,
                previousReceiptIdentity,
                previousManifestIdentity,
                authorizedHeadPointerSha256
            );
            namedGameRoot = null!;
            generation = null!;
            return result;
        }
        finally
        {
            generation?.Dispose();
            namedGameRoot?.Dispose();
        }
    }

    private static CommittedRecoveryPointer ReadPreviousPointer(
        LinuxAnchoredFileSystem game,
        CommittedRecoveryPointer current
    )
    {
        if (current.PreviousGenerationId is null || current.PreviousPointerSha256 is null)
            throw new OwnershipDocumentException("The selected recovery generation isn't present in the committed chain.");
        string generationPath = $".smapi-installer/recovery/generations/{current.GenerationId:N}";
        using LinuxAnchoredFileSystem generation = game.OpenSubdirectory(generationPath);
        if (generation.Identity.UnixMode != PrivateDirectoryMode)
            throw new OwnershipDocumentException("A committed recovery-chain directory isn't private.");
        (byte[] bytes, _) = ReadRequired(generation, "previous-pointer.json", CanonicalRecoveryPointerDocument.MaximumBytes);
        if (Sha256Digest.Hash(bytes) != current.PreviousPointerSha256)
            throw new OwnershipDocumentException("A previous recovery pointer doesn't match the committed chain digest.");
        CommittedRecoveryPointer previous = CanonicalRecoveryPointerDocument.Parse(bytes);
        if (
            previous.GenerationId != current.PreviousGenerationId
            || previous.ResultManifestSha256 != current.PreviousManifestSha256
            || previous.ResultReceiptSha256 != current.PreviousReceiptSha256
        )
        {
            throw new OwnershipDocumentException("A previous recovery pointer doesn't match the committed chain transition.");
        }
        return previous;
    }

    private static void AssertCompleteUserBackup(
        RollbackSnapshot snapshot,
        InstallationReceipt? previousReceipt
    )
    {
        if (previousReceipt is null || snapshot.Entries.Count != previousReceipt.Entries.Count + 1)
            throw new OwnershipDocumentException("A committed user backup isn't complete for its authenticated receipt.");
        Dictionary<string, RollbackSnapshotEntry> entries = snapshot.Entries.ToDictionary(entry => entry.Path.Value, StringComparer.Ordinal);
        foreach (InstallationReceiptEntry receiptEntry in previousReceipt.Entries)
        {
            if (
                !entries.TryGetValue(receiptEntry.Path.Value, out RollbackSnapshotEntry? snapshotEntry)
                || snapshotEntry.Kind != RollbackEntryKind.Restore
                || snapshotEntry.ExpectedCurrent != snapshotEntry.Backup
                || snapshotEntry.Backup?.Sha256 != receiptEntry.InstalledSha256
                || snapshotEntry.Backup.UnixMode != receiptEntry.UnixMode
            )
            {
                throw new OwnershipDocumentException("A committed user backup doesn't match its authenticated receipt entries.");
            }
        }
        if (
            !entries.TryGetValue("StardewValley-original", out RollbackSnapshotEntry? launcherBackup)
            || launcherBackup.Kind != RollbackEntryKind.Restore
            || launcherBackup.ExpectedCurrent != launcherBackup.Backup
            || launcherBackup.Backup?.Sha256 != previousReceipt.Launcher.OriginalLauncherSha256
            || launcherBackup.Backup.UnixMode != previousReceipt.Launcher.OriginalLauncherUnixMode
        )
        {
            throw new OwnershipDocumentException("A committed user backup doesn't contain the authenticated original launcher.");
        }
    }

    LinuxAnchoredFile ICommittedRecoveryContentAuthority.OpenGameFile(
        NormalizedRelativePath path,
        RecoveryFileIdentity expectedIdentity
    )
    {
        this.AssertUsable();
        if (!this.GameFiles.TryGetValue(path.Value, out RecoveryContentBinding? binding))
            throw new OwnershipDocumentException("The selected recovery generation doesn't contain the requested game file.");
        if (binding.Expected != expectedIdentity)
            throw new OwnershipDocumentException("The requested recovery identity doesn't match the selected generation.");
        LinuxAnchoredFile file = this.Generation.OpenRegularFileForRead(binding.Name);
        try
        {
            if (file.Identity != binding.FileIdentity)
                throw new OwnershipDocumentException("A committed recovery content file changed after selection.");
            AssertContentIdentity(this.Generation, file, expectedIdentity);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    LinuxAnchoredFile ICommittedRecoveryContentAuthority.OpenPreviousReceipt(Sha256Digest expectedSha256)
        => this.OpenPreviousDocument("previous-receipt.json", this.PreviousReceiptIdentity, expectedSha256);

    LinuxAnchoredFile ICommittedRecoveryContentAuthority.OpenPreviousManifest(Sha256Digest expectedSha256)
        => this.OpenPreviousDocument("previous-manifest.json", this.PreviousManifestIdentity, expectedSha256);

    void ICommittedRecoveryContentAuthority.AssertUsable() => this.AssertUsable();

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.Disposed)
            return;
        this.Disposed = true;
        this.Generation.Dispose();
        this.NamedGameRoot.Dispose();
    }

    private LinuxAnchoredFile OpenPreviousDocument(
        string name,
        LinuxFileIdentity? expectedIdentity,
        Sha256Digest expectedSha256
    )
    {
        this.AssertUsable();
        if (expectedIdentity is null)
            throw new OwnershipDocumentException("The selected recovery generation has no previous ownership document.");
        LinuxAnchoredFile file = this.Generation.OpenRegularFileForRead(name);
        try
        {
            if (
                file.Identity != expectedIdentity
                || file.Identity.UnixMode != PrivateFileMode
                || Sha256Digest.Parse(this.Generation.ComputeSha256(file)) != expectedSha256
            )
            {
                throw new OwnershipDocumentException("A previous ownership document changed after recovery selection.");
            }
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private void AssertUsable()
    {
        if (this.Disposed)
            throw new ObjectDisposedException(nameof(CommittedRecoveryHandle));
        if (
            !this.GameRoot.Matches(this.NamedGameRoot.GetCurrentRootIdentity())
            || this.NamedGameRoot.Stat(this.GenerationPath)?.IsSameObject(this.GenerationIdentity) != true
            || this.Generation.GetCurrentRootIdentity() != this.GenerationIdentity
        )
            throw new OwnershipDocumentException("The committed recovery generation changed after it was opened.");
        using (LinuxAnchoredFile snapshot = this.Generation.OpenRegularFileForRead("snapshot.json"))
        {
            if (
                snapshot.Identity != this.SnapshotIdentity
                || Sha256Digest.Parse(this.Generation.ComputeSha256(snapshot)) != this.SnapshotSha256
            )
                throw new OwnershipDocumentException("The committed recovery snapshot changed after selection.");
        }
    }

    private static (byte[] Bytes, LinuxFileIdentity Identity) ReadRequired(
        LinuxAnchoredFileSystem generation,
        string name,
        int maxBytes
    )
    {
        using LinuxAnchoredFile file = generation.OpenRegularFileForRead(name);
        if (file.Identity.UnixMode != PrivateFileMode || file.Identity.Size is <= 0 or > int.MaxValue)
            throw new OwnershipDocumentException("A committed recovery document has unsafe metadata.");
        return (generation.ReadAllBytes(file, maxBytes), file.Identity);
    }

    private static void AssertContentIdentity(
        LinuxAnchoredFileSystem generation,
        LinuxAnchoredFile file,
        RecoveryFileIdentity expected
    )
    {
        if (
            file.Identity.Kind != LinuxAnchoredEntryKind.RegularFile
            || file.Identity.LinkCount != 1
            || file.Identity.UnixMode != PrivateFileMode
            || file.Identity.Size != expected.SizeBytes
            || Sha256Digest.Parse(generation.ComputeSha256(file)) != expected.Sha256
        )
        {
            throw new OwnershipDocumentException("A committed recovery content file doesn't match its snapshot identity.");
        }
    }

    private sealed record RecoveryContentBinding(
        string Name,
        LinuxFileIdentity FileIdentity,
        RecoveryFileIdentity Expected
    );
}
