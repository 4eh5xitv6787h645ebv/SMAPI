using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Ownership.Persistence;

[TestFixture]
[SupportedOSPlatform("linux")]
public class OwnershipDocumentStoreTests
{
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private string TemporaryDirectory = null!;
    private string StateDirectory = null!;
    private string WorkspaceDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        this.TemporaryDirectory = Path.Combine(Path.GetTempPath(), $"smapi-ownership-store-{Guid.NewGuid():N}");
        this.StateDirectory = Path.Combine(this.TemporaryDirectory, "state");
        this.WorkspaceDirectory = Path.Combine(this.TemporaryDirectory, "workspace");
        Directory.CreateDirectory(this.StateDirectory);
        Directory.CreateDirectory(this.WorkspaceDirectory);
        File.SetUnixFileMode(this.StateDirectory, PrivateDirectoryMode);
        File.SetUnixFileMode(this.WorkspaceDirectory, PrivateDirectoryMode);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TemporaryDirectory))
            Directory.Delete(this.TemporaryDirectory, true);
    }

    [Test]
    public void LinuxStorage_WritesOverwritesAndReadsOnlyFixedPrivateSlots()
    {
        PackageManifest first = CreateManifest(alpha: 1);
        PackageManifest second = CreateManifest(alpha: 2);
        InstallationReceipt receipt = OwnershipTestData.Receipt(second);

        using OwnershipDocumentStore store = OwnershipDocumentStore.OpenLinux(this.StateDirectory, this.WorkspaceDirectory);
        store.WriteManifest(first);
        store.WriteManifest(second);
        store.WriteReceipt(receipt, second);

        store.ReadManifest().ToCanonicalJson().Should().Be(second.ToCanonicalJson());
        store.ReadReceipt(second).ToCanonicalJson().Should().Be(receipt.ToCanonicalJson());
        Directory.GetFiles(this.StateDirectory).Select(Path.GetFileName).Should().Equal("package-manifest.json");
        Directory.GetFiles(this.WorkspaceDirectory).Select(Path.GetFileName).Should().Equal("receipt.json");
        File.GetUnixFileMode(Path.Combine(this.StateDirectory, "package-manifest.json")).Should().Be(PrivateFileMode);
        File.GetUnixFileMode(Path.Combine(this.WorkspaceDirectory, "receipt.json")).Should().Be(PrivateFileMode);
    }

    [Test]
    public void LinuxStorage_RejectsInvalidLengthsWithoutReplacingDocument()
    {
        using LinuxAnchoredOwnershipDocumentStorage storage = new(this.StateDirectory, this.WorkspaceDirectory);
        storage.WriteAtomically(OwnershipDocumentSlot.PackageManifest, new byte[] { 1, 2 }, 10);

        Action empty = () => storage.WriteAtomically(OwnershipDocumentSlot.PackageManifest, ReadOnlyMemory<byte>.Empty, 10);
        Action oversizeWrite = () => storage.WriteAtomically(OwnershipDocumentSlot.PackageManifest, new byte[] { 3, 4, 5 }, 2);
        Action oversizeRead = () => storage.ReadBounded(OwnershipDocumentSlot.PackageManifest, 1);

        empty.Should().Throw<OwnershipDocumentException>();
        oversizeWrite.Should().Throw<OwnershipDocumentException>();
        oversizeRead.Should().Throw<OwnershipDocumentException>();
        File.ReadAllBytes(Path.Combine(this.StateDirectory, "package-manifest.json")).Should().Equal(1, 2);
        Directory.GetFiles(this.StateDirectory).Should().ContainSingle();
    }

    [Test]
    public void LinuxStorage_RejectsSymlinkRootAndNonPrivateRootMode()
    {
        string symlink = Path.Combine(this.TemporaryDirectory, "state-link");
        Directory.CreateSymbolicLink(symlink, this.StateDirectory);
        File.SetUnixFileMode(this.WorkspaceDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);

        Action symbolicRoot = () => OwnershipDocumentStore.OpenLinux(symlink, this.StateDirectory);
        Action permissiveRoot = () => OwnershipDocumentStore.OpenLinux(this.StateDirectory, this.WorkspaceDirectory);

        symbolicRoot.Should().Throw<IOException>();
        permissiveRoot.Should().Throw<IOException>().WithMessage("*exact 0700*");
    }

    [Test]
    public void LinuxStorage_RejectsSymlinkInAnIntermediateRootSegment()
    {
        string realParent = Path.Combine(this.TemporaryDirectory, "real-parent");
        string realState = Path.Combine(realParent, "private-state");
        string parentLink = Path.Combine(this.TemporaryDirectory, "parent-link");
        Directory.CreateDirectory(realState);
        File.SetUnixFileMode(realState, PrivateDirectoryMode);
        Directory.CreateSymbolicLink(parentLink, realParent);

        Action open = () => OwnershipDocumentStore.OpenLinux(Path.Combine(parentLink, "private-state"), this.WorkspaceDirectory);

        open.Should().Throw<IOException>();
        Directory.GetFiles(realState).Should().BeEmpty();
    }

    [Test]
    public void LinuxStorage_RejectsSymlinkAndHardlinkLeavesWithoutTouchingTheirTargets()
    {
        string symlinkTarget = Path.Combine(this.TemporaryDirectory, "symlink-target");
        string hardlinkTarget = Path.Combine(this.TemporaryDirectory, "hardlink-target");
        File.WriteAllText(symlinkTarget, "symlink-safe");
        File.WriteAllText(hardlinkTarget, "hardlink-safe");
        File.SetUnixFileMode(symlinkTarget, PrivateFileMode);
        File.SetUnixFileMode(hardlinkTarget, PrivateFileMode);
        File.CreateSymbolicLink(Path.Combine(this.StateDirectory, "package-manifest.json"), symlinkTarget);
        CreateHardLink(Path.Combine(this.WorkspaceDirectory, "receipt.json"), hardlinkTarget);

        using OwnershipDocumentStore store = OwnershipDocumentStore.OpenLinux(this.StateDirectory, this.WorkspaceDirectory);
        PackageManifest manifest = CreateManifest();
        Action symlinkWrite = () => store.WriteManifest(manifest);
        Action hardlinkWrite = () => store.WriteReceipt(OwnershipTestData.Receipt(manifest), manifest);

        symlinkWrite.Should().Throw<IOException>();
        hardlinkWrite.Should().Throw<IOException>();
        File.ReadAllText(symlinkTarget).Should().Be("symlink-safe");
        File.ReadAllText(hardlinkTarget).Should().Be("hardlink-safe");
    }

    [Test]
    public void LinuxStorage_RemainsAnchoredWhenRootPathIsSwapped()
    {
        using OwnershipDocumentStore store = OwnershipDocumentStore.OpenLinux(this.StateDirectory, this.WorkspaceDirectory);
        string capturedState = Path.Combine(this.TemporaryDirectory, "captured-state");
        Directory.Move(this.StateDirectory, capturedState);
        Directory.CreateDirectory(this.StateDirectory);
        File.SetUnixFileMode(this.StateDirectory, PrivateDirectoryMode);

        store.WriteManifest(CreateManifest());

        File.Exists(Path.Combine(capturedState, "package-manifest.json")).Should().BeTrue();
        Directory.GetFiles(this.StateDirectory).Should().BeEmpty();
    }

    [Test]
    public void LinuxStorage_RejectsRegularLeafSwapAfterObservation()
    {
        PackageManifest manifest = CreateManifest();
        using OwnershipDocumentStore store = OwnershipDocumentStore.OpenLinux(this.StateDirectory, this.WorkspaceDirectory);
        store.WriteManifest(manifest);
        string document = Path.Combine(this.StateDirectory, "package-manifest.json");
        string displaced = Path.Combine(this.StateDirectory, "displaced.json");
        File.Move(document, displaced);
        File.WriteAllText(document, "attacker replacement");
        File.SetUnixFileMode(document, PrivateFileMode);

        Action write = () => store.WriteManifest(manifest);
        Action read = () => store.ReadManifest();

        write.Should().Throw<IOException>().WithMessage("*replaced*");
        read.Should().Throw<IOException>().WithMessage("*replaced*");
        File.ReadAllText(document).Should().Be("attacker replacement");
        Directory.GetFiles(this.StateDirectory).Should().HaveCount(2);
    }

    [Test]
    public void LinuxStorage_RejectsRootModeChangeBeforeCreatingTemporaryFile()
    {
        using OwnershipDocumentStore store = OwnershipDocumentStore.OpenLinux(this.StateDirectory, this.WorkspaceDirectory);
        File.SetUnixFileMode(this.StateDirectory, PrivateDirectoryMode | UnixFileMode.GroupRead);

        Action write = () => store.WriteManifest(CreateManifest());

        write.Should().Throw<IOException>().WithMessage("*exact 0700*");
        Directory.GetFiles(this.StateDirectory).Should().BeEmpty();
    }

    [Test]
    public void LinuxStorage_RefusesRootBeforeOpeningOrMutatingStorage()
    {
        string absentState = Path.Combine(this.TemporaryDirectory, "absent-state");
        string absentWorkspace = Path.Combine(this.TemporaryDirectory, "absent-workspace");
        Action construct = () => new LinuxAnchoredOwnershipDocumentStorage(
            absentState,
            absentWorkspace,
            () => throw new PrivilegedInstallerException("root")
        );

        construct.Should().Throw<PrivilegedInstallerException>();
        Directory.Exists(absentState).Should().BeFalse();
        Directory.Exists(absentWorkspace).Should().BeFalse();

        int checks = 0;
        using LinuxAnchoredOwnershipDocumentStorage storage = new(
            this.StateDirectory,
            this.WorkspaceDirectory,
            () =>
            {
                checks++;
                if (checks > 1)
                    throw new PrivilegedInstallerException("became root");
            }
        );
        Action write = () => storage.WriteAtomically(OwnershipDocumentSlot.PackageManifest, new byte[] { 1 }, 10);
        write.Should().Throw<PrivilegedInstallerException>();
        Directory.GetFiles(this.StateDirectory).Should().BeEmpty();
    }

    [Test]
    public void TypedStore_RequiresVerifiedManifestForReceiptReadAndWrite()
    {
        RecordingStorage storage = new();
        using OwnershipDocumentStore store = new(storage);
        PackageManifest manifest = CreateManifest();
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        PackageManifest wrongManifest = CreateManifest(alpha: 2);

        store.WriteReceipt(receipt, manifest);
        store.ReadReceipt(manifest).ToCanonicalJson().Should().Be(receipt.ToCanonicalJson());
        Action wrongRead = () => store.ReadReceipt(wrongManifest);
        Action wrongWrite = () => store.WriteReceipt(receipt, wrongManifest);

        wrongRead.Should().Throw<OwnershipDocumentException>();
        wrongWrite.Should().Throw<OwnershipDocumentException>();
        storage.LastSlot.Should().Be(OwnershipDocumentSlot.InstallationReceipt);
        storage.LastMaxBytes.Should().Be(OwnershipPersistenceLimits.Default.MaxDocumentBytes);
    }

    private static PackageManifest CreateManifest(int alpha = 1)
    {
        return OwnershipTestData.Manifest(
            release: OwnershipTestData.Release(alpha: alpha),
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile, mode: 493)]
        );
    }

    private static void CreateHardLink(string path, string target)
    {
        if (link(target, path) != 0)
            throw new IOException($"Couldn't create a hardlink test fixture (errno {Marshal.GetLastWin32Error()}).");
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int link(string oldPath, string newPath);

    private sealed class RecordingStorage : IOwnershipDocumentStorage
    {
        private readonly Dictionary<OwnershipDocumentSlot, byte[]> Documents = new();
        public OwnershipDocumentSlot? LastSlot { get; private set; }
        public int LastMaxBytes { get; private set; }

        public byte[] ReadBounded(OwnershipDocumentSlot slot, int maxBytes)
        {
            this.LastSlot = slot;
            this.LastMaxBytes = maxBytes;
            return this.Documents[slot].ToArray();
        }

        public void WriteAtomically(OwnershipDocumentSlot slot, ReadOnlyMemory<byte> bytes, int maxBytes)
        {
            this.LastSlot = slot;
            this.LastMaxBytes = maxBytes;
            this.Documents[slot] = bytes.ToArray();
        }

        public void Dispose() { }
    }
}
