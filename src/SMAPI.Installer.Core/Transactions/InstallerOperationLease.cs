using System.Globalization;
using System.Text;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Transactions;

/// <summary>An exclusive descriptor-anchored operation lease for one exact game-root generation.</summary>
internal sealed class InstallerOperationLease : IDisposable
{
    private const string GenerationFileName = "operation-generation";
    private const int GenerationByteLength = 21;

    private LinuxFileIdentity GenerationIdentity;
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
        ulong generation
    )
    {
        this.CanonicalGameRoot = canonicalGameRoot;
        this.Game = game;
        this.Workspace = workspace;
        this.OperationLock = operationLock;
        this.RootIdentity = GameRootIdentity.From(canonicalGameRoot, game.Identity);
        this.GenerationIdentity = generationIdentity;
        this.Generation = generation;
    }

    public static InstallerOperationLease Acquire(string gameRoot)
    {
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
            InstallerOperationLease result = new(canonicalRoot, game, workspace, operationLock, identity, generation);
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
                temporaryIdentity = this.Workspace.Stat(temporaryName)
                    ?? throw new IOException("The new operation-generation file disappeared before publication.");
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
                    LinuxFileIdentity? temporaryCurrent = this.Workspace.Stat(temporaryName);
                    if (temporaryCurrent is not null)
                        this.Workspace.UnlinkFile(temporaryName, temporaryCurrent);
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

    private static (LinuxFileIdentity Identity, ulong Generation) ReadGeneration(LinuxAnchoredFileSystem workspace)
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
