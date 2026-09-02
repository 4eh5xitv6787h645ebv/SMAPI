using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Privacy;

/// <summary>Options for a bounded local installer log.</summary>
public sealed record InstallerLogOptions(
    string StateRoot,
    int MaximumFileBytes = 1024 * 1024,
    int MaximumFileCount = 5,
    int MaximumMessageCharacters = 4096,
    int MaximumRawMessageCharacters = 16384,
    int MaximumSensitiveValueCount = 64,
    int MaximumSensitiveValueCharacters = 4096,
    int MaximumSensitiveValueTotalCharacters = 32768,
    int MaximumDirectoryEntries = 256,
    long MaximumAggregateBytes = 0,
    int MaximumEntryCount = 2048,
    int TerminalReserveBytes = 512
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
    private const string LifetimeLockPath = "installer-log.lock";
    private const int PrivateDirectoryMode = 0x1c0;
    private const int PrivateFileMode = 0x180;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex OwnedLogFilename = new(
        @"\A[0-9]{8}T[0-9]{6}Z-[0-9a-f]{32}\.jsonl\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout
    );
    private static readonly Regex UriQuery = new(
        @"(?<uri>https://[^\s?#]+)(?:\?[^\s#]*)?(?:#[^\s]*)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex Credential = new(
        @"\b(authorization|cookie|set-cookie|token|access_token|refresh_token|signature|sig|x-amz-[a-z-]+)\b\s*[:=]\s*[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex Bearer = new(
        @"\bbearer\s+[a-z0-9._~+/=-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly InstallerLogOptions Options;
    private readonly LinuxAnchoredFileSystem FileSystem;
    private readonly LinuxAnchoredFile File;
    private readonly LinuxAnchoredFile LifetimeLock;
    private readonly string RelativeLogPath;
    private readonly string[] SensitiveValues;
    private readonly Guid OperationId;
    private readonly object SyncRoot = new();
    private int WrittenBytes;
    private int WrittenEntries;
    private bool TruncationMarkerWritten;
    private bool TerminalWritten;
    private bool Disposed;

    /// <summary>The private local log path.</summary>
    public string Path { get; }

    /// <summary>Construct a new operation log.</summary>
    public InstallerLog(InstallerLogOptions options, Guid operationId, DateTimeOffset now, IEnumerable<string>? sensitiveValues = null)
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        this.Options = ValidateOptions(options);
        if (operationId == Guid.Empty)
            throw new ArgumentException("An operation ID is required.", nameof(operationId));

        this.SensitiveValues = GetSensitiveValues(this.Options, sensitiveValues);
        this.OperationId = operationId;

        string timestamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        this.RelativeLogPath = $"{timestamp}-{operationId:N}.jsonl";
        string logsDirectory = System.IO.Path.Combine(this.Options.StateRoot, "logs");
        this.Path = System.IO.Path.Combine(logsDirectory, this.RelativeLogPath);

        LinuxAnchoredFileSystem? logsFileSystem = null;
        LinuxAnchoredFile? lifetimeLock = null;
        LinuxAnchoredFile? logFile = null;
        try
        {
            uint effectiveUserId = geteuid();
            using LinuxAnchoredFileSystem filesystemRoot = new("/");
            string stateRelativePath = GetRootRelativePath(this.Options.StateRoot);
            LinuxFileIdentity? existingState = filesystemRoot.Stat(stateRelativePath);
            if (existingState is not null)
                AssertPrivate(existingState, LinuxAnchoredEntryKind.Directory, PrivateDirectoryMode, effectiveUserId, "installer state root");
            LinuxFileIdentity stateIdentity = filesystemRoot.EnsureDirectory(stateRelativePath, PrivateDirectoryMode, out bool stateCreated);
            AssertPrivate(stateIdentity, LinuxAnchoredEntryKind.Directory, PrivateDirectoryMode, effectiveUserId, "installer state root");
            using LinuxAnchoredFileSystem stateFileSystem = filesystemRoot.OpenSubdirectory(stateRelativePath);
            if (!stateFileSystem.Identity.IsSameObject(stateIdentity))
                throw new IOException("The installer state root identity changed while it was opened.");
            AssertPrivate(stateFileSystem.Identity, LinuxAnchoredEntryKind.Directory, PrivateDirectoryMode, effectiveUserId, "installer state root");
            EnsurePrivateStateMarker(stateFileSystem, stateCreated, effectiveUserId);

            LinuxFileIdentity? existingLogs = stateFileSystem.Stat("logs");
            if (existingLogs is not null)
                AssertPrivate(existingLogs, LinuxAnchoredEntryKind.Directory, PrivateDirectoryMode, effectiveUserId, "installer log directory");
            LinuxFileIdentity logsIdentity = stateFileSystem.EnsureDirectory("logs", PrivateDirectoryMode);
            AssertPrivate(logsIdentity, LinuxAnchoredEntryKind.Directory, PrivateDirectoryMode, effectiveUserId, "installer log directory");
            logsFileSystem = stateFileSystem.OpenSubdirectory("logs");
            if (!logsFileSystem.Identity.IsSameObject(logsIdentity))
                throw new IOException("The installer log directory identity changed while it was opened.");
            AssertPrivate(logsFileSystem.Identity, LinuxAnchoredEntryKind.Directory, PrivateDirectoryMode, effectiveUserId, "installer log directory");

            LinuxFileIdentity? existingLock = logsFileSystem.Stat(LifetimeLockPath);
            if (existingLock is not null)
                AssertPrivate(existingLock, LinuxAnchoredEntryKind.RegularFile, PrivateFileMode, effectiveUserId, "installer log lock");
            lifetimeLock = logsFileSystem.AcquireExclusiveFileLock(LifetimeLockPath, PrivateFileMode);
            AssertPrivate(lifetimeLock.Identity, LinuxAnchoredEntryKind.RegularFile, PrivateFileMode, effectiveUserId, "installer log lock");
            if (existingLock is not null && !lifetimeLock.Identity.IsSameObject(existingLock))
                throw new IOException("The installer log lock identity changed while it was acquired.");

            Rotate(logsFileSystem, this.Options, effectiveUserId);
            logFile = logsFileSystem.CreateNewFile(this.RelativeLogPath, PrivateFileMode);
            AssertPrivate(logFile.Identity, LinuxAnchoredEntryKind.RegularFile, PrivateFileMode, effectiveUserId, "installer log");
        }
        catch
        {
            logFile?.Dispose();
            lifetimeLock?.Dispose();
            logsFileSystem?.Dispose();
            throw;
        }

        this.FileSystem = logsFileSystem!;
        this.LifetimeLock = lifetimeLock!;
        this.File = logFile!;
    }

    /// <summary>Write an ordinary entry if it fits while preserving truncation and terminal reserves.</summary>
    public bool Write(InstallerLogEntry entry)
    {
        return this.Write(entry, isTerminal: false);
    }

    /// <summary>Write the single terminal operation entry from the capacity reserved for it.</summary>
    public bool WriteTerminal(InstallerLogEntry entry)
    {
        return this.Write(entry, isTerminal: true);
    }

    /// <summary>Redact a bounded message for storage in the private local log.</summary>
    public string Redact(string? value)
    {
        string result = value ?? string.Empty;
        if (result.Length > this.Options.MaximumRawMessageCharacters)
            return "[message omitted: raw input exceeded limit]";

        foreach (string sensitiveValue in this.SensitiveValues)
            result = result.Replace(sensitiveValue, "[redacted]", StringComparison.Ordinal);
        try
        {
            result = UriQuery.Replace(result, match => match.Groups["uri"].Value);
            result = Credential.Replace(result, match => $"{match.Groups[1].Value}=[redacted]");
            result = Bearer.Replace(result, "Bearer [redacted]");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[message omitted: redaction pattern limit exceeded]";
        }
        return EscapeUnsafeUnicode(result);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (this.SyncRoot)
        {
            if (this.Disposed)
                return;
            this.File.Dispose();
            this.LifetimeLock.Dispose();
            this.FileSystem.Dispose();
            this.Disposed = true;
        }
    }

    private bool Write(InstallerLogEntry entry, bool isTerminal)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (this.SyncRoot)
        {
            if (this.Disposed)
                throw new ObjectDisposedException(nameof(InstallerLog));
            if (entry.OperationId != this.OperationId)
                throw new ArgumentException("A log entry's operation ID doesn't match its log.", nameof(entry));
            if (this.TerminalWritten || (!isTerminal && this.TruncationMarkerWritten))
                return false;

            byte[] record = this.SerializeRecord(entry);
            if (isTerminal)
            {
                if (record.Length > this.Options.TerminalReserveBytes || this.WrittenBytes + record.Length > this.Options.MaximumFileBytes)
                    return false;
                this.Append(record);
                this.TerminalWritten = true;
                return true;
            }

            byte[] truncationMarker = this.SerializeTruncationMarker(entry.Timestamp);
            if (
                this.WrittenEntries >= this.Options.MaximumEntryCount
                || this.WrittenBytes + record.Length + truncationMarker.Length + this.Options.TerminalReserveBytes > this.Options.MaximumFileBytes
            )
            {
                this.AppendTruncationMarker(truncationMarker);
                return false;
            }

            this.Append(record);
            this.WrittenEntries++;
            return true;
        }
    }

    private void AppendTruncationMarker(byte[] marker)
    {
        if (this.TruncationMarkerWritten)
            return;
        if (this.WrittenBytes + marker.Length + this.Options.TerminalReserveBytes > this.Options.MaximumFileBytes)
            throw new IOException("The installer log couldn't preserve its truncation and terminal reserves.");
        this.Append(marker);
        this.TruncationMarkerWritten = true;
    }

    private void Append(byte[] record)
    {
        this.WrittenBytes = checked((int)this.FileSystem.AppendAndFsync(
            this.File,
            this.RelativeLogPath,
            record,
            this.WrittenBytes,
            this.Options.MaximumFileBytes
        ));
    }

    private byte[] SerializeRecord(InstallerLogEntry entry)
    {
        string message = this.Redact(entry.Message);
        if (message.Length > this.Options.MaximumMessageCharacters)
            message = Truncate(message, this.Options.MaximumMessageCharacters);
        string eventCode = ValidateIdentifier(entry.EventCode, nameof(entry.EventCode));
        string? releaseTag = entry.ReleaseTag is null ? null : ValidateIdentifier(entry.ReleaseTag, nameof(entry.ReleaseTag));
        string? stableErrorCode = entry.StableErrorCode is null ? null : ValidateIdentifier(entry.StableErrorCode, nameof(entry.StableErrorCode));
        string? relativeOwnedPath = entry.RelativeOwnedPath is null ? null : ValidateOwnedPath(entry.RelativeOwnedPath);
        return SerializeWithNewline(new
        {
            timestamp = entry.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            operationId = entry.OperationId.ToString("N"),
            level = entry.Level.ToString(),
            eventCode,
            message,
            releaseTag,
            relativeOwnedPath,
            stableErrorCode
        });
    }

    private byte[] SerializeTruncationMarker(DateTimeOffset timestamp)
    {
        return SerializeWithNewline(new
        {
            timestamp = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            operationId = this.OperationId.ToString("N"),
            level = InstallerLogLevel.Warning.ToString(),
            eventCode = "log.truncated",
            message = "Further entries were omitted (bounded log)."
        });
    }

    private static byte[] SerializeWithNewline<T>(T value)
    {
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        byte[] record = new byte[line.Length + 1];
        line.CopyTo(record, 0);
        record[^1] = (byte)'\n';
        return record;
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
        if (options.MaximumRawMessageCharacters < options.MaximumMessageCharacters || options.MaximumRawMessageCharacters > 65536)
            throw new ArgumentOutOfRangeException(nameof(options), "The raw message bound must contain the stored-message bound and be at most 65536 characters.");
        if (options.MaximumSensitiveValueCount is < 2 or > 256)
            throw new ArgumentOutOfRangeException(nameof(options), "The sensitive-value count bound must be between 2 and 256.");
        if (options.MaximumSensitiveValueCharacters is < 64 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(options), "The per-sensitive-value bound must be between 64 and 16384 characters.");
        if (options.MaximumSensitiveValueTotalCharacters < options.MaximumSensitiveValueCharacters || options.MaximumSensitiveValueTotalCharacters > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "The aggregate sensitive-value bound is invalid.");
        if (options.MaximumDirectoryEntries < options.MaximumFileCount + 1 || options.MaximumDirectoryEntries > 10000)
            throw new ArgumentOutOfRangeException(nameof(options), "The bounded directory-entry limit is invalid.");
        if (options.MaximumEntryCount is < 1 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(options), "The bounded log-entry count is invalid.");
        if (options.TerminalReserveBytes is < 128 || options.TerminalReserveBytes > options.MaximumFileBytes / 2)
            throw new ArgumentOutOfRangeException(nameof(options), "The terminal record reserve is invalid.");

        long aggregateBytes = options.MaximumAggregateBytes == 0
            ? checked((long)options.MaximumFileBytes * options.MaximumFileCount)
            : options.MaximumAggregateBytes;
        if (aggregateBytes < options.MaximumFileBytes || aggregateBytes > 320L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "The aggregate log-byte bound is invalid.");
        return options with
        {
            StateRoot = System.IO.Path.GetFullPath(options.StateRoot),
            MaximumAggregateBytes = aggregateBytes
        };
    }

    private static string[] GetSensitiveValues(InstallerLogOptions options, IEnumerable<string>? suppliedValues)
    {
        List<string> values = new();
        int observed = 0;
        long totalCharacters = 0;
        IEnumerable<string> candidates = suppliedValues ?? Array.Empty<string>();
        foreach (string value in candidates.Concat(new[]
        {
            options.StateRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        }))
        {
            observed++;
            if (observed > options.MaximumSensitiveValueCount)
                throw new ArgumentException("The sensitive-value collection exceeds its bounded count.", nameof(suppliedValues));
            if (value is null)
                continue;
            if (value.Length > options.MaximumSensitiveValueCharacters)
                throw new ArgumentException("A sensitive value exceeds its bounded character count.", nameof(suppliedValues));
            if (string.IsNullOrWhiteSpace(value) || values.Contains(value, StringComparer.Ordinal))
                continue;
            totalCharacters = checked(totalCharacters + value.Length);
            if (totalCharacters > options.MaximumSensitiveValueTotalCharacters)
                throw new ArgumentException("The sensitive values exceed their bounded aggregate character count.", nameof(suppliedValues));
            values.Add(value);
        }

        return values.OrderByDescending(value => value.Length).ToArray();
    }

    private static void Rotate(LinuxAnchoredFileSystem fileSystem, InstallerLogOptions options, uint effectiveUserId)
    {
        List<(string Name, LinuxFileIdentity Identity)> files = new();
        foreach (string name in fileSystem.EnumerateEntryNames(maximumEntries: options.MaximumDirectoryEntries))
        {
            if (!OwnedLogFilename.IsMatch(name))
                continue;
            LinuxFileIdentity identity = fileSystem.Stat(name)
                ?? throw new IOException("An installer-owned log disappeared during rotation.");
            AssertPrivate(identity, LinuxAnchoredEntryKind.RegularFile, PrivateFileMode, effectiveUserId, "installer log");
            if (identity.Size < 0 || identity.Size > options.MaximumFileBytes)
                throw new IOException("An installer-owned log exceeds its configured per-file byte bound.");
            files.Add((name, identity));
        }

        (string Name, LinuxFileIdentity Identity)[] newestFirst = files
            .OrderByDescending(entry => entry.Identity.ModificationSeconds)
            .ThenByDescending(entry => entry.Identity.ModificationNanoseconds)
            .ThenByDescending(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        long retainedBytes = newestFirst.Sum(entry => entry.Identity.Size);
        int retainedCount = newestFirst.Length;
        long priorByteLimit = options.MaximumAggregateBytes - options.MaximumFileBytes;
        foreach ((string name, LinuxFileIdentity identity) in Enumerable.Reverse(newestFirst))
        {
            if (retainedCount <= options.MaximumFileCount - 1 && retainedBytes <= priorByteLimit)
                break;
            fileSystem.UnlinkFile(name, identity);
            retainedCount--;
            retainedBytes -= identity.Size;
        }
        if (retainedCount > options.MaximumFileCount - 1 || retainedBytes > priorByteLimit)
            throw new IOException("The retained installer logs exceed their aggregate bounds.");
    }

    private static void EnsurePrivateStateMarker(LinuxAnchoredFileSystem fileSystem, bool stateCreated, uint effectiveUserId)
    {
        const string markerPath = "state-version";
        if (stateCreated)
        {
            byte[] contents = Encoding.UTF8.GetBytes(StateMarkerContents);
            using LinuxAnchoredFile marker = fileSystem.CreateNewFile(markerPath, PrivateFileMode);
            AssertPrivate(marker.Identity, LinuxAnchoredEntryKind.RegularFile, PrivateFileMode, effectiveUserId, "installer state marker");
            fileSystem.AppendAndFsync(marker, markerPath, contents, 0, contents.Length);
            return;
        }

        LinuxFileIdentity existingIdentity = fileSystem.Stat(markerPath)
            ?? throw new IOException("An existing installer state root has no ownership marker.");
        AssertPrivate(existingIdentity, LinuxAnchoredEntryKind.RegularFile, PrivateFileMode, effectiveUserId, "installer state marker");
        using LinuxAnchoredFile existingMarker = fileSystem.OpenRegularFileForRead(markerPath);
        if (!existingMarker.Identity.IsSameObject(existingIdentity))
            throw new IOException("The installer state marker identity changed while it was opened.");
        AssertPrivate(existingMarker.Identity, LinuxAnchoredEntryKind.RegularFile, PrivateFileMode, effectiveUserId, "installer state marker");
        byte[] existingContents = fileSystem.ReadAllBytesExact(existingMarker, 128);
        if (!existingContents.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(StateMarkerContents)))
            throw new IOException("An unknown existing installer state root requires manual inspection.");
    }

    private static void AssertPrivate(
        LinuxFileIdentity identity,
        LinuxAnchoredEntryKind expectedKind,
        int expectedMode,
        uint effectiveUserId,
        string description
    )
    {
        if (
            identity.Kind != expectedKind
            || identity.OwnerUserId != effectiveUserId
            || identity.UnixMode != expectedMode
            || identity.SpecialModeBits != 0
            || (expectedKind == LinuxAnchoredEntryKind.RegularFile && identity.LinkCount != 1)
        )
            throw new IOException($"The {description} isn't an exact private current-user object.");
    }

    private static string EscapeUnsafeUnicode(string value)
    {
        StringBuilder? escaped = null;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                char low = value[index + 1];
                UnicodeCategory pairCategory = Rune.GetUnicodeCategory(new Rune(char.ConvertToUtf32(character, low)));
                if (pairCategory is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                {
                    escaped ??= new StringBuilder(value.Length + 16).Append(value, 0, index);
                    escaped.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    escaped.Append("\\u").Append(((int)low).ToString("X4", CultureInfo.InvariantCulture));
                }
                else
                    escaped?.Append(character).Append(low);
                index++;
                continue;
            }

            bool malformedSurrogate = char.IsSurrogate(character);
            UnicodeCategory singleCategory = malformedSurrogate ? UnicodeCategory.Surrogate : char.GetUnicodeCategory(character);
            if (
                malformedSurrogate
                || singleCategory is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            )
            {
                escaped ??= new StringBuilder(value.Length + 16).Append(value, 0, index);
                escaped.Append(malformedSurrogate ? "\\uFFFD" : $"\\u{(int)character:X4}");
            }
            else
                escaped?.Append(character);
        }
        return escaped?.ToString() ?? value;
    }

    private static string Truncate(string value, int maximumCharacters)
    {
        const string suffix = "…[truncated]";
        int prefixLength = maximumCharacters - suffix.Length;
        if (prefixLength > 0 && prefixLength < value.Length && char.IsHighSurrogate(value[prefixLength - 1]))
            prefixLength--;
        return value[..Math.Max(0, prefixLength)] + suffix;
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

    [DllImport("libc", SetLastError = false)]
    private static extern uint geteuid();
}
