using System.Text;
using System.Text.Json;
using StardewModdingAPI.Installer.Core.Planning;

namespace StardewModdingAPI.Installer.Core.Ownership.Persistence;

/// <summary>Strict canonical codecs and trust-boundary validation for persisted ownership state.</summary>
public static class CanonicalOwnershipDocuments
{
    private const int RollbackSchemaVersion = 1;

    public static byte[] SerializeManifest(PackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Encoding.UTF8.GetBytes(manifest.ToCanonicalJson());
    }

    public static PackageManifest ParseManifest(ReadOnlyMemory<byte> bytes, OwnershipPersistenceLimits? limits = null)
    {
        limits ??= OwnershipPersistenceLimits.Default;
        return ParseCanonical(bytes, limits, root =>
        {
            AssertExactObject(root, "manifest", "schema_version", "release", "entries");
            AssertSchema(root, PackageManifest.CurrentSchemaVersion);
            InstallationReleaseIdentity release = ParseRelease(root.GetProperty("release"));
            PackageManifestEntry[] entries = ParseArray(root.GetProperty("entries"), limits, "manifest entries", ParseManifestEntry);
            return new PackageManifest(release, entries);
        }, SerializeManifest, "package manifest");
    }

    public static byte[] SerializeReceipt(InstallationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return Encoding.UTF8.GetBytes(receipt.ToCanonicalJson());
    }

    /// <summary>Parse a receipt only in the context of the already verified manifest it claims to have installed.</summary>
    public static InstallationReceipt ParseReceipt(
        ReadOnlyMemory<byte> bytes,
        PackageManifest verifiedManifest,
        OwnershipPersistenceLimits? limits = null
    )
    {
        ArgumentNullException.ThrowIfNull(verifiedManifest);
        limits ??= OwnershipPersistenceLimits.Default;
        InstallationReceipt receipt = ParseCanonical(bytes, limits, root =>
        {
            AssertExactObject(root, "receipt", "schema_version", "release", "manifest_sha256", "transaction_id", "entries", "launcher");
            AssertSchema(root, InstallationReceipt.CurrentSchemaVersion);
            InstallationReleaseIdentity release = ParseRelease(root.GetProperty("release"));
            Sha256Digest manifestSha256 = ParseDigest(root.GetProperty("manifest_sha256"), "manifest_sha256");
            string transactionId = GetString(root.GetProperty("transaction_id"), "transaction_id");
            InstallationReceiptEntry[] entries = ParseArray(root.GetProperty("entries"), limits, "receipt entries", ParseReceiptEntry);

            JsonElement launcherElement = root.GetProperty("launcher");
            AssertExactObject(launcherElement, "launcher", "installed_sha256", "original_sha256");
            LauncherReceipt launcher = new(
                ParseDigest(launcherElement.GetProperty("installed_sha256"), "launcher.installed_sha256"),
                ParseDigest(launcherElement.GetProperty("original_sha256"), "launcher.original_sha256")
            );
            return new InstallationReceipt(release, manifestSha256, transactionId, entries, launcher);
        }, SerializeReceipt, "installation receipt");

        AssertReceiptMatchesManifest(receipt, verifiedManifest);
        return receipt;
    }

