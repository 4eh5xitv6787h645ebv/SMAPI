using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Ownership.Persistence;

/// <summary>The only persisted ownership-document slots exposed across the storage trust boundary.</summary>
internal enum OwnershipDocumentSlot
{
    PackageManifest,
    InstallationReceipt,
    RollbackSnapshot
}

/// <summary>Bounded storage addressed only through fixed ownership-document slots.</summary>
internal interface IOwnershipDocumentStorage : IDisposable
{
    byte[] ReadBounded(OwnershipDocumentSlot slot, int maxBytes);
    void WriteAtomically(OwnershipDocumentSlot slot, ReadOnlyMemory<byte> bytes, int maxBytes);
}

/// <summary>
/// Linux ownership-document storage anchored to pre-existing private state and game-workspace directories.
/// Both roots must be real directories with exact <c>0700</c> permissions. Document names are fixed here and
/// can't be supplied by callers.
/// </summary>
internal sealed class LinuxAnchoredOwnershipDocumentStorage : IOwnershipDocumentStorage
{
    private const int PrivateDirectoryMode = 0x1c0; // 0700
    private const int PrivateFileMode = 0x180; // 0600
    private const string ManifestName = "package-manifest.json";
    private const string ReceiptName = "receipt.json";
    private const string RollbackName = "rollback-snapshot.json";

    private readonly LinuxAnchoredFileSystem State;
    private readonly LinuxAnchoredFileSystem GameWorkspace;
    private readonly Action AssertNotRoot;
    private readonly Dictionary<OwnershipDocumentSlot, LinuxFileIdentity> KnownDocuments = new();
    private readonly object SyncRoot = new();
    private bool Disposed;

    public LinuxAnchoredOwnershipDocumentStorage(string privateStateDirectory, string privateGameWorkspaceDirectory)
        : this(privateStateDirectory, privateGameWorkspaceDirectory, LinuxPrivilegeGuard.AssertNotRoot) { }

    internal LinuxAnchoredOwnershipDocumentStorage(
        string privateStateDirectory,
        string privateGameWorkspaceDirectory,
        Action assertNotRoot
    )
    {
        this.AssertNotRoot = assertNotRoot ?? throw new ArgumentNullException(nameof(assertNotRoot));
        this.AssertNotRoot();

        LinuxAnchoredFileSystem? state = null;
        LinuxAnchoredFileSystem? workspace = null;
        try
        {
            state = new LinuxAnchoredFileSystem(privateStateDirectory);
            AssertPrivateDirectory(state, "ownership state");
            workspace = new LinuxAnchoredFileSystem(privateGameWorkspaceDirectory);
            AssertPrivateDirectory(workspace, "game ownership workspace");
            this.State = state;
            this.GameWorkspace = workspace;
        }
        catch
        {
            workspace?.Dispose();
            state?.Dispose();
            throw;
        }
    }

