using System.Collections.ObjectModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StardewModdingAPI.Installer.Core.Ownership;

/// <summary>One regular file the verified package intends to own at its installation destination.</summary>
public sealed class PackageManifestEntry
{
    /// <summary>The installation-relative destination.</summary>
    public NormalizedRelativePath Path { get; }

    /// <summary>The expected file digest.</summary>
    public Sha256Digest Sha256 { get; }

    /// <summary>The expected file length.</summary>
    public long SizeBytes { get; }

    /// <summary>The expected Unix permission bits.</summary>
    public int UnixMode { get; }

    /// <summary>The semantic ownership category.</summary>
    public OwnedEntryKind Kind { get; }

    /// <summary>Construct an immutable manifest entry.</summary>
    public PackageManifestEntry(NormalizedRelativePath path, Sha256Digest sha256, long sizeBytes, int unixMode, OwnedEntryKind kind)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sha256);
        if (sizeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        if (unixMode is < 0 or > 511)
            throw new ArgumentOutOfRangeException(nameof(unixMode), "Only ordinary 0000-0777 Unix permission bits are allowed.");

        OwnedNamespacePolicy.AssertAllowed(path, kind);
        this.Path = path;
        this.Sha256 = sha256;
        this.SizeBytes = sizeBytes;
        this.UnixMode = unixMode;
        this.Kind = kind;
    }
}

/// <summary>A verified package's immutable intended installation layout.</summary>
public sealed class PackageManifest
{
    /// <summary>The only currently supported manifest schema.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The manifest schema.</summary>
    public int SchemaVersion => PackageManifest.CurrentSchemaVersion;

    /// <summary>The exact release identity.</summary>
    public InstallationReleaseIdentity Release { get; }

    /// <summary>Entries sorted by canonical relative path.</summary>
    public IReadOnlyList<PackageManifestEntry> Entries { get; }

    /// <summary>Construct and validate an immutable package manifest.</summary>
    public PackageManifest(InstallationReleaseIdentity release, IEnumerable<PackageManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(entries);

        PackageManifestEntry[] ordered = entries.OrderBy(entry => entry.Path.Value, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0)
            throw new ArgumentException("A package manifest must contain at least one owned file.", nameof(entries));
        OwnershipCollectionValidation.AssertDistinctFilePaths(ordered.Select(entry => entry.Path), nameof(entries));
        if (ordered.Count(entry => entry.Kind == OwnedEntryKind.Launcher && entry.Path.Value == "StardewValley") != 1)
            throw new ArgumentException("A Linux package manifest must contain exactly one installed launcher destination.", nameof(entries));

        this.Release = release;
        this.Entries = new ReadOnlyCollection<PackageManifestEntry>(ordered);
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
            writer.WriteStartArray("entries");
            foreach (PackageManifestEntry entry in this.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("path", entry.Path.Value);
                writer.WriteString("sha256", entry.Sha256.Value);
                writer.WriteNumber("size_bytes", entry.SizeBytes);
                writer.WriteNumber("unix_mode", entry.UnixMode);
                writer.WriteString("kind", CanonicalOwnershipJson.GetKindName(entry.Kind));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Get the digest of the canonical manifest bytes.</summary>
    public Sha256Digest GetCanonicalDigest()
    {
        return Sha256Digest.Hash(Encoding.UTF8.GetBytes(this.ToCanonicalJson()));
    }
}

internal static class OwnershipCollectionValidation
{
    public static void AssertDistinctFilePaths(IEnumerable<NormalizedRelativePath> paths, string parameterName)
    {
        NormalizedRelativePath[] values = paths.ToArray();
        HashSet<string> allPaths = new(values.Select(path => path.Value), StringComparer.OrdinalIgnoreCase);
        if (allPaths.Count != values.Length)
            throw new ArgumentException("Owned paths must be unique even on case-insensitive filesystems.", parameterName);

        foreach (NormalizedRelativePath path in values)
        {
            string value = path.Value;
            for (int index = value.IndexOf('/'); index >= 0; index = value.IndexOf('/', index + 1))
            {
                if (allPaths.Contains(value.Substring(0, index)))
                    throw new ArgumentException("An owned regular file can't also be another owned path's parent.", parameterName);
            }
        }
    }
}

internal static class CanonicalOwnershipJson
{
    public static Utf8JsonWriter CreateWriter(Stream stream)
    {
        return new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.Default,
                Indented = false,
                SkipValidation = false
            }
        );
    }

    public static void WriteRelease(Utf8JsonWriter writer, InstallationReleaseIdentity release)
    {
        writer.WriteStartObject();
        writer.WriteString("repository", release.Repository);
        writer.WriteString("tag", release.Tag);
        writer.WriteString("embedded_version", release.EmbeddedVersion);
        writer.WriteString("package_asset_name", release.PackageAssetName);
        writer.WriteString("source_commit", release.SourceCommit);
        writer.WriteString("source_tree", release.SourceTree);
        writer.WriteString("package_sha256", release.PackageSha256.Value);
        writer.WriteEndObject();
    }

    public static string GetKindName(OwnedEntryKind kind)
    {
        return kind switch
        {
            OwnedEntryKind.RuntimeFile => "runtime_file",
            OwnedEntryKind.InternalFile => "internal_file",
            OwnedEntryKind.BundledModFile => "bundled_mod_file",
            OwnedEntryKind.Launcher => "launcher",
            OwnedEntryKind.GeneratedFile => "generated_file",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}
