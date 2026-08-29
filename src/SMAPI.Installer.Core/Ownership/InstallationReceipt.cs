using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace StardewModdingAPI.Installer.Core.Ownership;

/// <summary>One exact regular file recorded as installed by a completed transaction.</summary>
public sealed class InstallationReceiptEntry
{
    /// <summary>The installation-relative path.</summary>
    public NormalizedRelativePath Path { get; }

    /// <summary>The digest written by the completed transaction.</summary>
    public Sha256Digest InstalledSha256 { get; }

    /// <summary>The permission bits written by the completed transaction.</summary>
    public int UnixMode { get; }

    /// <summary>The semantic ownership category.</summary>
    public OwnedEntryKind Kind { get; }

    /// <summary>Construct an immutable receipt entry.</summary>
    internal InstallationReceiptEntry(NormalizedRelativePath path, Sha256Digest installedSha256, int unixMode, OwnedEntryKind kind)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(installedSha256);
        if (unixMode is < 0 or > 511)
            throw new ArgumentOutOfRangeException(nameof(unixMode));
        OwnedNamespacePolicy.AssertAllowed(path, kind);

        this.Path = path;
        this.InstalledSha256 = installedSha256;
        this.UnixMode = unixMode;
        this.Kind = kind;
    }
}

/// <summary>The semantic identities needed to prove whether the installed and backed-up launchers are still known.</summary>
public sealed record LauncherReceipt
{
    /// <summary>The SMAPI launcher installed at <c>StardewValley</c>.</summary>
    public Sha256Digest InstalledLauncherSha256 { get; }

    /// <summary>The original launcher moved to <c>StardewValley-original</c>.</summary>
    public Sha256Digest OriginalLauncherSha256 { get; }

    /// <summary>The expected executable mode for the installed SMAPI launcher.</summary>
    public int InstalledLauncherUnixMode { get; }

    /// <summary>The exact mode retained for the original launcher backup.</summary>
    public int OriginalLauncherUnixMode { get; }

    /// <summary>Construct an immutable launcher receipt.</summary>
    internal LauncherReceipt(
        Sha256Digest installedLauncherSha256,
        Sha256Digest originalLauncherSha256,
        int installedLauncherUnixMode = 0x1ed,
        int originalLauncherUnixMode = 0x1ed
    )
    {
        ArgumentNullException.ThrowIfNull(installedLauncherSha256);
        ArgumentNullException.ThrowIfNull(originalLauncherSha256);
        if (installedLauncherUnixMode is < 0 or > 0x1ff)
            throw new ArgumentOutOfRangeException(nameof(installedLauncherUnixMode));
        if (originalLauncherUnixMode is < 0 or > 0x1ff)
            throw new ArgumentOutOfRangeException(nameof(originalLauncherUnixMode));
        this.InstalledLauncherSha256 = installedLauncherSha256;
        this.OriginalLauncherSha256 = originalLauncherSha256;
        this.InstalledLauncherUnixMode = installedLauncherUnixMode;
        this.OriginalLauncherUnixMode = originalLauncherUnixMode;
    }
}

/// <summary>The immutable ownership receipt committed after a successful installation transaction.</summary>
public sealed class InstallationReceipt
{
    /// <summary>The current release-authority receipt schema.</summary>
    public const int CurrentSchemaVersion = 4;

    internal const int LegacySchemaVersion = 3;

    /// <summary>The receipt schema.</summary>
    public int SchemaVersion { get; }

    /// <summary>The exact installed release.</summary>
    public InstallationReleaseIdentity Release { get; }

    /// <summary>The canonical digest of the package manifest that was applied.</summary>
    public Sha256Digest ManifestSha256 { get; }

    /// <summary>The committed transaction identifier.</summary>
    public string TransactionId { get; }

    /// <summary>Installed entries sorted by canonical path.</summary>
    public IReadOnlyList<InstallationReceiptEntry> Entries { get; }

    /// <summary>The installed and original launcher hashes.</summary>
    public LauncherReceipt Launcher { get; }

    /// <summary>Curated URL-free release evidence for a schema-4 installation.</summary>
    public VerifiedTaggedPackageTrust? ReleaseTrust { get; }

