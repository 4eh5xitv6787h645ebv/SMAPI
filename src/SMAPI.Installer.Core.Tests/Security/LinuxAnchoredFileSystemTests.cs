using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Security;

[TestFixture]
public sealed class LinuxAnchoredFileSystemTests
{
    private const int OpenReadWrite = 2;
    private const int OpenNonBlocking = 0x800;
    private string TempRoot = null!;
    private string RootPath = null!;

    [SetUp]
    public void SetUp()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Anchored filesystem tests require Linux.");

        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-anchored-fs-{Guid.NewGuid():N}");
        this.RootPath = Path.Combine(this.TempRoot, "root");
        Directory.CreateDirectory(this.RootPath);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public void Constructor_SymbolicLinkRoot_Rejects()
    {
        string linkPath = Path.Combine(this.TempRoot, "linked-root");
        Directory.CreateSymbolicLink(linkPath, this.RootPath);

        Action action = () => new LinuxAnchoredFileSystem(linkPath);

        action.Should().Throw<IOException>().WithMessage("*real accessible directory*");
    }

    [TestCase("../escape")]
    [TestCase("/absolute")]
    [TestCase("a/../escape")]
    [TestCase("a\\b")]
    [TestCase("a//b")]
    public void Stat_UnsafeRelativePath_Rejects(string path)
    {
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);

        Action action = () => fileSystem.Stat(path);

