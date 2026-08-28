using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Engine;

/// <summary>A stable, user-presentable result for Linux game-folder validation.</summary>
public enum LinuxGameFolderStatus
{
    Valid,
    MissingDirectory,
    UnsafeRoot,
    MissingGameAssembly,
    UnsafeGameAssembly,
    InvalidGameAssembly,
    UnsupportedGameVersion,
    MissingGameDependencies,
    UnsafeGameDependencies,
    InvalidGameDependencies,
    MissingLauncher,
    UnsafeLauncher
}

/// <summary>A bounded read-only observation of one possible Linux Stardew Valley installation.</summary>
public sealed class LinuxGameFolderCandidate
{
    /// <summary>The normalized input path, or the canonical anchored path when the root could be opened safely.</summary>
    public string CanonicalPath { get; }

    /// <summary>The stable validation result.</summary>
    public LinuxGameFolderStatus Status { get; }

    /// <summary>The observed game assembly version, when it could be read safely.</summary>
    public Version? GameVersion { get; }

    /// <summary>The exact directory identity, when the root could be anchored safely.</summary>
    public GameRootIdentity? GameRoot { get; }

    /// <summary>Whether this candidate is safe and supported for installer inspection.</summary>
    public bool IsValid => this.Status == LinuxGameFolderStatus.Valid;

    internal LinuxGameFolderCandidate(
        string canonicalPath,
        LinuxGameFolderStatus status,
        Version? gameVersion = null,
        GameRootIdentity? gameRoot = null
    )
    {
        this.CanonicalPath = canonicalPath;
        this.Status = status;
        this.GameVersion = gameVersion;
        this.GameRoot = gameRoot;
    }
}

/// <summary>An expected validation failure which frontends can map without parsing text.</summary>
public sealed class LinuxGameFolderException : Exception
{
    /// <summary>The stable folder-validation result.</summary>
    public LinuxGameFolderStatus Status { get; }

    internal LinuxGameFolderException(LinuxGameFolderStatus status, string message)
        : base(message)
    {
        this.Status = status;
    }
}

/// <summary>Find and descriptor-validate bounded Linux Stardew Valley installation candidates.</summary>
public sealed class LinuxGameDiscovery
{
    private const int MaximumCandidates = 64;
    private const int MaximumCandidateInputs = 256;
    private const int MaximumSteamLibraryDocumentBytes = 256 * 1024;
    private static readonly Version MinimumSupportedGameVersion = new(1, 6, 14);

