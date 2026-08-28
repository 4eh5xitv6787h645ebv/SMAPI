using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;

namespace StardewModdingAPI.Installer.Core.Tests.Ownership.Persistence;

[TestFixture]
public class OwnershipDocumentStoreTests
{
    private string TemporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        this.TemporaryDirectory = Path.Combine(Path.GetTempPath(), $"smapi-ownership-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TemporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TemporaryDirectory))
            Directory.Delete(this.TemporaryDirectory, true);
    }

    [Test]
    public void AtomicStorage_WritesOverwritesAndReadsExactBoundedBytes()
    {
        AtomicOwnershipDocumentStorage storage = new();
        string path = Path.Combine(this.TemporaryDirectory, "state", "receipt.json");

        storage.WriteAtomically(path, new byte[] { 1, 2, 3 }, 10);
        storage.WriteAtomically(path, new byte[] { 4, 5 }, 10);

        storage.ReadBounded(path, 10).Should().Equal(4, 5);
        Directory.GetFiles(Path.GetDirectoryName(path)!).Should().ContainSingle().Which.Should().Be(path);
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public void AtomicStorage_RejectsRelativeEmptyAndOversizeOperationsWithoutOverwriting()
    {
        AtomicOwnershipDocumentStorage storage = new();
        string path = Path.Combine(this.TemporaryDirectory, "state.json");
        storage.WriteAtomically(path, new byte[] { 1, 2 }, 10);

        Action relative = () => storage.ReadBounded("relative.json", 10);
        Action empty = () => storage.WriteAtomically(path, ReadOnlyMemory<byte>.Empty, 10);
        Action oversizeWrite = () => storage.WriteAtomically(path, new byte[] { 3, 4, 5 }, 2);
        Action oversizeRead = () => storage.ReadBounded(path, 1);

        relative.Should().Throw<ArgumentException>();
        empty.Should().Throw<OwnershipDocumentException>();
        oversizeWrite.Should().Throw<OwnershipDocumentException>();
        oversizeRead.Should().Throw<OwnershipDocumentException>();
        File.ReadAllBytes(path).Should().Equal(1, 2);
    }

    [Test]
    public void TypedStore_RequiresVerifiedManifestForReceiptReadAndWrite()
    {
        RecordingStorage storage = new();
        OwnershipDocumentStore store = new(storage);
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile, mode: 493)]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        PackageManifest wrongManifest = OwnershipTestData.Manifest(
            release: OwnershipTestData.Release(alpha: 2),
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile, mode: 493)]
        );

        store.WriteReceipt("/receipt.json", receipt, manifest);
        store.ReadReceipt("/receipt.json", manifest).ToCanonicalJson().Should().Be(receipt.ToCanonicalJson());
        Action wrongRead = () => store.ReadReceipt("/receipt.json", wrongManifest);
        Action wrongWrite = () => store.WriteReceipt("/receipt.json", receipt, wrongManifest);

        wrongRead.Should().Throw<OwnershipDocumentException>();
        wrongWrite.Should().Throw<OwnershipDocumentException>();
        storage.LastMaxBytes.Should().Be(OwnershipPersistenceLimits.Default.MaxDocumentBytes);
    }

    private sealed class RecordingStorage : IOwnershipDocumentStorage
    {
        private byte[] Bytes = Array.Empty<byte>();
        public int LastMaxBytes { get; private set; }

        public byte[] ReadBounded(string absolutePath, int maxBytes)
        {
            this.LastMaxBytes = maxBytes;
            return this.Bytes.ToArray();
        }

        public void WriteAtomically(string absolutePath, ReadOnlyMemory<byte> bytes, int maxBytes)
        {
            this.LastMaxBytes = maxBytes;
            this.Bytes = bytes.ToArray();
        }
    }
}
