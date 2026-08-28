using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Ownership;

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
    NormalizedRelativePath? RelativeOwnedPath = null,
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
    private const string StateMarkerContents = "smapi-installer-state-v1\n";
    private static readonly Regex OwnedLogFilename = new(@"\A[0-9]{8}T[0-9]{6}Z-[0-9a-f]{32}\.jsonl\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UriQuery = new(@"(?<uri>https://[^\s?#]+)(?:\?[^\s#]*)?(?:#[^\s]*)?", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Credential = new(@"(?i)\b(authorization|cookie|set-cookie|token|access_token|refresh_token|signature|sig|x-amz-[a-z-]+)\b\s*[:=]\s*[^\s,;]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Bearer = new(@"(?i)\bbearer\s+[a-z0-9._~+/=-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ControlCharacters = new(@"[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly InstallerLogOptions Options;
    private readonly LinuxAnchoredFileSystem FileSystem;
    private readonly LinuxAnchoredFile File;
    private readonly string RelativeLogPath;
    private readonly string[] SensitiveValues;
    private readonly Guid OperationId;
    private readonly object SyncRoot = new();
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
        LinuxPrivilegeGuard.AssertNotRoot();
        this.Options = ValidateOptions(options);
        if (operationId == Guid.Empty)
            throw new ArgumentException("An operation ID is required.", nameof(operationId));

        this.SensitiveValues = (sensitiveValues ?? Array.Empty<string>())
            .Concat(new[]
            {
                this.Options.StateRoot,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray()
            .ToArray();

        this.OperationId = operationId;

        string timestamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        this.RelativeLogPath = $"{timestamp}-{operationId:N}.jsonl";
        string logsDirectory = System.IO.Path.Combine(this.Options.StateRoot, "logs");
        this.Path = System.IO.Path.Combine(logsDirectory, this.RelativeLogPath);

        LinuxAnchoredFileSystem? logsFileSystem = null;
        LinuxAnchoredFile? logFile = null;
        try
        {
            using LinuxAnchoredFileSystem filesystemRoot = new("/");
            string stateRelativePath = GetRootRelativePath(this.Options.StateRoot);
            LinuxFileIdentity stateIdentity = filesystemRoot.EnsureDirectory(stateRelativePath, Convert.ToInt32("700", 8), out bool stateCreated);
            using LinuxAnchoredFileSystem stateFileSystem = filesystemRoot.OpenSubdirectory(stateRelativePath);
            if (!stateFileSystem.Identity.IsSameObject(stateIdentity))
                throw new IOException("The installer state root identity changed while it was opened.");
            EnsurePrivateStateMarker(stateFileSystem, stateCreated);

            LinuxFileIdentity logsIdentity = stateFileSystem.EnsureDirectory("logs", Convert.ToInt32("700", 8));
            logsFileSystem = stateFileSystem.OpenSubdirectory("logs");
            if (!logsFileSystem.Identity.IsSameObject(logsIdentity))
                throw new IOException("The installer log directory identity changed while it was opened.");

            Rotate(logsFileSystem, this.Options.MaximumFileCount - 1);
            logFile = logsFileSystem.CreateNewFile(this.RelativeLogPath, Convert.ToInt32("600", 8));
        }
        catch
        {
            logFile?.Dispose();
            logsFileSystem?.Dispose();
            throw;
        }

        this.FileSystem = logsFileSystem!;
        this.File = logFile!;
    }

    /// <summary>Write an entry if it fits within the configured bound.</summary>
    /// <returns>Whether the entry was written.</returns>
    public bool Write(InstallerLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (this.SyncRoot)
        {
            if (this.Disposed)
                throw new ObjectDisposedException(nameof(InstallerLog));
            if (entry.OperationId != this.OperationId)
                throw new ArgumentException("A log entry's operation ID doesn't match its log.", nameof(entry));

            string message = this.Redact(entry.Message);
            if (message.Length > this.Options.MaximumMessageCharacters)
                message = message[..this.Options.MaximumMessageCharacters] + "…[truncated]";
            string eventCode = ValidateIdentifier(entry.EventCode, nameof(entry.EventCode));
            string? releaseTag = entry.ReleaseTag is null ? null : ValidateIdentifier(entry.ReleaseTag, nameof(entry.ReleaseTag));
            string? stableErrorCode = entry.StableErrorCode is null ? null : ValidateIdentifier(entry.StableErrorCode, nameof(entry.StableErrorCode));
            string? relativeOwnedPath = entry.RelativeOwnedPath is null ? null : ValidateOwnedPath(entry.RelativeOwnedPath);

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

            byte[] record = new byte[line.Length + 1];
            line.CopyTo(record, 0);
            record[^1] = (byte)'\n';
            this.WrittenBytes = checked((int)this.FileSystem.AppendAndFsync(
                this.File,
                this.RelativeLogPath,
                record,
                this.WrittenBytes,
                this.Options.MaximumFileBytes
            ));
            return true;
        }
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
        lock (this.SyncRoot)
        {
            if (this.Disposed)
                return;
            this.File.Dispose();
            this.FileSystem.Dispose();
            this.Disposed = true;
        }
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

    private static void Rotate(LinuxAnchoredFileSystem fileSystem, int retainedPriorFiles)
    {
        (string Name, LinuxFileIdentity Identity)[] files = fileSystem.EnumerateEntryNames()
            .Where(name => InstallerLog.OwnedLogFilename.IsMatch(name))
            .Select(name => (Name: name, Identity: fileSystem.Stat(name) ?? throw new IOException("An installer-owned log disappeared during rotation.")))
            .OrderByDescending(entry => entry.Identity.ModificationSeconds)
            .ThenByDescending(entry => entry.Identity.ModificationNanoseconds)
            .ThenByDescending(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        foreach ((string name, LinuxFileIdentity identity) in files.Skip(retainedPriorFiles))
            fileSystem.UnlinkFile(name, identity);
    }

    private static void EnsurePrivateStateMarker(LinuxAnchoredFileSystem fileSystem, bool stateCreated)
    {
        const string markerPath = "state-version";
        if (stateCreated)
        {
            byte[] contents = Encoding.UTF8.GetBytes(StateMarkerContents);
            using LinuxAnchoredFile marker = fileSystem.CreateNewFile(markerPath, Convert.ToInt32("600", 8));
            fileSystem.AppendAndFsync(marker, markerPath, contents, 0, contents.Length);
            return;
        }

        using LinuxAnchoredFile existingMarker = fileSystem.OpenRegularFileForRead(markerPath);
        byte[] existingContents = fileSystem.ReadAllBytes(existingMarker, 128);
        if (!existingContents.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(StateMarkerContents)))
            throw new IOException("An unknown existing installer state root requires manual inspection.");
    }

    private static string GetRootRelativePath(string absolutePath)
    {
        if (!absolutePath.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("The installer state root must be an absolute Linux path.", nameof(absolutePath));
        string relativePath = absolutePath.TrimStart('/');
        if (relativePath.Length == 0)
            throw new ArgumentException("The filesystem root can't be used as installer state.", nameof(absolutePath));
        return NormalizedRelativePath.Parse(relativePath).Value;
    }

    private static string ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(character => !((character is >= 'a' and <= 'z') || (character is >= 'A' and <= 'Z') || (character is >= '0' and <= '9') || character is '.' or '-' or '_' or '+')))
            throw new ArgumentException("A log identifier contains unsupported characters.", parameterName);
        return value;
    }

    private static string ValidateOwnedPath(NormalizedRelativePath value)
    {
        if (!OwnedNamespacePolicy.IsAllowedTransactionDestination(value))
            throw new ArgumentException("A logged path isn't in the compiled installer-owned namespace.", nameof(value));
        return value.Value;
    }
}
