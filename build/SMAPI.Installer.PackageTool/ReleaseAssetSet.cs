using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

[assembly: InternalsVisibleTo("SMAPI.Installer.PackageTool.Tests")]

namespace StardewModdingAPI.Installer.PackageTool;

/// <summary>
/// The explicit inputs recorded in one release artifact set. Workflow-run, runner, timestamp, reference-build,
/// and .NET fields are informational and aren't authenticated by clean-machine quartet verification.
/// </summary>
internal sealed record ReleaseAssetSetInputs(
    ForkReleaseIdentity Identity,
    string SourceCommit,
    string SourceTree,
    string Workflow,
    string WorkflowRun,
    string RunnerImage,
    string RunnerArchitecture,
    string ReferenceAssembliesCommit,
    string TimestampUtc,
    string DotNetInfo
);

/// <summary>The independently known immutable authority inputs required to verify a downloaded release quartet.</summary>
internal sealed record ReleaseVerificationInputs(
    ForkReleaseIdentity Identity,
    string SourceCommit,
    string SourceTree
);

/// <summary>
/// Internal pure construction and verification core for the exact Linux installer release-asset quartet.
/// The production command applies the GitHub tag-push context guard before calling creation.
/// </summary>
internal sealed class ReleaseAssetSet
{
    private const string ChecksumsName = "SHA256SUMS";
    private const string MetadataName = "build-metadata.json";
    private const string ReproducibilityStatement = "Inputs and provenance are recorded; byte-for-byte reproducibility is not claimed.";
    private const int MaximumDotNetInfoBytes = 256 * 1024;
    private static readonly Regex GitObjectPattern = new(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant);
    private static readonly Regex TimestampPattern = new(
        @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\z",
        RegexOptions.CultureInvariant
    );

    /// <summary>
    /// Create a new output directory containing exactly the release quartet. This pure internal operation doesn't
    /// prove GitHub provenance; production callers must apply the tag-push context guard first.
    /// </summary>
    public async Task CreateAsync(
        string finalizedPackagePath,
        string outputDirectory,
        ReleaseAssetSetInputs inputs,
        CancellationToken cancellationToken = default
    )
    {
        ReleaseAssetSet.ValidateInputs(inputs);
        if (string.IsNullOrWhiteSpace(finalizedPackagePath))
            throw new ArgumentException("The finalized package path is required.", nameof(finalizedPackagePath));
        string packageSource = Path.GetFullPath(finalizedPackagePath);
        if (!string.Equals(Path.GetFileName(packageSource), inputs.Identity.PackageAssetName, StringComparison.Ordinal))
            throw new PackageSecurityException("The finalized package filename doesn't match the selected release identity.");
        string output = ReleaseAssetSet.GetNewOutputDirectory(outputDirectory);
        Directory.CreateDirectory(output);
        try
        {
            string package = Path.Combine(output, inputs.Identity.PackageAssetName);
            await ReleaseAssetSet.CopyBoundedAsync(
                packageSource,
                package,
                PackageVerificationLimits.Default.MaxPackageBytes,
                cancellationToken
            ).ConfigureAwait(false);

            LinuxInstallManifestBuildResult manifest = await new LinuxInstallManifestBuilder().BuildAsync(
                package,
                inputs.Identity,
                inputs.SourceCommit,
                inputs.SourceTree,
                inputs.Workflow,
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);
            string manifestName = VerifiedInstallerPackageFactory.GetManifestAssetName(inputs.Identity);
            string manifestPath = Path.Combine(output, manifestName);
            await ReleaseAssetSet.WriteNewBytesAsync(manifestPath, manifest.GetCanonicalBytes(), cancellationToken).ConfigureAwait(false);

            Artifact manifestArtifact = await ReleaseAssetSet.HashAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            Artifact packageArtifact = await ReleaseAssetSet.HashAsync(package, cancellationToken).ConfigureAwait(false);
            Artifact[] artifacts = [manifestArtifact, packageArtifact];
            await ReleaseAssetSet.WriteNewBytesAsync(
                Path.Combine(output, ReleaseAssetSet.ChecksumsName),
                ReleaseAssetSet.CreateChecksums(artifacts),
                cancellationToken
            ).ConfigureAwait(false);
            await ReleaseAssetSet.WriteNewBytesAsync(
                Path.Combine(output, ReleaseAssetSet.MetadataName),
                ReleaseAssetSet.CreateMetadata(inputs, artifacts),
                cancellationToken
            ).ConfigureAwait(false);

            await this.VerifyReleaseAsync(
                output,
                new ReleaseVerificationInputs(inputs.Identity, inputs.SourceCommit, inputs.SourceTree),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                ReleaseAssetSet.TryDeleteCreatedOutput(output, inputs.Identity);
            }
            catch
            {
                // The output directory was newly created by this method; cleanup is best effort on failure.
            }
            throw;
        }
    }

