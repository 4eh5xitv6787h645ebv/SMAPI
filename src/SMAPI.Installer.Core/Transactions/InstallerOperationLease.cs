using System.Globalization;
using System.Text;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Transactions;

internal interface IInstallerOperationLeaseFaultInjector
{
    void BeforeGenerationPublicationIdentityCheck(string temporaryName);
}

internal sealed class NullInstallerOperationLeaseFaultInjector : IInstallerOperationLeaseFaultInjector
{
    public static NullInstallerOperationLeaseFaultInjector Instance { get; } = new();
    public void BeforeGenerationPublicationIdentityCheck(string temporaryName) { }
}

/// <summary>An exclusive descriptor-anchored operation lease for one exact game-root generation.</summary>
internal sealed class InstallerOperationLease : IDisposable
{
    private const string GenerationFileName = "operation-generation";
    private const int GenerationByteLength = 21;

    private LinuxFileIdentity GenerationIdentity;
    private readonly IInstallerOperationLeaseFaultInjector FaultInjector;
    private bool Disposed;

    public string CanonicalGameRoot { get; }
    public LinuxAnchoredFileSystem Game { get; }
    public LinuxAnchoredFileSystem Workspace { get; }
    public LinuxAnchoredFile OperationLock { get; }
    public GameRootIdentity RootIdentity { get; }
    public ulong Generation { get; private set; }

    private InstallerOperationLease(
        string canonicalGameRoot,
        LinuxAnchoredFileSystem game,
        LinuxAnchoredFileSystem workspace,
        LinuxAnchoredFile operationLock,
        LinuxFileIdentity generationIdentity,
        ulong generation,
        IInstallerOperationLeaseFaultInjector faultInjector
    )
    {
        this.CanonicalGameRoot = canonicalGameRoot;
        this.Game = game;
        this.Workspace = workspace;
        this.OperationLock = operationLock;
        this.RootIdentity = GameRootIdentity.From(canonicalGameRoot, game.Identity);
        this.GenerationIdentity = generationIdentity;
        this.Generation = generation;
        this.FaultInjector = faultInjector;
    }

    public static InstallerOperationLease Acquire(string gameRoot)
        => Acquire(gameRoot, null);

    internal static InstallerOperationLease Acquire(
        string gameRoot,
        IInstallerOperationLeaseFaultInjector? faultInjector
    )
    {
        faultInjector ??= NullInstallerOperationLeaseFaultInjector.Instance;
        LinuxPrivilegeGuard.AssertNotRoot();
        string canonicalRoot = TransactionPath.GetCanonicalRoot(gameRoot, nameof(gameRoot));
        LinuxAnchoredFileSystem? game = null;
        LinuxAnchoredFileSystem? workspace = null;
        LinuxAnchoredFile? operationLock = null;
        try
        {
            game = new LinuxAnchoredFileSystem(canonicalRoot);
            workspace = InstallerTransactionExecutor.EnsureWorkspace(game);
            operationLock = InstallerTransactionExecutor.AcquireLock(workspace);
            LinuxFileIdentity currentRoot = game.GetCurrentRootIdentity();
            if (!currentRoot.IsSameObject(game.Identity))
                throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The selected game root changed while its operation lock was acquired.");

            (LinuxFileIdentity identity, ulong generation) = OpenOrCreateGeneration(workspace);
            InstallerOperationLease result = new(canonicalRoot, game, workspace, operationLock, identity, generation, faultInjector);
            game = null;
            workspace = null;
            operationLock = null;
            return result;
        }
        finally
        {
            operationLock?.Dispose();
            workspace?.Dispose();
            game?.Dispose();
        }
    }

    public ulong ReserveNextGeneration(ulong expectedGeneration)
    {
        this.AssertUsable();
        (LinuxFileIdentity currentIdentity, ulong current) = ReadGeneration(this.Workspace);
        if (currentIdentity != this.GenerationIdentity || current != this.Generation || current != expectedGeneration)
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The installer operation generation changed after confirmation.");
        if (current == ulong.MaxValue)
            throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "The installer operation generation is exhausted.");

