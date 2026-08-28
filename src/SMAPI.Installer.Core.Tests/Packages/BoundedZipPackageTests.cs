using System.IO.Compression;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
public sealed class BoundedZipPackageTests
{
    private const string ExpectedRoot = "SMAPI synthetic Linux installer";

    private string TempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-zip-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public async Task InspectAndExtractAsync_ValidArchive_ExtractsWithinExpectedRoot()
    {
        string archivePath = this.CreateArchive(archive =>
        {
            AddDirectory(archive, $"{BoundedZipPackageTests.ExpectedRoot}/");
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/install on Linux.sh", "launcher");
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/internal/linux/install.dat", "payload");
        });
        string destination = Path.Combine(this.TempRoot, "extracted");
        BoundedZipPackage package = new();

        ZipPackageInspection result = await package.InspectAndExtractAsync(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            destination,
            PermissiveLimits()
        );

        result.EntryCount.Should().Be(3);
        File.ReadAllText(Path.Combine(destination, BoundedZipPackageTests.ExpectedRoot, "install on Linux.sh"))
            .Should().Be("launcher");
        File.ReadAllText(Path.Combine(destination, BoundedZipPackageTests.ExpectedRoot, "internal/linux/install.dat"))
            .Should().Be("payload");
    }

    [TestCase("../escape")]
    [TestCase("/absolute")]
    [TestCase("C:/absolute")]
    [TestCase("SMAPI synthetic Linux installer/../escape")]
    [TestCase("SMAPI synthetic Linux installer\\escape")]
    [TestCase("SMAPI synthetic Linux installer//escape")]
    public void Inspect_UnsafePath_Rejects(string entryPath)
    {
        string archivePath = this.CreateArchive(archive => AddFile(archive, entryPath, "bad"));
        BoundedZipPackage package = new();

        Action action = () => package.Inspect(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            PermissiveLimits()
        );

        action.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void Inspect_UnexpectedRoot_Rejects()
    {
        string archivePath = this.CreateArchive(archive => AddFile(archive, "Other root/file", "bad"));
        BoundedZipPackage package = new();

        Action action = () => package.Inspect(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            PermissiveLimits()
        );

        action.Should().Throw<PackageSecurityException>().WithMessage("*top-level*");
    }

    [Test]
    public void Inspect_ExpectedRootAsFile_Rejects()
    {
        string archivePath = this.CreateArchive(archive =>
            AddFile(archive, BoundedZipPackageTests.ExpectedRoot, "not a directory")
        );
        BoundedZipPackage package = new();

        Action action = () => package.Inspect(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            PermissiveLimits()
        );

        action.Should().Throw<PackageSecurityException>().WithMessage("*isn't a directory*");
    }

    [Test]
    public void Inspect_DuplicatePath_Rejects()
    {
        string archivePath = this.CreateArchive(archive =>
        {
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/file", "one");
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/file", "two");
        });
        BoundedZipPackage package = new();

        Action action = () => package.Inspect(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            PermissiveLimits()
        );

        action.Should().Throw<PackageSecurityException>().WithMessage("*duplicate*");
    }

    [Test]
    public void Inspect_CaseCollidingPath_Rejects()
    {
        string archivePath = this.CreateArchive(archive =>
        {
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/File", "one");
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/file", "two");
        });
        BoundedZipPackage package = new();

        Action action = () => package.Inspect(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            PermissiveLimits()
        );

        action.Should().Throw<PackageSecurityException>().WithMessage("*case-colliding*");
    }

    [Test]
    public void Inspect_CaseCollidingParentDirectories_Rejects()
    {
        string archivePath = this.CreateArchive(archive =>
        {
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/Folder/one", "one");
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/folder/two", "two");
        });
        BoundedZipPackage package = new();

        Action action = () => package.Inspect(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            PermissiveLimits()
        );

        action.Should().Throw<PackageSecurityException>().WithMessage("*case-colliding path segments*");
    }

    [Test]
    public void Inspect_FileUsedAsParentDirectory_Rejects()
    {
        string archivePath = this.CreateArchive(archive =>
        {
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/parent", "file");
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/parent/child", "child");
        });
        BoundedZipPackage package = new();

        Action action = () => package.Inspect(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            PermissiveLimits()
        );

        action.Should().Throw<PackageSecurityException>().WithMessage("*parent directory*");
    }

    [TestCase(0xA000)] // symbolic link
    [TestCase(0x2000)] // character device
    [TestCase(0x6000)] // block device
    [TestCase(0x1000)] // FIFO
    [TestCase(0xC000)] // socket
    public void Inspect_SpecialUnixEntry_Rejects(int unixType)
    {
        string archivePath = this.CreateArchive(archive =>
        {
            ZipArchiveEntry entry = AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/special", "target");
            entry.ExternalAttributes = unchecked((int)((uint)(unixType | 0x1FF) << 16));
        });
        BoundedZipPackage package = new();

        Action action = () => package.Inspect(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            PermissiveLimits()
        );

        action.Should().Throw<PackageSecurityException>().WithMessage("*link, device, socket, or FIFO*");
    }

    [Test]
    public void Inspect_EntryCountLimit_Rejects()
    {
        string archivePath = this.CreateArchive(archive =>
        {
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/one", "1");
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/two", "2");
        });
        BoundedZipPackage package = new();

        Action action = () => package.Inspect(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            new ZipPackageLimits(1024 * 1024, 1, 20, 1024, 2048, 100)
        );

        action.Should().Throw<PackageSecurityException>().WithMessage("*too many entries*");
    }

    [Test]
    public void Inspect_DepthLimit_Rejects()
    {
        string archivePath = this.CreateArchive(archive =>
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/one/two/three", "deep")
        );
        BoundedZipPackage package = new();

        Action action = () => package.Inspect(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            new ZipPackageLimits(1024 * 1024, 10, 3, 1024, 2048, 100)
        );

        action.Should().Throw<PackageSecurityException>().WithMessage("*deep*");
    }

    [Test]
    public void Inspect_ExpandedEntryOrTotalLimit_Rejects()
    {
        string archivePath = this.CreateArchive(archive =>
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/large", new byte[128], CompressionLevel.NoCompression)
        );
        BoundedZipPackage package = new();

        Action action = () => package.Inspect(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            new ZipPackageLimits(1024 * 1024, 10, 10, 64, 64, 100)
        );

        action.Should().Throw<PackageSecurityException>().WithMessage("*expanded size*");
    }

    [Test]
    public void Inspect_CompressionRatioLimit_Rejects()
    {
        string archivePath = this.CreateArchive(archive =>
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/compressible", new byte[100_000])
        );
        BoundedZipPackage package = new();

        Action action = () => package.Inspect(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            new ZipPackageLimits(1024 * 1024, 10, 10, 200_000, 200_000, 2)
        );

        action.Should().Throw<PackageSecurityException>().WithMessage("*compression-ratio*");
    }

    [Test]
    public async Task InspectAndExtractAsync_PreexistingDestination_RejectsWithoutMutation()
    {
        string archivePath = this.CreateArchive(archive =>
            AddFile(archive, $"{BoundedZipPackageTests.ExpectedRoot}/file", "payload")
        );
        string destination = Path.Combine(this.TempRoot, "existing");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "sentinel"), "preserve");
        BoundedZipPackage package = new();

