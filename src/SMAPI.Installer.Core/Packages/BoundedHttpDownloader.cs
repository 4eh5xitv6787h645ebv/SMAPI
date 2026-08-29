using System.Buffers;
using System.Net;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>Streams an HTTP asset to a bounded temporary file using explicitly reviewed redirects.</summary>
/// <remarks>
/// Each attempt owns a unique private sibling staging file. The destination identity is captured before network I/O;
/// therefore, when attempts target the same destination concurrently, the first exact atomic publisher wins and every
/// later attempt fails closed without replacing that result.
/// </remarks>
public sealed class BoundedHttpDownloader : IReleaseAssetDownloader, IDisposable
{
    private const int PrivateFileMode = 0x180;
    private readonly HttpClient Client;
    private readonly IDownloadUriPolicy UriPolicy;

    /// <summary>Construct an instance using a redirect-disabled, cookie-free HTTP handler.</summary>
    /// <param name="uriPolicy">The policy applied before every request.</param>
    public BoundedHttpDownloader(IDownloadUriPolicy uriPolicy)
        : this(
            uriPolicy,
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            }
        )
    {
    }

    /// <summary>Construct with an internal deterministic handler while preserving the exact caller policy.</summary>
    internal BoundedHttpDownloader(IDownloadUriPolicy uriPolicy, HttpMessageHandler handler)
    {
        this.UriPolicy = uriPolicy ?? throw new ArgumentNullException(nameof(uriPolicy));
        this.Client = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
    }

    /// <inheritdoc />
    public async Task<DownloadResult> DownloadAsync(
        Uri sourceUri,
        string destinationPath,
        DownloadLimits limits,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        ArgumentNullException.ThrowIfNull(sourceUri);
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("The destination path is required.", nameof(destinationPath));
        ArgumentNullException.ThrowIfNull(limits);
        this.UriPolicy.AssertAllowed(sourceUri, isInitial: true);
        cancellationToken.ThrowIfCancellationRequested();

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        string destinationDirectory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new ArgumentException("The destination must have a parent directory.", nameof(destinationPath));
        string destinationName = Path.GetFileName(fullDestinationPath);
        if (destinationName.Length == 0)
            throw new ArgumentException("The destination must name a file.", nameof(destinationPath));

        using CancellationTokenSource timeoutSource = new(limits.Timeout);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token
        );
        LinuxAnchoredFileSystem? destinationFileSystem = null;
        string? temporaryName = null;
        LinuxFileIdentity? createdTemporaryIdentity = null;

        try
        {
            Directory.CreateDirectory(destinationDirectory);
            destinationFileSystem = new LinuxAnchoredFileSystem(destinationDirectory);
            LinuxFileIdentity destinationDirectoryIdentity = destinationFileSystem.Identity;
            LinuxFileIdentity? expectedDestination = destinationFileSystem.Stat(destinationName);
            (HttpResponseMessage response, Uri finalUri) = await this.GetResponseAsync(
                sourceUri,
                limits.MaxRedirects,
                linkedSource.Token
            ).ConfigureAwait(false);
            using (response)
            {
                long? declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength is < 0)
                    throw new PackageSecurityException("The release server returned an invalid content length.");
                if (declaredLength > limits.MaxBytes)
                    throw new PackageSecurityException("The release asset exceeds the configured download size limit.");
                long totalBytes = 0;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    temporaryName = $".smapi-download-{Guid.NewGuid():N}.tmp";
                    await using Stream input = await response.Content.ReadAsStreamAsync(linkedSource.Token).ConfigureAwait(false);
                    using LinuxAnchoredFile output = destinationFileSystem.CreateNewFile(temporaryName, PrivateFileMode);
                    createdTemporaryIdentity = output.Identity;

                    while (true)
                    {
                        int bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), linkedSource.Token).ConfigureAwait(false);
                        if (bytesRead == 0)
                            break;

                        long writeOffset = totalBytes;
                        totalBytes = checked(totalBytes + bytesRead);
                        if (totalBytes > limits.MaxBytes)
                            throw new PackageSecurityException("The release asset exceeded the configured download size limit while streaming.");

                        await RandomAccess.WriteAsync(
                            output.Handle,
                            buffer.AsMemory(0, bytesRead),
                            writeOffset,
                            linkedSource.Token
                        ).ConfigureAwait(false);
                        progress?.Report(new DownloadProgress(totalBytes, declaredLength));
                    }

                    if (declaredLength.HasValue && totalBytes != declaredLength.Value)
                        throw new PackageSecurityException("The release download ended before its declared content length was received.");
                    LinuxFileIdentity stagedIdentity = destinationFileSystem.Stat(temporaryName)
                        ?? throw new IOException("The private download staging file disappeared.");
                    if (
                        !stagedIdentity.IsSameObject(createdTemporaryIdentity)
                        || stagedIdentity.Size != totalBytes
                        || stagedIdentity.LinkCount != 1
                        || stagedIdentity.UnixMode != PrivateFileMode
                    )
                        throw new IOException("The private download staging file changed while streaming.");
                    stagedIdentity = destinationFileSystem.ChmodFile(temporaryName, stagedIdentity, PrivateFileMode);
                    linkedSource.Token.ThrowIfCancellationRequested();

                    BoundedHttpDownloader.AssertNamedDirectoryStillSelected(
                        destinationDirectory,
                        destinationDirectoryIdentity
                    );
                    LinuxFileIdentity published = destinationFileSystem.ReplaceFileAtomically(
                        temporaryName,
                        destinationName,
                        stagedIdentity,
                        expectedDestination
                    );
                    if (published.Size != totalBytes || published.UnixMode != PrivateFileMode || published.LinkCount != 1)
                        throw new IOException("The published release download failed exact metadata verification.");
                    temporaryName = null;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                return new DownloadResult(fullDestinationPath, totalBytes, finalUri);
            }
        }
        catch (OperationCanceledException ex) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            BoundedHttpDownloader.CleanupOwnedTemporary(
                destinationFileSystem,
                temporaryName,
                createdTemporaryIdentity
            );
            throw new PackageSecurityException(
                $"The release download timed out before it completed ({ex.GetType().Name})."
            );
        }
        catch (OperationCanceledException)
        {
            BoundedHttpDownloader.CleanupOwnedTemporary(
                destinationFileSystem,
                temporaryName,
                createdTemporaryIdentity
            );
            throw;
        }
        catch (PackageSecurityException)
        {
            BoundedHttpDownloader.CleanupOwnedTemporary(
                destinationFileSystem,
                temporaryName,
                createdTemporaryIdentity
            );
            throw;
        }
        catch (Exception ex)
        {
            BoundedHttpDownloader.CleanupOwnedTemporary(
                destinationFileSystem,
                temporaryName,
                createdTemporaryIdentity
            );
            throw new PackageSecurityException(
                $"The release download failed without exposing request credentials ({ex.GetType().Name})."
            );
        }
        finally
        {
            destinationFileSystem?.Dispose();
        }
    }

    /// <summary>Download into an already-retained anchored directory, publishing a previously absent exact leaf.</summary>
    internal async Task<DownloadResult> DownloadAsync(
        Uri sourceUri,
        AnchoredDownloadTarget destination,
        DownloadLimits limits,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        ArgumentNullException.ThrowIfNull(sourceUri);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(limits);
        this.UriPolicy.AssertAllowed(sourceUri, isInitial: true);
        destination.AssertReady();
        cancellationToken.ThrowIfCancellationRequested();

        using CancellationTokenSource timeoutSource = new(limits.Timeout);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token
        );
        string? temporaryName = null;
        LinuxFileIdentity? temporaryIdentity = null;
        try
        {
            (HttpResponseMessage response, Uri finalUri) = await this.GetResponseAsync(
                sourceUri,
                limits.MaxRedirects,
                linkedSource.Token
            ).ConfigureAwait(false);
            using (response)
            {
                long? declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength is < 0)
                    throw new PackageSecurityException("The release server returned an invalid content length.");
                if (declaredLength > limits.MaxBytes)
                    throw new PackageSecurityException("The release asset exceeds the configured download size limit.");
                if (declaredLength.HasValue && declaredLength.Value != destination.ExpectedBytes)
                    throw new PackageSecurityException("The release asset length differs from its catalog advertisement.");

                long totalBytes = 0;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    temporaryName = destination.GetFreshTemporaryName();
                    await using Stream input = await response.Content.ReadAsStreamAsync(linkedSource.Token).ConfigureAwait(false);
                    using LinuxAnchoredFile output = destination.FileSystem.CreateNewFile(temporaryName, PrivateFileMode);
                    temporaryIdentity = output.Identity;
                    while (true)
                    {
                        int bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), linkedSource.Token).ConfigureAwait(false);
                        if (bytesRead == 0)
                            break;

                        long writeOffset = totalBytes;
                        totalBytes = checked(totalBytes + bytesRead);
                        if (totalBytes > limits.MaxBytes)
                            throw new PackageSecurityException("The release asset exceeded the configured download size limit while streaming.");
                        await RandomAccess.WriteAsync(
                            output.Handle,
                            buffer.AsMemory(0, bytesRead),
                            writeOffset,
                            linkedSource.Token
                        ).ConfigureAwait(false);
                        progress?.Report(new DownloadProgress(totalBytes, declaredLength));
                    }

                    if (declaredLength.HasValue && totalBytes != declaredLength.Value)
                        throw new PackageSecurityException("The release download ended before its declared content length was received.");
                    if (totalBytes != destination.ExpectedBytes)
                        throw new PackageSecurityException("The release asset length differs from its catalog advertisement.");
                    LinuxFileIdentity staged = destination.FileSystem.Stat(temporaryName)
                        ?? throw new IOException("The private download staging file disappeared.");
                    if (
                        !staged.IsSameObject(temporaryIdentity)
                        || staged.Size != totalBytes
                        || staged.LinkCount != 1
                        || staged.OwnerUserId != destination.OwnerUserId
                        || staged.SpecialModeBits != 0
                        || staged.UnixMode != PrivateFileMode
                    )
                    {
                        throw new IOException("The private download staging file changed while streaming.");
                    }
                    linkedSource.Token.ThrowIfCancellationRequested();
                    destination.AssertReady();
                    LinuxFileIdentity published = destination.FileSystem.RenameFileNoReplace(
                        temporaryName,
                        destination.LeafName,
                        staged
                    );
                    temporaryName = null;
                    destination.SetPublished(published);
                    if (
                        published.Size != totalBytes
                        || published.UnixMode != PrivateFileMode
                        || published.LinkCount != 1
                        || published.OwnerUserId != destination.OwnerUserId
                        || published.SpecialModeBits != 0
                    )
                    {
                        throw new IOException("The published release download failed exact metadata verification.");
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                return new DownloadResult(destination.ProcPath, totalBytes, finalUri);
            }
        }
        catch (OperationCanceledException ex) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            CleanupOwnedTemporary(destination.FileSystem, temporaryName, temporaryIdentity);
            throw new PackageSecurityException($"The release download timed out before it completed ({ex.GetType().Name}).");
        }
        catch (OperationCanceledException)
        {
            CleanupOwnedTemporary(destination.FileSystem, temporaryName, temporaryIdentity);
            throw;
        }
        catch (PackageSecurityException)
        {
            CleanupOwnedTemporary(destination.FileSystem, temporaryName, temporaryIdentity);
            throw;
        }
        catch (Exception ex)
        {
            CleanupOwnedTemporary(destination.FileSystem, temporaryName, temporaryIdentity);
            throw new PackageSecurityException($"The release download failed without exposing request credentials ({ex.GetType().Name}).");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.Client.Dispose();
    }

    private async Task<(HttpResponseMessage Response, Uri FinalUri)> GetResponseAsync(
        Uri sourceUri,
        int maxRedirects,
        CancellationToken cancellationToken
    )
    {
        Uri currentUri = sourceUri;
        for (int redirectCount = 0; ; redirectCount++)
        {
            this.UriPolicy.AssertAllowed(currentUri, isInitial: redirectCount == 0);

            using HttpRequestMessage request = new(HttpMethod.Get, currentUri);
            request.Headers.Accept.ParseAdd("application/octet-stream");
            request.Headers.UserAgent.ParseAdd("SMAPI-Linux-Installer");

            HttpResponseMessage response = await this.Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            ).ConfigureAwait(false);

            if (!BoundedHttpDownloader.IsRedirect(response.StatusCode))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    HttpStatusCode statusCode = response.StatusCode;
                    response.Dispose();
                    throw new PackageSecurityException(
                        $"The release server returned HTTP {(int)statusCode} from {BoundedHttpDownloader.GetSafeOrigin(currentUri)}."
                    );
                }
                if (response.Content.Headers.ContentEncoding.Count > 0)
                {
                    response.Dispose();
                    throw new PackageSecurityException("The release server returned an unexpected HTTP content encoding.");
                }
                return (response, currentUri);
            }

            if (redirectCount >= maxRedirects)
            {
                response.Dispose();
                throw new PackageSecurityException("The release download exceeded the reviewed redirect limit.");
            }

            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location == null)
                throw new PackageSecurityException("The release server returned a redirect without a destination.");

            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static string GetSafeOrigin(Uri uri)
    {
        return $"{uri.Scheme}://{uri.DnsSafeHost}";
    }

    private static void AssertNamedDirectoryStillSelected(
        string directoryPath,
        LinuxFileIdentity expectedIdentity
    )
    {
        using LinuxAnchoredFileSystem currentlyNamed = new(directoryPath);
        if (!currentlyNamed.Identity.IsSameObject(expectedIdentity))
            throw new IOException("The selected download directory changed before atomic publication.");
    }

    private static void CleanupOwnedTemporary(
        LinuxAnchoredFileSystem? destinationFileSystem,
        string? temporaryName,
        LinuxFileIdentity? createdIdentity
    )
    {
        if (destinationFileSystem is null || temporaryName is null || createdIdentity is null)
            return;
        try
        {
            LinuxFileIdentity? current = destinationFileSystem.Stat(temporaryName);
            if (current is not null && current.IsSameObject(createdIdentity))
                destinationFileSystem.UnlinkFile(temporaryName, current);
        }
        catch
        {
            // Best-effort cleanup is limited to the exact file this attempt created.
        }
    }
}