        ulong next = current + 1;
        string temporaryName = $"operation-generation-{Guid.NewGuid():N}.tmp";
        LinuxFileIdentity? temporaryIdentity = null;
        try
        {
            using (LinuxAnchoredFile temporary = this.Workspace.CreateNewFile(temporaryName, 0x180))
            {
                byte[] bytes = EncodeGeneration(next);
                this.Workspace.AppendAndFsync(temporary, temporaryName, bytes, 0, GenerationByteLength);
                this.FaultInjector.BeforeGenerationPublicationIdentityCheck(temporaryName);
                LinuxFileIdentity namedIdentity = this.Workspace.Stat(temporaryName)
                    ?? throw new IOException("The new operation-generation file disappeared before publication.");
                if (
                    !namedIdentity.IsSameObject(temporary.Identity)
                    || namedIdentity.Kind != LinuxAnchoredEntryKind.RegularFile
                    || namedIdentity.LinkCount != 1
                    || namedIdentity.UnixMode != 0x180
                    || namedIdentity.Size != GenerationByteLength
                    || !this.Workspace.ReadAllBytes(temporary, GenerationByteLength).AsSpan().SequenceEqual(bytes)
                )
                    throw new IOException("The new operation-generation path or contents changed before publication.");
                temporaryIdentity = namedIdentity;
            }

            this.GenerationIdentity = this.Workspace.ReplaceFileAtomically(
                temporaryName,
                GenerationFileName,
                temporaryIdentity,
                this.GenerationIdentity
            );
            this.Workspace.FsyncDirectory();
            this.Generation = next;
            return next;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (temporaryIdentity is not null)
            {
                try
                {
                    this.Workspace.UnlinkFile(temporaryName, temporaryIdentity);
                }
                catch
                {
                    // Preserve the primary durable-generation failure.
                }
            }
            throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "Couldn't durably reserve the next installer operation generation.", exception);
        }
    }

    public void AssertRootAndGeneration(GameRootIdentity expectedRoot, ulong expectedGeneration)
    {
        this.AssertUsable();
        using LinuxAnchoredFileSystem currentlyNamedRoot = new(this.CanonicalGameRoot);
        if (
            expectedRoot != this.RootIdentity
            || !expectedRoot.Matches(this.Game.GetCurrentRootIdentity())
            || !expectedRoot.Matches(currentlyNamedRoot.Identity)
        )
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The selected game-root identity changed after confirmation.");
        (LinuxFileIdentity identity, ulong generation) = ReadGeneration(this.Workspace);
        if (identity != this.GenerationIdentity || generation != this.Generation || generation != expectedGeneration)
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The installer operation generation changed after confirmation.");
    }

    public void Dispose()
    {
        if (this.Disposed)
            return;
        this.Disposed = true;
        this.OperationLock.Dispose();
        this.Workspace.Dispose();
        this.Game.Dispose();
    }

    private static (LinuxFileIdentity Identity, ulong Generation) OpenOrCreateGeneration(LinuxAnchoredFileSystem workspace)
    {
        LinuxFileIdentity? identity = workspace.Stat(GenerationFileName);
        if (identity is null)
        {
            using LinuxAnchoredFile created = workspace.CreateNewFile(GenerationFileName, 0x180);
            byte[] bytes = EncodeGeneration(0);
            workspace.AppendAndFsync(created, GenerationFileName, bytes, 0, GenerationByteLength);
            workspace.FsyncDirectory();
        }
        return ReadGeneration(workspace);
    }

    internal static (LinuxFileIdentity Identity, ulong Generation) ReadGeneration(LinuxAnchoredFileSystem workspace)
    {
        using LinuxAnchoredFile file = workspace.OpenRegularFileForRead(GenerationFileName);
        if (file.Identity.UnixMode != 0x180 || file.Identity.Size != GenerationByteLength)
            throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "The installer operation-generation file has unsafe metadata.");
        byte[] bytes = workspace.ReadAllBytes(file, GenerationByteLength);
        string text = Encoding.ASCII.GetString(bytes);
        if (
            text.Length != GenerationByteLength
            || text[^1] != '\n'
            || !ulong.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong generation)
            || generation.ToString("D20", CultureInfo.InvariantCulture) + "\n" != text
        )
        {
            throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "The installer operation-generation file isn't canonical.");
        }
        return (file.Identity, generation);
    }

    private static byte[] EncodeGeneration(ulong generation)
    {
        return Encoding.ASCII.GetBytes(generation.ToString("D20", CultureInfo.InvariantCulture) + "\n");
    }

    private void AssertUsable()
    {
        if (this.Disposed)
            throw new ObjectDisposedException(nameof(InstallerOperationLease));
    }
}