    /// <summary>
    /// Verify the package and manifest authority through the complete Core chain. Informational runner metadata is
    /// checked only for a strict bounded profile because a clean machine has no independent value to compare it to.
    /// </summary>
    public async Task VerifyReleaseAsync(
        string assetDirectory,
        ReleaseVerificationInputs inputs,
        CancellationToken cancellationToken = default
    )
    {
        ReleaseAssetSet.ValidateVerificationInputs(inputs);
        string directory = ReleaseAssetSet.GetExistingDirectory(assetDirectory);
        string manifestName = VerifiedInstallerPackageFactory.GetManifestAssetName(inputs.Identity);
        ReleaseAssetSet.AssertExactAssetNames(directory, inputs.Identity.PackageAssetName, manifestName);
        string packagePath = Path.Combine(directory, inputs.Identity.PackageAssetName);
        string manifestPath = Path.Combine(directory, manifestName);
        LinuxInstallManifestBuildResult manifest = await new LinuxInstallManifestBuilder().BuildAsync(
            packagePath,
            inputs.Identity,
            inputs.SourceCommit,
            inputs.SourceTree,
            ReleaseAssetSet.GetTagWorkflow(inputs.Identity),
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        await ReleaseAssetSet.AssertExactBytesAsync(
            manifestPath,
            manifest.GetCanonicalBytes(),
            16 * 1024 * 1024,
            cancellationToken
        ).ConfigureAwait(false);
        Artifact manifestArtifact = await ReleaseAssetSet.HashAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        Artifact packageArtifact = await ReleaseAssetSet.HashAsync(packagePath, cancellationToken).ConfigureAwait(false);
        Artifact[] artifacts = [manifestArtifact, packageArtifact];
        await ReleaseAssetSet.AssertExactBytesAsync(
            Path.Combine(directory, ReleaseAssetSet.ChecksumsName),
            ReleaseAssetSet.CreateChecksums(artifacts),
            PackageVerificationLimits.Default.MaxChecksumBytes,
            cancellationToken
        ).ConfigureAwait(false);
        string checksums = await ReleaseAssetSet.ReadBoundedUtf8Async(
            Path.Combine(directory, ReleaseAssetSet.ChecksumsName),
            PackageVerificationLimits.Default.MaxChecksumBytes,
            cancellationToken
        ).ConfigureAwait(false);
        string metadata = await ReleaseAssetSet.ReadBoundedUtf8Async(
            Path.Combine(directory, ReleaseAssetSet.MetadataName),
            PackageVerificationLimits.Default.MaxMetadataBytes,
            cancellationToken
        ).ConfigureAwait(false);
        ReleaseAssetSet.AssertMetadataProfile(metadata, inputs, artifacts);

        VerifiedReleasePackage release = await new ReleasePackageVerifier().VerifyAsync(
            packagePath,
            checksums,
            metadata,
            inputs.Identity,
            inputs.SourceCommit,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        VerifiedInstallerPackage installer;
        try
        {
            installer = await new VerifiedInstallerPackageFactory().VerifyAsync(
                release,
                manifestPath,
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            await release.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        await using (installer.ConfigureAwait(false))
        await using (VerifiedPackageContent content = await new VerifiedPackageContentFactory().ExtractAsync(
            installer,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false))
        {
            if (!content.Release.Equals(manifest.Manifest.Release))
                throw new PackageSecurityException("The extracted payload authority doesn't match the generated manifest.");
        }
    }

    private static void ValidateInputs(ReleaseAssetSetInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(inputs.Identity);
        if (!ReleaseAssetSet.GitObjectPattern.IsMatch(inputs.SourceCommit ?? ""))
            throw new ArgumentException("The source commit must be a full lowercase Git object ID.", nameof(inputs));
        if (!ReleaseAssetSet.GitObjectPattern.IsMatch(inputs.SourceTree ?? ""))
            throw new ArgumentException("The source tree must be a full lowercase Git object ID.", nameof(inputs));
        if (!ReleaseAssetSet.GitObjectPattern.IsMatch(inputs.ReferenceAssembliesCommit ?? ""))
            throw new ArgumentException("The reference-assemblies commit must be a full lowercase Git object ID.", nameof(inputs));
        ReleaseAssetSet.RequireBoundedText(inputs.Workflow, 512, "workflow");
        string expectedWorkflow = ReleaseAssetSet.GetTagWorkflow(inputs.Identity);
        if (!string.Equals(inputs.Workflow, expectedWorkflow, StringComparison.Ordinal))
            throw new ArgumentException("Only the exact reviewed tag workflow may create or verify a release quartet.", nameof(inputs));
        ReleaseAssetSet.RequireBoundedText(inputs.WorkflowRun, 2048, "workflow run");
        ReleaseAssetSet.RequireBoundedText(inputs.RunnerImage, 256, "runner image");
        ReleaseAssetSet.RequireBoundedText(inputs.RunnerArchitecture, 64, "runner architecture");
        ReleaseAssetSet.RequireBoundedText(inputs.DotNetInfo, ReleaseAssetSet.MaximumDotNetInfoBytes, ".NET information", bytes: true);
        if (
            inputs.TimestampUtc == null
            || !ReleaseAssetSet.TimestampPattern.IsMatch(inputs.TimestampUtc)
            || !DateTimeOffset.TryParseExact(
                inputs.TimestampUtc,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out _
            )
        )
        {
            throw new ArgumentException("The build timestamp must be canonical UTC seconds.", nameof(inputs));
        }
    }

    private static void ValidateVerificationInputs(ReleaseVerificationInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(inputs.Identity);
        if (!ReleaseAssetSet.GitObjectPattern.IsMatch(inputs.SourceCommit ?? ""))
            throw new ArgumentException("The source commit must be a full lowercase Git object ID.", nameof(inputs));
        if (!ReleaseAssetSet.GitObjectPattern.IsMatch(inputs.SourceTree ?? ""))
            throw new ArgumentException("The source tree must be a full lowercase Git object ID.", nameof(inputs));
    }

    private static string GetTagWorkflow(ForkReleaseIdentity identity)
    {
        return $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{identity.Tag}";
    }

    private static byte[] CreateChecksums(IReadOnlyList<Artifact> artifacts)
    {
        string text = string.Concat(artifacts.Select(artifact => $"{artifact.Sha256}  {artifact.Name}\n"));
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(text);
    }

    private static byte[] CreateMetadata(ReleaseAssetSetInputs inputs, IReadOnlyList<Artifact> artifacts)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteStartObject("release");
            writer.WriteString("version", inputs.Identity.EmbeddedVersion);
            writer.WriteString("tag", inputs.Identity.Tag);
            writer.WriteEndObject();
            writer.WriteStartObject("source");
            writer.WriteString("repository", ForkReleaseIdentity.RepositoryUrl);
            writer.WriteString("commit", inputs.SourceCommit);
            writer.WriteString("tree", inputs.SourceTree);
            writer.WriteEndObject();
            writer.WriteStartObject("build");
            writer.WriteString("workflow", inputs.Workflow);
            writer.WriteString("run", inputs.WorkflowRun);
            writer.WriteString("runner_image", inputs.RunnerImage);
            writer.WriteString("runner_arch", inputs.RunnerArchitecture);
            writer.WriteString("reference_assemblies_commit", inputs.ReferenceAssembliesCommit);
            writer.WriteString("configuration", "Release");
            writer.WriteString("runtime_identifier", "linux-x64");
            writer.WriteString("timestamp_utc", inputs.TimestampUtc);
            writer.WriteString("dotnet_info", inputs.DotNetInfo);
            writer.WriteEndObject();
            writer.WriteStartArray("artifacts");
            foreach (Artifact artifact in artifacts)
            {
                writer.WriteStartObject();
                writer.WriteString("name", artifact.Name);
                writer.WriteNumber("size_bytes", artifact.SizeBytes);
                writer.WriteString("sha256", artifact.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("reproducibility", ReleaseAssetSet.ReproducibilityStatement);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void AssertMetadataProfile(
        string metadata,
        ReleaseVerificationInputs inputs,
        IReadOnlyList<Artifact> artifacts
    )
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                metadata,
                new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 }
            );
            JsonElement root = document.RootElement;
            AssertExactProperties(root, "root", "schema_version", "release", "source", "build", "artifacts", "reproducibility");
            if (root.GetProperty("schema_version").ValueKind != JsonValueKind.Number || root.GetProperty("schema_version").GetInt32() != 1)
                throw new PackageSecurityException("build-metadata.json has an unsupported schema version.");

            JsonElement release = root.GetProperty("release");
            AssertExactProperties(release, "release", "version", "tag");
            AssertExactString(release, "version", inputs.Identity.EmbeddedVersion, "release");
            AssertExactString(release, "tag", inputs.Identity.Tag, "release");

            JsonElement source = root.GetProperty("source");
            AssertExactProperties(source, "source", "repository", "commit", "tree");
            AssertExactString(source, "repository", ForkReleaseIdentity.RepositoryUrl, "source");
            AssertExactString(source, "commit", inputs.SourceCommit, "source");
            AssertExactString(source, "tree", inputs.SourceTree, "source");

            JsonElement build = root.GetProperty("build");
            AssertExactProperties(
                build,
                "build",
                "workflow",
                "run",
                "runner_image",
                "runner_arch",
                "reference_assemblies_commit",
                "configuration",
                "runtime_identifier",
                "timestamp_utc",
                "dotnet_info"
            );
            AssertExactString(build, "workflow", ReleaseAssetSet.GetTagWorkflow(inputs.Identity), "build");
            AssertExactString(build, "configuration", "Release", "build");
            AssertExactString(build, "runtime_identifier", "linux-x64", "build");
            string workflowRun = RequireBoundedMetadataString(build, "run", "build", 2048);
            if (!workflowRun.StartsWith($"https://github.com/{ForkReleaseIdentity.Repository}/actions/runs/", StringComparison.Ordinal))
                throw new PackageSecurityException("build-metadata.json doesn't contain a reviewed-repository workflow run URL.");
            _ = RequireBoundedMetadataString(build, "runner_image", "build", 256);
            _ = RequireBoundedMetadataString(build, "runner_arch", "build", 64);
            string referenceCommit = RequireBoundedMetadataString(build, "reference_assemblies_commit", "build", 40);
            if (!ReleaseAssetSet.GitObjectPattern.IsMatch(referenceCommit))
                throw new PackageSecurityException("build-metadata.json has an invalid reference-assemblies commit.");
            string timestamp = RequireBoundedMetadataString(build, "timestamp_utc", "build", 20);
            if (
                !ReleaseAssetSet.TimestampPattern.IsMatch(timestamp)
                || !DateTimeOffset.TryParseExact(
                    timestamp,
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out _
                )
            )
                throw new PackageSecurityException("build-metadata.json has a noncanonical UTC build timestamp.");
            _ = RequireBoundedMetadataString(build, "dotnet_info", "build", ReleaseAssetSet.MaximumDotNetInfoBytes, bytes: true);

            JsonElement artifactArray = root.GetProperty("artifacts");
            if (artifactArray.ValueKind != JsonValueKind.Array || artifactArray.GetArrayLength() != artifacts.Count)
                throw new PackageSecurityException("build-metadata.json must contain exactly the two ordered release artifacts.");
            int index = 0;
            foreach (JsonElement element in artifactArray.EnumerateArray())
            {
                AssertExactProperties(element, $"artifacts[{index}]", "name", "size_bytes", "sha256");
                Artifact expected = artifacts[index];
                AssertExactString(element, "name", expected.Name, $"artifacts[{index}]");
                JsonElement size = element.GetProperty("size_bytes");
                if (size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long actualSize) || actualSize != expected.SizeBytes)
                    throw new PackageSecurityException("build-metadata.json has an incorrect release artifact size.");
                AssertExactString(element, "sha256", expected.Sha256, $"artifacts[{index}]");
                index++;
            }
            AssertExactString(root, "reproducibility", ReleaseAssetSet.ReproducibilityStatement, "root");
        }
        catch (PackageSecurityException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new PackageSecurityException("build-metadata.json isn't valid strict bounded release metadata.", ex);
        }
    }

    private static void AssertExactProperties(JsonElement element, string description, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new PackageSecurityException($"build-metadata.json '{description}' must be an object.");
        string[] actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length || !actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new PackageSecurityException($"build-metadata.json '{description}' doesn't use the exact canonical property profile.");
    }

    private static void AssertExactString(JsonElement parent, string name, string expected, string description)
    {
        string actual = RequireBoundedMetadataString(parent, name, description, Math.Max(expected.Length, 1));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new PackageSecurityException($"build-metadata.json '{description}.{name}' doesn't match the release identity.");
    }

    private static string RequireBoundedMetadataString(
        JsonElement parent,
        string name,
        string description,
        int maximum,
        bool bytes = false
    )
    {
        JsonElement element = parent.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String)
            throw new PackageSecurityException($"build-metadata.json '{description}.{name}' must be a string.");
        string value = element.GetString()!;
        int length = bytes ? Encoding.UTF8.GetByteCount(value) : value.Length;
        if (string.IsNullOrWhiteSpace(value) || length > maximum)
            throw new PackageSecurityException($"build-metadata.json '{description}.{name}' is empty or excessive.");
        return value;
    }

