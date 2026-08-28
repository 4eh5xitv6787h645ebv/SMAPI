using System.Globalization;
using System.Runtime.ExceptionServices;
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
    public const int LegacySchemaVersion = 1;
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; }
    public Guid GenerationId { get; }
    public InstallationAction Action { get; }
    public Sha256Digest SnapshotSha256 { get; }
    public Sha256Digest? ResultManifestSha256 { get; }
    public Sha256Digest? ResultReceiptSha256 { get; }
    public Sha256Digest? PreviousManifestSha256 { get; }
    public Sha256Digest? PreviousReceiptSha256 { get; }
    public Guid? PreviousGenerationId { get; }
    public Sha256Digest? PreviousPointerSha256 { get; }
    public Sha256Digest? RetentionSha256 { get; }

    public CommittedRecoveryPointer(
        Guid generationId,
        InstallationAction action,
        Sha256Digest snapshotSha256,
        Sha256Digest? resultManifestSha256,
        Sha256Digest? resultReceiptSha256,
        Sha256Digest? previousManifestSha256,
        Sha256Digest? previousReceiptSha256,
        Guid? previousGenerationId,
        Sha256Digest? previousPointerSha256,
        Sha256Digest? retentionSha256 = null,
        int schemaVersion = LegacySchemaVersion
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
        if (schemaVersion is not LegacySchemaVersion and not CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "The recovery pointer schema isn't supported.");
        if (schemaVersion == LegacySchemaVersion && retentionSha256 is not null)
            throw new ArgumentException("A v1 recovery pointer can't reference a retention document.", nameof(retentionSha256));
        bool hasResult = resultManifestSha256 is not null;
        bool hasPrevious = previousManifestSha256 is not null;
        bool validTransition = action switch
        {
            InstallationAction.Install => hasResult && !hasPrevious,
            InstallationAction.Update or InstallationAction.Repair => hasResult && hasPrevious,
            InstallationAction.Uninstall => !hasResult && hasPrevious,
            // Backup normally preserves the tuple, but may atomically normalize an exact generated-file recipe
            // evolution while retaining the prior tuple in this same recovery generation.
            InstallationAction.Backup => hasResult && hasPrevious,
            InstallationAction.Rollback => hasResult || hasPrevious,
            _ => false
        };
        if (!validTransition)
            throw new ArgumentException("The recovery action doesn't match its ownership-tuple transition.", nameof(action));

        this.SchemaVersion = schemaVersion;
        this.GenerationId = generationId;
        this.Action = action;
        this.SnapshotSha256 = snapshotSha256;
        this.ResultManifestSha256 = resultManifestSha256;
        this.ResultReceiptSha256 = resultReceiptSha256;
        this.PreviousManifestSha256 = previousManifestSha256;
        this.PreviousReceiptSha256 = previousReceiptSha256;
        this.PreviousGenerationId = previousGenerationId;
        this.PreviousPointerSha256 = previousPointerSha256;
        this.RetentionSha256 = retentionSha256;
    }

    public CommittedRecoveryPointer WithRetention(Sha256Digest? retentionSha256, int schemaVersion = CurrentSchemaVersion)
        => new(
            this.GenerationId,
            this.Action,
            this.SnapshotSha256,
            this.ResultManifestSha256,
            this.ResultReceiptSha256,
            this.PreviousManifestSha256,
            this.PreviousReceiptSha256,
            this.PreviousGenerationId,
            this.PreviousPointerSha256,
            retentionSha256,
            schemaVersion
        );
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
            writer.WriteNumber("schema_version", pointer.SchemaVersion);
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
            if (pointer.SchemaVersion >= CommittedRecoveryPointer.CurrentSchemaVersion)
                WriteDigest(writer, "retention_sha256", pointer.RetentionSha256);
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
            int schemaVersion = root.GetProperty("schema_version").GetInt32();
            string[] expectedProperties =
            [
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
            ];
            if (schemaVersion == CommittedRecoveryPointer.CurrentSchemaVersion)
                expectedProperties = [.. expectedProperties, "retention_sha256"];
            else if (schemaVersion != CommittedRecoveryPointer.LegacySchemaVersion)
                throw new OwnershipDocumentException("The recovery pointer schema isn't supported.");
            AssertExactProperties(root, expectedProperties);
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
                ParseNullableDigest(root.GetProperty("previous_pointer_sha256")),
                schemaVersion == CommittedRecoveryPointer.CurrentSchemaVersion
                    ? ParseNullableDigest(root.GetProperty("retention_sha256"))
                    : null,
                schemaVersion
            );
            if (!bytes.Span.SequenceEqual(Serialize(pointer)))
                throw new OwnershipDocumentException("The recovery pointer isn't in its unique canonical representation.");
            return pointer;
        }
        catch (OwnershipDocumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException)
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

internal sealed record RecoveryRetentionRecord(
    Sha256Digest PublicationHeadPointerSha256,
    int PublicationHeadPointerSchemaVersion,
    Sha256Digest? PreviousRetentionSha256,
    Guid CutoffGenerationId,
    Sha256Digest CutoffPointerSha256,
    Guid TruncatedGenerationId,
    Sha256Digest TruncatedPointerSha256,
    IReadOnlyList<Guid> PublicationRetainedGenerationIds,
    IReadOnlyList<Guid> RemovedGenerationIds
);

internal static class CanonicalRecoveryRetentionDocument
{
    public const int MaximumBytes = 16 * 1024;
    private const int SchemaVersion = 2;

