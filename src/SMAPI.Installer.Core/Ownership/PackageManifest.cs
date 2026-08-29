using System.Collections.ObjectModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StardewModdingAPI.Installer.Core.Planning;

namespace StardewModdingAPI.Installer.Core.Ownership;

/// <summary>The only game-derived file recipe accepted by the Linux installer core.</summary>
public sealed class GeneratedFileRecipe
{
    internal const string CopyGameDepsRecipe = "copy_game_deps_v1";
    private static readonly NormalizedRelativePath ExpectedPath = NormalizedRelativePath.Parse("StardewModdingAPI-net6.deps.json");
    private static readonly NormalizedRelativePath ExpectedSourcePath = NormalizedRelativePath.Parse("Stardew Valley.deps.json");

    /// <summary>The generated installation destination.</summary>
    public NormalizedRelativePath Path { get; }

    /// <summary>The fixed core-recognized recipe identifier.</summary>
    public string Recipe { get; }

    /// <summary>The fixed game-relative input selected by the recipe, never by a frontend.</summary>
    public NormalizedRelativePath SourcePath { get; }

    /// <summary>The exact source identity bound during inspection, or <see langword="null"/> in a release template.</summary>
    public RecoveryFileIdentity? SourceIdentity { get; }

    internal GeneratedFileRecipe(
        NormalizedRelativePath path,
        string recipe,
        NormalizedRelativePath sourcePath,
        RecoveryFileIdentity? sourceIdentity = null
    )
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(recipe))
            throw new ArgumentException("The generated-file recipe identifier is required.", nameof(recipe));
        ArgumentNullException.ThrowIfNull(sourcePath);
        if (!path.Equals(ExpectedPath) || recipe != CopyGameDepsRecipe || !sourcePath.Equals(ExpectedSourcePath))
            throw new ArgumentException("The generated-file recipe isn't one of the exact core-owned Linux recipes.");
        if (sourceIdentity is not null && sourceIdentity.FileType != RecoveryFileType.RegularFile)
            throw new ArgumentException("A generated-file recipe source must be a regular file.", nameof(sourceIdentity));

        this.Path = path;
        this.Recipe = recipe;
        this.SourcePath = sourcePath;
        this.SourceIdentity = sourceIdentity;
    }

    internal GeneratedFileRecipe Resolve(RecoveryFileIdentity identity)
        => new(this.Path, this.Recipe, this.SourcePath, identity);

    /// <summary>Create the unresolved release template for the one supported game-derived file.</summary>
    internal static GeneratedFileRecipe CreateCopyGameDepsTemplate()
        => new(ExpectedPath, CopyGameDepsRecipe, ExpectedSourcePath);
}

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
    internal PackageManifestEntry(NormalizedRelativePath path, Sha256Digest sha256, long sizeBytes, int unixMode, OwnedEntryKind kind)
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
    /// <summary>The current release-authority manifest schema.</summary>
    public const int CurrentSchemaVersion = 4;

    internal const int LegacySchemaVersion = 2;
    internal const int GeneratedFilesSchemaVersion = 3;

    /// <summary>The manifest schema.</summary>
    public int SchemaVersion { get; }

    /// <summary>The exact release identity.</summary>
    public InstallationReleaseIdentity Release { get; }

    /// <summary>Entries sorted by canonical relative path.</summary>
    public IReadOnlyList<PackageManifestEntry> Entries { get; }

    /// <summary>Descriptor-anchored game-derived files, sorted by destination.</summary>
    public IReadOnlyList<GeneratedFileRecipe> GeneratedFiles { get; }

    /// <summary>The exact tagged-release policy committed by a schema-4 manifest.</summary>
    public TaggedReleaseAuthorityPolicy? ReleaseAuthorityPolicy { get; }

    /// <summary>Whether every declared generated file has an exact inspected source and result entry.</summary>
    internal bool HasResolvedGeneratedFiles => this.GeneratedFiles.All(recipe => recipe.SourceIdentity is not null);

    /// <summary>Construct and validate an immutable package manifest.</summary>
    internal PackageManifest(
        InstallationReleaseIdentity release,
        IEnumerable<PackageManifestEntry> entries,
        IEnumerable<GeneratedFileRecipe>? generatedFiles = null,
        int? schemaVersion = null,
        TaggedReleaseAuthorityPolicy? releaseAuthorityPolicy = null
    )
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(entries);

        PackageManifestEntry[] ordered = entries.OrderBy(entry => entry.Path.Value, StringComparer.Ordinal).ToArray();
        GeneratedFileRecipe[] orderedGenerated = (generatedFiles ?? Array.Empty<GeneratedFileRecipe>())
            .OrderBy(entry => entry.Path.Value, StringComparer.Ordinal)
            .ToArray();
        int actualSchemaVersion = schemaVersion ?? (releaseAuthorityPolicy is null
            ? PackageManifest.GeneratedFilesSchemaVersion
            : PackageManifest.CurrentSchemaVersion);
        if (actualSchemaVersion is not (PackageManifest.LegacySchemaVersion or PackageManifest.GeneratedFilesSchemaVersion or PackageManifest.CurrentSchemaVersion))
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (actualSchemaVersion == PackageManifest.LegacySchemaVersion && orderedGenerated.Length != 0)
            throw new ArgumentException("Manifest schema 2 can't contain generated-file recipes.", nameof(generatedFiles));
        if ((actualSchemaVersion == PackageManifest.CurrentSchemaVersion) != (releaseAuthorityPolicy is not null))
            throw new ArgumentException("Manifest schema 4 requires one exact release-authority policy, and older schemas forbid it.", nameof(releaseAuthorityPolicy));
        if (releaseAuthorityPolicy is not null && !releaseAuthorityPolicy.Equals(TaggedReleaseAuthorityPolicy.Create(release)))
            throw new ArgumentException("The release-authority policy doesn't match the exact manifest release.", nameof(releaseAuthorityPolicy));
        if (ordered.Length == 0)
            throw new ArgumentException("A package manifest must contain at least one owned file.", nameof(entries));
        OwnershipCollectionValidation.AssertDistinctFilePaths(ordered.Select(entry => entry.Path), nameof(entries));
        OwnershipCollectionValidation.AssertDistinctFilePaths(orderedGenerated.Select(entry => entry.Path), nameof(generatedFiles));
        if (orderedGenerated.Select(entry => entry.Path.Value).Intersect(ordered.Where(entry => entry.Kind != OwnedEntryKind.GeneratedFile).Select(entry => entry.Path.Value), StringComparer.OrdinalIgnoreCase).Any())
            throw new ArgumentException("A generated destination can't also be a package-backed entry.", nameof(generatedFiles));
        foreach (GeneratedFileRecipe generated in orderedGenerated)
        {
            PackageManifestEntry? result = ordered.SingleOrDefault(entry => entry.Path.Equals(generated.Path));
            if ((generated.SourceIdentity is null) != (result is null))
                throw new ArgumentException("A generated result entry must exist exactly when its source recipe is resolved.", nameof(entries));
            if (result is not null && (
                result.Kind != OwnedEntryKind.GeneratedFile
                || result.Sha256 != generated.SourceIdentity!.Sha256
                || result.SizeBytes != generated.SourceIdentity.SizeBytes
                || result.UnixMode != generated.SourceIdentity.UnixMode
            ))
                throw new ArgumentException("A generated result entry must exactly copy its bound source identity.", nameof(entries));
        }
        if (actualSchemaVersion >= PackageManifest.GeneratedFilesSchemaVersion && ordered.Any(entry => entry.Kind == OwnedEntryKind.GeneratedFile && orderedGenerated.All(recipe => !recipe.Path.Equals(entry.Path))))
            throw new ArgumentException("A generated entry must be authorized by an exact recipe.", nameof(entries));
        if (ordered.Count(entry => entry.Kind == OwnedEntryKind.Launcher && entry.Path.Value == "StardewValley") != 1)
            throw new ArgumentException("A Linux package manifest must contain exactly one installed launcher destination.", nameof(entries));

        this.SchemaVersion = actualSchemaVersion;
        this.Release = release;
        this.Entries = new ReadOnlyCollection<PackageManifestEntry>(ordered);
        this.GeneratedFiles = new ReadOnlyCollection<GeneratedFileRecipe>(orderedGenerated);
        this.ReleaseAuthorityPolicy = releaseAuthorityPolicy;
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
            if (this.SchemaVersion >= PackageManifest.GeneratedFilesSchemaVersion)
            {
                writer.WriteStartArray("generated_files");
                foreach (GeneratedFileRecipe generated in this.GeneratedFiles)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", generated.Path.Value);
                    writer.WriteString("recipe", generated.Recipe);
                    writer.WriteString("source_path", generated.SourcePath.Value);
                    if (generated.SourceIdentity is null)
                        writer.WriteNull("source_identity");
                    else
                    {
                        writer.WriteStartObject("source_identity");
                        writer.WriteString("sha256", generated.SourceIdentity.Sha256.Value);
                        writer.WriteNumber("size_bytes", generated.SourceIdentity.SizeBytes);
                        writer.WriteNumber("unix_mode", generated.SourceIdentity.UnixMode);
                        writer.WriteString("file_type", "regular_file");
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (this.SchemaVersion == PackageManifest.CurrentSchemaVersion)
            {
                writer.WritePropertyName("release_authority_policy");
                CanonicalOwnershipJson.WriteReleaseAuthorityPolicy(writer, this.ReleaseAuthorityPolicy!);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Get the digest of the canonical manifest bytes.</summary>
    public Sha256Digest GetCanonicalDigest()
    {
        return Sha256Digest.Hash(Encoding.UTF8.GetBytes(this.ToCanonicalJson()));
    }

    /// <summary>Resolve every trusted recipe with exact core-observed source identities.</summary>
    internal PackageManifest ResolveGeneratedFiles(IReadOnlyDictionary<string, RecoveryFileIdentity> sources)
    {
        if (this.GeneratedFiles.Count == 0)
            return this;
        GeneratedFileRecipe[] resolved = this.GeneratedFiles
            .Select(recipe => recipe.Resolve(sources.TryGetValue(recipe.SourcePath.Value, out RecoveryFileIdentity? identity)
                ? identity
                : throw new ArgumentException($"Generated source '{recipe.SourcePath}' wasn't observed.", nameof(sources))))
            .ToArray();
        PackageManifestEntry[] packageEntries = this.Entries.Where(entry => entry.Kind != OwnedEntryKind.GeneratedFile).ToArray();
        PackageManifestEntry[] resultEntries = resolved.Select(recipe => new PackageManifestEntry(
            recipe.Path,
            recipe.SourceIdentity!.Sha256,
            recipe.SourceIdentity.SizeBytes,
            recipe.SourceIdentity.UnixMode,
            OwnedEntryKind.GeneratedFile
        )).ToArray();
        return new PackageManifest(this.Release, packageEntries.Concat(resultEntries), resolved, this.SchemaVersion, this.ReleaseAuthorityPolicy);
    }

    /// <summary>Reconstruct the exact unresolved release manifest which was an attestation subject.</summary>
    internal (Sha256Digest Sha256, long SizeBytes) GetAttestedTemplateIdentity()
    {
        if (this.SchemaVersion != PackageManifest.CurrentSchemaVersion || this.ReleaseAuthorityPolicy is null)
            throw new InvalidOperationException("Only schema-4 manifests have a reconstructable attested release template.");

        PackageManifest template = new(
            this.Release,
            this.Entries.Where(entry => entry.Kind != OwnedEntryKind.GeneratedFile),
            this.GeneratedFiles.Select(recipe => new GeneratedFileRecipe(recipe.Path, recipe.Recipe, recipe.SourcePath)),
            this.SchemaVersion,
            this.ReleaseAuthorityPolicy
        );
        byte[] bytes = Encoding.UTF8.GetBytes(template.ToCanonicalJson());
        return (Sha256Digest.Hash(bytes), bytes.LongLength);
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
        writer.WriteNumber("package_size_bytes", release.PackageSizeBytes);
        writer.WriteString("build_workflow", release.BuildWorkflow);
        writer.WriteString("build_configuration", release.BuildConfiguration);
        writer.WriteString("runtime_identifier", release.RuntimeIdentifier);
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

    public static void WriteReleaseAuthorityPolicy(Utf8JsonWriter writer, TaggedReleaseAuthorityPolicy policy)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", policy.Kind);
        writer.WriteString("repository", policy.Repository);
        writer.WriteString("source_reference", policy.SourceReference);
        writer.WriteString("source_commit", policy.SourceCommit);
        writer.WriteString("build_workflow", policy.BuildWorkflow);
        writer.WriteString("runner_environment", policy.RunnerEnvironment);
        writer.WriteString("trigger", policy.Trigger);
        writer.WriteString("repository_identifier", policy.RepositoryIdentifier);
        writer.WriteString("repository_owner_identifier", policy.RepositoryOwnerIdentifier);
        writer.WriteString("package_subject_name", policy.PackageSubjectName);
        writer.WriteString("manifest_subject_name", policy.ManifestSubjectName);
        writer.WriteEndObject();
    }

    public static void WriteReleaseAuthorityEvidence(Utf8JsonWriter writer, VerifiedTaggedPackageTrust trust)
    {
        (ulong runId, int runAttempt) = trust.Evidence.GetRunIdentity();
        writer.WriteStartObject();
        writer.WriteString("kind", TaggedReleaseAuthorityPolicy.GitHubArtifactAttestationV1);
        WriteAttestedSubject(writer, "package_subject", trust.PackageSubject);
        WriteAttestedSubject(writer, "manifest_subject", trust.ManifestSubject);
        writer.WriteString("run_id", runId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteNumber("run_attempt", runAttempt);
        writer.WriteNumber("transparency_log_timestamp_utc_ticks", trust.Evidence.TransparencyLogTimestampUtc.UtcDateTime.Ticks);
        writer.WriteEndObject();
    }

    private static void WriteAttestedSubject(Utf8JsonWriter writer, string propertyName, VerifiedAttestedSubject subject)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString("name", subject.Name);
        writer.WriteString("sha256", subject.Sha256.Value);
        writer.WriteNumber("size_bytes", subject.ObservedSizeBytes);
        writer.WriteEndObject();
    }
}