    private static string GetNewOutputDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The output directory is required.", nameof(path));
        string fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
            throw new PackageSecurityException("The release output directory must not already exist.");
        return fullPath;
    }

    private static string GetExistingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The asset directory is required.", nameof(path));
        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new PackageSecurityException("The release asset directory must be an existing ordinary directory.");
        return fullPath;
    }

    private static void AssertExactAssetNames(string directory, string packageName, string manifestName)
    {
        string[] expected = [ReleaseAssetSet.ChecksumsName, ReleaseAssetSet.MetadataName, manifestName, packageName];
        FileSystemInfo[] entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToArray();
        if (
            entries.Length != expected.Length
            || entries.Any(entry => entry is not FileInfo || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            || !entries.Select(entry => entry.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal)
        )
        {
            throw new PackageSecurityException("The release asset directory must contain exactly the expected four ordinary files.");
        }
        try
        {
            using LinuxAnchoredFileSystem assets = new(directory);
            foreach (string name in expected)
            {
                using LinuxAnchoredFile file = assets.OpenRegularFileForRead(name);
                if (file.Identity.Size <= 0)
                    throw new IOException("A release asset is empty.");
            }
        }
        catch (IOException ex)
        {
            throw new PackageSecurityException("Every release asset must be a non-empty single-link regular file.", ex);
        }
    }

    private static async Task CopyBoundedAsync(string source, string destination, long maximumBytes, CancellationToken cancellationToken)
    {
        try
        {
            using LinuxAnchoredFileSystem sourceDirectory = new(Path.GetDirectoryName(source)!);
            using LinuxAnchoredFile input = sourceDirectory.OpenRegularFileForRead(Path.GetFileName(source));
            if (input.Identity.Size <= 0 || input.Identity.Size > maximumBytes)
                throw new PackageSecurityException("The finalized package has an invalid or excessive size.");
            long expected = input.Identity.Size;
            await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            _ = await sourceDirectory.CopyAndHashAsync(input, output, maximumBytes, cancellationToken).ConfigureAwait(false);
            if (output.Length != expected)
                throw new PackageSecurityException("The finalized package changed while it was copied.");
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        catch (IOException ex)
        {
            throw new PackageSecurityException("The finalized package must be a stable single-link regular file.", ex);
        }
    }

    private static async Task WriteNewBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static void TryDeleteCreatedOutput(string output, ForkReleaseIdentity identity)
    {
        string[] createdNames =
        [
            identity.PackageAssetName,
            VerifiedInstallerPackageFactory.GetManifestAssetName(identity),
            ReleaseAssetSet.ChecksumsName,
            ReleaseAssetSet.MetadataName
        ];
        foreach (string name in createdNames)
        {
            string path = Path.Combine(output, name);
            try
            {
                if (File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                    File.Delete(path);
            }
            catch (FileNotFoundException)
            {
                // A partial create need not have reached every asset.
            }
            catch (DirectoryNotFoundException)
            {
                // The newly-created output was already removed.
            }
        }
        Directory.Delete(output, recursive: false);
    }

    private static Task<Artifact> HashAsync(string path, CancellationToken cancellationToken)
    {
        using LinuxAnchoredFileSystem directory = new(Path.GetDirectoryName(path)!);
        using LinuxAnchoredFile file = directory.OpenRegularFileForRead(Path.GetFileName(path));
        long size = file.Identity.Size;
        if (size <= 0 || size > PackageVerificationLimits.Default.MaxPackageBytes)
            throw new PackageSecurityException("A release artifact has an invalid or excessive size.");
        string hash = directory.ComputeSha256(file, cancellationToken);
        return Task.FromResult(new Artifact(Path.GetFileName(path), size, hash));
    }

    private static Task AssertExactBytesAsync(
        string path,
        byte[] expected,
        long maximumBytes,
        CancellationToken cancellationToken
    )
    {
        if (expected.LongLength > maximumBytes)
            throw new PackageSecurityException("An expected release document exceeds its verification bound.");
        using LinuxAnchoredFileSystem directory = new(Path.GetDirectoryName(path)!);
        using LinuxAnchoredFile file = directory.OpenRegularFileForRead(Path.GetFileName(path));
        if (file.Identity.Size != expected.LongLength || file.Identity.Size > maximumBytes)
            throw new PackageSecurityException("A release document doesn't have its deterministic expected size.");
        byte[] actual = directory.ReadAllBytes(file, expected.Length, cancellationToken);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new PackageSecurityException("A release document doesn't match its deterministic expected bytes.");
        return Task.CompletedTask;
    }

    private static Task<string> ReadBoundedUtf8Async(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        using LinuxAnchoredFileSystem directory = new(Path.GetDirectoryName(path)!);
        using LinuxAnchoredFile file = directory.OpenRegularFileForRead(Path.GetFileName(path));
        if (file.Identity.Size <= 0 || file.Identity.Size > maximumBytes)
            throw new PackageSecurityException("A release document has an invalid or excessive size.");
        byte[] bytes = directory.ReadAllBytes(file, maximumBytes, cancellationToken);
        try
        {
            return Task.FromResult(new UTF8Encoding(false, true).GetString(bytes));
        }
        catch (DecoderFallbackException ex)
        {
            throw new PackageSecurityException("A release document isn't valid UTF-8.", ex);
        }
    }

    private static void RequireBoundedText(string? value, int maximum, string description, bool bytes = false)
    {
        int length = value == null ? 0 : bytes ? Encoding.UTF8.GetByteCount(value) : value.Length;
        bool invalidControl = value?.Any(character => char.IsControl(character) && (description != ".NET information" || character is not '\r' and not '\n' and not '\t')) == true;
        if (string.IsNullOrWhiteSpace(value) || length > maximum || invalidControl)
            throw new ArgumentException($"The {description} is missing, excessive, or contains control characters.");
    }

    private sealed record Artifact(string Name, long SizeBytes, string Sha256);
}