    public static byte[] Serialize(RecoveryRetentionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        AssertRecord(record);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.Default,
            Indented = false,
            SkipValidation = false
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", SchemaVersion);
            writer.WriteString("publication_head_pointer_sha256", record.PublicationHeadPointerSha256.Value);
            writer.WriteNumber("publication_head_pointer_schema_version", record.PublicationHeadPointerSchemaVersion);
            WriteDigest(writer, "previous_retention_sha256", record.PreviousRetentionSha256);
            writer.WriteString("cutoff_generation_id", record.CutoffGenerationId.ToString("N"));
            writer.WriteString("cutoff_pointer_sha256", record.CutoffPointerSha256.Value);
            writer.WriteString("truncated_generation_id", record.TruncatedGenerationId.ToString("N"));
            writer.WriteString("truncated_pointer_sha256", record.TruncatedPointerSha256.Value);
            WriteIds(writer, "publication_retained_generation_ids", record.PublicationRetainedGenerationIds);
            WriteIds(writer, "removed_generation_ids", record.RemovedGenerationIds);
            writer.WriteEndObject();
        }
        byte[] bytes = stream.ToArray();
        if (bytes.Length > MaximumBytes)
            throw new OwnershipDocumentException("The canonical recovery-retention record exceeds its byte limit.");
        return bytes;
    }

    public static RecoveryRetentionRecord Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes)
            throw new OwnershipDocumentException("The recovery-retention record has an invalid byte length.");
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
                "publication_head_pointer_sha256",
                "publication_head_pointer_schema_version",
                "previous_retention_sha256",
                "cutoff_generation_id",
                "cutoff_pointer_sha256",
                "truncated_generation_id",
                "truncated_pointer_sha256",
                "publication_retained_generation_ids",
                "removed_generation_ids"
            );
            if (root.GetProperty("schema_version").GetInt32() != SchemaVersion)
                throw new OwnershipDocumentException("The recovery-retention schema isn't supported.");
            RecoveryRetentionRecord record = new(
                Sha256Digest.Parse(GetString(root, "publication_head_pointer_sha256")),
                root.GetProperty("publication_head_pointer_schema_version").GetInt32(),
                ParseNullableDigest(root.GetProperty("previous_retention_sha256")),
                ParseGuid(root, "cutoff_generation_id"),
                Sha256Digest.Parse(GetString(root, "cutoff_pointer_sha256")),
                ParseGuid(root, "truncated_generation_id"),
                Sha256Digest.Parse(GetString(root, "truncated_pointer_sha256")),
                ParseIds(root, "publication_retained_generation_ids"),
                ParseIds(root, "removed_generation_ids")
            );
            AssertRecord(record);
            if (!bytes.Span.SequenceEqual(Serialize(record)))
                throw new OwnershipDocumentException("The recovery-retention record isn't in its unique canonical representation.");
            return record;
        }
        catch (OwnershipDocumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            throw new OwnershipDocumentException("The recovery-retention record is invalid.", exception);
        }
    }

    private static void AssertRecord(RecoveryRetentionRecord record)
    {
        if (
            record.PublicationHeadPointerSchemaVersion is not CommittedRecoveryPointer.LegacySchemaVersion
                and not CommittedRecoveryPointer.CurrentSchemaVersion
            || (record.PublicationHeadPointerSchemaVersion == CommittedRecoveryPointer.LegacySchemaVersion
                && record.PreviousRetentionSha256 is not null)
            ||
            record.CutoffGenerationId == Guid.Empty
            || record.TruncatedGenerationId == Guid.Empty
            || record.CutoffGenerationId == record.TruncatedGenerationId
        )
            throw new OwnershipDocumentException("The recovery-retention cutoff is invalid.");
        Guid[] retained = record.PublicationRetainedGenerationIds.ToArray();
        Guid[] removed = record.RemovedGenerationIds.ToArray();
        if (
            retained.Length is <= 0 or > CommittedRecoveryHandle.MaximumRecoveryChainDepth
            || removed.Length is <= 0 or > CommittedRecoveryHandle.MaximumRecoveryChainDepth
            || retained[^1] != record.CutoffGenerationId
            || removed[0] != record.TruncatedGenerationId
            || retained.Concat(removed).Any(id => id == Guid.Empty)
            || retained.Concat(removed).Distinct().Count() != retained.Length + removed.Length
        )
            throw new OwnershipDocumentException("The recovery-retention generation catalog is invalid.");
    }

    private static void WriteIds(Utf8JsonWriter writer, string name, IReadOnlyList<Guid> ids)
    {
        writer.WriteStartArray(name);
        foreach (Guid id in ids)
            writer.WriteStringValue(id.ToString("N"));
        writer.WriteEndArray();
    }

    private static void WriteDigest(Utf8JsonWriter writer, string name, Sha256Digest? digest)
    {
        if (digest is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, digest.Value);
    }

    private static Sha256Digest? ParseNullableDigest(JsonElement element)
        => element.ValueKind == JsonValueKind.Null
            ? null
            : element.ValueKind == JsonValueKind.String
                ? Sha256Digest.Parse(element.GetString()!)
                : throw new OwnershipDocumentException("A recovery-retention digest isn't a string or null.");

    private static Guid[] ParseIds(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array)
            throw new OwnershipDocumentException($"Recovery-retention property '{name}' isn't an array.");
        return value.EnumerateArray().Select(element =>
        {
            if (element.ValueKind != JsonValueKind.String || !Guid.TryParseExact(element.GetString(), "N", out Guid id))
                throw new OwnershipDocumentException($"Recovery-retention property '{name}' contains a non-canonical ID.");
            return id;
        }).ToArray();
    }

    private static Guid ParseGuid(JsonElement root, string name)
    {
        string value = GetString(root, name);
        if (!Guid.TryParseExact(value, "N", out Guid id))
            throw new OwnershipDocumentException($"Recovery-retention property '{name}' isn't a canonical ID.");
        return id;
    }

    private static string GetString(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new OwnershipDocumentException($"Recovery-retention property '{name}' isn't a non-empty string.");
        return value.GetString()!;
    }

    private static void AssertExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new OwnershipDocumentException("The recovery-retention root isn't an object.");
        HashSet<string> remaining = new(expected, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
                throw new OwnershipDocumentException("The recovery-retention record contains an unknown or duplicate property.");
        }
        if (remaining.Count != 0)
            throw new OwnershipDocumentException("The recovery-retention record is missing a required property.");
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

    internal void ReplacePointerAtomically(
        InstallerOperationLease lease,
        string pendingRelativePath,
        LinuxFileIdentity pendingIdentity
    )
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (this.PointerIdentity is null)
            throw new OwnershipDocumentException("There is no current recovery pointer to replace.");
        this.AssertUsable(lease);
        try
        {
            lease.Game.ReplaceFileAtomically(
                pendingRelativePath,
                TransactionPlan.CoreRecoveryPointerRelativePath,
                pendingIdentity,
                this.PointerIdentity
            );
        }
        catch (IOException exception)
        {
            throw new OwnershipDocumentException("The recovery-pointer publication path changed before its atomic visibility boundary.", exception);
        }
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
    bool IsUserCheckpoint,
    InstallationReleaseIdentity? RestoreRelease
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

/// <summary>An opaque exact recovery-pruning decision which must be revalidated after user confirmation.</summary>
public sealed class RecoveryPrunePlan
{
    public GameRootIdentity GameRoot { get; }
    public ulong OperationGeneration { get; }
    public Sha256Digest HeadPointerSha256 { get; }
    public int RetainNewest { get; }
    public IReadOnlyList<Guid> OrderedCatalogGenerationIds { get; }
    public IReadOnlyList<Guid> RetainedGenerationIds { get; }
    public IReadOnlyList<Guid> RemovedGenerationIds { get; }
    public IReadOnlyList<Guid> CleanupGenerationIds { get; }
    public Sha256Digest ConfirmationDigest { get; }
    internal IReadOnlyList<Sha256Digest> RetentionDocumentCatalog { get; }
    internal Sha256Digest? PendingPointerSha256 { get; }

    internal RecoveryPrunePlan(
        GameRootIdentity gameRoot,
        ulong operationGeneration,
        Sha256Digest headPointerSha256,
        int retainNewest,
        IEnumerable<Guid> orderedCatalogGenerationIds,
        IEnumerable<Guid> retainedGenerationIds,
        IEnumerable<Guid> removedGenerationIds,
        IEnumerable<Guid> cleanupGenerationIds,
        IEnumerable<Sha256Digest> retentionDocumentCatalog,
        Sha256Digest? pendingPointerSha256
    )
    {
        ArgumentNullException.ThrowIfNull(gameRoot);
        ArgumentNullException.ThrowIfNull(headPointerSha256);
        if (retainNewest < 1)
            throw new ArgumentOutOfRangeException(nameof(retainNewest));
        Guid[] catalog = orderedCatalogGenerationIds.ToArray();
        Guid[] retained = retainedGenerationIds.ToArray();
        Guid[] removed = removedGenerationIds.ToArray();
        Guid[] cleanup = cleanupGenerationIds.ToArray();
        Sha256Digest[] retentionDocuments = retentionDocumentCatalog.ToArray();
        if (
            catalog.Length is <= 0 or > CommittedRecoveryHandle.MaximumRecoveryChainDepth
            || retained.Length != Math.Min(retainNewest, catalog.Length)
            || !catalog.Take(retained.Length).SequenceEqual(retained)
            || !catalog.Skip(retained.Length).SequenceEqual(removed)
            || retained.Intersect(removed).Any()
            || cleanup.Length < removed.Length
            || !cleanup.Take(removed.Length).SequenceEqual(removed)
            || cleanup.Any(id => !removed.Contains(id) && retained.Contains(id))
            || catalog.Concat(cleanup).Any(id => id == Guid.Empty)
            || catalog.Distinct().Count() != catalog.Length
            || removed.Distinct().Count() != removed.Length
            || cleanup.Distinct().Count() != cleanup.Length
            || retentionDocuments.Distinct().Count() != retentionDocuments.Length
        )
            throw new ArgumentException("The recovery-prune catalog and exact retained/removed IDs are inconsistent.");
        this.GameRoot = gameRoot;
        this.OperationGeneration = operationGeneration;
        this.HeadPointerSha256 = headPointerSha256;
        this.RetainNewest = retainNewest;
        this.OrderedCatalogGenerationIds = Array.AsReadOnly(catalog);
        this.RetainedGenerationIds = Array.AsReadOnly(retained);
        this.RemovedGenerationIds = Array.AsReadOnly(removed);
        this.CleanupGenerationIds = Array.AsReadOnly(cleanup);
        this.RetentionDocumentCatalog = Array.AsReadOnly(retentionDocuments);
        this.PendingPointerSha256 = pendingPointerSha256;
        this.ConfirmationDigest = Sha256Digest.Hash(this.GetCanonicalBytes());
    }