    public byte[] ReadBounded(OwnershipDocumentSlot slot, int maxBytes)
    {
        lock (this.SyncRoot)
        {
            this.AssertAvailable();
            if (maxBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));

            (LinuxAnchoredFileSystem root, string name) = this.Resolve(slot);
            AssertPrivateDirectory(root, slot == OwnershipDocumentSlot.InstallationReceipt ? "game ownership workspace" : "ownership state");
            using LinuxAnchoredFile file = root.OpenRegularFileForRead(name);
            AssertPrivateFile(file.Identity);
            this.AssertKnownIdentity(slot, file.Identity);
            if (file.Identity.Size <= 0 || file.Identity.Size > maxBytes)
                throw new OwnershipDocumentException("The persisted ownership document has an invalid or excessive byte length.");
            try
            {
                byte[] bytes = root.ReadAllBytes(file, maxBytes);
                this.KnownDocuments[slot] = file.Identity;
                return bytes;
            }
            catch (IOException ex)
            {
                throw new OwnershipDocumentException("The persisted ownership document changed or exceeded its byte limit.", ex);
            }
        }
    }

    public void WriteAtomically(OwnershipDocumentSlot slot, ReadOnlyMemory<byte> bytes, int maxBytes)
    {
        lock (this.SyncRoot)
        {
            this.AssertAvailable();
            if (maxBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            if (bytes.Length == 0 || bytes.Length > maxBytes)
                throw new OwnershipDocumentException("The ownership document has an invalid or excessive byte length.");

            (LinuxAnchoredFileSystem root, string name) = this.Resolve(slot);
            AssertPrivateDirectory(root, slot == OwnershipDocumentSlot.InstallationReceipt ? "game ownership workspace" : "ownership state");
            LinuxFileIdentity? previous = root.Stat(name);
            if (previous != null)
            {
                AssertPrivateFile(previous);
                this.AssertKnownIdentity(slot, previous);
            }
            else if (this.KnownDocuments.ContainsKey(slot))
                throw new IOException("A previously observed ownership document disappeared before replacement.");

            string temporaryName = $".{name}.tmp-{Guid.NewGuid():N}";
            LinuxFileIdentity? temporaryIdentity = null;
            try
            {
                using (LinuxAnchoredFile temporary = root.CreateNewFile(temporaryName, PrivateFileMode))
                {
                    root.AppendAndFsync(temporary, temporaryName, bytes.Span, expectedLength: 0, maximumLength: maxBytes);
                    temporaryIdentity = root.Stat(temporaryName)
                        ?? throw new IOException("The temporary ownership document disappeared before replacement.");
                    AssertPrivateFile(temporaryIdentity);
                }

                LinuxFileIdentity persistedIdentity = root.ReplaceFileAtomically(temporaryName, name, temporaryIdentity, previous);
                temporaryIdentity = null;
                using LinuxAnchoredFile persisted = root.OpenRegularFileForRead(name);
                if (persisted.Identity != persistedIdentity)
                    throw new IOException("The ownership-document leaf changed after atomic replacement.");
                AssertPrivateFile(persisted.Identity);
                byte[] actual = root.ReadAllBytes(persisted, maxBytes);
                if (!actual.AsSpan().SequenceEqual(bytes.Span))
                    throw new IOException("The durably replaced ownership document didn't match the supplied bytes.");
                this.KnownDocuments[slot] = persisted.Identity;
            }
            finally
            {
                if (temporaryIdentity != null)
                {
                    try
                    {
                        root.UnlinkFile(temporaryName, temporaryIdentity);
                    }
                    catch
                    {
                        // Preserve the primary failure; private-state inspection can remove unexpected debris.
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (this.Disposed)
            return;
        this.GameWorkspace.Dispose();
        this.State.Dispose();
        this.Disposed = true;
    }

    private static void AssertPrivateDirectory(LinuxAnchoredFileSystem root, string description)
    {
        if (root.GetCurrentRootIdentity().UnixMode != PrivateDirectoryMode)
            throw new IOException($"The private {description} directory must have exact 0700 permissions.");
    }

    private static void AssertPrivateFile(LinuxFileIdentity identity)
    {
        if (identity.Kind != LinuxAnchoredEntryKind.RegularFile || identity.LinkCount != 1 || identity.UnixMode != PrivateFileMode)
            throw new IOException("A persisted ownership document must be a single-link regular file with exact 0600 permissions.");
    }

    private (LinuxAnchoredFileSystem Root, string Name) Resolve(OwnershipDocumentSlot slot)
    {
        return slot switch
        {
            OwnershipDocumentSlot.PackageManifest => (this.State, ManifestName),
            OwnershipDocumentSlot.RollbackSnapshot => (this.State, RollbackName),
            OwnershipDocumentSlot.InstallationReceipt => (this.GameWorkspace, ReceiptName),
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }

    private void AssertAvailable()
    {
        if (this.Disposed)
            throw new ObjectDisposedException(nameof(LinuxAnchoredOwnershipDocumentStorage));
        this.AssertNotRoot();
    }

    private void AssertKnownIdentity(OwnershipDocumentSlot slot, LinuxFileIdentity observed)
    {
        if (this.KnownDocuments.TryGetValue(slot, out LinuxFileIdentity? known) && observed != known)
            throw new IOException("A persisted ownership-document leaf was replaced after it was observed.");
    }
}

/// <summary>Typed ownership persistence which cannot load a receipt or rollback snapshot without its verified parent state.</summary>
public sealed class OwnershipDocumentStore : IDisposable
{
    private readonly IOwnershipDocumentStorage Storage;
    private readonly OwnershipPersistenceLimits Limits;
    private readonly bool OwnsStorage;

    internal OwnershipDocumentStore(IOwnershipDocumentStorage storage, OwnershipPersistenceLimits? limits = null, bool ownsStorage = false)
    {
        this.Storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.Limits = limits ?? OwnershipPersistenceLimits.Default;
        this.OwnsStorage = ownsStorage;
    }

    /// <summary>Open production Linux storage rooted at two pre-existing exact-0700 private directories.</summary>
    public static OwnershipDocumentStore OpenLinux(
        string privateStateDirectory,
        string privateGameWorkspaceDirectory,
        OwnershipPersistenceLimits? limits = null
    )
    {
        return new OwnershipDocumentStore(
            new LinuxAnchoredOwnershipDocumentStorage(privateStateDirectory, privateGameWorkspaceDirectory),
            limits,
            ownsStorage: true
        );
    }

    public PackageManifest ReadManifest()
    {
        return CanonicalOwnershipDocuments.ParseManifest(this.Read(OwnershipDocumentSlot.PackageManifest), this.Limits);
    }

    public void WriteManifest(PackageManifest manifest)
    {
        this.Write(OwnershipDocumentSlot.PackageManifest, CanonicalOwnershipDocuments.SerializeManifest(manifest));
    }

    public InstallationReceipt ReadReceipt(PackageManifest verifiedManifest)
    {
        return CanonicalOwnershipDocuments.ParseReceipt(this.Read(OwnershipDocumentSlot.InstallationReceipt), verifiedManifest, this.Limits);
    }

    public void WriteReceipt(InstallationReceipt receipt, PackageManifest verifiedManifest)
    {
        CanonicalOwnershipDocuments.AssertReceiptMatchesManifest(receipt, verifiedManifest);
        this.Write(OwnershipDocumentSlot.InstallationReceipt, CanonicalOwnershipDocuments.SerializeReceipt(receipt));
    }

    public StardewModdingAPI.Installer.Core.Planning.RollbackSnapshot ReadRollbackSnapshot(InstallationReceipt? currentReceipt)
    {
        return CanonicalOwnershipDocuments.ParseRollbackSnapshot(
            this.Read(OwnershipDocumentSlot.RollbackSnapshot),
            currentReceipt,
            this.Limits
        );
    }

    public void WriteRollbackSnapshot(
        StardewModdingAPI.Installer.Core.Planning.RollbackSnapshot snapshot,
        InstallationReceipt? currentReceipt
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ExpectedCurrentReceiptSha256 != currentReceipt?.GetCanonicalDigest())
            throw new OwnershipDocumentException("The rollback snapshot doesn't target the supplied current receipt state.");
        this.Write(OwnershipDocumentSlot.RollbackSnapshot, CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot));
    }

    public void Dispose()
    {
        if (this.OwnsStorage)
            this.Storage.Dispose();
    }

    private ReadOnlyMemory<byte> Read(OwnershipDocumentSlot slot)
    {
        return this.Storage.ReadBounded(slot, this.Limits.MaxDocumentBytes);
    }

    private void Write(OwnershipDocumentSlot slot, byte[] bytes)
    {
        if (bytes.Length > this.Limits.MaxDocumentBytes)
            throw new OwnershipDocumentException("The canonical ownership document exceeds the configured byte limit.");
        this.Storage.WriteAtomically(slot, bytes, this.Limits.MaxDocumentBytes);
    }
}