/// <summary>An opaque, retained, previously absent publication target for one bounded download.</summary>
internal sealed class AnchoredDownloadTarget
{
    private int Published;
    private int TemporaryIssued;

    public LinuxAnchoredFileSystem FileSystem { get; }
    public string LeafName { get; }
    public string ProcPath { get; }
    public uint OwnerUserId { get; }
    public long ExpectedBytes { get; }
    public LinuxFileIdentity? PublishedIdentity { get; private set; }
    private readonly Action<LinuxFileIdentity> OnPublished;

    public AnchoredDownloadTarget(
        LinuxAnchoredFileSystem fileSystem,
        string leafName,
        string procDirectoryPath,
        uint ownerUserId,
        long expectedBytes,
        Action<LinuxFileIdentity> onPublished
    )
    {
        this.FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        if (string.IsNullOrEmpty(leafName) || leafName.Contains('/') || leafName.Any(char.IsControl))
            throw new PackageSecurityException("The reviewed release asset name isn't a safe exact leaf.");
        this.LeafName = leafName;
        this.ProcPath = Path.Combine(procDirectoryPath, leafName);
        this.OwnerUserId = ownerUserId;
        this.ExpectedBytes = expectedBytes > 0
            ? expectedBytes
            : throw new ArgumentOutOfRangeException(nameof(expectedBytes));
        this.OnPublished = onPublished ?? throw new ArgumentNullException(nameof(onPublished));
    }

    public void AssertReady()
    {
        LinuxFileIdentity root = this.FileSystem.GetCurrentRootIdentity();
        if (
            Volatile.Read(ref this.Published) != 0
            || root.Kind != LinuxAnchoredEntryKind.Directory
            || root.LinkCount < 1
            || root.OwnerUserId != this.OwnerUserId
            || root.SpecialModeBits != 0
            || root.UnixMode != 0x1c0
            || this.FileSystem.Stat(this.LeafName) is not null
        )
        {
            throw new PackageSecurityException("The retained private release workspace changed before download publication.");
        }
    }

    public string GetFreshTemporaryName()
    {
        if (Interlocked.Exchange(ref this.TemporaryIssued, 1) != 0)
            throw new PackageSecurityException("The bounded release download attempted more than one private staging file.");
        return $".download-{Guid.NewGuid():N}.tmp";
    }

    public void SetPublished(LinuxFileIdentity identity)
    {
        this.OnPublished(identity);
        this.PublishedIdentity = identity;
        Volatile.Write(ref this.Published, 1);
    }
}