    private byte[] GetCanonicalBytes()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.Default,
            Indented = false,
            SkipValidation = false
        }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("game_root");
            writer.WriteString("canonical_path", this.GameRoot.CanonicalPath);
            writer.WriteNumber("device_major", this.GameRoot.DeviceMajor);
            writer.WriteNumber("device_minor", this.GameRoot.DeviceMinor);
            writer.WriteNumber("inode", this.GameRoot.Inode);
            writer.WriteEndObject();
            writer.WriteNumber("operation_generation", this.OperationGeneration);
            writer.WriteString("head_pointer_sha256", this.HeadPointerSha256.Value);
            writer.WriteNumber("retain_newest", this.RetainNewest);
            WriteIds(writer, "ordered_catalog_generation_ids", this.OrderedCatalogGenerationIds);
            WriteIds(writer, "retained_generation_ids", this.RetainedGenerationIds);
            WriteIds(writer, "removed_generation_ids", this.RemovedGenerationIds);
            WriteIds(writer, "cleanup_generation_ids", this.CleanupGenerationIds);
            writer.WriteStartArray("retention_document_catalog");
            foreach (Sha256Digest digest in this.RetentionDocumentCatalog)
                writer.WriteStringValue(digest.Value);
            writer.WriteEndArray();
            if (this.PendingPointerSha256 is null)
                writer.WriteNull("pending_pointer_sha256");
            else
                writer.WriteString("pending_pointer_sha256", this.PendingPointerSha256.Value);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteIds(Utf8JsonWriter writer, string name, IReadOnlyList<Guid> ids)
    {
        writer.WriteStartArray(name);
        foreach (Guid id in ids)
            writer.WriteStringValue(id.ToString("N"));
        writer.WriteEndArray();
    }
}

internal enum RecoveryPruneBoundary
{
    BeforeRetentionDocumentPublish,
    AfterRetentionDocumentPublish,
    BeforePointerPublish,
    AfterPointerPublish,
    BeforeGenerationCleanup,
    AfterGenerationCleanup
}

internal interface IRecoveryPruneFaultInjector
{
    void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null);
    void AfterCleanupEntryUnlink(Guid generationId, string relativeEntryPath) { }
    void BeforeCleanupDirectoryOpen(Guid generationId, string relativeDirectoryPath) { }
    void BeforePendingPointerCleanupUnlink() { }
    void BeforeRetentionDocumentCleanupUnlink(Sha256Digest digest) { }
}

internal sealed class NullRecoveryPruneFaultInjector : IRecoveryPruneFaultInjector
{
    public static NullRecoveryPruneFaultInjector Instance { get; } = new();
    public void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null) { }
}

/// <summary>An opaque descriptor-anchored authority for one committed recovery generation.</summary>
/// <remarks>The caller owns each returned handle and must dispose it after all inspections or executions which borrow it finish.</remarks>
public sealed class CommittedRecoveryHandle : IDisposable, ICommittedRecoveryContentAuthority
{
    internal const int MaximumRecoveryChainDepth = 64;
    private const int MaximumRetentionDocumentCount = 64;
    private const string LegacyRetentionPath = ".smapi-installer/recovery/retention.json";
    private const string LegacyPendingRetentionPath = ".smapi-installer/recovery/retention.pending";
    private const string PendingPointerPath = ".smapi-installer/recovery/current.pending";
    private const string RetentionDirectoryPath = ".smapi-installer/recovery/retention";
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
    /// <summary>The authenticated installed release restored by this generation, or <see langword="null"/> for an uninstalled result.</summary>
    public InstallationReleaseIdentity? RestoreRelease { get; }
    internal Sha256Digest? PreviousManifestSha256 => this.Pointer.PreviousManifestSha256;
    internal Sha256Digest? PreviousReceiptSha256 => this.Pointer.PreviousReceiptSha256;

