using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
public sealed class ReleasePackageVerifierTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Tree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const int AddSeals = 1033;
    private const int GetSeals = 1034;
    private const int SealSeal = 0x0001;
    private const int SealShrink = 0x0002;
    private const int SealGrow = 0x0004;
    private const int SealWrite = 0x0008;
    private const int RequiredSeals = SealSeal | SealShrink | SealGrow | SealWrite;

    private string TempRoot = null!;
    private ForkReleaseIdentity Identity = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-package-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempRoot);
        this.Identity = ForkReleaseIdentity.Parse(
            "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1"
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public async Task VerifyAsync_AllArtifactsAgree_ReturnsVerifiedIdentity()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        ReleasePackageVerifier verifier = new();

        await using VerifiedReleasePackage result = await verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );

        result.Sha256.Should().Be(hash);
        result.SizeBytes.Should().Be(bytes.Length);
        result.SourceCommit.Should().Be(ReleasePackageVerifierTests.Commit);
        result.SourceTree.Should().Be(ReleasePackageVerifierTests.Tree);
        result.InstallationIdentity.Tag.Should().Be(this.Identity.Tag);
        result.InstallationIdentity.EmbeddedVersion.Should().Be(this.Identity.EmbeddedVersion);
        result.InstallationIdentity.PackageAssetName.Should().Be(this.Identity.PackageAssetName);
        result.InstallationIdentity.SourceCommit.Should().Be(ReleasePackageVerifierTests.Commit);
        result.InstallationIdentity.SourceTree.Should().Be(ReleasePackageVerifierTests.Tree);
        result.InstallationIdentity.PackageSha256.Value.Should().Be(hash);
        result.InstallationIdentity.PackageSizeBytes.Should().Be(bytes.Length);
        result.InstallationIdentity.BuildWorkflow.Should().Be(
            $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}"
        );
        result.InstallationIdentity.BuildConfiguration.Should().Be("Release");
        result.InstallationIdentity.RuntimeIdentifier.Should().Be("linux-x64");
    }

    [Test]
    public async Task VerifyAsync_ArtifactsArrayMetadata_RemainsCompatible()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();

        await using VerifiedReleasePackage result = await new ReleasePackageVerifier().VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length, useArtifactsArray: true),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );

        result.Sha256.Should().Be(hash);
    }

    [Test]
    [Platform("Linux")]
    public async Task LeasePackageForExternalRead_SourceReplacementAndDeletionCannotChangeExactAuthority()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        await using VerifiedReleasePackage verified = await new ReleasePackageVerifier().VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );
        string original = path + ".original";
        File.Move(path, original);
        File.WriteAllBytes(path, "replacement package bytes"u8.ToArray());
        File.Delete(path);

        using LinuxSealedFileLease lease = verified.LeasePackageForExternalRead();

        File.ReadAllBytes(lease.ProcPath).Should().Equal(bytes);
        Action overwrite = () => File.WriteAllBytes(lease.ProcPath, "changed"u8.ToArray());
        Exception error = overwrite.Should().Throw<Exception>().Which;
        (error is IOException or UnauthorizedAccessException).Should().BeTrue();
        File.ReadAllBytes(lease.ProcPath).Should().Equal(bytes);
    }

    [Test]
    [Platform("Linux")]
    public async Task LeasePackageForExternalRead_LeaseSurvivesOwnerDisposalAndPreventsDescriptorReuse()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        await using VerifiedReleasePackage verified = await new ReleasePackageVerifier().VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );
        using LinuxSealedFileLease lease = verified.LeasePackageForExternalRead();
        string procPath = lease.ProcPath;
        int retainedDescriptor = int.Parse(Path.GetFileName(procPath), System.Globalization.CultureInfo.InvariantCulture);

        await verified.DisposeAsync();
        using SafeFileHandle next = LinuxSealedFile.CreateAnonymous("smapi-installer-package-fd-nonreuse-test");

        checked((int)next.DangerousGetHandle()).Should().NotBe(retainedDescriptor);
        File.ReadAllBytes(procPath).Should().Equal(bytes);
        Action reuse = () => verified.LeasePackageForExternalRead().Dispose();
        reuse.Should().Throw<ObjectDisposedException>();
        lease.Dispose();
        File.Exists(procPath).Should().BeFalse();
        verified.Dispose();
    }

    [Test]
    public async Task VerifyInstallerPackage_CrossVerifiedCompanion_ReturnsOpaqueAuthority()
    {
        (string packagePath, byte[] packageBytes, string packageHash) = this.CreatePackage();
        string workflow = $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}";
        InstallationReleaseIdentity release = new(
            InstallationReleaseIdentity.ReviewedRepository,
            this.Identity.Tag,
            this.Identity.EmbeddedVersion,
            this.Identity.PackageAssetName,
            ReleasePackageVerifierTests.Commit,
            ReleasePackageVerifierTests.Tree,
            Sha256Digest.Parse(packageHash),
            packageBytes.Length,
            workflow,
            "Release",
            "linux-x64"
        );
        PackageManifest manifest = new(
            release,
            [
                new PackageManifestEntry(
                    NormalizedRelativePath.Parse("StardewValley"),
                    Sha256Digest.Parse(new string('d', 64)),
                    42,
                    493,
                    OwnedEntryKind.Launcher
                )
            ]
        );
        byte[] manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifest.ToCanonicalJson());
        string manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string manifestName = VerifiedInstallerPackageFactory.GetManifestAssetName(this.Identity);
        string manifestPath = Path.Combine(this.TempRoot, manifestName);
        File.WriteAllBytes(manifestPath, manifestBytes);
        string checksums = $"{packageHash}  {this.Identity.PackageAssetName}\n{manifestHash}  {manifestName}\n";

        VerifiedReleasePackage verified = await new ReleasePackageVerifier().VerifyAsync(
            packagePath,
            checksums,
            this.CreateMetadata(
                packageHash,
                packageBytes.Length,
                companion: (manifestName, manifestBytes.Length, manifestHash)
            ),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );
        await using VerifiedInstallerPackage authority = await new VerifiedInstallerPackageFactory().VerifyAsync(
            verified,
            manifestPath
        );

        authority.Release.Should().Be(release);
        authority.ManifestSha256.Value.Should().Be(manifestHash);
        authority.Manifest.Entries.Should().ContainSingle(entry => entry.Path.Value == "StardewValley");
    }

    [Test]
    public async Task VerifyInstallerPackage_ChangedCompanion_Rejects()
    {
        (string packagePath, byte[] packageBytes, string packageHash) = this.CreatePackage();
        string manifestName = VerifiedInstallerPackageFactory.GetManifestAssetName(this.Identity);
        byte[] manifestBytes = "not a canonical manifest"u8.ToArray();
        string manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string manifestPath = Path.Combine(this.TempRoot, manifestName);
        File.WriteAllBytes(manifestPath, manifestBytes);
        string checksums = $"{packageHash}  {this.Identity.PackageAssetName}\n{manifestHash}  {manifestName}\n";
        await using VerifiedReleasePackage verified = await new ReleasePackageVerifier().VerifyAsync(
            packagePath,
            checksums,
            this.CreateMetadata(
                packageHash,
                packageBytes.Length,
                companion: (manifestName, manifestBytes.Length, manifestHash)
            ),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );
        File.WriteAllBytes(manifestPath, "same size but different bytes"u8.ToArray());

        Func<Task> action = () => new VerifiedInstallerPackageFactory().VerifyAsync(verified, manifestPath);

        await action.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyAsync_DuplicateUnknownOrOverdeepMetadata_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        string valid = this.CreateMetadata(hash, bytes.Length);
        string duplicate = "{\"schema_version\":999," + valid[1..];
        string unknownRoot = valid[..^1] + ",\"unknown\":true}";
        string unknownArtifact = valid.Replace(
            $"\"sha256\":\"{hash}\"",
            $"\"sha256\":\"{hash}\",\"unknown\":true",
            StringComparison.Ordinal
        );
        string duplicateArtifact = valid.Replace(
            $"\"sha256\":\"{hash}\"",
            $"\"sha256\":\"{hash}\",\"sha256\":\"{hash}\"",
            StringComparison.Ordinal
        );
        string bothArtifactShapes = valid[..^1] + ",\"artifacts\":[]}";
        string overdeep = "{\"schema_version\":1,\"release\":"
            + new string('[', 12)
            + "null"
            + new string(']', 12)
            + "}";

        foreach (string metadata in new[]
        {
            duplicate,
            unknownRoot,
            unknownArtifact,
            duplicateArtifact,
            bothArtifactShapes,
            overdeep
        })
        {
            Func<Task> action = () => new ReleasePackageVerifier().VerifyAsync(
                path,
                $"{hash}  {this.Identity.PackageAssetName}\n",
                metadata,
                this.Identity,
                ReleasePackageVerifierTests.Commit
            );
            PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
            exception.FailureKind.Should().Be(PackageSecurityFailureKind.MetadataRejected);
        }
    }

    [Test]
    public async Task VerifyThenReplaceSource_ExtractionUsesExactRetainedSealedHandle()
    {
        const string expectedRoot = "SMAPI synthetic Linux installer";
        (string path, byte[] bytes, string hash) = this.CreateZipPackage(expectedRoot, "verified");
        SafeFileHandle? retainedHandle = null;
        ReleasePackageVerifier verifier = new(new ReleasePackageVerifierFaults(AfterMemfdSeal: handle =>
        {
            retainedHandle = handle;
            (fcntl(handle, ReleasePackageVerifierTests.GetSeals, 0) & ReleasePackageVerifierTests.RequiredSeals)
                .Should().Be(ReleasePackageVerifierTests.RequiredSeals);
        }));
        VerifiedReleasePackage verified = await verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );
        if (OperatingSystem.IsLinux())
            retainedHandle.Should().NotBeNull();

        File.WriteAllBytes(path, CreateZipBytes(expectedRoot, "replacement"));
        string extraction = Path.Combine(this.TempRoot, "extracted");
        await new BoundedZipPackage().InspectAndExtractAsync(
            verified,
            expectedRoot,
            extraction,
            new ZipPackageLimits(1024 * 1024, 10, 10, 1024, 4096, 1000)
        );

        File.ReadAllText(Path.Combine(extraction, expectedRoot, "payload.txt")).Should().Be("verified");
        if (OperatingSystem.IsLinux())
        {
            Convert.ToInt32(File.GetUnixFileMode(extraction) & (UnixFileMode)0x1ff)
                .Should().Be(Convert.ToInt32("700", 8));
            Convert.ToInt32(File.GetUnixFileMode(Path.Combine(extraction, expectedRoot)) & (UnixFileMode)0x1ff)
                .Should().Be(Convert.ToInt32("700", 8));
            Convert.ToInt32(File.GetUnixFileMode(Path.Combine(extraction, expectedRoot, "payload.txt")) & (UnixFileMode)0x1ff)
                .Should().Be(Convert.ToInt32("600", 8));
        }

        await verified.DisposeAsync();
        if (OperatingSystem.IsLinux())
            retainedHandle!.IsClosed.Should().BeTrue();
    }

    [TestCase("cancel")]
    [TestCase("seal-failure")]
    [Platform("Linux")]
    public async Task VerifyAsync_MemfdSealCancellationOrFailure_ClosesExactDescriptor(string kind)
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        SafeFileHandle? captured = null;
        ReleasePackageVerifier verifier = new(new ReleasePackageVerifierFaults(BeforeMemfdSeal: handle =>
        {
            captured = handle;
            if (kind == "cancel")
                throw new OperationCanceledException("synthetic cancellation before sealing");
            fcntl(handle, ReleasePackageVerifierTests.AddSeals, ReleasePackageVerifierTests.SealSeal)
                .Should().Be(0, $"synthetic pre-seal failed with errno {Marshal.GetLastWin32Error()}");
        }));

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );

        if (kind == "cancel")
            await action.Should().ThrowAsync<OperationCanceledException>();
        else
            await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*seal*");
        captured.Should().NotBeNull();
        captured!.IsClosed.Should().BeTrue();
    }

    [Test]
    [Platform("Linux")]
    public async Task VerifyAsync_MemfdEntryPointUnavailable_FailsClosedWithBoundedError()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        ReleasePackageVerifier verifier = new(new ReleasePackageVerifierFaults(
            CreateMemfdOverride: () => throw new EntryPointNotFoundException("private native detail")
        ));

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );

        await action.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("*doesn't provide the required anonymous sealed-package staging support*");
    }

    [Test]
    [Platform("Linux")]
    public async Task UseVerifiedStream_PreSealWriteAliasIsKernelImmutableBeforeZipConsumption()
    {
        const string expectedRoot = "SMAPI synthetic Linux installer";
        (string path, byte[] bytes, string hash) = this.CreateZipPackage(expectedRoot, "verified");
        bool faultReached = false;
        SafeFileHandle? retainedHandle = null;
        SafeFileHandle? writeAlias = null;
        ReleasePackageVerifier verifier = new(new ReleasePackageVerifierFaults(
            AfterMemfdSeal: handle => retainedHandle = handle,
            AfterPreUseHash: (stream, alias) =>
            {
                faultReached = true;
                writeAlias = alias;
                alias.Should().NotBeNull();
                (fcntl(alias!, ReleasePackageVerifierTests.GetSeals, 0) & ReleasePackageVerifierTests.RequiredSeals)
                    .Should().Be(ReleasePackageVerifierTests.RequiredSeals);
                stream.CanWrite.Should().BeFalse();
                Action managedWrite = () => stream.WriteByte(0x41);
                managedWrite.Should().Throw<NotSupportedException>();
                Action nativeWrite = () => RandomAccess.Write(alias!, new byte[] { 0x41 }, 0);
                Exception nativeWriteError = nativeWrite.Should().Throw<Exception>().Which;
                (nativeWriteError is IOException || nativeWriteError is UnauthorizedAccessException).Should().BeTrue();
                ftruncate(alias!, bytes.Length - 1).Should().Be(-1, "F_SEAL_SHRINK must reject truncation");
                ftruncate(alias!, bytes.Length + 1).Should().Be(-1, "F_SEAL_GROW must reject extension");
            }
        ));
        VerifiedReleasePackage verified = await verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );
        string extraction = Path.Combine(this.TempRoot, "immutable-extraction");

        try
        {
            await new BoundedZipPackage().InspectAndExtractAsync(
                verified,
                expectedRoot,
                extraction,
                new ZipPackageLimits(1024 * 1024, 10, 10, 1024, 4096, 1000)
            );

            faultReached.Should().BeTrue();
            File.ReadAllText(Path.Combine(extraction, expectedRoot, "payload.txt")).Should().Be("verified");
        }
        finally
        {
            await verified.DisposeAsync();
        }
        retainedHandle.Should().NotBeNull();
        writeAlias.Should().NotBeNull();
        retainedHandle!.IsClosed.Should().BeTrue();
        writeAlias!.IsClosed.Should().BeTrue();
    }

    [TestCase("digest")]
    [TestCase("writable-authority")]
    public async Task UseVerifiedStream_ChangedIntegrityAuthorityIsClassifiedAtSource(string kind)
    {
        byte[] bytes = "synthetic retained package authority"u8.ToArray();
        string actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string recordedHash = kind == "digest" ? new string('0', 64) : actualHash;
        MemoryStream stream = new(bytes, writable: kind == "writable-authority");
        using VerifiedReleasePackage package = new(
            this.Identity,
            recordedHash,
            bytes.LongLength,
            Commit,
            Tree,
            $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}",
            "Release",
            "linux-x64",
            [new VerifiedReleaseArtifactIdentity(this.Identity.PackageAssetName, bytes.LongLength, recordedHash)],
            stagingDirectory: null,
            stagingPath: null,
            stream
        );

        Func<Task> consume = () => package.UseVerifiedStreamAsync(
            (_, _) => Task.FromResult(true),
            CancellationToken.None
        );

        PackageSecurityException exception = (await consume.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.IntegrityRejected);
    }

    [Test]
    public async Task VerifyAsync_ChecksumDoesNotMatchPackage_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        string otherHash = new('0', 64);
        ReleasePackageVerifier verifier = new();

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            $"{otherHash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(otherHash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>())
            .WithMessage("*SHA256SUMS*")
            .Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.IntegrityRejected);
    }

    [Test]
    public async Task VerifyAsync_MetadataHashDoesNotMatchPackage_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        ReleasePackageVerifier verifier = new();

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(new string('0', 64), bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>())
            .WithMessage("*build-metadata.json*")
            .Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.MetadataRejected);
    }

    [Test]
    public async Task VerifyAsync_MetadataIdentityMismatch_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        ReleasePackageVerifier verifier = new();
        string metadata = this.CreateMetadata(hash, bytes.Length, repository: "https://github.com/other/repo");

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            metadata,
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>())
            .WithMessage("*repository*")
            .Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.ReleaseIdentityRejected);
    }

    [Test]
    public async Task VerifyAsync_ReleaseTargetCommitMismatch_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        ReleasePackageVerifier verifier = new();

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            new string('c', 40)
        );

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>())
            .WithMessage("*release target*")
            .Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.ReleaseIdentityRejected);
    }

    [Test]
    public async Task VerifyAsync_DuplicateOrUnexpectedChecksumEntry_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        ReleasePackageVerifier verifier = new();
        string checksums = $"{hash}  {this.Identity.PackageAssetName}\n{hash}  other.zip\n";

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            checksums,
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.MetadataRejected);
    }

    [Test]
    public async Task VerifyAsync_MalformedChecksumDocument_RejectsAsIntegrity()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();

        Func<Task> action = () => new ReleasePackageVerifier().VerifyAsync(
            path,
            $"not-a-sha256  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.IntegrityRejected);
    }

    [Test]
    public async Task VerifyAsync_PackageFilenameDoesNotMatchRelease_RejectsAsReleaseIdentity()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        string mismatchedPath = Path.Combine(this.TempRoot, "unrelated-installer.zip");
        File.Move(path, mismatchedPath);

        Func<Task> action = () => new ReleasePackageVerifier().VerifyAsync(
            mismatchedPath,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.ReleaseIdentityRejected);
    }

    [Test]
    public async Task VerifyAsync_BoundedDocumentsAndPackage_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        ReleasePackageVerifier verifier = new();
        PackageVerificationLimits limits = new(
            maxPackageBytes: bytes.Length - 1,
            maxChecksumBytes: 1024,
            maxMetadataBytes: 4096
        );

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit,
            limits: limits
        );

        await action.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("*size*");
    }

    private (string Path, byte[] Bytes, string Hash) CreatePackage()
    {
        byte[] bytes = "synthetic installer package"u8.ToArray();
        string path = Path.Combine(this.TempRoot, this.Identity.PackageAssetName);
        File.WriteAllBytes(path, bytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (path, bytes, hash);
    }

    private string CreateMetadata(
        string hash,
        long size,
        string? repository = null,
        bool useArtifactsArray = false,
        (string Name, long Size, string Hash)? companion = null
    )
    {
        object release = new { version = this.Identity.EmbeddedVersion, tag = this.Identity.Tag };
        object source = new
        {
            repository = repository ?? ForkReleaseIdentity.RepositoryUrl,
            commit = ReleasePackageVerifierTests.Commit,
            tree = ReleasePackageVerifierTests.Tree
        };
        object build = new
        {
            workflow = $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}",
            run = "https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/1/attempts/1",
            runner_image = "ubuntu24",
            runner_arch = "X64",
            reference_assemblies_commit = new string('c', 40),
            configuration = "Release",
            runtime_identifier = "linux-x64",
            timestamp_utc = "2026-08-28T00:00:00Z",
            dotnet_info = ".NET SDK synthetic"
        };
        object artifact = new { name = this.Identity.PackageAssetName, size_bytes = size, sha256 = hash };
        object[] artifacts = companion is { } companionArtifact
            ?
            [
                artifact,
                new { name = companionArtifact.Name, size_bytes = companionArtifact.Size, sha256 = companionArtifact.Hash }
            ]
            : [artifact];
        return useArtifactsArray || companion != null
            ? JsonSerializer.Serialize(new
            {
                schema_version = 1,
                release,
                source,
                build,
                artifacts,
                reproducibility = "Inputs recorded; byte equality not claimed."
            })
            : JsonSerializer.Serialize(new
            {
                schema_version = 1,
                release,
                source,
                build,
                artifact,
                reproducibility = "Inputs recorded; byte equality not claimed."
            });
    }

    private (string Path, byte[] Bytes, string Hash) CreateZipPackage(string expectedRoot, string contents)
    {
        byte[] bytes = CreateZipBytes(expectedRoot, contents);
        string path = Path.Combine(this.TempRoot, this.Identity.PackageAssetName);
        File.WriteAllBytes(path, bytes);
        return (path, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static byte[] CreateZipBytes(string expectedRoot, string contents)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry root = archive.CreateEntry(expectedRoot + "/", CompressionLevel.NoCompression);
            root.ExternalAttributes = unchecked((int)((uint)(0x4000 | 0x1ED) << 16));
            ZipArchiveEntry payload = archive.CreateEntry(expectedRoot + "/payload.txt", CompressionLevel.Optimal);
            using StreamWriter writer = new(payload.Open());
            writer.Write(contents);
        }
        return stream.ToArray();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(SafeFileHandle descriptor, int command, int argument);

    [DllImport("libc", SetLastError = true)]
    private static extern int ftruncate(SafeFileHandle descriptor, long length);
}