    /// <summary>Construct and validate an immutable installation receipt.</summary>
    internal InstallationReceipt(
        InstallationReleaseIdentity release,
        Sha256Digest manifestSha256,
        string transactionId,
        IEnumerable<InstallationReceiptEntry> entries,
        LauncherReceipt launcher,
        VerifiedTaggedPackageTrust? releaseTrust = null,
        int? schemaVersion = null
    )
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(manifestSha256);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(launcher);
        if (!IsCanonicalTransactionId(transactionId))
            throw new ArgumentException("The transaction ID must contain 32 lowercase hexadecimal characters.", nameof(transactionId));
        int actualSchemaVersion = schemaVersion ?? (releaseTrust is null
            ? InstallationReceipt.LegacySchemaVersion
            : InstallationReceipt.CurrentSchemaVersion);
        if (actualSchemaVersion is not (InstallationReceipt.LegacySchemaVersion or InstallationReceipt.CurrentSchemaVersion))
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if ((actualSchemaVersion == InstallationReceipt.CurrentSchemaVersion) != (releaseTrust is not null))
            throw new ArgumentException("Receipt schema 4 requires exact release evidence, and legacy schema 3 forbids it.", nameof(releaseTrust));
        if (releaseTrust is not null && !releaseTrust.Identity.Equals(release))
            throw new ArgumentException("The release evidence doesn't match the receipt release.", nameof(releaseTrust));

        InstallationReceiptEntry[] ordered = entries.OrderBy(entry => entry.Path.Value, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0)
            throw new ArgumentException("An installation receipt must contain at least one owned file.", nameof(entries));
        OwnershipCollectionValidation.AssertDistinctFilePaths(ordered.Select(entry => entry.Path), nameof(entries));

        InstallationReceiptEntry[] launcherEntries = ordered.Where(entry => entry.Kind == OwnedEntryKind.Launcher).ToArray();
        if (
            launcherEntries.Length != 1
            || launcherEntries[0].Path.Value != "StardewValley"
            || launcherEntries[0].InstalledSha256 != launcher.InstalledLauncherSha256
            || launcherEntries[0].UnixMode != launcher.InstalledLauncherUnixMode
        )
        {
            throw new ArgumentException("The receipt must contain exactly one launcher matching its launcher receipt.", nameof(entries));
        }

        this.SchemaVersion = actualSchemaVersion;
        this.Release = release;
        this.ManifestSha256 = manifestSha256;
        this.TransactionId = transactionId;
        this.Entries = new ReadOnlyCollection<InstallationReceiptEntry>(ordered);
        this.Launcher = launcher;
        this.ReleaseTrust = releaseTrust;
    }

    /// <summary>Serialize in canonical property and entry order.</summary>
    public string ToCanonicalJson()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = CanonicalOwnershipJson.CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", this.SchemaVersion);
            writer.WritePropertyName("release");
            CanonicalOwnershipJson.WriteRelease(writer, this.Release);
            writer.WriteString("manifest_sha256", this.ManifestSha256.Value);
            if (this.SchemaVersion == InstallationReceipt.CurrentSchemaVersion)
            {
                writer.WritePropertyName("release_authority_evidence");
                CanonicalOwnershipJson.WriteReleaseAuthorityEvidence(writer, this.ReleaseTrust!);
            }
            writer.WriteString("transaction_id", this.TransactionId);
            writer.WriteStartArray("entries");
            foreach (InstallationReceiptEntry entry in this.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("path", entry.Path.Value);
                writer.WriteString("installed_sha256", entry.InstalledSha256.Value);
                writer.WriteNumber("unix_mode", entry.UnixMode);
                writer.WriteString("kind", CanonicalOwnershipJson.GetKindName(entry.Kind));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("launcher");
            writer.WriteString("installed_sha256", this.Launcher.InstalledLauncherSha256.Value);
            writer.WriteNumber("installed_unix_mode", this.Launcher.InstalledLauncherUnixMode);
            writer.WriteString("original_sha256", this.Launcher.OriginalLauncherSha256.Value);
            writer.WriteNumber("original_unix_mode", this.Launcher.OriginalLauncherUnixMode);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Get the digest of the canonical receipt bytes.</summary>
    public Sha256Digest GetCanonicalDigest()
    {
        return Sha256Digest.Hash(Encoding.UTF8.GetBytes(this.ToCanonicalJson()));
    }

    private static bool IsCanonicalTransactionId(string value)
    {
        return value is { Length: 32 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
