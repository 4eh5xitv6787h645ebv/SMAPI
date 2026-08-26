using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;

namespace SMAPI.Tests.Framework.Health;

[TestFixture]
internal sealed class ModHealthReportPublisherTests
{
    [Test]
    public void Publish_WritesPrivateMatchingPairAndMarkerLast()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Linux-only publisher test.");
        using TestDirectory directory = new();
        string output = Path.Combine(directory.Path, "HealthReports");
        using LinuxModHealthReportFileSystem fileSystem = new(output);
        ModHealthReportPublisher publisher = new(fileSystem);

        ModHealthPublishedReport result = publisher.Publish(CreateRequest(), CreatePayload(), CancellationToken.None);

        string[] names = Directory.GetFiles(output).Select(Path.GetFileName).Where(name => !name!.StartsWith('.')).Order().ToArray()!;
        names.Should().HaveCount(3);
        result.TextPath.Should().Contain(CreatePayload().Model.Header.ReportId);
        result.Summary.Should().BeEquivalentTo(ModHealthCompletionSummary.FromReport(CreatePayload().Model));
        names.Should().Contain(Path.GetFileNameWithoutExtension(result.TextPath) + ".complete");
        File.ReadAllText(Path.Combine(output, Path.GetFileName(result.TextPath))).Should().Be(CreatePayload().Text);
        File.ReadAllText(Path.Combine(output, Path.GetFileName(result.JsonPath))).Should().Be(CreatePayload().Json);
        File.GetUnixFileMode(output).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (string path in Directory.GetFiles(output))
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public void Publish_CollisionPreservesExistingArtifactsAndUsesSuffix()
    {
        using InMemoryFileSystem fileSystem = new();
        ModHealthExportRequest request = CreateRequest();
        string stem = $"SMAPI-health-20260826-123456-{CreatePayload().Model.Header.ReportId}";
        fileSystem.Files[$"{stem}.txt"] = Encoding.UTF8.GetBytes("existing");
        ModHealthReportPublisher publisher = new(fileSystem);

        ModHealthPublishedReport result = publisher.Publish(request, CreatePayload(), CancellationToken.None);

        Encoding.UTF8.GetString(fileSystem.Files[$"{stem}.txt"]).Should().Be("existing");
        result.TextPath.Should().EndWith("-2.txt");
        fileSystem.PublicationOrder.TakeLast(3).Select(name => Path.GetExtension(name)).Should().Equal(".txt", ".json", ".complete");
    }

    [Test]
    public void Publish_MarkerFailureRemovesPartialPairAndTemps()
    {
        using InMemoryFileSystem fileSystem = new() { FailMarkerPublication = true };
        ModHealthReportPublisher publisher = new(fileSystem);

        FluentActions.Invoking(() => publisher.Publish(CreateRequest(), CreatePayload(), CancellationToken.None))
            .Should().Throw<IOException>();

        fileSystem.Files.Keys.Should().NotContain(name => name.EndsWith(".txt") || name.EndsWith(".json") || name.Contains(".tmp-"));
        fileSystem.Files.Keys.Should().NotContain(name => name.EndsWith(".complete"));
    }

    [Test]
    public void Publish_FinalDirectorySyncFailureRemovesMarkerPairAndTemps()
    {
        using InMemoryFileSystem fileSystem = new() { FailDirectorySyncCall = 2 };
        ModHealthReportPublisher publisher = new(fileSystem);

        FluentActions.Invoking(() => publisher.Publish(CreateRequest(), CreatePayload(), CancellationToken.None))
            .Should().Throw<IOException>();

        fileSystem.Files.Keys.Should().NotContain(name => name.EndsWith(".txt") || name.EndsWith(".json") || name.EndsWith(".complete") || name.Contains(".tmp-"));
    }

    [Test]
    public void Publish_RetainsOnlyFiveCompletePairsAndUnrelatedFiles()
    {
        using InMemoryFileSystem fileSystem = new();
        fileSystem.Files["keep-me.txt"] = [1];
        ModHealthReportPublisher publisher = new(fileSystem, () => new DateTimeOffset(2026, 8, 26, 13, 0, 0, TimeSpan.Zero));

        for (int i = 0; i < 7; i++)
        {
            ModHealthExportRequest request = CreateRequest(Guid.NewGuid());
            publisher.Publish(request, CreatePayload(request.RequestId), CancellationToken.None);
        }

        fileSystem.Files.Keys.Count(name => name.EndsWith(".complete")).Should().Be(5);
        fileSystem.Files.Should().ContainKey("keep-me.txt");
    }

    [Test]
    public void Publish_CleansStaleIncompleteButPreservesFreshIncomplete()
    {
        using InMemoryFileSystem fileSystem = new();
        string stale = "SMAPI-health-20260826-120000-report-1111111111111111.txt";
        string fresh = "SMAPI-health-20260826-120001-report-2222222222222222.json";
        fileSystem.Files[stale] = [1];
        fileSystem.Files[fresh] = [2];
        fileSystem.Timestamps[stale] = new DateTimeOffset(2026, 8, 26, 12, 40, 0, TimeSpan.Zero);
        fileSystem.Timestamps[fresh] = new DateTimeOffset(2026, 8, 26, 12, 55, 0, TimeSpan.Zero);
        ModHealthReportPublisher publisher = new(fileSystem, () => new DateTimeOffset(2026, 8, 26, 13, 0, 0, TimeSpan.Zero));

        publisher.Publish(CreateRequest(), CreatePayload(), CancellationToken.None);

        fileSystem.Files.Should().NotContainKey(stale).And.ContainKey(fresh);
    }