        action.Should().Throw<ArgumentException>();
    }

    [Test]
    public void CreateNewFile_SymbolicLinkParent_RejectsWithoutWritingOutsideRoot()
    {
        string outside = Path.Combine(this.TempRoot, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(this.RootPath, "linked"), outside);
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);

        Action action = () => fileSystem.CreateNewFile("linked/new-file", 0x180).Dispose();

        action.Should().Throw<IOException>().WithMessage("*symbolic link or non-directory*");
        File.Exists(Path.Combine(outside, "new-file")).Should().BeFalse();
    }

    [Test]
    public void OpenRegularFileForRead_SymbolicLinkLeaf_RejectsWithoutFollowingIt()
    {
        string outside = Path.Combine(this.TempRoot, "outside.txt");
        File.WriteAllText(outside, "private");
        File.CreateSymbolicLink(Path.Combine(this.RootPath, "linked.txt"), outside);
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);

        Action action = () => fileSystem.OpenRegularFileForRead("linked.txt").Dispose();

        action.Should().Throw<IOException>();
        File.ReadAllText(outside).Should().Be("private");
    }

    [Test]
    public void OpenRegularFileForRead_HardlinkedRegularFile_Rejects()
    {
        string original = Path.Combine(this.RootPath, "original.txt");
        string linked = Path.Combine(this.RootPath, "linked.txt");
        File.WriteAllText(original, "content");
        link(original, linked).Should().Be(0, $"link(2) failed with errno {Marshal.GetLastWin32Error()}");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);

        Action action = () => fileSystem.OpenRegularFileForRead("original.txt").Dispose();

        action.Should().Throw<IOException>().WithMessage("*multiple hard links*");
    }

    [Test]
    public void OpenRegularFileForRead_Fifo_RejectsPromptlyWithoutWaitingForWriter()
    {
        string fifo = Path.Combine(this.RootPath, "pipe");
        mkfifo(fifo, 0x180).Should().Be(0, $"mkfifo(2) failed with errno {Marshal.GetLastWin32Error()}");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);

        Exception failure = CapturePromptFailure(() => fileSystem.OpenRegularFileForRead("pipe").Dispose(), fifo);

        failure.Should().BeOfType<IOException>().Which.Message.Should().Contain("unsupported special file");
    }

    [Test]
    public void Stat_Fifo_RejectsSpecialFile()
    {
        string fifo = Path.Combine(this.RootPath, "pipe");
        mkfifo(fifo, 0x180).Should().Be(0, $"mkfifo(2) failed with errno {Marshal.GetLastWin32Error()}");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);

        Action action = () => fileSystem.Stat("pipe");

        action.Should().Throw<IOException>().WithMessage("*unsupported special file*");
    }

    [Test]
    public void ComputeSha256_PathReplacedAfterOpen_HashesCapturedHandle()
    {
        string originalPath = Path.Combine(this.RootPath, "payload.bin");
        byte[] originalBytes = Encoding.UTF8.GetBytes("trusted original bytes");
        File.WriteAllBytes(originalPath, originalBytes);
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        using LinuxAnchoredFile opened = fileSystem.OpenRegularFileForRead("payload.bin");

        File.Move(originalPath, Path.Combine(this.RootPath, "moved.bin"));
        File.WriteAllText(originalPath, "replacement bytes");

        fileSystem.ComputeSha256(opened).Should().Be(Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant());
    }

    [Test]
    public void ComputeSha256_CancelledBeforeRead_StopsPromptly()
    {
        File.WriteAllText(Path.Combine(this.RootPath, "payload.bin"), "trusted bytes");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        using LinuxAnchoredFile opened = fileSystem.OpenRegularFileForRead("payload.bin");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Action hash = () => fileSystem.ComputeSha256(opened, cancellation.Token);

        hash.Should().Throw<OperationCanceledException>();
    }

    [Test]
    public void ComputeSha256_CancelledDuringLargeFile_StopsBeforeCompletion()
    {
        using (FileStream sparse = File.Create(Path.Combine(this.RootPath, "large.bin")))
            sparse.SetLength(512L * 1024 * 1024);
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        using LinuxAnchoredFile opened = fileSystem.OpenRegularFileForRead("large.bin");
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(5));

        Action hash = () => fileSystem.ComputeSha256(opened, cancellation.Token);

        hash.Should().Throw<OperationCanceledException>();
    }

    [Test]
    public void AppendAndFsync_LeafReplacedAfterOpen_RejectsWithoutWritingReplacement()
    {
        string path = Path.Combine(this.RootPath, "log");
        string outside = Path.Combine(this.TempRoot, "outside");
        File.WriteAllText(path, string.Empty);
        File.WriteAllText(outside, "preserve");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        using LinuxAnchoredFile opened = fileSystem.OpenRegularFileForRead("log");
        File.Move(path, Path.Combine(this.RootPath, "captured"));
        File.CreateSymbolicLink(path, outside);

        Action action = () => fileSystem.AppendAndFsync(opened, "log", Encoding.UTF8.GetBytes("record"), 0, 64);

        action.Should().Throw<IOException>();
        File.ReadAllText(outside).Should().Be("preserve");
        new FileInfo(Path.Combine(this.RootPath, "captured")).Length.Should().Be(0);
    }

    [Test]
    public void AppendAndFsync_ExceedsBound_RejectsBeforeWriting()
    {
        File.WriteAllText(Path.Combine(this.RootPath, "log"), "1234");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        using LinuxAnchoredFile opened = fileSystem.OpenRegularFileForRead("log");

        Action action = () => fileSystem.AppendAndFsync(opened, "log", Encoding.UTF8.GetBytes("56"), 4, 5);

        action.Should().Throw<IOException>().WithMessage("*byte bound*");
        File.ReadAllText(Path.Combine(this.RootPath, "log")).Should().Be("1234");
    }

    [Test]
    public void EnumerateEntryNames_RepeatedCallsReturnImmediateNamesDeterministically()
    {
        File.WriteAllText(Path.Combine(this.RootPath, "zeta"), string.Empty);
        File.WriteAllText(Path.Combine(this.RootPath, "alpha"), string.Empty);
        Directory.CreateDirectory(Path.Combine(this.RootPath, "middle"));
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);

        fileSystem.EnumerateEntryNames().Should().Equal("alpha", "middle", "zeta");
        fileSystem.EnumerateEntryNames().Should().Equal("alpha", "middle", "zeta");
    }

    [Test]
    public void CopyFile_OpenSource_CreatesPrivateVerifiedDestination()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "payload"), "trusted payload");
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("payload");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);
        destinationFileSystem.EnsureDirectory("stage", 0x1c0);

        LinuxFileIdentity result = destinationFileSystem.CopyFile(source, "stage/payload", 0x180);

        File.ReadAllText(Path.Combine(this.RootPath, "stage", "payload")).Should().Be("trusted payload");
        result.Kind.Should().Be(LinuxAnchoredEntryKind.RegularFile);
        result.LinkCount.Should().Be(1);
        result.UnixMode.Should().Be(0x180);
    }

    [Test]
    public void CopyFile_CancelledBeforeCopy_DoesNotCreateDestination()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "payload"), "trusted payload");
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("payload");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Action copy = () => destinationFileSystem.CopyFile(source, "payload", 0x180, cancellation.Token);

        copy.Should().Throw<OperationCanceledException>();
        File.Exists(Path.Combine(this.RootPath, "payload")).Should().BeFalse();
    }

    [Test]
    public void CopyFile_CancelledDuringLargeCopy_RemovesPartialDestination()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        using (FileStream sparse = File.Create(Path.Combine(sourceRoot, "large.bin")))
            sparse.SetLength(512L * 1024 * 1024);
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("large.bin");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(5));

        Action copy = () => destinationFileSystem.CopyFile(source, "large.bin", 0x180, cancellation.Token);

        copy.Should().Throw<OperationCanceledException>();
        File.Exists(Path.Combine(this.RootPath, "large.bin")).Should().BeFalse();
    }

    [Test]
    public void CopyFileBounded_ExactCapturedBytes_CreatesVerifiedDestinationWithExactMode()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        byte[] content = Enumerable.Range(0, (128 * 1024) + 17).Select(index => (byte)(index % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(sourceRoot, "payload"), content);
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("payload");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);

        LinuxFileIdentity result = destinationFileSystem.CopyFileBounded(source, "payload", 0x180, content.Length, content.Length);

        File.ReadAllBytes(Path.Combine(this.RootPath, "payload")).Should().Equal(content);
        result.Kind.Should().Be(LinuxAnchoredEntryKind.RegularFile);
        result.LinkCount.Should().Be(1);
        result.Size.Should().Be(content.Length);
        result.UnixMode.Should().Be(0x180);
    }

    [Test]
    public void CopyFileBounded_SourceExceedsMaximum_RejectsBeforeCreatingDestination()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "payload"), "12345");
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("payload");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);

        Action copy = () => destinationFileSystem.CopyFileBounded(source, "payload", 0x180, 5, 4);

        copy.Should().Throw<IOException>().WithMessage("*byte bound*");
        File.Exists(Path.Combine(this.RootPath, "payload")).Should().BeFalse();
    }

    [Test]
    public void CopyFileBounded_SourceChangedAfterCapture_RejectsBeforeCreatingDestination()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        string sourcePath = Path.Combine(sourceRoot, "payload");
        File.WriteAllText(sourcePath, "captured");
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("payload");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);
        File.AppendAllText(sourcePath, " growth");

        Action copy = () => destinationFileSystem.CopyFileBounded(source, "payload", 0x180, source.Identity.Size, 1024);

        copy.Should().Throw<IOException>().WithMessage("*changed after it was captured*");
        File.Exists(Path.Combine(this.RootPath, "payload")).Should().BeFalse();
    }

    [Test]
    public void CopyFileBounded_ExpectedSizeDiffersFromCapturedSize_RejectsBeforeCreatingDestination()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "payload"), "captured");
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("payload");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);

        Action copy = () => destinationFileSystem.CopyFileBounded(source, "payload", 0x180, source.Identity.Size - 1, 1024);

        copy.Should().Throw<IOException>().WithMessage("*expected size changed*");
        File.Exists(Path.Combine(this.RootPath, "payload")).Should().BeFalse();
    }

    [Test]
    public void CopyFileBounded_CancelledBeforeCopy_DoesNotCreateDestination()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "payload"), "captured");
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("payload");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Action copy = () => destinationFileSystem.CopyFileBounded(
            source,
            "payload",
            0x180,
            source.Identity.Size,
            1024,
            cancellation.Token
        );

        copy.Should().Throw<OperationCanceledException>();
        File.Exists(Path.Combine(this.RootPath, "payload")).Should().BeFalse();
    }

    [Test]
    public void CopyFileBounded_SourceGrowsDuringCopy_RejectsAndRemovesExactPartialDestination()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        string sourcePath = Path.Combine(sourceRoot, "payload");
        File.WriteAllBytes(sourcePath, new byte[32]);
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("payload");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);
        bool grew = false;

        Action copy = () => destinationFileSystem.CopyFileBounded(
            source,
            "payload",
            0x180,
            source.Identity.Size,
            1024,
            afterChunkCopiedForTesting: _ =>
            {
                if (!grew)
                {
                    File.AppendAllText(sourcePath, "growth");
                    grew = true;
                }
            },
            beforeVerificationForTesting: null
        );

        copy.Should().Throw<IOException>().WithMessage("*source identity changed*");
        grew.Should().BeTrue();
        File.Exists(Path.Combine(this.RootPath, "payload")).Should().BeFalse();
    }

    [Test]
    public void CopyFileBounded_CancelledDuringCopy_RemovesExactPartialDestination()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllBytes(Path.Combine(sourceRoot, "payload"), new byte[256 * 1024]);
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("payload");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);
        using CancellationTokenSource cancellation = new();

        Action copy = () => destinationFileSystem.CopyFileBounded(
            source,
            "payload",
            0x180,
            source.Identity.Size,
            source.Identity.Size,
            afterChunkCopiedForTesting: _ => cancellation.Cancel(),
            beforeVerificationForTesting: null,
            cancellation.Token
        );

        copy.Should().Throw<OperationCanceledException>();
        File.Exists(Path.Combine(this.RootPath, "payload")).Should().BeFalse();
    }

    [Test]
    public void CopyFileBounded_SourceTruncatesDuringCopy_RejectsBoundedReadAndRemovesPartialDestination()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        string sourcePath = Path.Combine(sourceRoot, "payload");
        File.WriteAllBytes(sourcePath, new byte[256 * 1024]);
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("payload");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);
        bool truncated = false;

        Action copy = () => destinationFileSystem.CopyFileBounded(
            source,
            "payload",
            0x180,
            source.Identity.Size,
            256 * 1024,
            afterChunkCopiedForTesting: copied =>
            {
                if (!truncated)
                {
                    using FileStream writer = File.OpenWrite(sourcePath);
                    writer.SetLength(copied);
                    truncated = true;
                }
            },
            beforeVerificationForTesting: null
        );

        copy.Should().Throw<EndOfStreamException>().WithMessage("*became shorter*");
        truncated.Should().BeTrue();
        File.Exists(Path.Combine(this.RootPath, "payload")).Should().BeFalse();
    }

    [Test]
    public void CopyFileBounded_DestinationBytesChangedBeforeVerification_RejectsAndRemovesIt()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "payload"), "trusted");
        string destinationPath = Path.Combine(this.RootPath, "payload");
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("payload");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);

        Action copy = () => destinationFileSystem.CopyFileBounded(
            source,
            "payload",
            0x180,
            source.Identity.Size,
            1024,
            afterChunkCopiedForTesting: null,
            beforeVerificationForTesting: () => File.WriteAllText(destinationPath, "altered")
        );

        copy.Should().Throw<IOException>().WithMessage("*exact byte*");
        File.Exists(destinationPath).Should().BeFalse();
    }

    [Test]
    public void CopyFileBounded_DestinationModeChangedBeforeVerification_RejectsAndRemovesIt()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "payload"), "trusted");
        string destinationPath = Path.Combine(this.RootPath, "payload");
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("payload");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);

        Action copy = () => destinationFileSystem.CopyFileBounded(
            source,
            "payload",
            0x180,
            source.Identity.Size,
            1024,
            afterChunkCopiedForTesting: null,
            beforeVerificationForTesting: () =>
                chmod(destinationPath, 0x100).Should().Be(0, $"chmod(2) failed with errno {Marshal.GetLastWin32Error()}")
        );

        copy.Should().Throw<IOException>().WithMessage("*mode verification*");
        File.Exists(destinationPath).Should().BeFalse();
    }

    [Test]
    public void CopyFileBounded_DestinationNameReplacedBySymlink_RejectsWithoutFollowingOrRemovingReplacement()
    {
        string sourceRoot = Path.Combine(this.TempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "payload"), "trusted");
        string destinationPath = Path.Combine(this.RootPath, "payload");
        string parkedPath = Path.Combine(this.RootPath, "parked");
        string outsidePath = Path.Combine(this.TempRoot, "outside");
        File.WriteAllText(outsidePath, "preserve");
        using LinuxAnchoredFileSystem sourceFileSystem = new(sourceRoot);
        using LinuxAnchoredFile source = sourceFileSystem.OpenRegularFileForRead("payload");
        using LinuxAnchoredFileSystem destinationFileSystem = new(this.RootPath);

        Action copy = () => destinationFileSystem.CopyFileBounded(
            source,
            "payload",
            0x180,
            source.Identity.Size,
            1024,
            afterChunkCopiedForTesting: null,
            beforeVerificationForTesting: () =>
            {
                File.Move(destinationPath, parkedPath);
                File.CreateSymbolicLink(destinationPath, outsidePath);
            }
        );

        copy.Should().Throw<IOException>();
        File.ResolveLinkTarget(destinationPath, returnFinalTarget: false)!.FullName.Should().Be(outsidePath);
        File.ReadAllText(outsidePath).Should().Be("preserve");
        File.ReadAllText(parkedPath).Should().Be("trusted");
    }

    [Test]
    public void RenameFileNoReplace_DestinationExists_RejectsAndPreservesBothFiles()
    {
        File.WriteAllText(Path.Combine(this.RootPath, "source"), "source bytes");
        File.WriteAllText(Path.Combine(this.RootPath, "destination"), "destination bytes");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        LinuxFileIdentity sourceIdentity = fileSystem.Stat("source")!;

        Action action = () => fileSystem.RenameFileNoReplace("source", "destination", sourceIdentity);

        action.Should().Throw<IOException>().WithMessage("*destination already exists*");
        File.ReadAllText(Path.Combine(this.RootPath, "source")).Should().Be("source bytes");
        File.ReadAllText(Path.Combine(this.RootPath, "destination")).Should().Be("destination bytes");
    }

    [Test]
    public void RenameFileNoReplace_SourceIdentityChanged_RejectsReplacement()
    {
        string sourcePath = Path.Combine(this.RootPath, "source");
        File.WriteAllText(sourcePath, "original");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        LinuxFileIdentity originalIdentity = fileSystem.Stat("source")!;
        File.Move(sourcePath, Path.Combine(this.RootPath, "old-source"));
        File.WriteAllText(sourcePath, "replacement");

        Action action = () => fileSystem.RenameFileNoReplace("source", "destination", originalIdentity);

        action.Should().Throw<IOException>().WithMessage("*identity changed*");
        File.ReadAllText(sourcePath).Should().Be("replacement");
        File.Exists(Path.Combine(this.RootPath, "destination")).Should().BeFalse();
    }

    [Test]
    public void RenameFileNoReplace_MatchingIdentity_RenamesWithoutReplacement()
    {
        File.WriteAllText(Path.Combine(this.RootPath, "source"), "source bytes");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        LinuxFileIdentity sourceIdentity = fileSystem.Stat("source")!;

        LinuxFileIdentity result = fileSystem.RenameFileNoReplace("source", "destination", sourceIdentity);

        fileSystem.Stat("source").Should().BeNull();
        result.IsSameObject(sourceIdentity).Should().BeTrue();
        File.ReadAllText(Path.Combine(this.RootPath, "destination")).Should().Be("source bytes");
    }

    [TestCase("rename")]
    [TestCase("replace")]
    [TestCase("unlink")]
    public void RegularFileMutation_SourceSwappedToFifo_RejectsPromptlyWithoutMutation(string operation)
    {
        string source = Path.Combine(this.RootPath, "source");
        string parked = Path.Combine(this.RootPath, "parked-source");
        string destination = Path.Combine(this.RootPath, "destination");
        File.WriteAllText(source, "trusted source");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        LinuxFileIdentity expectedSource = fileSystem.Stat("source")!;
        File.Move(source, parked);
        mkfifo(source, 0x180).Should().Be(0, $"mkfifo(2) failed with errno {Marshal.GetLastWin32Error()}");

        Action mutation = operation switch
        {
            "rename" => () => fileSystem.RenameFileNoReplace("source", "destination", expectedSource),
            "replace" => () => fileSystem.ReplaceFileAtomically("source", "destination", expectedSource, null),
            "unlink" => () => fileSystem.UnlinkFile("source", expectedSource),
            _ => throw new AssertionException($"Unknown mutation surface '{operation}'.")
        };
        Exception failure = CapturePromptFailure(mutation, source);

        failure.Should().BeOfType<IOException>();
        File.ReadAllText(parked).Should().Be("trusted source");
        File.Exists(destination).Should().BeFalse();
        Action inspectSource = () => fileSystem.Stat("source");
        inspectSource.Should().Throw<IOException>().WithMessage("*unsupported special file*");
    }

    [Test]
    public void UnlinkFile_PathReplacedAfterStat_RejectsAndPreservesReplacement()
    {
        string target = Path.Combine(this.RootPath, "target");
        File.WriteAllText(target, "original");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        LinuxFileIdentity originalIdentity = fileSystem.Stat("target")!;
        File.Move(target, Path.Combine(this.RootPath, "old-target"));
        File.WriteAllText(target, "replacement");

        Action action = () => fileSystem.UnlinkFile("target", originalIdentity);

        action.Should().Throw<IOException>().WithMessage("*identity changed*");
        File.ReadAllText(target).Should().Be("replacement");
    }

    [Test]
    public void ChmodFile_MatchingIdentity_SetsExactMode()
    {
        File.WriteAllText(Path.Combine(this.RootPath, "target"), "content");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        LinuxFileIdentity identity = fileSystem.Stat("target")!;

        LinuxFileIdentity changed = fileSystem.ChmodFile("target", identity, 0x140);

        changed.UnixMode.Should().Be(0x140);
        changed.IsSameObject(identity).Should().BeTrue();
    }

    [Test]
    public void CreateNewFile_SelectedRootPathSwapped_StaysAnchoredToOriginalDirectory()
    {
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        string movedRoot = Path.Combine(this.TempRoot, "moved-root");
        string outside = Path.Combine(this.TempRoot, "outside");
        Directory.Move(this.RootPath, movedRoot);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(this.RootPath, outside);

        using LinuxAnchoredFile created = fileSystem.CreateNewFile("anchored", 0x180);
        fileSystem.FsyncDirectory();

        File.Exists(Path.Combine(movedRoot, "anchored")).Should().BeTrue();
        File.Exists(Path.Combine(outside, "anchored")).Should().BeFalse();
    }

    [Test]
    public void EnsureDirectory_ParentSwappedForSymlink_RejectsWithoutWritingOutsideRoot()
    {
        string parent = Path.Combine(this.RootPath, "parent");
        string parked = Path.Combine(this.RootPath, "parked");
        string outside = Path.Combine(this.TempRoot, "outside");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        Directory.Move(parent, parked);
        Directory.CreateSymbolicLink(parent, outside);

        Action action = () => fileSystem.EnsureDirectory("parent/new", 0x1c0);

        action.Should().Throw<IOException>();
        Directory.Exists(Path.Combine(outside, "new")).Should().BeFalse();
    }

    [Test]
    public void AcquireExclusiveFileLock_SecondOpenFailsUntilFirstHandleCloses()
    {
        using LinuxAnchoredFileSystem firstFileSystem = new(this.RootPath);
        using LinuxAnchoredFileSystem secondFileSystem = new(this.RootPath);
        using LinuxAnchoredFile first = firstFileSystem.AcquireExclusiveFileLock("operation.lock", 0x180);

        Action blocked = () => secondFileSystem.AcquireExclusiveFileLock("operation.lock", 0x180).Dispose();

        blocked.Should().Throw<IOException>().WithMessage("*exclusive lock*");
        first.Dispose();
        using LinuxAnchoredFile acquiredAfterRelease = secondFileSystem.AcquireExclusiveFileLock("operation.lock", 0x180);
        acquiredAfterRelease.Identity.UnixMode.Should().Be(0x180);
    }

    [Test]
    public void AcquireExclusiveFileLock_SymlinkAndHardlink_RejectWithoutMutatingTargets()
    {
        string outside = Path.Combine(this.TempRoot, "outside-lock");
        File.WriteAllText(outside, "preserve");
        File.CreateSymbolicLink(Path.Combine(this.RootPath, "symlink-lock"), outside);
        File.WriteAllText(Path.Combine(this.RootPath, "real-lock"), "preserve");
        link(Path.Combine(this.RootPath, "real-lock"), Path.Combine(this.RootPath, "hardlink-lock"))
            .Should().Be(0, $"link(2) failed with errno {Marshal.GetLastWin32Error()}");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);

        Action symlink = () => fileSystem.AcquireExclusiveFileLock("symlink-lock", 0x180).Dispose();
        Action hardlink = () => fileSystem.AcquireExclusiveFileLock("hardlink-lock", 0x180).Dispose();

        symlink.Should().Throw<IOException>();
        hardlink.Should().Throw<IOException>().WithMessage("*multiple hard links*");
        File.ReadAllText(outside).Should().Be("preserve");
        File.ReadAllText(Path.Combine(this.RootPath, "real-lock")).Should().Be("preserve");
    }

    [Test]
    public void TruncateAndFsync_PathReplacedAfterOpen_RejectsAndPreservesReplacement()
    {
        string path = Path.Combine(this.RootPath, "events");
        File.WriteAllText(path, "valid\npartial");
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        using LinuxAnchoredFile opened = fileSystem.OpenRegularFileForReadWrite("events");
        File.Move(path, Path.Combine(this.RootPath, "captured"));
        File.WriteAllText(path, "replacement");

        Action truncate = () => fileSystem.TruncateAndFsync(opened, "events", 6);

        truncate.Should().Throw<IOException>();
        File.ReadAllText(path).Should().Be("replacement");
        File.ReadAllText(Path.Combine(this.RootPath, "captured")).Should().Be("valid\npartial");
    }

    [Test]
    public void RemoveEmptyDirectory_IdentityChangedOrNonempty_Rejects()
    {
        string directory = Path.Combine(this.RootPath, "directory");
        Directory.CreateDirectory(directory);
        using LinuxAnchoredFileSystem fileSystem = new(this.RootPath);
        LinuxFileIdentity original = fileSystem.Stat("directory")!;
        File.WriteAllText(Path.Combine(directory, "content"), "preserve");

        Action nonempty = () => fileSystem.RemoveEmptyDirectory("directory", original);

        nonempty.Should().Throw<IOException>();
        File.ReadAllText(Path.Combine(directory, "content")).Should().Be("preserve");
    }

    private static Exception CapturePromptFailure(Action action, string fifoPath)
    {
        Task<Exception?> attempt = Task.Run(() =>
        {
            try
            {
                action();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        if (!attempt.Wait(TimeSpan.FromSeconds(2)))
        {
            // Open both ends without blocking so a vulnerable read-only open can finish and the test never strands a worker.
            int unblockDescriptor = open(fifoPath, OpenReadWrite | OpenNonBlocking, 0);
            try
            {
                unblockDescriptor.Should().BeGreaterThanOrEqualTo(0, $"open(2) failed with errno {Marshal.GetLastWin32Error()}");
                attempt.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue("the blocked filesystem operation should be released for safe test cleanup");
            }
            finally
            {
                if (unblockDescriptor >= 0)
                    close(unblockDescriptor).Should().Be(0, $"close(2) failed with errno {Marshal.GetLastWin32Error()}");
            }
            Assert.Fail("The anchored filesystem operation blocked while opening a FIFO leaf.");
        }

        return attempt.Result ?? throw new AssertionException("The anchored filesystem unexpectedly accepted a FIFO leaf.");
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int link(string oldPath, string newPath);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int mkfifo(string path, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int chmod(string path, int mode);
}