    public static byte[] SerializeRollbackSnapshot(RollbackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = CanonicalOwnershipJson.CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", RollbackSchemaVersion);
            writer.WriteString("expected_installed_receipt_sha256", snapshot.ExpectedInstalledReceiptSha256.Value);
            writer.WriteStartArray("entries");
            foreach (RollbackSnapshotEntry entry in snapshot.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("path", entry.Path.Value);
                writer.WriteString("owned_kind", CanonicalOwnershipJson.GetKindName(entry.OwnedKind));
                writer.WriteString("kind", entry.Kind switch
                {
                    RollbackEntryKind.Restore => "restore",
                    RollbackEntryKind.Remove => "remove",
                    _ => throw new ArgumentOutOfRangeException(nameof(entry.Kind))
                });
                if (entry.ExpectedCurrentSha256 is null)
                    writer.WriteNull("expected_current_sha256");
                else
                    writer.WriteString("expected_current_sha256", entry.ExpectedCurrentSha256.Value);
                if (entry.BackupSha256 is null)
                    writer.WriteNull("backup_sha256");
                else
                    writer.WriteString("backup_sha256", entry.BackupSha256.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Parse a rollback snapshot only for the exact installed receipt state it reverses.</summary>
    public static RollbackSnapshot ParseRollbackSnapshot(
        ReadOnlyMemory<byte> bytes,
        InstallationReceipt installedReceipt,
        OwnershipPersistenceLimits? limits = null
    )
    {
        ArgumentNullException.ThrowIfNull(installedReceipt);
        limits ??= OwnershipPersistenceLimits.Default;
        RollbackSnapshot snapshot = ParseCanonical(bytes, limits, root =>
        {
            AssertExactObject(root, "rollback snapshot", "schema_version", "expected_installed_receipt_sha256", "entries");
            AssertSchema(root, RollbackSchemaVersion);
            Sha256Digest receiptSha256 = ParseDigest(
                root.GetProperty("expected_installed_receipt_sha256"),
                "expected_installed_receipt_sha256"
            );
            RollbackSnapshotEntry[] entries = ParseArray(root.GetProperty("entries"), limits, "rollback entries", ParseRollbackEntry);
            return new RollbackSnapshot(receiptSha256, entries);
        }, SerializeRollbackSnapshot, "rollback snapshot");

        if (snapshot.ExpectedInstalledReceiptSha256 != installedReceipt.GetCanonicalDigest())
            throw new OwnershipDocumentException("The rollback snapshot doesn't target the supplied installed receipt.");
        return snapshot;
    }

    /// <summary>Validate a receipt before persistence, not only when it is subsequently loaded.</summary>
    public static void AssertReceiptMatchesManifest(InstallationReceipt receipt, PackageManifest verifiedManifest)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(verifiedManifest);

        if (!receipt.Release.Equals(verifiedManifest.Release))
            throw new OwnershipDocumentException("The receipt release identity doesn't match the verified manifest.");
        if (receipt.ManifestSha256 != verifiedManifest.GetCanonicalDigest())
            throw new OwnershipDocumentException("The receipt manifest digest doesn't match the verified manifest.");
        if (receipt.Entries.Count != verifiedManifest.Entries.Count)
            throw new OwnershipDocumentException("The receipt entry set doesn't exactly match the verified manifest.");

        for (int index = 0; index < verifiedManifest.Entries.Count; index++)
        {
            PackageManifestEntry expected = verifiedManifest.Entries[index];
            InstallationReceiptEntry actual = receipt.Entries[index];
            if (
                !actual.Path.Equals(expected.Path)
                || actual.InstalledSha256 != expected.Sha256
                || actual.UnixMode != expected.UnixMode
                || actual.Kind != expected.Kind
            )
            {
                throw new OwnershipDocumentException($"Receipt entry '{actual.Path}' doesn't exactly match the verified manifest.");
            }
        }

        PackageManifestEntry expectedLauncher = verifiedManifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.Launcher);
        if (receipt.Launcher.InstalledLauncherSha256 != expectedLauncher.Sha256)
            throw new OwnershipDocumentException("The receipt launcher doesn't match the verified manifest launcher.");
    }

    private static T ParseCanonical<T>(
        ReadOnlyMemory<byte> bytes,
        OwnershipPersistenceLimits limits,
        Func<JsonElement, T> construct,
        Func<T, byte[]> serialize,
        string documentName
    )
    {
        if (bytes.Length == 0)
            throw new OwnershipDocumentException($"The {documentName} is empty.");
        if (bytes.Length > limits.MaxDocumentBytes)
            throw new OwnershipDocumentException($"The {documentName} exceeds the configured byte limit.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = limits.MaxJsonDepth
                }
            );
            T result = construct(document.RootElement);
            if (!bytes.Span.SequenceEqual(serialize(result)))
                throw new OwnershipDocumentException($"The {documentName} isn't in its unique canonical byte representation.");
            return result;
        }
        catch (OwnershipDocumentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new OwnershipDocumentException($"The {documentName} is invalid.", ex);
        }
    }

    private static InstallationReleaseIdentity ParseRelease(JsonElement element)
    {
        AssertExactObject(
            element,
            "release",
            "repository",
            "tag",
            "embedded_version",
            "package_asset_name",
            "source_commit",
            "source_tree",
            "package_sha256"
        );
        return new InstallationReleaseIdentity(
            GetString(element.GetProperty("repository"), "release.repository"),
            GetString(element.GetProperty("tag"), "release.tag"),
            GetString(element.GetProperty("embedded_version"), "release.embedded_version"),
            GetString(element.GetProperty("package_asset_name"), "release.package_asset_name"),
            GetString(element.GetProperty("source_commit"), "release.source_commit"),
            GetString(element.GetProperty("source_tree"), "release.source_tree"),
            ParseDigest(element.GetProperty("package_sha256"), "release.package_sha256")
        );
    }

    private static PackageManifestEntry ParseManifestEntry(JsonElement element)
    {
        AssertExactObject(element, "manifest entry", "path", "sha256", "size_bytes", "unix_mode", "kind");
        return new PackageManifestEntry(
            ParsePath(element.GetProperty("path"), "entry.path"),
            ParseDigest(element.GetProperty("sha256"), "entry.sha256"),
            GetInt64(element.GetProperty("size_bytes"), "entry.size_bytes"),
            GetInt32(element.GetProperty("unix_mode"), "entry.unix_mode"),
            ParseOwnedKind(element.GetProperty("kind"), "entry.kind")
        );
    }

    private static InstallationReceiptEntry ParseReceiptEntry(JsonElement element)
    {
        AssertExactObject(element, "receipt entry", "path", "installed_sha256", "unix_mode", "kind");
        return new InstallationReceiptEntry(
            ParsePath(element.GetProperty("path"), "entry.path"),
            ParseDigest(element.GetProperty("installed_sha256"), "entry.installed_sha256"),
            GetInt32(element.GetProperty("unix_mode"), "entry.unix_mode"),
            ParseOwnedKind(element.GetProperty("kind"), "entry.kind")
        );
    }

    private static RollbackSnapshotEntry ParseRollbackEntry(JsonElement element)
    {
        AssertExactObject(
            element,
            "rollback entry",
            "path",
            "owned_kind",
            "kind",
            "expected_current_sha256",
            "backup_sha256"
        );
        string kind = GetString(element.GetProperty("kind"), "entry.kind");
        RollbackEntryKind rollbackKind = kind switch
        {
            "restore" => RollbackEntryKind.Restore,
            "remove" => RollbackEntryKind.Remove,
            _ => throw new OwnershipDocumentException($"Unknown rollback entry kind '{kind}'.")
        };
        return new RollbackSnapshotEntry(
            ParsePath(element.GetProperty("path"), "entry.path"),
            ParseOwnedKind(element.GetProperty("owned_kind"), "entry.owned_kind"),
            rollbackKind,
            ParseNullableDigest(element.GetProperty("expected_current_sha256"), "entry.expected_current_sha256"),
            ParseNullableDigest(element.GetProperty("backup_sha256"), "entry.backup_sha256")
        );
    }

    private static T[] ParseArray<T>(
        JsonElement element,
        OwnershipPersistenceLimits limits,
        string name,
        Func<JsonElement, T> parseItem
    )
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new OwnershipDocumentException($"The {name} value must be an array.");
        int count = element.GetArrayLength();
        if (count > limits.MaxEntries)
            throw new OwnershipDocumentException($"The {name} array exceeds the configured entry limit.");
        T[] result = new T[count];
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
            result[index++] = parseItem(item);
        return result;
    }

    private static void AssertSchema(JsonElement root, int expected)
    {
        int actual = GetInt32(root.GetProperty("schema_version"), "schema_version");
        if (actual != expected)
            throw new OwnershipDocumentException($"Unsupported ownership document schema version {actual}.");
    }

    private static void AssertExactObject(JsonElement element, string name, params string[] expectedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new OwnershipDocumentException($"The {name} must be a JSON object.");

        HashSet<string> expected = new(expectedProperties, StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
                throw new OwnershipDocumentException($"The {name} contains unknown property '{property.Name}'.");
            if (!seen.Add(property.Name))
                throw new OwnershipDocumentException($"The {name} contains duplicate property '{property.Name}'.");
        }

        foreach (string property in expectedProperties)
        {
            if (!seen.Contains(property))
                throw new OwnershipDocumentException($"The {name} is missing property '{property}'.");
        }
    }

    private static string GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new OwnershipDocumentException($"'{name}' must be a JSON string.");
        return element.GetString()!;
    }

    private static int GetInt32(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
            throw new OwnershipDocumentException($"'{name}' must be a 32-bit JSON integer.");
        return value;
    }

    private static long GetInt64(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long value))
            throw new OwnershipDocumentException($"'{name}' must be a 64-bit JSON integer.");
        return value;
    }

    private static NormalizedRelativePath ParsePath(JsonElement element, string name)
    {
        return NormalizedRelativePath.Parse(GetString(element, name));
    }

    private static Sha256Digest ParseDigest(JsonElement element, string name)
    {
        return Sha256Digest.Parse(GetString(element, name));
    }

    private static Sha256Digest? ParseNullableDigest(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Null ? null : ParseDigest(element, name);
    }

    private static OwnedEntryKind ParseOwnedKind(JsonElement element, string name)
    {
        string value = GetString(element, name);
        return value switch
        {
            "runtime_file" => OwnedEntryKind.RuntimeFile,
            "internal_file" => OwnedEntryKind.InternalFile,
            "bundled_mod_file" => OwnedEntryKind.BundledModFile,
            "launcher" => OwnedEntryKind.Launcher,
            "generated_file" => OwnedEntryKind.GeneratedFile,
            _ => throw new OwnershipDocumentException($"Unknown owned entry kind '{value}'.")
        };
    }
}
