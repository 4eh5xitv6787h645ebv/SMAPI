using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace StardewModdingAPI.Framework.Health;

/// <summary>The paths and immutable payload summary for a completely published report pair.</summary>
internal sealed record ModHealthPublishedReport(string TextPath, string JsonPath, ModHealthCompletionSummary? Summary = null);

/// <summary>Publishes a matching report pair.</summary>
internal interface IModHealthReportPublisher
{
    /// <summary>Publish a pair, with its completion marker committed last.</summary>
    ModHealthPublishedReport Publish(ModHealthExportRequest request, ModHealthReportPayload payload, CancellationToken cancellationToken);
}

/// <summary>Filesystem operations needed for secure report publication.</summary>
internal interface IModHealthReportFileSystem : IDisposable
{
    string RelativeDirectory { get; }
    void WritePrivateFile(string name, ReadOnlySpan<byte> contents);
    bool TryPublishNoReplace(string temporaryName, string finalName);
    void SyncDirectory();
    bool Exists(string name);
    DateTimeOffset GetLastWriteTimeUtc(string name);
    IEnumerable<string> EnumerateNames();
    void Delete(string name);
    IDisposable? TryAcquireMaintenanceLock();
}

/// <summary>Atomically publishes bounded report pairs into a private Linux directory.</summary>
internal sealed class ModHealthReportPublisher : IModHealthReportPublisher, IDisposable
{
    private const int MaximumRetainedPairs = 5;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan StaleIncompleteAge = TimeSpan.FromMinutes(10);
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Regex ArtifactPattern = new(
        @"^(?<stem>SMAPI-health-\d{8}-\d{6}-report-[0-9a-f]{16}(?:-\d+)?)\.(?<extension>txt|json|complete)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );
    private static readonly Regex TemporaryPattern = new(
        @"^\.SMAPI-health-\d{8}-\d{6}-report-[0-9a-f]{16}(?:-\d+)?\.(?:txt|json|complete)\.tmp-[0-9a-f]{32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private readonly IModHealthReportFileSystem FileSystem;
    private readonly Func<DateTimeOffset> GetUtcNow;

    public ModHealthReportPublisher(IModHealthReportFileSystem fileSystem, Func<DateTimeOffset>? getUtcNow = null)
    {
        this.FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        this.GetUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public ModHealthPublishedReport Publish(ModHealthExportRequest request, ModHealthReportPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payload);

        byte[] text = ModHealthReportPublisher.Utf8.GetBytes(payload.Text);
        byte[] json = ModHealthReportPublisher.Utf8.GetBytes(payload.Json);
        string timestamp = request.RequestedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string reportId = "report-" + request.RequestId.ToString("N", CultureInfo.InvariantCulture)[..16];
        if (!string.Equals(payload.Model.Header.ReportId, reportId, StringComparison.Ordinal))
            throw new InvalidOperationException("The report payload ID does not match its frozen export request.");
        string root = $"SMAPI-health-{timestamp}-{reportId}";

        for (int collision = 0; collision < 1000; collision++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string stem = collision == 0 ? root : $"{root}-{collision + 1}";
            string textName = $"{stem}.txt";
            string jsonName = $"{stem}.json";
            string markerName = $"{stem}.complete";
            if (this.FileSystem.Exists(textName) || this.FileSystem.Exists(jsonName) || this.FileSystem.Exists(markerName))
                continue;

            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            string textTemporary = $".{textName}.tmp-{token}";
            string jsonTemporary = $".{jsonName}.tmp-{token}";
            string markerTemporary = $".{markerName}.tmp-{token}";
            bool textPublished = false;
            bool jsonPublished = false;
            bool markerPublished = false;
            bool pairCommitted = false;
            try
            {
                this.FileSystem.WritePrivateFile(textTemporary, text);
                cancellationToken.ThrowIfCancellationRequested();
                this.FileSystem.WritePrivateFile(jsonTemporary, json);
                cancellationToken.ThrowIfCancellationRequested();
                this.FileSystem.WritePrivateFile(markerTemporary, ModHealthReportPublisher.Utf8.GetBytes($"{textName}\n{jsonName}\n"));
                cancellationToken.ThrowIfCancellationRequested();

                if (!this.FileSystem.TryPublishNoReplace(textTemporary, textName))
                    continue;
                textPublished = true;
                cancellationToken.ThrowIfCancellationRequested();
                if (!this.FileSystem.TryPublishNoReplace(jsonTemporary, jsonName))
                    continue;
                jsonPublished = true;
                cancellationToken.ThrowIfCancellationRequested();
                this.FileSystem.SyncDirectory();
                if (!this.FileSystem.TryPublishNoReplace(markerTemporary, markerName))
                    continue;
                markerPublished = true;
                this.FileSystem.SyncDirectory();
                pairCommitted = true;

                try
                {
                    this.TryMaintainReports(stem);
                }
                catch (IOException)
                {
                    // Maintenance is best-effort once a complete report pair has been committed.
                }
                catch (UnauthorizedAccessException)
                {
                    // Maintenance is best-effort once a complete report pair has been committed.
                }
                string prefix = this.FileSystem.RelativeDirectory.TrimEnd('/', '\\');
                return new(
                    $"{prefix}/{textName}",
                    $"{prefix}/{jsonName}",
                    ModHealthCompletionSummary.FromReport(payload.Model)
                );
            }
            finally
            {
                this.DeleteIfPresent(textTemporary);
                this.DeleteIfPresent(jsonTemporary);
                this.DeleteIfPresent(markerTemporary);
                if (!pairCommitted)
                {
                    if (markerPublished)
                        this.DeleteIfPresent(markerName);
                    if (textPublished)
                        this.DeleteIfPresent(textName);
                    if (jsonPublished)
                        this.DeleteIfPresent(jsonName);
                }
            }
        }

        throw new IOException("Could not allocate a unique mod health report filename.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.FileSystem.Dispose();
    }

    private void TryMaintainReports(string newlyPublishedStem)
    {
        using IDisposable? maintenanceLock = this.FileSystem.TryAcquireMaintenanceLock();
        if (maintenanceLock is null)
            return;

        DateTimeOffset now = this.GetUtcNow();
        string[] names = this.FileSystem.EnumerateNames().ToArray();
        HashSet<string> nameSet = names.ToHashSet(StringComparer.Ordinal);

        foreach (string name in names.Where(name => ModHealthReportPublisher.TemporaryPattern.IsMatch(name)))
        {
            if (this.FileSystem.GetLastWriteTimeUtc(name) <= now - ModHealthReportPublisher.StaleIncompleteAge)
                this.DeleteIfPresent(name);
        }

        var artifacts = names
            .Select(name => (Name: name, Match: ModHealthReportPublisher.ArtifactPattern.Match(name)))
            .Where(entry => entry.Match.Success)
            .GroupBy(entry => entry.Match.Groups["stem"].Value, StringComparer.Ordinal)
            .ToArray();

        foreach (var group in artifacts)
        {
            string marker = $"{group.Key}.complete";
            bool isComplete = nameSet.Contains(marker)
                && nameSet.Contains($"{group.Key}.txt")
                && nameSet.Contains($"{group.Key}.json");
            if (isComplete)
                continue;

            DateTimeOffset newest = group.Max(entry => this.FileSystem.GetLastWriteTimeUtc(entry.Name));
            if (newest <= now - ModHealthReportPublisher.StaleIncompleteAge)
            {
                foreach (var entry in group)
                    this.DeleteIfPresent(entry.Name);
            }
        }

        var complete = artifacts
            .Where(group =>
                nameSet.Contains($"{group.Key}.complete")
                && nameSet.Contains($"{group.Key}.txt")
                && nameSet.Contains($"{group.Key}.json")
            )
            .Select(group => new
            {
                Stem = group.Key,
                Timestamp = this.FileSystem.GetLastWriteTimeUtc($"{group.Key}.complete")
            })
            .OrderByDescending(pair => pair.Timestamp)
            .ThenByDescending(pair => pair.Stem, StringComparer.Ordinal)
            .ToArray();

        foreach (var pair in complete.Where((pair, index) => pair.Timestamp < now - ModHealthReportPublisher.MaximumAge || index >= ModHealthReportPublisher.MaximumRetainedPairs))
        {
            if (pair.Stem == newlyPublishedStem && complete.Length <= ModHealthReportPublisher.MaximumRetainedPairs)
                continue;
            this.DeleteIfPresent($"{pair.Stem}.complete");
            this.DeleteIfPresent($"{pair.Stem}.txt");
            this.DeleteIfPresent($"{pair.Stem}.json");
        }
    }

    private void DeleteIfPresent(string name)
    {
        try
        {
            this.FileSystem.Delete(name);
        }
        catch (FileNotFoundException)
        {
        }
    }
}
