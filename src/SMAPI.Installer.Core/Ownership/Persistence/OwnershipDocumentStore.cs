using System.Runtime.InteropServices;

namespace StardewModdingAPI.Installer.Core.Ownership.Persistence;

/// <summary>Bounded byte storage used by the ownership document trust boundary.</summary>
public interface IOwnershipDocumentStorage
{
    byte[] ReadBounded(string absolutePath, int maxBytes);
    void WriteAtomically(string absolutePath, ReadOnlyMemory<byte> bytes, int maxBytes);
}

/// <summary>Same-directory atomic filesystem storage with durable file contents.</summary>
public sealed class AtomicOwnershipDocumentStorage : IOwnershipDocumentStorage
{
    private const int LinuxOpenReadOnly = 0;
    private const int LinuxOpenDirectory = 0x10000;
    private const int LinuxOpenCloseOnExec = 0x80000;

    public byte[] ReadBounded(string absolutePath, int maxBytes)
    {
        string path = ValidateAbsolutePath(absolutePath);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maxBytes || stream.Length > int.MaxValue)
            throw new OwnershipDocumentException("The persisted ownership document has an invalid or excessive byte length.");

        byte[] bytes = new byte[(int)stream.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                throw new EndOfStreamException("The persisted ownership document changed while it was being read.");
            offset += read;
        }
        if (stream.ReadByte() != -1)
            throw new OwnershipDocumentException("The persisted ownership document grew beyond its validated byte length.");
        return bytes;
    }

    public void WriteAtomically(string absolutePath, ReadOnlyMemory<byte> bytes, int maxBytes)
    {
        string path = ValidateAbsolutePath(absolutePath);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (bytes.Length == 0 || bytes.Length > maxBytes)
            throw new OwnershipDocumentException("The ownership document has an invalid or excessive byte length.");

        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows() && Chmod(temporaryPath, Convert.ToUInt32("600", 8)) != 0)
                    throw CreateNativeIOException("Couldn't restrict ownership document permissions.");
                stream.Write(bytes.Span);
                stream.Flush(true);
            }
            File.Move(temporaryPath, path, true);
            if (OperatingSystem.IsLinux())
                SyncLinuxDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string ValidateAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A persisted ownership document path is required.", nameof(path));
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("A persisted ownership document path must be absolute.", nameof(path));
        return Path.GetFullPath(path);
    }

    private static void SyncLinuxDirectory(string directory)
    {
        int descriptor = Open(directory, LinuxOpenReadOnly | LinuxOpenDirectory | LinuxOpenCloseOnExec);
        if (descriptor < 0)
            throw CreateNativeIOException("Couldn't open the ownership document directory for synchronization.");
        try
        {
            if (Fsync(descriptor) != 0)
                throw CreateNativeIOException("Couldn't durably synchronize the ownership document directory.");
        }
        finally
        {
            Close(descriptor);
        }
    }

    private static IOException CreateNativeIOException(string message)
    {
        return new IOException(message, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
    private static extern int Chmod(string path, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
}

/// <summary>Typed ownership persistence which cannot load a receipt or rollback snapshot without its verified parent state.</summary>
public sealed class OwnershipDocumentStore
{
    private readonly IOwnershipDocumentStorage Storage;
    private readonly OwnershipPersistenceLimits Limits;

    public OwnershipDocumentStore(IOwnershipDocumentStorage storage, OwnershipPersistenceLimits? limits = null)
    {
        this.Storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.Limits = limits ?? OwnershipPersistenceLimits.Default;
    }

    public PackageManifest ReadManifest(string absolutePath)
    {
        return CanonicalOwnershipDocuments.ParseManifest(this.Read(absolutePath), this.Limits);
    }

    public void WriteManifest(string absolutePath, PackageManifest manifest)
    {
        this.Write(absolutePath, CanonicalOwnershipDocuments.SerializeManifest(manifest));
    }

    public InstallationReceipt ReadReceipt(string absolutePath, PackageManifest verifiedManifest)
    {
        return CanonicalOwnershipDocuments.ParseReceipt(this.Read(absolutePath), verifiedManifest, this.Limits);
    }

    public void WriteReceipt(string absolutePath, InstallationReceipt receipt, PackageManifest verifiedManifest)
    {
        CanonicalOwnershipDocuments.AssertReceiptMatchesManifest(receipt, verifiedManifest);
        this.Write(absolutePath, CanonicalOwnershipDocuments.SerializeReceipt(receipt));
    }

    public StardewModdingAPI.Installer.Core.Planning.RollbackSnapshot ReadRollbackSnapshot(string absolutePath, InstallationReceipt installedReceipt)
    {
        return CanonicalOwnershipDocuments.ParseRollbackSnapshot(this.Read(absolutePath), installedReceipt, this.Limits);
    }

    public void WriteRollbackSnapshot(
        string absolutePath,
        StardewModdingAPI.Installer.Core.Planning.RollbackSnapshot snapshot,
        InstallationReceipt installedReceipt
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(installedReceipt);
        if (snapshot.ExpectedInstalledReceiptSha256 != installedReceipt.GetCanonicalDigest())
            throw new OwnershipDocumentException("The rollback snapshot doesn't target the supplied installed receipt.");
        this.Write(absolutePath, CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot));
    }

    private ReadOnlyMemory<byte> Read(string path)
    {
        return this.Storage.ReadBounded(path, this.Limits.MaxDocumentBytes);
    }

    private void Write(string path, byte[] bytes)
    {
        if (bytes.Length > this.Limits.MaxDocumentBytes)
            throw new OwnershipDocumentException("The canonical ownership document exceeds the configured byte limit.");
        this.Storage.WriteAtomically(path, bytes, this.Limits.MaxDocumentBytes);
    }
}
