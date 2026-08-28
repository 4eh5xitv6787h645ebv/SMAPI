using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Engine;

/// <summary>
/// Resolves release-declared generated-file recipes only from descriptor-anchored game files. The resulting
/// manifest is immutable and binds both the trusted recipe and the exact copied bytes, size, and mode.
/// </summary>
internal sealed class GeneratedPackageContentAuthority : IVerifiedPackageContentAuthority
{
    private const long MaximumGeneratedSourceBytes = 16L * 1024 * 1024;
    private readonly IVerifiedPackageContentAuthority Package;

    public PackageManifest Manifest { get; }
    public Sha256Digest ManifestSha256 { get; }
    public object AuthorityIdentity => this.Package.AuthorityIdentity;

    private GeneratedPackageContentAuthority(
        IVerifiedPackageContentAuthority package,
        PackageManifest manifest
    )
    {
        this.Package = package;
        this.Manifest = manifest;
        this.ManifestSha256 = manifest.GetCanonicalDigest();
    }

    public static IVerifiedPackageContentAuthority Resolve(
        LinuxAnchoredFileSystem game,
        IVerifiedPackageContentAuthority package,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(package);
        package.AssertUsable();
        if (package.Manifest.GeneratedFiles.Count == 0)
            return package;

        Dictionary<string, RecoveryFileIdentity> sources = ObserveSources(
            game,
            package.Manifest.GeneratedFiles,
            rejectChangedResolvedIdentity: true,
            cancellationToken
        );

        if (package.Manifest.HasResolvedGeneratedFiles)
            return package;
        return new GeneratedPackageContentAuthority(package, package.Manifest.ResolveGeneratedFiles(sources));
    }

    internal static PackageManifest ResolveInstalledManifestEvolution(
        LinuxAnchoredFileSystem game,
        PackageManifest installedManifest,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(installedManifest);
        if (installedManifest.GeneratedFiles.Count == 0)
            return installedManifest;
        Dictionary<string, RecoveryFileIdentity> sources = ObserveSources(
            game,
            installedManifest.GeneratedFiles,
            rejectChangedResolvedIdentity: false,
            cancellationToken
        );
        return installedManifest.ResolveGeneratedFiles(sources);
    }

    private static Dictionary<string, RecoveryFileIdentity> ObserveSources(
        LinuxAnchoredFileSystem game,
        IReadOnlyList<GeneratedFileRecipe> recipes,
        bool rejectChangedResolvedIdentity,
        CancellationToken cancellationToken
    )
    {
        Dictionary<string, RecoveryFileIdentity> sources = new(StringComparer.Ordinal);
        foreach (GeneratedFileRecipe recipe in recipes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinuxFileIdentity? before = ReadSourceIdentity(game, recipe.SourcePath);
            if (
                before is null
                || before.Kind != LinuxAnchoredEntryKind.RegularFile
                || before.LinkCount != 1
                || before.Size <= 0
                || before.Size > MaximumGeneratedSourceBytes
            )
            {
                throw new InstallerTransactionException(
                    TransactionErrorCode.ExistingFileMismatch,
                    $"Required generated-file source '{recipe.SourcePath}' isn't a unique regular file."
                );
            }
            RecoveryFileObservation observation = InstallationStateInspector.ReadObservation(
                game,
                recipe.SourcePath,
                cancellationToken
            );
            if (ReadSourceIdentity(game, recipe.SourcePath) != before)
                throw new InstallerTransactionException(TransactionErrorCode.PathChanged, $"Generated-file source '{recipe.SourcePath}' changed during inspection.");
            RecoveryFileIdentity identity = observation.Identity
                ?? throw new InstallerTransactionException(
                    TransactionErrorCode.ExistingFileMismatch,
                    $"Required generated-file source '{recipe.SourcePath}' isn't a present regular file."
                );
            if (rejectChangedResolvedIdentity && recipe.SourceIdentity is not null && recipe.SourceIdentity != identity)
            {
                throw new ExecutionCompilationException(
                    ExecutionCompilationError.StaleManifest,
                    $"Generated-file source '{recipe.SourcePath}' changed after inspection."
                );
            }
            if (!sources.TryAdd(recipe.SourcePath.Value, identity) && sources[recipe.SourcePath.Value] != identity)
                throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, "Generated recipes disagree about one source identity.");
        }
        return sources;
    }

    public LinuxAnchoredFile OpenFile(PackageManifestEntry expected, CancellationToken cancellationToken = default)
    {
        if (expected.Kind == OwnedEntryKind.GeneratedFile)
            throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, "A generated game file isn't package-backed.");
        PackageManifestEntry packageEntry = this.Package.Manifest.Entries.SingleOrDefault(entry =>
            entry.Path.Equals(expected.Path)
            && entry.Kind == expected.Kind
            && entry.Sha256 == expected.Sha256
            && entry.SizeBytes == expected.SizeBytes
            && entry.UnixMode == expected.UnixMode
        ) ?? throw new ExecutionCompilationException(ExecutionCompilationError.StaleManifest, "A resolved package entry isn't present in its verified package template.");
        return this.Package.OpenFile(packageEntry, cancellationToken);
    }

    public void AssertUsable() => this.Package.AssertUsable();

    private static LinuxFileIdentity? ReadSourceIdentity(
        LinuxAnchoredFileSystem game,
        NormalizedRelativePath sourcePath
    )
    {
        try
        {
            return game.Stat(sourcePath.Value);
        }
        catch (IOException ex)
        {
            throw new InstallerTransactionException(
                TransactionErrorCode.ExistingFileMismatch,
                $"Generated-file source '{sourcePath}' isn't a safe unique regular file.",
                ex
            );
        }
    }
}
