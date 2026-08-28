using System.Text;
using System.Runtime.Versioning;
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
public sealed class InstallationExecutionMaterializerTests
{
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
    public void Apply_FreshInstall_CommitsFilesOwnershipAndExecutableRecoveryAsOneTuple()
    {
        string game = this.CreateDirectory();
        string packageRoot = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        Write(packageRoot, "StardewValley", "smapi launcher", 0x1ed);
        Write(packageRoot, "StardewModdingAPI.dll", "runtime", 0x1a4);
        PackageManifest manifest = new(
            OwnershipTestData.Release(),
            new[]
            {
                Entry("StardewValley", "smapi launcher", 0x1ed, OwnedEntryKind.Launcher),
                Entry("StardewModdingAPI.dll", "runtime", 0x1a4, OwnedEntryKind.RuntimeFile)
            }
        );
        using FilePackageAuthority package = new(manifest, packageRoot);
        Sha256Digest vanillaSha = Hash("vanilla launcher");
        InstallationPlanningRequest request = new(
            InstallationAction.Install,
            InstallationInventory.Create(
                manifest,
                null,
                [new CurrentFile(NormalizedRelativePath.Parse("StardewValley"), vanillaSha, 0x1ed)]
            ),
            LauncherState.Assess(vanillaSha, null, null),
            targetManifest: manifest,
            recoveryObservations:
            [
                new RecoveryFileObservation(
                    NormalizedRelativePath.Parse("StardewValley"),
                    new RecoveryFileIdentity(vanillaSha, Encoding.UTF8.GetByteCount("vanilla launcher"), 0x1ed)
                ),
                new RecoveryFileObservation(NormalizedRelativePath.Parse("StardewValley-original"), null),
                new RecoveryFileObservation(NormalizedRelativePath.Parse("StardewModdingAPI.dll"), null)
            ]
        );

        using (InstallerOperationLease lease = InstallerOperationLease.Acquire(game))
        {
            AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
            InstallationPlan plan = new InstallationPlanner().Plan(request);
            BoundInstallationPlan binding = new InstallerExecutionCompiler().BindPlan(
                plan,
                request,
                lease.RootIdentity,
                lease.Generation,
                package,
                currentRecoveryPointerSha256: state.PointerSha256
            );
            InstallationExecutionPreparation preparation = new InstallerExecutionCompiler().Prepare(
                binding,
                plan,
                request,
                Guid.NewGuid()
            );

            new InstallationExecutionMaterializer().Apply(lease, preparation, state).Status.Should().Be(TransactionStatus.Committed);
        }

        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("smapi launcher");
        File.GetUnixFileMode(Path.Combine(game, "StardewValley")).Should().Be((UnixFileMode)0x1ed);
        File.ReadAllText(Path.Combine(game, "StardewValley-original")).Should().Be("vanilla launcher");
        File.GetUnixFileMode(Path.Combine(game, "StardewValley-original")).Should().Be((UnixFileMode)0x1ed);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("runtime");

        using InstallerOperationLease verificationLease = InstallerOperationLease.Acquire(game);
        AnchoredCoreStateAuthority committed = AnchoredCoreStateAuthority.Inspect(verificationLease);
        using CommittedRecoveryHandle recovery = CommittedRecoveryHandle.OpenCurrent(verificationLease, committed);
        committed.ManifestSha256.Should().Be(manifest.GetCanonicalDigest());
        committed.Receipt.Should().NotBeNull();
        committed.Pointer.Should().NotBeNull();
        recovery.Action.Should().Be(InstallationAction.Install);
        recovery.Snapshot.Entries.Should().Contain(entry =>
            entry.Path.Value == "StardewValley"
            && entry.Kind == RollbackEntryKind.Restore
            && entry.BackupSha256 == vanillaSha
        );
    }

    private string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-materializer-tests-{Guid.NewGuid():N}");
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
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, (UnixFileMode)mode);
    }

    private sealed class FilePackageAuthority : IVerifiedPackageContentAuthority, IDisposable
    {
        private readonly LinuxAnchoredFileSystem Payload;
        public PackageManifest Manifest { get; }
        public Sha256Digest ManifestSha256 => this.Manifest.GetCanonicalDigest();

        public FilePackageAuthority(PackageManifest manifest, string payloadRoot)
        {
            this.Manifest = manifest;
            this.Payload = new LinuxAnchoredFileSystem(payloadRoot);
        }

        public LinuxAnchoredFile OpenFile(PackageManifestEntry expected)
        {
            if (!this.Manifest.Entries.Contains(expected))
                throw new InvalidOperationException("The requested entry isn't in this test authority.");
            LinuxAnchoredFile file = this.Payload.OpenRegularFileForRead(expected.Path.Value);
            if (
                file.Identity.Size != expected.SizeBytes
                || file.Identity.UnixMode != expected.UnixMode
                || Sha256Digest.Parse(this.Payload.ComputeSha256(file)) != expected.Sha256
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
}
