namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>Safety bounds for one release asset download.</summary>
public sealed record DownloadLimits
{
    /// <summary>The default bounds for a Linux installer asset.</summary>
    public static DownloadLimits Default { get; } = new(
        maxBytes: 512L * 1024 * 1024,
        timeout: TimeSpan.FromMinutes(10),
        maxRedirects: 5
    );

    /// <summary>The maximum bytes accepted from the response body.</summary>
    public long MaxBytes { get; }

    /// <summary>The total timeout across redirects and body streaming.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The maximum reviewed redirects.</summary>
    public int MaxRedirects { get; }

    /// <summary>Construct an instance.</summary>
    public DownloadLimits(long maxBytes, TimeSpan timeout, int maxRedirects)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (timeout <= TimeSpan.Zero || timeout == System.Threading.Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxRedirects is < 0 or > 20)
            throw new ArgumentOutOfRangeException(nameof(maxRedirects));

        this.MaxBytes = maxBytes;
        this.Timeout = timeout;
        this.MaxRedirects = maxRedirects;
    }
}

/// <summary>Streaming progress for a release asset download.</summary>
public sealed record DownloadProgress(long BytesReceived, long? TotalBytes);

/// <summary>The completed download.</summary>
public sealed record DownloadResult(string DestinationPath, long BytesReceived, Uri FinalUri);

/// <summary>Downloads one release asset into a local file.</summary>
public interface IReleaseAssetDownloader
{
    /// <summary>Download an asset, leaving no partial file on failure or cancellation.</summary>
    Task<DownloadResult> DownloadAsync(
        Uri sourceUri,
        string destinationPath,
        DownloadLimits limits,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Validates every initial and redirected download URI.</summary>
public interface IDownloadUriPolicy
{
    /// <summary>Validate a URI before an HTTP request is sent.</summary>
    /// <param name="uri">The absolute prospective request URI.</param>
    /// <param name="isInitial">Whether this is the caller-provided initial URI.</param>
    void AssertAllowed(Uri uri, bool isInitial);
}
