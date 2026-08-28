using System.Buffers;
using System.Net;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>Streams an HTTP asset to a bounded temporary file using explicitly reviewed redirects.</summary>
public sealed class BoundedHttpDownloader : IReleaseAssetDownloader, IDisposable
{
    private readonly HttpClient Client;
    private readonly IDownloadUriPolicy UriPolicy;

    /// <summary>Construct an instance using a redirect-disabled, cookie-free HTTP handler.</summary>
    /// <param name="uriPolicy">The policy applied before every request.</param>
    public BoundedHttpDownloader(IDownloadUriPolicy uriPolicy)
    {
        this.UriPolicy = uriPolicy ?? throw new ArgumentNullException(nameof(uriPolicy));
        this.Client = new HttpClient(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            },
            disposeHandler: true
        )
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
        string partialPath = fullDestinationPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestinationPath)!);
        BoundedHttpDownloader.DeletePartialFile(partialPath);

        using CancellationTokenSource timeoutSource = new(limits.Timeout);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token
        );

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

                long totalBytes = 0;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    await using Stream input = await response.Content.ReadAsStreamAsync(linkedSource.Token).ConfigureAwait(false);
                    await using FileStream output = new(
                        partialPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 64 * 1024,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan
                    );

                    while (true)
                    {
                        int bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), linkedSource.Token).ConfigureAwait(false);
                        if (bytesRead == 0)
                            break;

                        totalBytes = checked(totalBytes + bytesRead);
                        if (totalBytes > limits.MaxBytes)
                            throw new PackageSecurityException("The release asset exceeded the configured download size limit while streaming.");

                        await output.WriteAsync(buffer.AsMemory(0, bytesRead), linkedSource.Token).ConfigureAwait(false);
                        progress?.Report(new DownloadProgress(totalBytes, declaredLength));
                    }

                    if (declaredLength.HasValue && totalBytes != declaredLength.Value)
                        throw new PackageSecurityException("The release download ended before its declared content length was received.");

                    await output.FlushAsync(linkedSource.Token).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                linkedSource.Token.ThrowIfCancellationRequested();
                File.Move(partialPath, fullDestinationPath, overwrite: true);
                return new DownloadResult(fullDestinationPath, totalBytes, finalUri);
            }
        }
        catch (OperationCanceledException ex) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            BoundedHttpDownloader.DeletePartialFile(partialPath);
            throw new PackageSecurityException(
                $"The release download timed out before it completed ({ex.GetType().Name})."
            );
        }
        catch (OperationCanceledException)
        {
            BoundedHttpDownloader.DeletePartialFile(partialPath);
            throw;
        }
        catch (PackageSecurityException)
        {
            BoundedHttpDownloader.DeletePartialFile(partialPath);
            throw;
        }
        catch (Exception ex)
        {
            BoundedHttpDownloader.DeletePartialFile(partialPath);
            throw new PackageSecurityException(
                $"The release download failed without exposing request credentials ({ex.GetType().Name})."
            );
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

    private static void DeletePartialFile(string partialPath)
    {
        try
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
        }
        catch
        {
            // Best-effort cleanup. The original credential-safe failure remains the useful error.
        }
    }
}