    /// <summary>Validate a manually selected directory without changing it.</summary>
    public Task<LinuxGameFolderCandidate> ValidateAsync(string gameRoot, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Validate(gameRoot, MinimumSupportedGameVersion, cancellationToken),
            cancellationToken
        );
    }

    /// <summary>
    /// Check bounded conventional Steam, Flatpak Steam, and GOG locations plus any caller-provided paths.
    /// Missing conventional locations are omitted; existing invalid candidates remain visible with a stable reason.
    /// </summary>
    public Task<IReadOnlyList<LinuxGameFolderCandidate>> DiscoverAsync(
        IEnumerable<string>? additionalPaths = null,
        CancellationToken cancellationToken = default
    )
    {
        return Task.Run(
            () => this.Discover(additionalPaths, includeConventionalPaths: true, cancellationToken),
            cancellationToken
        );
    }

    internal IReadOnlyList<LinuxGameFolderCandidate> Discover(
        IEnumerable<string>? additionalPaths,
        bool includeConventionalPaths,
        CancellationToken cancellationToken,
        Version? minimumSupportedVersion = null
    )
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        cancellationToken.ThrowIfCancellationRequested();

        List<string> paths = new(MaximumCandidates);
        HashSet<string> inputPaths = new(StringComparer.Ordinal);
        int inputCount = 0;
        if (includeConventionalPaths)
        {
            foreach (string path in GetConventionalPaths(cancellationToken))
            {
                if (++inputCount > MaximumCandidateInputs || paths.Count >= MaximumCandidates)
                    break;
                AddCandidatePath(path, paths, inputPaths);
            }
        }
        if (additionalPaths is not null)
        {
            foreach (string path in additionalPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++inputCount > MaximumCandidateInputs || paths.Count >= MaximumCandidates)
                    break;
                AddCandidatePath(path, paths, inputPaths);
            }
        }

        List<LinuxGameFolderCandidate> result = new();
        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (string path in paths.Take(MaximumCandidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinuxGameFolderCandidate candidate = Validate(
                path,
                minimumSupportedVersion ?? MinimumSupportedGameVersion,
                cancellationToken
            );
            if (candidate.Status == LinuxGameFolderStatus.MissingDirectory && includeConventionalPaths)
                continue;
            if (observed.Add(candidate.CanonicalPath))
                result.Add(candidate);
        }
        return result
            .OrderBy(candidate => candidate.CanonicalPath, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddCandidatePath(string path, List<string> paths, HashSet<string> inputPaths)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        string key;
        try
        {
            key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            key = path;
        }
        if (inputPaths.Add(key))
            paths.Add(path);
    }

    internal static LinuxGameFolderCandidate Validate(
        string gameRoot,
        Version minimumSupportedVersion,
        CancellationToken cancellationToken
    )
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        ArgumentNullException.ThrowIfNull(minimumSupportedVersion);
        cancellationToken.ThrowIfCancellationRequested();

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new LinuxGameFolderCandidate(gameRoot ?? string.Empty, LinuxGameFolderStatus.UnsafeRoot);
        }
        if (!Directory.Exists(normalized))
            return new LinuxGameFolderCandidate(normalized, LinuxGameFolderStatus.MissingDirectory);

        try
        {
            using InstallerInspectionLease inspection = InstallerInspectionLease.Open(normalized);
            LinuxGameFolderCandidate result = Validate(inspection, minimumSupportedVersion, cancellationToken);
            inspection.AssertStable();
            return result;
        }
        catch (DirectoryNotFoundException)
        {
            return new LinuxGameFolderCandidate(normalized, LinuxGameFolderStatus.MissingDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InstallerTransactionException)
        {
            return new LinuxGameFolderCandidate(normalized, LinuxGameFolderStatus.UnsafeRoot);
        }
    }

    internal static LinuxGameFolderCandidate Validate(
        InstallerInspectionLease inspection,
        Version minimumSupportedVersion,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(inspection);
        return Validate(
            inspection.Game,
            inspection.RootIdentity,
            minimumSupportedVersion,
            cancellationToken,
            inspection.AssertStable
        );
    }

    private static LinuxGameFolderCandidate Validate(
        LinuxAnchoredFileSystem game,
        GameRootIdentity gameRoot,
        Version minimumSupportedVersion,
        CancellationToken cancellationToken,
        Action assertStable
    )
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(gameRoot);
        ArgumentNullException.ThrowIfNull(minimumSupportedVersion);
        ArgumentNullException.ThrowIfNull(assertStable);
        cancellationToken.ThrowIfCancellationRequested();

        LinuxGameFolderStatus? assemblyStatus = TryOpenMarker(
            game,
            "Stardew Valley.dll",
            LinuxGameFolderStatus.MissingGameAssembly,
            LinuxGameFolderStatus.UnsafeGameAssembly,
            out LinuxAnchoredFile? assembly
        );
        if (assemblyStatus is not null)
            return Candidate(gameRoot, assemblyStatus.Value);
        using (assembly)
        {
            Version version;
            try
            {
                byte[] bytes = ReadBounded(game, assembly!, 64 * 1024 * 1024, cancellationToken);
                using PEReader reader = new(new MemoryStream(bytes, writable: false));
                if (!reader.HasMetadata)
                    return Candidate(gameRoot, LinuxGameFolderStatus.InvalidGameAssembly);
                MetadataReader metadata = reader.GetMetadataReader();
                if (!metadata.IsAssembly)
                    return Candidate(gameRoot, LinuxGameFolderStatus.InvalidGameAssembly);
                AssemblyDefinition definition = metadata.GetAssemblyDefinition();
                if (metadata.GetString(definition.Name) != "Stardew Valley")
                    return Candidate(gameRoot, LinuxGameFolderStatus.InvalidGameAssembly);
                version = definition.Version;
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException)
            {
                return Candidate(gameRoot, LinuxGameFolderStatus.InvalidGameAssembly);
            }
            if (version < minimumSupportedVersion)
                return Candidate(gameRoot, LinuxGameFolderStatus.UnsupportedGameVersion, version);

            LinuxGameFolderStatus? dependenciesStatus = TryOpenMarker(
                game,
                "Stardew Valley.deps.json",
                LinuxGameFolderStatus.MissingGameDependencies,
                LinuxGameFolderStatus.UnsafeGameDependencies,
                out LinuxAnchoredFile? dependencies
            );
            if (dependenciesStatus is not null)
                return Candidate(gameRoot, dependenciesStatus.Value, version);
            using (dependencies)
            {
                try
                {
                    byte[] bytes = ReadBounded(game, dependencies!, 16 * 1024 * 1024, cancellationToken);
                    using JsonDocument document = JsonDocument.Parse(
                        bytes,
                        new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 }
                    );
                    JsonElement root = document.RootElement;
                    if (
                        root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("runtimeTarget", out JsonElement runtimeTarget)
                        || runtimeTarget.ValueKind != JsonValueKind.Object
                        || !runtimeTarget.TryGetProperty("name", out JsonElement runtimeName)
                        || runtimeName.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(runtimeName.GetString())
                        || !root.TryGetProperty("targets", out JsonElement targets)
                        || targets.ValueKind != JsonValueKind.Object
                        || !targets.TryGetProperty(runtimeName.GetString()!, out JsonElement target)
                        || target.ValueKind != JsonValueKind.Object
                    )
                    {
                        return Candidate(gameRoot, LinuxGameFolderStatus.InvalidGameDependencies, version);
                    }
                }
                catch (JsonException)
                {
                    return Candidate(gameRoot, LinuxGameFolderStatus.InvalidGameDependencies, version);
                }
            }

            LinuxGameFolderStatus? launcherStatus = TryOpenMarker(
                game,
                "StardewValley",
                LinuxGameFolderStatus.MissingLauncher,
                LinuxGameFolderStatus.UnsafeLauncher,
                out LinuxAnchoredFile? launcher
            );
            if (launcherStatus is not null)
                return Candidate(gameRoot, launcherStatus.Value, version);
            using (launcher)
            {
                if (launcher!.Identity.Size <= 0 || (launcher.Identity.UnixMode & 0x49) == 0)
                    return Candidate(gameRoot, LinuxGameFolderStatus.UnsafeLauncher, version);
            }

            cancellationToken.ThrowIfCancellationRequested();
            assertStable();
            return Candidate(gameRoot, LinuxGameFolderStatus.Valid, version);
        }
    }

    internal static void AssertValid(InstallerInspectionLease inspection, CancellationToken cancellationToken)
    {
        LinuxGameFolderCandidate candidate = Validate(inspection, MinimumSupportedGameVersion, cancellationToken);
        if (!candidate.IsValid)
        {
            throw new LinuxGameFolderException(
                candidate.Status,
                $"The selected game folder failed validation ({candidate.Status})."
            );
        }
    }

    internal static void AssertValid(InstallerOperationLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        LinuxGameFolderCandidate candidate = Validate(
            lease.Game,
            lease.RootIdentity,
            MinimumSupportedGameVersion,
            cancellationToken,
            () => lease.AssertRootAndGeneration(lease.RootIdentity, lease.Generation)
        );
        if (!candidate.IsValid)
        {
            throw new LinuxGameFolderException(
                candidate.Status,
                $"The selected game folder failed validation ({candidate.Status})."
            );
        }
    }

    private static LinuxGameFolderCandidate Candidate(
        GameRootIdentity gameRoot,
        LinuxGameFolderStatus status,
        Version? version = null
    )
    {
        return new LinuxGameFolderCandidate(
            gameRoot.CanonicalPath,
            status,
            version,
            gameRoot
        );
    }

    private static LinuxGameFolderStatus? TryOpenMarker(
        LinuxAnchoredFileSystem game,
        string relativePath,
        LinuxGameFolderStatus missing,
        LinuxGameFolderStatus unsafeStatus,
        out LinuxAnchoredFile? file
    )
    {
        file = null;
        LinuxFileIdentity? identity;
        try
        {
            identity = game.Stat(relativePath);
        }
        catch (IOException)
        {
            return unsafeStatus;
        }
        if (identity is null)
            return missing;
        if (identity.Kind != LinuxAnchoredEntryKind.RegularFile || identity.LinkCount != 1)
            return unsafeStatus;
        try
        {
            file = game.OpenRegularFileForRead(relativePath);
            if (file.Identity != identity)
            {
                file.Dispose();
                file = null;
                return unsafeStatus;
            }
            return null;
        }
        catch (IOException)
        {
            file?.Dispose();
            file = null;
            return unsafeStatus;
        }
    }

    private static byte[] ReadBounded(
        LinuxAnchoredFileSystem game,
        LinuxAnchoredFile file,
        int maximumBytes,
        CancellationToken cancellationToken
    )
    {
        return game.ReadAllBytes(file, maximumBytes, cancellationToken);
    }

    private static IEnumerable<string> GetConventionalPaths(CancellationToken cancellationToken)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            yield break;

        yield return Path.Combine(profile, "GOG Games", "Stardew Valley", "game");
        string[] steamRoots =
        [
            Path.Combine(profile, ".steam", "steam"),
            Path.Combine(profile, ".local", "share", "Steam"),
            Path.Combine(profile, ".var", "app", "com.valvesoftware.Steam", "data", "Steam")
        ];
        HashSet<string> libraries = new(StringComparer.Ordinal);
        foreach (string steamRoot in steamRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (libraries.Add(steamRoot))
                yield return Path.Combine(steamRoot, "steamapps", "common", "Stardew Valley");
            foreach (string library in ReadSteamLibraries(steamRoot, cancellationToken))
            {
                if (libraries.Add(library))
                    yield return Path.Combine(library, "steamapps", "common", "Stardew Valley");
            }
        }
    }

    private static IEnumerable<string> ReadSteamLibraries(string steamRoot, CancellationToken cancellationToken)
    {
        string path = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        byte[] bytes;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > MaximumSteamLibraryDocumentBytes)
                yield break;
            bytes = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = stream.Read(bytes, offset, Math.Min(16 * 1024, bytes.Length - offset));
                if (count == 0)
                    yield break;
                offset += count;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        string text = Encoding.UTF8.GetString(bytes);
        const string token = "\"path\"";
        int position = 0;
        while ((position = text.IndexOf(token, position, StringComparison.Ordinal)) >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            position += token.Length;
            while (position < text.Length && char.IsWhiteSpace(text[position]))
                position++;
            if (position >= text.Length || text[position] != '"')
                continue;
            position++;
            StringBuilder value = new();
            bool complete = false;
            while (position < text.Length && value.Length <= 4096)
            {
                char current = text[position++];
                if (current == '"')
                {
                    complete = true;
                    break;
                }
                if (current == '\\' && position < text.Length)
                {
                    char escaped = text[position++];
                    if (escaped is '\\' or '"')
                        value.Append(escaped);
                    else
                        value.Append('\\').Append(escaped);
                }
                else if (!char.IsControl(current))
                    value.Append(current);
            }
            if (complete && value.Length > 0 && Path.IsPathRooted(value.ToString()))
                yield return value.ToString();
        }
    }
}
