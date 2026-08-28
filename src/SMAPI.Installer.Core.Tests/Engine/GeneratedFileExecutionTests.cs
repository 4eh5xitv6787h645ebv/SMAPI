using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Recovery;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Tests.Ownership;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Tests.Engine;

[TestFixture]
[SupportedOSPlatform("linux")]
public sealed class GeneratedFileExecutionTests
{
    private const string SourcePath = "Stardew Valley.deps.json";
    private const string ResultPath = "StardewModdingAPI-net6.deps.json";
    private readonly List<string> TemporaryDirectories = new();

    [TearDown]
    public void TearDown()
    {
        foreach (string path in this.TemporaryDirectories)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Best-effort private test cleanup.
            }
        }
    }

    [Test]
    public void Install_DerivesExactResultAndPersistsRecipeAndResultAuthority()
    {
        string game = this.CreateGame("{\"runtimeTarget\":\"net6.0\"}", 0x1a0);
        using TemplatePackageAuthority package = this.CreatePackage();
        LinuxInstallerEngine engine = new();

        using (InspectedInstallationState inspection = Inspect(engine, game, package))
        {
            inspection.Plan.CanExecute.Should().BeTrue();
            PlannedOperation generated = inspection.Plan.Operations.Single(operation => operation.Path.Value == ResultPath);
            generated.ResultSha256.Should().Be(Hash("{\"runtimeTarget\":\"net6.0\"}"));
            engine.ExecuteAsync(inspection, inspection.ConfirmationDigest).GetAwaiter().GetResult().Status
                .Should().Be(TransactionStatus.Committed);
        }

        File.ReadAllText(Path.Combine(game, ResultPath)).Should().Be("{\"runtimeTarget\":\"net6.0\"}");
        File.GetUnixFileMode(Path.Combine(game, ResultPath)).Should().Be((UnixFileMode)0x1a0);
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
        PackageManifest committed = state.Manifest!;
        GeneratedFileRecipe recipe = committed.GeneratedFiles.Should().ContainSingle().Subject;
        recipe.SourcePath.Value.Should().Be(SourcePath);
        recipe.SourceIdentity.Should().Be(new RecoveryFileIdentity(Hash("{\"runtimeTarget\":\"net6.0\"}"), 26, 0x1a0));
        PackageManifestEntry result = committed.Entries.Single(entry => entry.Path.Value == ResultPath);
        result.Kind.Should().Be(OwnedEntryKind.GeneratedFile);
        result.Sha256.Should().Be(recipe.SourceIdentity!.Sha256);
        state.Receipt!.Entries.Single(entry => entry.Path.Value == ResultPath).InstalledSha256.Should().Be(result.Sha256);
        state.Receipt.ManifestSha256.Should().Be(committed.GetCanonicalDigest());
    }

    [Test]
    public void Execution_SourceContentChangesAfterInspection_FailsBeforeMutation()
    {
        string game = this.CreateGame("first", 0x1a4);
        using TemplatePackageAuthority package = this.CreatePackage();
        LinuxInstallerEngine engine = new();
        using InspectedInstallationState inspection = Inspect(engine, game, package);
        File.WriteAllText(Path.Combine(game, SourcePath), "second");

        Action execute = () => engine.ExecuteAsync(inspection, inspection.ConfirmationDigest).GetAwaiter().GetResult();

        execute.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StaleManifest);
        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("vanilla launcher");
        File.Exists(Path.Combine(game, ResultPath)).Should().BeFalse();
    }

    [Test]
    public void Execution_SourceModeChangesAfterInspection_FailsBeforeMutation()
    {
        string game = this.CreateGame("same bytes", 0x1a4);
        using TemplatePackageAuthority package = this.CreatePackage();
        LinuxInstallerEngine engine = new();
        using InspectedInstallationState inspection = Inspect(engine, game, package);
        File.SetUnixFileMode(Path.Combine(game, SourcePath), (UnixFileMode)0x180);

        Action execute = () => engine.ExecuteAsync(inspection, inspection.ConfirmationDigest).GetAwaiter().GetResult();

        execute.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StaleManifest);
        File.Exists(Path.Combine(game, ResultPath)).Should().BeFalse();
    }

    [Test]
    public void Inspection_SourceIsSymbolicLink_IsRejectedWithoutFollowingIt()
    {
        string game = this.CreateGameWithoutSource();
        string outside = Path.Combine(this.CreateDirectory(), "outside.json");
        File.WriteAllText(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(game, SourcePath), outside);
        using TemplatePackageAuthority package = this.CreatePackage();
        LinuxInstallerEngine engine = new();

        Action inspect = () => Inspect(engine, game, package).Dispose();

        inspect.Should().Throw<InstallerTransactionException>();
        File.ReadAllText(outside).Should().Be("outside");
        File.Exists(Path.Combine(game, ResultPath)).Should().BeFalse();
    }

    [Test]
    public void Inspection_SourceHasAnotherHardLink_IsRejected()
    {
        string game = this.CreateGame("shared", 0x1a4);
        link(Path.Combine(game, SourcePath), Path.Combine(game, "shared-deps.json"))
            .Should().Be(0, $"link(2) failed with errno {Marshal.GetLastWin32Error()}");
        using TemplatePackageAuthority package = this.CreatePackage();
        LinuxInstallerEngine engine = new();

        Action inspect = () => Inspect(engine, game, package).Dispose();

        inspect.Should().Throw<InstallerTransactionException>();
        File.Exists(Path.Combine(game, ResultPath)).Should().BeFalse();
    }

    [Test]
    public void Inspection_SourceIsSpecialFile_IsRejectedWithoutOpeningIt()
    {
        string game = this.CreateGameWithoutSource();
        mkfifo(Path.Combine(game, SourcePath), 0x180)
            .Should().Be(0, $"mkfifo(2) failed with errno {Marshal.GetLastWin32Error()}");
        using TemplatePackageAuthority package = this.CreatePackage();
        LinuxInstallerEngine engine = new();

        Action inspect = () => Inspect(engine, game, package).Dispose();

        inspect.Should().Throw<InstallerTransactionException>();
        File.Exists(Path.Combine(game, ResultPath)).Should().BeFalse();
    }

    [Test]
    public void Inspection_SourceExceedsBound_IsRejectedBeforeHashing()
    {
        string game = this.CreateGameWithoutSource();
        using (FileStream source = File.Create(Path.Combine(game, SourcePath)))
            source.SetLength(16L * 1024 * 1024 + 1);
        using TemplatePackageAuthority package = this.CreatePackage();
        LinuxInstallerEngine engine = new();

        Action inspect = () => Inspect(engine, game, package).Dispose();

        inspect.Should().Throw<InstallerTransactionException>();
        File.Exists(Path.Combine(game, ResultPath)).Should().BeFalse();
    }

    [Test]
    public void Execution_SourceSwappedToSymbolicLink_IsRejectedWithoutFollowingIt()
    {
        string game = this.CreateGame("inside", 0x1a4);
        string outside = Path.Combine(this.CreateDirectory(), "outside.json");
        File.WriteAllText(outside, "outside");
        using TemplatePackageAuthority package = this.CreatePackage();
        LinuxInstallerEngine engine = new();
        using InspectedInstallationState inspection = Inspect(engine, game, package);
        File.Delete(Path.Combine(game, SourcePath));
        File.CreateSymbolicLink(Path.Combine(game, SourcePath), outside);

        Action execute = () => engine.ExecuteAsync(inspection, inspection.ConfirmationDigest).GetAwaiter().GetResult();

        execute.Should().Throw<InstallerTransactionException>();
        File.ReadAllText(outside).Should().Be("outside");
        File.Exists(Path.Combine(game, ResultPath)).Should().BeFalse();
    }

    [Test]
    public void Inspection_Cancelled_DoesNotCreateInstallerState()
    {
        string game = this.CreateGame("deps", 0x1a4);
        using TemplatePackageAuthority package = this.CreatePackage();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        LinuxInstallerEngine engine = new();

        Action inspect = () =>
        {
            using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
            engine.InspectLocked(lease, InstallationAction.Install, package, null, cancellationToken: cancellation.Token).Dispose();
        };

        inspect.Should().Throw<OperationCanceledException>();
        File.Exists(Path.Combine(game, ResultPath)).Should().BeFalse();
    }

    [Test]
    public void UninstallRollback_RestoresCapturedGeneratedBytesWithoutReadingChangedGameSource()
    {
        string game = this.CreateGame("original deps", 0x1a4);
        using TemplatePackageAuthority package = this.CreatePackage();
        LinuxInstallerEngine engine = new();
        Execute(Inspect(engine, game, package), engine);
        using (InspectedInstallationState uninstall = engine.InspectAsync(game, InstallationAction.Uninstall).GetAwaiter().GetResult())
            Execute(uninstall, engine);
        File.WriteAllText(Path.Combine(game, SourcePath), "new game deps");

        using CommittedRecoveryHandle recovery = engine.OpenCurrentRecoveryAsync(game).GetAwaiter().GetResult();
        using InspectedInstallationState rollback = engine.InspectAsync(game, InstallationAction.Rollback, recovery: recovery).GetAwaiter().GetResult();
        Execute(rollback, engine);

        File.ReadAllText(Path.Combine(game, ResultPath)).Should().Be("original deps");
        File.ReadAllText(Path.Combine(game, SourcePath)).Should().Be("new game deps");
    }

    private string CreateGame(string deps, int mode)
    {
        string game = this.CreateGameWithoutSource();
        Write(game, SourcePath, deps, mode);
        return game;
    }

    private string CreateGameWithoutSource()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        return game;
    }

    private TemplatePackageAuthority CreatePackage()
    {
        string payload = this.CreateDirectory();
        Write(payload, "StardewValley", "smapi launcher", 0x1ed);
        Write(payload, "StardewModdingAPI.dll", "runtime", 0x1a4);
        PackageManifest manifest = new(
            OwnershipTestData.Release(),
            new[]
            {
                Entry("StardewValley", "smapi launcher", 0x1ed, OwnedEntryKind.Launcher),
                Entry("StardewModdingAPI.dll", "runtime", 0x1a4, OwnedEntryKind.RuntimeFile)
            },
            new[]
            {
                new GeneratedFileRecipe(
                    NormalizedRelativePath.Parse(ResultPath),
                    GeneratedFileRecipe.CopyGameDepsRecipe,
                    NormalizedRelativePath.Parse(SourcePath)
                )
            }
        );
        return new TemplatePackageAuthority(manifest, payload);
    }

    private static InspectedInstallationState Inspect(
        LinuxInstallerEngine engine,
        string game,
        IVerifiedPackageContentAuthority package
    )
    {
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        return engine.InspectLocked(lease, InstallationAction.Install, package, null);
    }

    private static void Execute(InspectedInstallationState inspection, LinuxInstallerEngine engine)
    {
        using (inspection)
        {
            inspection.Plan.CanExecute.Should().BeTrue(string.Join(", ", inspection.Plan.Conflicts.Select(conflict => conflict.Code)));
            engine.ExecuteAsync(inspection, inspection.ConfirmationDigest).GetAwaiter().GetResult().Status
                .Should().Be(TransactionStatus.Committed);
        }
    }

    private string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-generated-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        this.TemporaryDirectories.Add(path);
        return path;
    }

    private static PackageManifestEntry Entry(string path, string contents, int mode, OwnedEntryKind kind)
        => new(NormalizedRelativePath.Parse(path), Hash(contents), Encoding.UTF8.GetByteCount(contents), mode, kind);

    private static Sha256Digest Hash(string contents) => Sha256Digest.Hash(Encoding.UTF8.GetBytes(contents));

    private static void Write(string root, string relativePath, string contents, int mode)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, (UnixFileMode)mode);
    }

    private sealed class TemplatePackageAuthority : IVerifiedPackageContentAuthority, IDisposable
    {
        private readonly LinuxAnchoredFileSystem Payload;
        public PackageManifest Manifest { get; }
        public Sha256Digest ManifestSha256 => this.Manifest.GetCanonicalDigest();

        public TemplatePackageAuthority(PackageManifest manifest, string payloadRoot)
        {
            this.Manifest = manifest;
            this.Payload = new LinuxAnchoredFileSystem(payloadRoot);
        }

        public LinuxAnchoredFile OpenFile(PackageManifestEntry expected, CancellationToken cancellationToken = default)
        {
            this.Manifest.Entries.Should().Contain(expected);
            LinuxAnchoredFile file = this.Payload.OpenRegularFileForRead(expected.Path.Value);
            if (
                file.Identity.Size != expected.SizeBytes
                || file.Identity.UnixMode != expected.UnixMode
                || Sha256Digest.Parse(this.Payload.ComputeSha256(file, cancellationToken)) != expected.Sha256
            )
            {
                file.Dispose();
                throw new InvalidOperationException("The test package entry changed.");
            }
            return file;
        }

        public void AssertUsable() => this.Payload.GetCurrentRootIdentity().Should().Be(this.Payload.Identity);
        public void Dispose() => this.Payload.Dispose();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldPath, string newPath);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, int mode);
}