/// <summary>A genuinely read-only, retry-validated view used before user confirmation.</summary>
internal sealed class InstallerInspectionLease : IDisposable
{
    private bool Disposed;

    public string CanonicalGameRoot { get; }
    public LinuxAnchoredFileSystem Game { get; }
    public GameRootIdentity RootIdentity { get; }
    public ulong Generation { get; }

    private InstallerInspectionLease(
        string canonicalGameRoot,
        LinuxAnchoredFileSystem game,
        GameRootIdentity rootIdentity,
        ulong generation
    )
    {
        this.CanonicalGameRoot = canonicalGameRoot;
        this.Game = game;
        this.RootIdentity = rootIdentity;
        this.Generation = generation;
    }

    public static InstallerInspectionLease Open(string gameRoot)
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        string canonicalRoot = TransactionPath.GetCanonicalRoot(gameRoot, nameof(gameRoot));
        LinuxAnchoredFileSystem game = new(canonicalRoot);
        try
        {
            GameRootIdentity root = GameRootIdentity.From(canonicalRoot, game.Identity);
            ulong generation = ReadExistingGeneration(game);
            InstallerInspectionLease result = new(canonicalRoot, game, root, generation);
            game = null!;
            result.AssertStable();
            return result;
        }
        finally
        {
            game?.Dispose();
        }
    }

    public void AssertStable()
    {
        if (this.Disposed)
            throw new ObjectDisposedException(nameof(InstallerInspectionLease));
        using LinuxAnchoredFileSystem named = new(this.CanonicalGameRoot);
        if (
            !this.RootIdentity.Matches(this.Game.GetCurrentRootIdentity())
            || !this.RootIdentity.Matches(named.Identity)
            || ReadExistingGeneration(this.Game) != this.Generation
        )
        {
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The game root or installer generation changed during read-only inspection.");
        }
    }

    public void Dispose()
    {
        if (this.Disposed)
            return;
        this.Disposed = true;
        this.Game.Dispose();
    }

    private static ulong ReadExistingGeneration(LinuxAnchoredFileSystem game)
    {
        LinuxFileIdentity? workspaceIdentity = game.Stat(InstallerTransactionExecutor.WorkspaceName);
        if (workspaceIdentity is null)
            return 0;
        if (workspaceIdentity.Kind != LinuxAnchoredEntryKind.Directory || workspaceIdentity.UnixMode != 0x1c0)
            throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "The existing installer workspace has unsafe metadata.");
        using LinuxAnchoredFileSystem workspace = game.OpenSubdirectory(InstallerTransactionExecutor.WorkspaceName);
        using (LinuxAnchoredFile marker = workspace.OpenRegularFileForRead(InstallerTransactionExecutor.WorkspaceMarkerName))
        {
            byte[] expected = Encoding.UTF8.GetBytes(InstallerTransactionExecutor.WorkspaceMarkerContents);
            if (
                marker.Identity.UnixMode != 0x180
                || marker.Identity.Size != expected.Length
                || !workspace.ReadAllBytes(marker, expected.Length).AsSpan().SequenceEqual(expected)
            )
                throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "The existing installer workspace marker isn't recognized.");
        }
        LinuxFileIdentity? generationIdentity = workspace.Stat("operation-generation");
        return generationIdentity is null ? 0 : InstallerOperationLease.ReadGeneration(workspace).Generation;
    }
}