    internal GameRootIdentity GameRoot { get; }
    internal CommittedRecoveryPointer Pointer { get; }
    GameRootIdentity ICommittedRecoveryContentAuthority.GameRoot => this.GameRoot;
    InstallationAction ICommittedRecoveryContentAuthority.OriginAction => this.Action;
    Sha256Digest? ICommittedRecoveryContentAuthority.PreviousManifestSha256 => this.PreviousManifestSha256;
    Sha256Digest? ICommittedRecoveryContentAuthority.PreviousReceiptSha256 => this.PreviousReceiptSha256;
    Sha256Digest ICommittedRecoveryContentAuthority.AuthorizedHeadPointerSha256 => this.AuthorizedHeadPointerSha256;
    InstallationReleaseIdentity? ICommittedRecoveryContentAuthority.RestoreRelease => this.RestoreRelease;

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
        Sha256Digest authorizedHeadPointerSha256,
        InstallationReleaseIdentity? restoreRelease
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
        this.RestoreRelease = restoreRelease;
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
        AnchoredCoreStateAuthority currentState,
        CancellationToken cancellationToken = default,
        ITransactionProgressSink? progress = null
    )
    {
        ITransactionProgressSink safeProgress = new NonThrowingTransactionProgressSink(progress);
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(canonicalGameRoot))
            throw new ArgumentException("A canonical game root is required.", nameof(canonicalGameRoot));
        ArgumentNullException.ThrowIfNull(gameRoot);
        ArgumentNullException.ThrowIfNull(currentState);
        currentState.AssertUsable(game, gameRoot);
        CommittedRecoveryPointer pointer = currentState.Pointer
            ?? throw new OwnershipDocumentException("There is no committed recovery generation to open.");
        return Open(
            game,
            canonicalGameRoot,
            gameRoot,
            pointer,
            currentState.PointerSha256 ?? throw new OwnershipDocumentException("The current recovery pointer digest is unavailable."),
            cancellationToken,
            safeProgress
        );
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
        Guid generationId,
        CancellationToken cancellationToken = default,
        ITransactionProgressSink? progress = null
    )
    {
        ITransactionProgressSink safeProgress = new NonThrowingTransactionProgressSink(progress);
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(canonicalGameRoot))
            throw new ArgumentException("A canonical game root is required.", nameof(canonicalGameRoot));
        ArgumentNullException.ThrowIfNull(gameRoot);
        ArgumentNullException.ThrowIfNull(currentState);
        if (generationId == Guid.Empty)
            throw new ArgumentException("A recovery generation ID is required.", nameof(generationId));
        currentState.AssertUsable(game, gameRoot);
        Sha256Digest headDigest = currentState.PointerSha256
            ?? throw new OwnershipDocumentException("The current recovery pointer digest is unavailable.");
        CommittedRecoveryPointer pointer = ReadHistoryState(game, currentState, cancellationToken).Chain
            .SingleOrDefault(item => item.GenerationId == generationId)
            ?? throw new OwnershipDocumentException("The selected recovery generation isn't present in the retained authenticated history.");
        return Open(game, canonicalGameRoot, gameRoot, pointer, headDigest, cancellationToken, safeProgress);
    }

    internal static RecoveryHistory ListHistory(
        LinuxAnchoredFileSystem game,
        string canonicalGameRoot,
        GameRootIdentity gameRoot,
        AnchoredCoreStateAuthority currentState,
        CancellationToken cancellationToken = default,
        ITransactionProgressSink? progress = null
    )
    {
        ITransactionProgressSink safeProgress = new NonThrowingTransactionProgressSink(progress);
        currentState.AssertUsable(game, gameRoot);
        Sha256Digest headDigest = currentState.PointerSha256
            ?? throw new OwnershipDocumentException("There is no committed recovery history to list.");
        IReadOnlyList<CommittedRecoveryPointer> chain = ReadHistoryState(game, currentState, cancellationToken).Chain;
        List<RecoveryGenerationInfo> items = new(chain.Count);
        safeProgress.Report(new(TransactionStage.VerifyingRecovery, 0, chain.Count));
        for (int index = 0; index < chain.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommittedRecoveryPointer pointer = chain[index];
            safeProgress.Report(new(TransactionStage.VerifyingRecovery, index, null));
            using CommittedRecoveryHandle verified = Open(game, canonicalGameRoot, gameRoot, pointer, headDigest, cancellationToken, safeProgress);
            items.Add(new RecoveryGenerationInfo(
                pointer.GenerationId,
                pointer.Action,
                index == 0,
                pointer.Action == InstallationAction.Backup,
                verified.RestoreRelease
            ));
            safeProgress.Report(new(TransactionStage.VerifyingRecovery, index + 1, chain.Count));
        }
        return new RecoveryHistory(headDigest, items);
    }

    internal static void AssertAuthenticatedHistory(
        InstallerOperationLease lease,
        AnchoredCoreStateAuthority currentState,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(currentState);
        currentState.AssertUsable(lease);
        if (currentState.Pointer is not null)
            _ = ReadHistoryState(lease.Game, currentState, cancellationToken);
    }

    internal static RecoveryCapacityState InspectCapacity(
        LinuxAnchoredFileSystem game,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(game);
        const string generationsPath = ".smapi-installer/recovery/generations";
        LinuxFileIdentity? identity = game.Stat(generationsPath);
        if (identity is null)
            return new RecoveryCapacityState(0, MaximumRecoveryChainDepth);
        if (identity.Kind != LinuxAnchoredEntryKind.Directory || identity.UnixMode != PrivateDirectoryMode)
            throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "The recovery-generation store has unsafe metadata.");
        using LinuxAnchoredFileSystem generations = game.OpenSubdirectory(generationsPath);
        IReadOnlyList<string> names;
        try
        {
            names = generations.EnumerateEntryNames(maximumEntries: MaximumRecoveryChainDepth);
        }
        catch (IOException exception)
        {
            throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "The bounded recovery-generation store is full or unsafe.", exception);
        }
        foreach (string name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(name, "N", out Guid generationId) || generationId == Guid.Empty)
                throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "The recovery-generation store contains an unknown entry.");
            LinuxFileIdentity? generation = generations.Stat(name);
            if (generation?.Kind != LinuxAnchoredEntryKind.Directory || generation.UnixMode != PrivateDirectoryMode)
                throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "The recovery-generation store contains an unsafe entry.");
        }
        return new RecoveryCapacityState(names.Count, MaximumRecoveryChainDepth);
    }

    internal static RecoveryPrunePlan CreatePrunePlan(
        LinuxAnchoredFileSystem game,
        GameRootIdentity gameRoot,
        ulong operationGeneration,
        AnchoredCoreStateAuthority currentState,
        int retainNewest,
        CancellationToken cancellationToken = default,
        ITransactionProgressSink? progress = null
    )
    {
        ITransactionProgressSink safeProgress = new NonThrowingTransactionProgressSink(progress);
        if (retainNewest < 1 || retainNewest > MaximumRecoveryChainDepth)
            throw new ArgumentOutOfRangeException(nameof(retainNewest), $"Retention must be between 1 and {MaximumRecoveryChainDepth} generations.");
        currentState.AssertUsable(game, gameRoot);
        Sha256Digest headDigest = currentState.PointerSha256
            ?? throw new OwnershipDocumentException("There is no committed recovery history to prune.");
        RecoveryHistoryState state = ReadHistoryState(game, currentState, cancellationToken);
        Guid[] catalog = state.Chain.Select(pointer => pointer.GenerationId).ToArray();
        int retainedCount = Math.Min(retainNewest, catalog.Length);
        Guid[] retained = catalog.Take(retainedCount).ToArray();
        Guid[] newlyRemoved = catalog.Skip(retainedCount).ToArray();
        HashSet<Guid> physical = ReadPhysicalGenerationIds(game, cancellationToken);
        safeProgress.Report(new(TransactionStage.VerifyingRecovery, 0, state.Chain.Count));
        int verifiedCount = 0;
        foreach (CommittedRecoveryPointer pointer in state.Chain)
        {
            cancellationToken.ThrowIfCancellationRequested();
            safeProgress.Report(new(TransactionStage.VerifyingRecovery, verifiedCount, null));
            using CommittedRecoveryHandle verified = Open(
                game,
                gameRoot.CanonicalPath,
                gameRoot,
                pointer,
                headDigest,
                cancellationToken,
                safeProgress
            );
            verifiedCount++;
            safeProgress.Report(new(TransactionStage.VerifyingRecovery, verifiedCount, state.Chain.Count));
        }
        if (state.Retention is not null)
            VerifyPendingRemovedGenerations(game, state, physical, cancellationToken);
        HashSet<Guid> visible = catalog.ToHashSet();
        HashSet<Guid> permittedRemoved = state.Retention?.RemovedGenerationIds.ToHashSet()
            ?? new HashSet<Guid>();
        Guid[] unknown = physical.Where(id => !visible.Contains(id) && !permittedRemoved.Contains(id)).ToArray();
        if (unknown.Length > 0)
            throw new OwnershipDocumentException("The recovery-generation store contains state outside the authenticated history.");
        Guid[] pendingRemoved = state.Retention?.RemovedGenerationIds
            .Where(id => physical.Contains(id) && !newlyRemoved.Contains(id))
            .ToArray() ?? [];
        Guid[] cleanup = newlyRemoved.Concat(pendingRemoved).ToArray();
        return new RecoveryPrunePlan(
            gameRoot,
            operationGeneration,
            headDigest,
            retainNewest,
            catalog,
            retained,
            newlyRemoved,
            cleanup,
            state.RetentionDocumentCatalog,
            state.PendingPointerSha256
        );
    }

    private static void VerifyPendingRemovedGenerations(
        LinuxAnchoredFileSystem game,
        RecoveryHistoryState state,
        IReadOnlySet<Guid> physical,
        CancellationToken cancellationToken
    )
    {
        RecoveryRetentionRecord retention = state.Retention
            ?? throw new ArgumentException("A retention record is required.", nameof(state));
        bool encounteredMissing = false;
        for (int index = 0; index < retention.RemovedGenerationIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid expectedId = retention.RemovedGenerationIds[index];
            if (!physical.Contains(expectedId))
            {
                encounteredMissing = true;
                continue;
            }
            if (encounteredMissing)
                throw new OwnershipDocumentException("The recovery-retention physical cleanup has a non-contiguous gap.");
            AssertSafePartialCleanupGeneration(game, expectedId, cancellationToken);
        }
    }

    internal static int ExecutePrunePlan(
        InstallerOperationLease lease,
        AnchoredCoreStateAuthority currentState,
        RecoveryPrunePlan plan,
        IRecoveryPruneFaultInjector? faultInjector = null,
        CancellationToken cancellationToken = default,
        ITransactionProgressSink? progress = null
    )
    {
        RecoveryPruneAttempt attempt = ExecutePrunePlanAttempt(lease, currentState, plan, faultInjector, cancellationToken, progress, observeCleanupCancellation: false);
        if (attempt.Failure is not null)
            ExceptionDispatchInfo.Capture(attempt.Failure).Throw();
        return attempt.Outcome.PhysicallyCleanedGenerationIds.Count;
    }

    internal static RecoveryPruneOutcome ExecutePrunePlanWithOutcome(
        InstallerOperationLease lease,
        AnchoredCoreStateAuthority currentState,
        RecoveryPrunePlan plan,
        IRecoveryPruneFaultInjector? faultInjector = null,
        CancellationToken cancellationToken = default,
        ITransactionProgressSink? progress = null
    )
        => ExecutePrunePlanAttempt(lease, currentState, plan, faultInjector, cancellationToken, progress, observeCleanupCancellation: true).Outcome;

    private static RecoveryPruneAttempt ExecutePrunePlanAttempt(
        InstallerOperationLease lease,
        AnchoredCoreStateAuthority currentState,
        RecoveryPrunePlan plan,
        IRecoveryPruneFaultInjector? faultInjector,
        CancellationToken cancellationToken,
        ITransactionProgressSink? progress,
        bool observeCleanupCancellation
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ITransactionProgressSink safeProgress = new NonThrowingTransactionProgressSink(progress);
        faultInjector ??= NullRecoveryPruneFaultInjector.Instance;
        List<Guid> physicallyCleaned = new();
        bool logicalStatePublished = false;
        bool auxiliaryCleanupPending = false;
        try
        {
            lease.AssertRootAndGeneration(plan.GameRoot, plan.OperationGeneration);
            currentState.AssertUsable(lease);
            RecoveryPrunePlan exact = CreatePrunePlan(
                lease.Game,
                lease.RootIdentity,
                lease.Generation,
                currentState,
                plan.RetainNewest,
                cancellationToken,
                safeProgress
            );
            if (exact.ConfirmationDigest != plan.ConfirmationDigest)
                throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The exact recovery history changed after prune inspection.");
            RecoveryHistoryState state = ReadHistoryState(lease.Game, currentState, cancellationToken);
            int logicalRemovalCount = plan.RemovedGenerationIds.Count;
            RecoveryRetentionRecord? publication = null;
            byte[]? publicationBytes = null;
            Sha256Digest? publicationSha256 = null;
            CommittedRecoveryPointer? publishedPointer = null;
            byte[]? publishedPointerBytes = null;
            if (logicalRemovalCount > 0)
            {
                CommittedRecoveryPointer cutoff = state.Chain[plan.RetainedGenerationIds.Count - 1];
                CommittedRecoveryPointer truncated = state.Chain[plan.RetainedGenerationIds.Count];
                publication = new RecoveryRetentionRecord(
                    plan.HeadPointerSha256,
                    currentState.Pointer!.SchemaVersion,
                    currentState.Pointer.RetentionSha256,
                    cutoff.GenerationId,
                    GetPointerDigest(cutoff),
                    truncated.GenerationId,
                    GetPointerDigest(truncated),
                    plan.RetainedGenerationIds.ToArray(),
                    plan.CleanupGenerationIds.ToArray()
                );
                publicationBytes = CanonicalRecoveryRetentionDocument.Serialize(publication);
                publicationSha256 = Sha256Digest.Hash(publicationBytes);
                publishedPointer = currentState.Pointer.WithRetention(publicationSha256);
                publishedPointerBytes = CanonicalRecoveryPointerDocument.Serialize(publishedPointer);
            }

            Sha256Digest? currentRetentionSha256 = currentState.Pointer?.RetentionSha256;
            Sha256Digest[] orphanDocuments = state.RetentionDocumentCatalog
                .Where(digest => digest != currentRetentionSha256)
                .ToArray();
            if (plan.CleanupGenerationIds.Count == 0 && orphanDocuments.Length == 0)
                throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "The recovery-prune plan has no retained history or physical cleanup change to apply.");
            cancellationToken.ThrowIfCancellationRequested();
            lease.ReserveNextGeneration(lease.Generation);
            auxiliaryCleanupPending = plan.PendingPointerSha256 is not null || orphanDocuments.Length > 0;
            RemovePendingPointer(lease.Game, plan.PendingPointerSha256, faultInjector);
            currentState.AssertUsable(lease);
            DeleteRetentionDocuments(lease.Game, orphanDocuments, currentRetentionSha256, faultInjector);
            auxiliaryCleanupPending = false;
            if (publication is not null)
            {
                auxiliaryCleanupPending = true;
                PublishRetentionDocument(lease.Game, publicationSha256!, publicationBytes!, faultInjector);
                LinuxFileIdentity pendingPointerIdentity = StagePendingPointer(lease.Game, publishedPointerBytes!);
                faultInjector.AtBoundary(RecoveryPruneBoundary.BeforePointerPublish);
                currentState.ReplacePointerAtomically(lease, PendingPointerPath, pendingPointerIdentity);
                logicalStatePublished = true;
                auxiliaryCleanupPending = currentRetentionSha256 is not null;
                faultInjector.AtBoundary(RecoveryPruneBoundary.AfterPointerPublish);
                currentState = AnchoredCoreStateAuthority.Inspect(lease);
                state = ReadHistoryState(lease.Game, currentState);
                if (
                    state.Retention is null
                    || state.RetentionSha256 != publicationSha256
                    || currentState.Pointer?.RetentionSha256 != publicationSha256
                    || currentState.Pointer != publishedPointer
                )
                    throw new OwnershipDocumentException("The published recovery-retention boundary failed exact verification.");
            }

            foreach (Guid generationId in plan.CleanupGenerationIds.Reverse())
            {
                if (observeCleanupCancellation)
                    cancellationToken.ThrowIfCancellationRequested();
                safeProgress.Report(new(TransactionStage.CleaningRecovery, physicallyCleaned.Count, plan.CleanupGenerationIds.Count));
                faultInjector.AtBoundary(RecoveryPruneBoundary.BeforeGenerationCleanup, generationId);
                if (DeleteGenerationIfPresent(lease.Game, generationId, faultInjector))
                    physicallyCleaned.Add(generationId);
                faultInjector.AtBoundary(RecoveryPruneBoundary.AfterGenerationCleanup, generationId);
            }
            Sha256Digest? retainedDocument = currentState.Pointer?.RetentionSha256;
            DeleteRetentionDocuments(
                lease.Game,
                ReadRetentionDocumentCatalog(lease.Game).Where(digest => digest != retainedDocument),
                retainedDocument,
                faultInjector
            );
            auxiliaryCleanupPending = false;
            Guid[] logical = logicalStatePublished ? plan.RemovedGenerationIds.ToArray() : Array.Empty<Guid>();
            Guid[] pending = plan.CleanupGenerationIds.Except(physicallyCleaned).ToArray();
            return new(new(RecoveryPruneOutcomeStatus.Succeeded, logical, physicallyCleaned.ToArray(), pending, false, null, null), null);
        }
        catch (Exception exception)
        {
            Guid[] logical = logicalStatePublished ? plan.RemovedGenerationIds.ToArray() : Array.Empty<Guid>();
            HashSet<Guid> newlyRemoved = plan.RemovedGenerationIds.ToHashSet();
            Guid[] pending = plan.CleanupGenerationIds
                .Where(generationId =>
                    !physicallyCleaned.Contains(generationId)
                    && (logicalStatePublished || !newlyRemoved.Contains(generationId))
                )
                .ToArray();
            RecoveryPruneOutcomeStatus status = exception switch
            {
                OperationCanceledException when logicalStatePublished || physicallyCleaned.Count > 0 => RecoveryPruneOutcomeStatus.CancelledWithCleanupPending,
                OperationCanceledException => RecoveryPruneOutcomeStatus.CancelledBeforePublication,
                SimulatedProcessTerminationException => RecoveryPruneOutcomeStatus.Interrupted,
                _ when logicalStatePublished || physicallyCleaned.Count > 0 || pending.Length > 0 || auxiliaryCleanupPending => RecoveryPruneOutcomeStatus.FailedWithCleanupPending,
                _ => RecoveryPruneOutcomeStatus.FailedBeforePublication
            };
            TransactionErrorCode? code = exception is OperationCanceledException
                ? null
                : InstallerTransactionExecutor.GetErrorCode(exception);
            string? message = exception is OperationCanceledException
                ? "Recovery cleanup was cancelled at a safe boundary."
                : InstallerTransactionExecutor.SafeMessage(code);
            return new(new(status, logical, physicallyCleaned.ToArray(), pending, auxiliaryCleanupPending, code, message), exception);
        }
    }

    private static RecoveryHistoryState ReadHistoryState(
        LinuxAnchoredFileSystem game,
        AnchoredCoreStateAuthority currentState,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertNoLegacyRetentionState(game);
        CommittedRecoveryPointer pointer = currentState.Pointer
            ?? throw new OwnershipDocumentException("There is no committed recovery history.");
        IReadOnlyList<Sha256Digest> retentionDocuments = ReadRetentionDocumentCatalog(game, cancellationToken);
        RecoveryRetentionRecord? retention = null;
        Sha256Digest? retentionSha256 = pointer.RetentionSha256;
        if (retentionSha256 is not null)
        {
            if (!retentionDocuments.Contains(retentionSha256))
                throw new OwnershipDocumentException("The current recovery pointer references a missing retention document.");
            retention = ReadRetentionDocument(game, retentionSha256, cancellationToken);
        }
        Sha256Digest? pendingPointerSha256 = ReadPendingPointerSha256(game, cancellationToken);
        List<CommittedRecoveryPointer> chain = new();
        HashSet<Guid> visited = new();
        for (int depth = 0; depth < MaximumRecoveryChainDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(pointer.GenerationId))
                throw new OwnershipDocumentException("The committed recovery chain contains a cycle.");
            string generationPath = $".smapi-installer/recovery/generations/{pointer.GenerationId:N}";
            if (game.Stat(generationPath) is null)
                throw new OwnershipDocumentException("A retained committed recovery generation is missing.");
            chain.Add(pointer);
            if (retention is not null && pointer.GenerationId == retention.CutoffGenerationId)
            {
                int publicationHeadIndex = chain.FindIndex(item =>
                    item.RetentionSha256 == retentionSha256
                    && item.SchemaVersion == CommittedRecoveryPointer.CurrentSchemaVersion
                    && GetPointerDigest(item.WithRetention(
                        retention.PreviousRetentionSha256,
                        retention.PublicationHeadPointerSchemaVersion
                    )) == retention.PublicationHeadPointerSha256
                );
                Sha256Digest cutoffDigest = publicationHeadIndex == chain.Count - 1
                    ? retention.PublicationHeadPointerSha256
                    : GetPointerDigest(pointer);
                if (
                    publicationHeadIndex < 0
                    || cutoffDigest != retention.CutoffPointerSha256
                    || pointer.PreviousGenerationId != retention.TruncatedGenerationId
                    || pointer.PreviousPointerSha256 != retention.TruncatedPointerSha256
                    || !chain.Skip(publicationHeadIndex).Select(item => item.GenerationId)
                        .SequenceEqual(retention.PublicationRetainedGenerationIds)
                )
                    throw new OwnershipDocumentException("The recovery-retention record isn't bound to the retained pointer chain.");
                return new RecoveryHistoryState(chain, retention, retentionSha256, retentionDocuments, pendingPointerSha256);
            }
            if (pointer.PreviousGenerationId is null)
            {
                if (retention is not null)
                    throw new OwnershipDocumentException("The recovery-retention cutoff isn't reachable from the current pointer.");
                return new RecoveryHistoryState(chain, null, null, retentionDocuments, pendingPointerSha256);
            }
            pointer = ReadPreviousPointer(game, pointer, cancellationToken);
        }
        throw new OwnershipDocumentException("The committed recovery chain exceeds its depth limit.");
    }

    private static HashSet<Guid> ReadPhysicalGenerationIds(
        LinuxAnchoredFileSystem game,
        CancellationToken cancellationToken = default
    )
    {
        const string generationsPath = ".smapi-installer/recovery/generations";
        LinuxFileIdentity? identity = game.Stat(generationsPath);
        if (identity is null || identity.Kind != LinuxAnchoredEntryKind.Directory || identity.UnixMode != PrivateDirectoryMode)
            throw new OwnershipDocumentException("The recovery-generation store has unsafe metadata.");
        using LinuxAnchoredFileSystem generations = game.OpenSubdirectory(generationsPath);
        IReadOnlyList<string> names;
        try
        {
            names = generations.EnumerateEntryNames(maximumEntries: MaximumRecoveryChainDepth);
        }
        catch (IOException exception)
        {
            throw new OwnershipDocumentException("The recovery-generation store exceeds its bounded catalog.", exception);
        }
        HashSet<Guid> result = new();
        foreach (string name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(name, "N", out Guid generationId) || generationId == Guid.Empty || !result.Add(generationId))
                throw new OwnershipDocumentException("The recovery-generation store contains a non-canonical entry.");
            LinuxFileIdentity generation = generations.Stat(name)
                ?? throw new OwnershipDocumentException("A recovery generation disappeared during bounded enumeration.");
            if (generation.Kind != LinuxAnchoredEntryKind.Directory || generation.UnixMode != PrivateDirectoryMode)
                throw new OwnershipDocumentException("A recovery-generation directory has unsafe metadata.");
        }
        return result;
    }

    private static void AssertNoLegacyRetentionState(LinuxAnchoredFileSystem game)
    {
        if (game.Stat(LegacyRetentionPath) is not null || game.Stat(LegacyPendingRetentionPath) is not null)
            throw new OwnershipDocumentException("Legacy unauthenticated recovery-retention state can't be trusted or migrated automatically.");
    }

    private static IReadOnlyList<Sha256Digest> ReadRetentionDocumentCatalog(
        LinuxAnchoredFileSystem game,
        CancellationToken cancellationToken = default
    )
    {
        LinuxFileIdentity? directoryIdentity = game.Stat(RetentionDirectoryPath);
        if (directoryIdentity is null)
            return [];
        if (directoryIdentity.Kind != LinuxAnchoredEntryKind.Directory || directoryIdentity.UnixMode != PrivateDirectoryMode)
            throw new OwnershipDocumentException("The recovery-retention document store has unsafe metadata.");
        using LinuxAnchoredFileSystem directory = game.OpenSubdirectory(RetentionDirectoryPath);
        if (!directory.Identity.IsSameObject(directoryIdentity))
            throw new OwnershipDocumentException("The recovery-retention document store changed while it was opened.");
        IReadOnlyList<string> names;
        try
        {
            names = directory.EnumerateEntryNames(maximumEntries: MaximumRetentionDocumentCount);
        }
        catch (IOException exception)
        {
            throw new OwnershipDocumentException("The recovery-retention document store exceeds its bounded catalog.", exception);
        }
        List<Sha256Digest> result = new(names.Count);
        foreach (string name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name.Length != 69 || !name.EndsWith(".json", StringComparison.Ordinal))
                throw new OwnershipDocumentException("The recovery-retention store contains a non-canonical entry.");
            Sha256Digest digest;
            try
            {
                digest = Sha256Digest.Parse(name[..64]);
            }
            catch (FormatException exception)
            {
                throw new OwnershipDocumentException("The recovery-retention store contains an invalid digest name.", exception);
            }
            if (name != $"{digest.Value}.json")
                throw new OwnershipDocumentException("The recovery-retention store contains a non-canonical digest name.");
            _ = ReadRetentionDocument(game, digest, cancellationToken);
            result.Add(digest);
        }
        return result;
    }

    private static RecoveryRetentionRecord ReadRetentionDocument(
        LinuxAnchoredFileSystem game,
        Sha256Digest digest,
        CancellationToken cancellationToken = default
    )
        => ReadRetentionDocumentWithIdentity(game, digest, cancellationToken).Record;

    private static (RecoveryRetentionRecord Record, LinuxFileIdentity Identity) ReadRetentionDocumentWithIdentity(
        LinuxAnchoredFileSystem game,
        Sha256Digest digest,
        CancellationToken cancellationToken = default
    )
    {
        string path = GetRetentionDocumentPath(digest);
        LinuxFileIdentity identity = game.Stat(path)
            ?? throw new OwnershipDocumentException("A recovery-retention document is missing.");
        if (
            identity.Kind != LinuxAnchoredEntryKind.RegularFile
            || identity.LinkCount != 1
            || identity.UnixMode != PrivateFileMode
            || identity.Size is <= 0 or > CanonicalRecoveryRetentionDocument.MaximumBytes
        )
            throw new OwnershipDocumentException("A recovery-retention document has unsafe metadata.");
        using LinuxAnchoredFile file = game.OpenRegularFileForRead(path);
        if (file.Identity != identity)
            throw new OwnershipDocumentException("A recovery-retention document changed while it was opened.");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = game.ReadAllBytes(file, CanonicalRecoveryRetentionDocument.MaximumBytes);
        if (Sha256Digest.Hash(bytes) != digest)
            throw new OwnershipDocumentException("A recovery-retention document doesn't match its content-addressed name.");
        return (CanonicalRecoveryRetentionDocument.Parse(bytes), file.Identity);
    }

    private static (Sha256Digest? Sha256, LinuxFileIdentity? Identity) ReadPendingPointer(
        LinuxAnchoredFileSystem game,
        CancellationToken cancellationToken = default
    )
    {
        LinuxFileIdentity? identity = game.Stat(PendingPointerPath);
        if (identity is null)
            return (null, null);
        if (
            identity.Kind != LinuxAnchoredEntryKind.RegularFile
            || identity.LinkCount != 1
            || identity.UnixMode != PrivateFileMode
            || identity.Size > CanonicalRecoveryPointerDocument.MaximumBytes
        )
            throw new OwnershipDocumentException("The pending recovery-pointer publication has unsafe metadata.");
        using LinuxAnchoredFile file = game.OpenRegularFileForRead(PendingPointerPath);
        if (file.Identity != identity)
            throw new OwnershipDocumentException("The pending recovery-pointer publication changed while it was opened.");
        cancellationToken.ThrowIfCancellationRequested();
        Sha256Digest digest = Sha256Digest.Hash(game.ReadAllBytes(file, CanonicalRecoveryPointerDocument.MaximumBytes));
        return (digest, file.Identity);
    }

    private static Sha256Digest? ReadPendingPointerSha256(
        LinuxAnchoredFileSystem game,
        CancellationToken cancellationToken = default
    )
        => ReadPendingPointer(game, cancellationToken).Sha256;

    private static void RemovePendingPointer(
        LinuxAnchoredFileSystem game,
        Sha256Digest? expectedSha256,
        IRecoveryPruneFaultInjector faultInjector
    )
    {
        (Sha256Digest? currentSha256, LinuxFileIdentity? identity) = ReadPendingPointer(game);
        if (currentSha256 != expectedSha256)
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The pending recovery-pointer publication changed after inspection.");
        if (currentSha256 is null)
            return;
        faultInjector.BeforePendingPointerCleanupUnlink();
        try
        {
            game.UnlinkFile(PendingPointerPath, identity!);
        }
        catch (IOException exception)
        {
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The pending recovery-pointer publication changed before cleanup.", exception);
        }
    }

    private static void PublishRetentionDocument(
        LinuxAnchoredFileSystem game,
        Sha256Digest digest,
        byte[] bytes,
        IRecoveryPruneFaultInjector faultInjector
    )
    {
        faultInjector.AtBoundary(RecoveryPruneBoundary.BeforeRetentionDocumentPublish);
        game.EnsureDirectory(RetentionDirectoryPath, PrivateDirectoryMode);
        string path = GetRetentionDocumentPath(digest);
        LinuxFileIdentity? existing = game.Stat(path);
        if (existing is null)
        {
            using LinuxAnchoredFile file = game.CreateNewFile(path, PrivateFileMode);
            game.AppendAndFsync(file, path, bytes, 0, bytes.Length);
        }
        RecoveryRetentionRecord published = ReadRetentionDocument(game, digest);
        if (!CanonicalRecoveryRetentionDocument.Serialize(published).AsSpan().SequenceEqual(bytes))
            throw new OwnershipDocumentException("An existing recovery-retention document doesn't match the exact publication.");
        faultInjector.AtBoundary(RecoveryPruneBoundary.AfterRetentionDocumentPublish);
    }

    private static LinuxFileIdentity StagePendingPointer(LinuxAnchoredFileSystem game, byte[] bytes)
    {
        using LinuxAnchoredFile pending = game.CreateNewFile(PendingPointerPath, PrivateFileMode);
        game.AppendAndFsync(pending, PendingPointerPath, bytes, 0, bytes.Length);
        LinuxFileIdentity identity = game.Stat(PendingPointerPath)
            ?? throw new OwnershipDocumentException("The pending recovery-pointer publication disappeared.");
        if (
            !identity.IsSameObject(pending.Identity)
            || identity.Kind != LinuxAnchoredEntryKind.RegularFile
            || identity.LinkCount != 1
            || identity.UnixMode != PrivateFileMode
            || identity.Size != bytes.LongLength
        )
            throw new OwnershipDocumentException("The pending recovery-pointer publication path changed before publication.");
        return identity;
    }

    private static void DeleteRetentionDocuments(
        LinuxAnchoredFileSystem game,
        IEnumerable<Sha256Digest> digests,
        Sha256Digest? preservedDigest,
        IRecoveryPruneFaultInjector faultInjector
    )
    {
        foreach (Sha256Digest digest in digests.Distinct())
        {
            if (digest == preservedDigest)
                throw new OwnershipDocumentException("The active authenticated recovery-retention document can't be garbage-collected.");
            (_, LinuxFileIdentity identity) = ReadRetentionDocumentWithIdentity(game, digest);
            string path = GetRetentionDocumentPath(digest);
            faultInjector.BeforeRetentionDocumentCleanupUnlink(digest);
            try
            {
                game.UnlinkFile(path, identity);
            }
            catch (IOException exception)
            {
                throw new OwnershipDocumentException("A recovery-retention document changed before garbage collection.", exception);
            }
        }
    }

    private static string GetRetentionDocumentPath(Sha256Digest digest)
        => $"{RetentionDirectoryPath}/{digest.Value}.json";

    private static Sha256Digest GetPointerDigest(CommittedRecoveryPointer pointer)
        => Sha256Digest.Hash(CanonicalRecoveryPointerDocument.Serialize(pointer));

    private sealed record RecoveryHistoryState(
        IReadOnlyList<CommittedRecoveryPointer> Chain,
        RecoveryRetentionRecord? Retention,
        Sha256Digest? RetentionSha256,
        IReadOnlyList<Sha256Digest> RetentionDocumentCatalog,
        Sha256Digest? PendingPointerSha256
    );

    private static bool DeleteGenerationIfPresent(
        LinuxAnchoredFileSystem game,
        Guid generationId,
        IRecoveryPruneFaultInjector faultInjector
    )
    {
        string path = $".smapi-installer/recovery/generations/{generationId:N}";
        LinuxFileIdentity? generationIdentity = game.Stat(path);
        if (generationIdentity is null)
            return false;
        faultInjector.BeforeCleanupDirectoryOpen(generationId, ".");
        using (LinuxAnchoredFileSystem generation = game.OpenSubdirectory(path))
        {
            if (!generation.Identity.IsSameObject(generationIdentity) || generation.Identity.UnixMode != PrivateDirectoryMode)
                throw new OwnershipDocumentException("The recovery-generation directory changed before cleanup.");
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
                faultInjector.BeforeCleanupDirectoryOpen(generationId, "files");
                using (LinuxAnchoredFileSystem files = generation.OpenSubdirectory("files"))
                {
                    if (!files.Identity.IsSameObject(filesIdentity) || files.Identity.UnixMode != PrivateDirectoryMode)
                        throw new OwnershipDocumentException("The recovery-content directory changed before cleanup.");
                    IReadOnlyList<string> names = files.EnumerateEntryNames(maximumEntries: TransactionPlan.MaximumOperationCount);
                    for (int index = 0; index < names.Count; index++)
                    {
                        string name = names[index];
                        if (!IsCanonicalContentIndex(name))
                            throw new OwnershipDocumentException("Recovery content indices aren't canonical during pruning.");
                        LinuxFileIdentity identity = files.Stat(name)
                            ?? throw new OwnershipDocumentException("Recovery content disappeared during pruning.");
                        if (identity.Kind != LinuxAnchoredEntryKind.RegularFile || identity.LinkCount != 1 || identity.UnixMode != PrivateFileMode)
                            throw new OwnershipDocumentException("Recovery content has unsafe metadata and wasn't pruned.");
                        files.UnlinkFile(name, identity);
                        faultInjector.AfterCleanupEntryUnlink(generationId, $"files/{name}");
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
                faultInjector.AfterCleanupEntryUnlink(generationId, name);
            }
        }
        LinuxFileIdentity emptiedGeneration = game.Stat(path)
            ?? throw new OwnershipDocumentException("The recovery generation disappeared during pruning.");
        if (!emptiedGeneration.IsSameObject(generationIdentity))
            throw new OwnershipDocumentException("The recovery generation changed during pruning.");
        game.RemoveEmptyDirectory(path, emptiedGeneration);
        return true;
    }

    private static void AssertSafePartialCleanupGeneration(
        LinuxAnchoredFileSystem game,
        Guid generationId,
        CancellationToken cancellationToken
    )
    {
        string path = $".smapi-installer/recovery/generations/{generationId:N}";
        using LinuxAnchoredFileSystem generation = game.OpenSubdirectory(path);
        if (generation.Identity.UnixMode != PrivateDirectoryMode)
            throw new OwnershipDocumentException("A partially cleaned recovery-generation directory isn't private.");
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
            throw new OwnershipDocumentException("A partially cleaned recovery generation contains unknown state.");
        foreach (string name in rootEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinuxFileIdentity identity = generation.Stat(name)
                ?? throw new OwnershipDocumentException("A partially cleaned recovery entry disappeared during inspection.");
            if (name == "files")
            {
                if (identity.Kind != LinuxAnchoredEntryKind.Directory || identity.UnixMode != PrivateDirectoryMode)
                    throw new OwnershipDocumentException("A partially cleaned recovery-content directory is unsafe.");
                using LinuxAnchoredFileSystem files = generation.OpenSubdirectory(name);
                foreach (string contentName in files.EnumerateEntryNames(maximumEntries: TransactionPlan.MaximumOperationCount))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCanonicalContentIndex(contentName))
                        throw new OwnershipDocumentException("A partially cleaned recovery content index isn't canonical.");
                    LinuxFileIdentity content = files.Stat(contentName)
                        ?? throw new OwnershipDocumentException("Partially cleaned recovery content disappeared during inspection.");
                    if (content.Kind != LinuxAnchoredEntryKind.RegularFile || content.LinkCount != 1 || content.UnixMode != PrivateFileMode)
                        throw new OwnershipDocumentException("Partially cleaned recovery content has unsafe metadata.");
                }
            }
            else if (identity.Kind != LinuxAnchoredEntryKind.RegularFile || identity.LinkCount != 1 || identity.UnixMode != PrivateFileMode)
                throw new OwnershipDocumentException("A partially cleaned recovery document has unsafe metadata.");
        }
    }

    private static bool IsCanonicalContentIndex(string name)
        => name.Length == 8
            && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && index >= 0
            && index < TransactionPlan.MaximumOperationCount
            && name == index.ToString("D8", CultureInfo.InvariantCulture);

    private static CommittedRecoveryHandle Open(
        LinuxAnchoredFileSystem game,
        string canonicalGameRoot,
        GameRootIdentity gameRoot,
        CommittedRecoveryPointer pointer,
        Sha256Digest authorizedHeadPointerSha256,
        CancellationToken cancellationToken = default,
        ITransactionProgressSink? progress = null
    )
    {
        ITransactionProgressSink safeProgress = new NonThrowingTransactionProgressSink(progress);
        string generationPath = $".smapi-installer/recovery/generations/{pointer.GenerationId:N}";
        LinuxAnchoredFileSystem namedGameRoot = new(canonicalGameRoot);
        LinuxAnchoredFileSystem? generation = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
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
            RollbackSnapshotEntry[] restoreEntries = snapshot.Entries
                .Where(entry => entry.Kind == RollbackEntryKind.Restore)
                .ToArray();
            safeProgress.Report(new(TransactionStage.VerifyingRecovery, 0, restoreEntries.Length));
            foreach (RollbackSnapshotEntry entry in restoreEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                safeProgress.Report(new(TransactionStage.VerifyingRecovery, contentIndex, null));
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
                AssertContentIdentity(generation, file, entry.Backup, cancellationToken);
                gameFiles.Add(entry.Path.Value, new RecoveryContentBinding(name, file.Identity, entry.Backup!));
                contentIndex++;
                safeProgress.Report(new(TransactionStage.VerifyingRecovery, contentIndex, restoreEntries.Length));
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
                authorizedHeadPointerSha256,
                previousReceiptModel?.Release
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
        CommittedRecoveryPointer current,
        CancellationToken cancellationToken = default
    )
    {
        if (current.PreviousGenerationId is null || current.PreviousPointerSha256 is null)
            throw new OwnershipDocumentException("The selected recovery generation isn't present in the committed chain.");
        string generationPath = $".smapi-installer/recovery/generations/{current.GenerationId:N}";
        using LinuxAnchoredFileSystem generation = game.OpenSubdirectory(generationPath);
        if (generation.Identity.UnixMode != PrivateDirectoryMode)
            throw new OwnershipDocumentException("A committed recovery-chain directory isn't private.");
        cancellationToken.ThrowIfCancellationRequested();
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
                || snapshotEntry.OwnedKind != receiptEntry.Kind
                || snapshotEntry.ExpectedCurrent != snapshotEntry.Backup
                || (
                    receiptEntry.Kind != OwnedEntryKind.GeneratedFile
                    && (
                        snapshotEntry.Backup?.Sha256 != receiptEntry.InstalledSha256
                        || snapshotEntry.Backup.UnixMode != receiptEntry.UnixMode
                    )
                )
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
        RecoveryFileIdentity expected,
        CancellationToken cancellationToken = default
    )
    {
        if (
            file.Identity.Kind != LinuxAnchoredEntryKind.RegularFile
            || file.Identity.LinkCount != 1
            || file.Identity.UnixMode != PrivateFileMode
            || file.Identity.Size != expected.SizeBytes
            || Sha256Digest.Parse(generation.ComputeSha256(file, cancellationToken)) != expected.Sha256
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
