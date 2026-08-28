using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StardewModdingAPI.Installer.Core.Privacy;

/// <summary>Options for a bounded local installer log.</summary>
public sealed record InstallerLogOptions(
    string StateRoot,
    int MaximumFileBytes = 1024 * 1024,
    int MaximumFileCount = 5,
    int MaximumMessageCharacters = 4096
);

/// <summary>A privacy-scoped structured installer log entry.</summary>
public sealed record InstallerLogEntry(
    DateTimeOffset Timestamp,
    Guid OperationId,
    InstallerLogLevel Level,
    string EventCode,
    string Message,
    string? ReleaseTag = null,
    string? RelativeOwnedPath = null,
    string? StableErrorCode = null
);

/// <summary>A structured installer log level.</summary>
public enum InstallerLogLevel
{
    Information,
    Warning,
    Error
}

/// <summary>Writes local-only bounded JSONL logs with explicit redaction.</summary>
public sealed class InstallerLog : IDisposable
{
    private static readonly Regex UriQuery = new(@"(?<uri>https://[^\s?#]+)(?:\?[^\s#]*)?(?:#[^\s]*)?", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Credential = new(@"(?i)\b(authorization|cookie|set-cookie|token|access_token|refresh_token|signature|sig|x-amz-[a-z-]+)\b\s*[:=]\s*[^\s,;]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Bearer = new(@"(?i)\bbearer\s+[a-z0-9._~+/=-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ControlCharacters = new(@"[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly InstallerLogOptions Options;
    private readonly FileStream Stream;
    private readonly string[] SensitiveValues;
    private int WrittenBytes;
    private bool Disposed;

    /// <summary>The private local log path.</summary>
    public string Path { get; }

    /// <summary>Construct a new operation log.</summary>
    /// <param name="options">The bounds and state location.</param>
    /// <param name="operationId">The operation ID used in the non-sensitive filename.</param>
    /// <param name="now">The current time.</param>
    /// <param name="sensitiveValues">Exact local path, mod/save/report, or secret canaries to redact wherever they appear.</param>
    public InstallerLog(InstallerLogOptions options, Guid operationId, DateTimeOffset now, IEnumerable<string>? sensitiveValues = null)
    {
        this.Options = ValidateOptions(options);
        if (operationId == Guid.Empty)
            throw new ArgumentException("An operation ID is required.", nameof(operationId));

        this.SensitiveValues = sensitiveValues?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray()
            ?? Array.Empty<string>();

        string logsDirectory = System.IO.Path.Combine(this.Options.StateRoot, "logs");
        Directory.CreateDirectory(logsDirectory);
        SetMode(logsDirectory, Convert.ToInt32("700", 8));
        Rotate(logsDirectory, this.Options.MaximumFileCount - 1);

        string timestamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        this.Path = System.IO.Path.Combine(logsDirectory, $"{timestamp}-{operationId:N}.jsonl");
        this.Stream = new FileStream(this.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
        SetMode(this.Path, Convert.ToInt32("600", 8));
    }

    /// <summary>Write an entry if it fits within the configured bound.</summary>
    /// <returns>Whether the entry was written.</returns>
    public bool Write(InstallerLogEntry entry)
    {
        if (this.Disposed)
            throw new ObjectDisposedException(nameof(InstallerLog));
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.OperationId == Guid.Empty)
            throw new ArgumentException("A log entry requires an operation ID.", nameof(entry));

        string message = this.Redact(entry.Message);
        if (message.Length > this.Options.MaximumMessageCharacters)
            message = message[..this.Options.MaximumMessageCharacters] + "…[truncated]";
        string eventCode = ValidateIdentifier(entry.EventCode, nameof(entry.EventCode));
        string? releaseTag = entry.ReleaseTag is null ? null : ValidateIdentifier(entry.ReleaseTag, nameof(entry.ReleaseTag));
        string? stableErrorCode = entry.StableErrorCode is null ? null : ValidateIdentifier(entry.StableErrorCode, nameof(entry.StableErrorCode));
        string? relativeOwnedPath = entry.RelativeOwnedPath is null ? null : ValidateRelativeOwnedPath(entry.RelativeOwnedPath);

        byte[] line = JsonSerializer.SerializeToUtf8Bytes(new
        {
            timestamp = entry.Timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            operationId = entry.OperationId.ToString("N"),
            level = entry.Level.ToString(),
            eventCode,
            message,
            releaseTag,
            relativeOwnedPath,
            stableErrorCode
        });
        if (this.WrittenBytes + line.Length + 1 > this.Options.MaximumFileBytes)
            return false;

        this.Stream.Write(line);
        this.Stream.WriteByte((byte)'\n');
        this.Stream.Flush(flushToDisk: true);
        this.WrittenBytes += line.Length + 1;
        return true;
    }

    /// <summary>Redact a message for storage in the private local log.</summary>
    public string Redact(string? value)
    {
        string result = value ?? string.Empty;
        foreach (string sensitiveValue in this.SensitiveValues)
            result = result.Replace(sensitiveValue, "[redacted]", StringComparison.Ordinal);
        result = UriQuery.Replace(result, match => match.Groups["uri"].Value);
        result = Credential.Replace(result, match => $"{match.Groups[1].Value}=[redacted]");
        result = Bearer.Replace(result, "Bearer [redacted]");
        result = ControlCharacters.Replace(result, "�");
        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.Disposed)
            return;
        this.Stream.Dispose();
        this.Disposed = true;
    }

    private static InstallerLogOptions ValidateOptions(InstallerLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!System.IO.Path.IsPathRooted(options.StateRoot))
            throw new ArgumentException("The installer state root must be absolute.", nameof(options));
        if (options.MaximumFileBytes is < 1024 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "The log file bound must be between 1 KiB and 16 MiB.");
        if (options.MaximumFileCount is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(options), "The log count bound must be between 1 and 20.");
        if (options.MaximumMessageCharacters is < 128 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(options), "The log message bound must be between 128 and 16384 characters.");
        return options with { StateRoot = System.IO.Path.GetFullPath(options.StateRoot) };
    }

    private static void Rotate(string directory, int retainedPriorFiles)
    {
        string[] files = Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .ThenByDescending(path => path, StringComparer.Ordinal)
            .ToArray();
        foreach (string path in files.Skip(retainedPriorFiles))
            File.Delete(path);
    }

    private static string ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(character => !((character is >= 'a' and <= 'z') || (character is >= 'A' and <= 'Z') || (character is >= '0' and <= '9') || character is '.' or '-' or '_' or '+')))
            throw new ArgumentException("A log identifier contains unsupported characters.", parameterName);
        return value;
    }

    private static string ValidateRelativeOwnedPath(string value)
    {
        if (value.Length > 512 || System.IO.Path.IsPathRooted(value) || value.StartsWith('/') || value.StartsWith('\\') || value.Contains('\\'))
            throw new ArgumentException("A logged owned path must be normalized and relative.", nameof(value));
        string[] segments = value.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new ArgumentException("A logged owned path contains an invalid segment.", nameof(value));
        return value;
    }

    private static void SetMode(string path, int mode)
    {
        if (!OperatingSystem.IsLinux())
            return;
        if (chmod(path, (uint)mode) != 0)
            throw new IOException("Couldn't set private installer-log permissions.");
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, uint mode);
}