        Func<Task> action = () => package.InspectAndExtractAsync(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            destination,
            PermissiveLimits()
        );

        await action.Should().ThrowAsync<PackageSecurityException>();
        File.ReadAllText(Path.Combine(destination, "sentinel")).Should().Be("preserve");
    }

    [Test]
    public void Inspect_CorruptZip_ReportsPackageSecurityFailure()
    {
        string archivePath = Path.Combine(this.TempRoot, "corrupt.zip");
        File.WriteAllText(archivePath, "this is not a ZIP archive");
        BoundedZipPackage package = new();

        Action action = () => package.Inspect(
            archivePath,
            BoundedZipPackageTests.ExpectedRoot,
            PermissiveLimits()
        );

        action.Should().Throw<PackageSecurityException>().WithMessage("*structurally valid ZIP*");
    }

    private string CreateArchive(Action<ZipArchive> build)
    {
        string archivePath = Path.Combine(this.TempRoot, $"archive-{Guid.NewGuid():N}.zip");
        using FileStream stream = File.Create(archivePath);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: false);
        build(archive);
        return archivePath;
    }

    private static ZipArchiveEntry AddFile(
        ZipArchive archive,
        string path,
        string content,
        CompressionLevel compressionLevel = CompressionLevel.Optimal
    )
    {
        return AddFile(archive, path, System.Text.Encoding.UTF8.GetBytes(content), compressionLevel);
    }

    private static ZipArchiveEntry AddFile(
        ZipArchive archive,
        string path,
        byte[] content,
        CompressionLevel compressionLevel = CompressionLevel.Optimal
    )
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, compressionLevel);
        using Stream output = entry.Open();
        output.Write(content);
        return entry;
    }

    private static void AddDirectory(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.ExternalAttributes = unchecked((int)((uint)(0x4000 | 0x1ED) << 16));
    }

    private static ZipPackageLimits PermissiveLimits()
    {
        return new ZipPackageLimits(
            maxArchiveBytes: 2 * 1024 * 1024,
            maxEntries: 100,
            maxDepth: 20,
            maxEntryExpandedBytes: 1024 * 1024,
            maxTotalExpandedBytes: 2 * 1024 * 1024,
            maxCompressionRatio: 1000
        );
    }
}