    [Test]
    public void Publish_SkipsMaintenanceWhenAnotherProcessOwnsLock()
    {
        using InMemoryFileSystem fileSystem = new() { MaintenanceLockAvailable = false };
        ModHealthReportPublisher publisher = new(fileSystem);

        for (int i = 0; i < 6; i++)
        {
            ModHealthExportRequest request = CreateRequest(Guid.NewGuid());
            publisher.Publish(request, CreatePayload(request.RequestId), CancellationToken.None);
        }

        fileSystem.Files.Keys.Count(name => name.EndsWith(".complete")).Should().Be(6);
    }

    [Test]
    public void Constructor_RejectsSymlinkOutputDirectory()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Linux-only publisher test.");
        using TestDirectory directory = new();
        string target = Path.Combine(directory.Path, "target");
        string link = Path.Combine(directory.Path, "HealthReports");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);

        FluentActions.Invoking(() => new LinuxModHealthReportFileSystem(link)).Should().Throw<IOException>();
    }

    [Test]
    public void Publish_RejectsSymlinkArtifactWithoutTouchingTarget()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Linux-only publisher test.");
        using TestDirectory directory = new();
        string output = Path.Combine(directory.Path, "HealthReports");
        using LinuxModHealthReportFileSystem fileSystem = new(output);
        ModHealthExportRequest request = CreateRequest();
        string target = Path.Combine(directory.Path, "outside.txt");
        File.WriteAllText(target, "unchanged");
        string stem = $"SMAPI-health-20260826-123456-{CreatePayload().Model.Header.ReportId}";
        File.CreateSymbolicLink(Path.Combine(output, $"{stem}.txt"), target);

        FluentActions.Invoking(() => new ModHealthReportPublisher(fileSystem).Publish(request, CreatePayload(), CancellationToken.None))
            .Should().Throw<IOException>();
        File.ReadAllText(target).Should().Be("unchanged");
        Directory.GetFiles(output).Should().ContainSingle();
    }

    [Test]
    public void MaintenanceLock_IsExclusiveAcrossDirectoryHandles()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Linux-only publisher test.");
        using TestDirectory directory = new();
        string output = Path.Combine(directory.Path, "HealthReports");
        using LinuxModHealthReportFileSystem first = new(output);
        using LinuxModHealthReportFileSystem second = new(output);

        using IDisposable? firstLock = first.TryAcquireMaintenanceLock();
        firstLock.Should().NotBeNull();
        second.TryAcquireMaintenanceLock().Should().BeNull();
    }

    private static ModHealthReportPayload CreatePayload(Guid? requestId = null)
    {
        Guid id = requestId ?? Guid.Parse("11111111-2222-3333-4444-555555555555");
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical();
        report = report with { Header = report.Header with { ReportId = "report-" + id.ToString("N")[..16] } };
        return new ModHealthReportPayloadFactory().Create(report);
    }

    private static ModHealthExportRequest CreateRequest(Guid? id = null)
    {
        return new(
            id ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
            new DateTimeOffset(2026, 8, 26, 12, 34, 56, TimeSpan.Zero),
            ModHealthCaptureOwner.Health,
            ModHealthCaptureOrigin.Manual,
            ModHealthCompletionReason.UserStop,
            null,
            new ModHealthLedger().GetSnapshot(),
            ImmutableArray<ModHealthMark>.Empty,
            33.333,
            IsFinal: true
        );
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smapi-health-publisher-tests", Guid.NewGuid().ToString("N"));

        public TestDirectory()
        {
            Directory.CreateDirectory(this.Path);
        }

        public void Dispose()
        {
            Directory.Delete(this.Path, recursive: true);
        }
    }

    private sealed class InMemoryFileSystem : IModHealthReportFileSystem
    {
        public string RelativeDirectory => "ErrorLogs/HealthReports";
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> Timestamps { get; } = new(StringComparer.Ordinal);
        public List<string> PublicationOrder { get; } = [];
        public bool FailMarkerPublication { get; init; }
        public int? FailDirectorySyncCall { get; init; }
        public bool MaintenanceLockAvailable { get; init; } = true;
        private int DirectorySyncCalls;

        public void WritePrivateFile(string name, ReadOnlySpan<byte> contents)
        {
            this.Files.Add(name, contents.ToArray());
            this.Timestamps[name] = new DateTimeOffset(2026, 8, 26, 13, 0, 0, TimeSpan.Zero);
        }

        public bool TryPublishNoReplace(string temporaryName, string finalName)
        {
            if (this.FailMarkerPublication && finalName.EndsWith(".complete", StringComparison.Ordinal))
                throw new IOException("injected marker failure");
            if (this.Files.ContainsKey(finalName))
                return false;
            this.Files[finalName] = this.Files[temporaryName];
            this.Timestamps[finalName] = this.Timestamps[temporaryName];
            this.Files.Remove(temporaryName);
            this.Timestamps.Remove(temporaryName);
            this.PublicationOrder.Add(finalName);
            return true;
        }

        public bool Exists(string name) => this.Files.ContainsKey(name);
        public void SyncDirectory()
        {
            if (++this.DirectorySyncCalls == this.FailDirectorySyncCall)
                throw new IOException("injected directory sync failure");
        }
        public DateTimeOffset GetLastWriteTimeUtc(string name) => this.Timestamps.GetValueOrDefault(name, new DateTimeOffset(2026, 8, 26, 13, 0, 0, TimeSpan.Zero));
        public IEnumerable<string> EnumerateNames() => this.Files.Keys.ToArray();

        public void Delete(string name)
        {
            if (!this.Files.Remove(name))
                throw new FileNotFoundException();
            this.Timestamps.Remove(name);
        }

        public IDisposable? TryAcquireMaintenanceLock() => this.MaintenanceLockAvailable ? new NoOpDisposable() : null;
        public void Dispose() { }

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
