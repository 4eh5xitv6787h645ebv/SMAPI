using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        DateTimeOffset now = new(2026, 8, 26, 13, 0, 0, TimeSpan.Zero);
        fileSystem.Timestamps[$"{stem}.txt"] = now;
        ModHealthReportPublisher publisher = new(fileSystem, () => now);

        ModHealthPublishedReport result = publisher.Publish(request, CreatePayload(), CancellationToken.None);

        Encoding.UTF8.GetString(fileSystem.Files[$"{stem}.txt"]).Should().Be("existing");
        result.TextPath.Should().EndWith("-2.txt");
        fileSystem.PublicationOrder.TakeLast(3).Select(name => Path.GetExtension(name)).Should().Equal(".txt", ".json", ".complete");
    }

    [Test]
    public async Task Publish_ConcurrentLinuxHandlesWithSameFrozenRequestUseDistinctCollisionSafePairs()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Linux-only publisher test.");
        using TestDirectory directory = new();
        string output = Path.Combine(directory.Path, "HealthReports");
        ModHealthExportRequest request = CreateRequest();
        ModHealthReportPayload payload = CreatePayload();
        using Barrier start = new(participantCount: 2);

        Task<ModHealthPublishedReport> first = Task.Run(() => PublishFromIndependentHandle());
        Task<ModHealthPublishedReport> second = Task.Run(() => PublishFromIndependentHandle());
        ModHealthPublishedReport[] published = await Task.WhenAll(first, second);

        published.Select(result => result.TextPath).Should().OnlyHaveUniqueItems();
        published.Select(result => result.JsonPath).Should().OnlyHaveUniqueItems();
        Directory.GetFiles(output, "*.complete").Should().HaveCount(2);
        foreach (ModHealthPublishedReport result in published)
        {
            string markerPath = Path.Combine(output, Path.ChangeExtension(Path.GetFileName(result.TextPath), ".complete"));
            File.ReadAllLines(markerPath).Should().Equal(Path.GetFileName(result.TextPath), Path.GetFileName(result.JsonPath));
        }

        ModHealthPublishedReport PublishFromIndependentHandle()
        {
            using LinuxModHealthReportFileSystem fileSystem = new(output);
            start.SignalAndWait(TimeSpan.FromSeconds(30)).Should().BeTrue();
            return new ModHealthReportPublisher(fileSystem).Publish(request, payload, CancellationToken.None);
        }
    }

    [Test]
    [NonParallelizable]
    public void Publish_PermissiveUmaskStillCreatesOwnerOnlyDirectoryAndFiles()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Linux-only publisher test.");
        using TestDirectory directory = new();
        string output = Path.Combine(directory.Path, "HealthReports");
        uint previousUmask = ModHealthReportPublisherTests.umask(0);
        try
        {
            using LinuxModHealthReportFileSystem fileSystem = new(output);
            new ModHealthReportPublisher(fileSystem).Publish(CreateRequest(), CreatePayload(), CancellationToken.None);
        }
        finally
        {
            ModHealthReportPublisherTests.umask(previousUmask);
        }

        File.GetUnixFileMode(output).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (string path in Directory.GetFiles(output))
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(3, false)]
    [TestCase(1, true)]
    public void Publish_WriteOrPermissionFailureLeavesNoVisiblePairOrTemporaryFile(int failWriteCall, bool permissionFailure)
    {
        using InMemoryFileSystem fileSystem = new()
        {
            FailWriteCall = failWriteCall,
            WriteFailure = permissionFailure ? new UnauthorizedAccessException("injected permission failure") : new IOException("injected write failure")
        };

        Action publish = () => new ModHealthReportPublisher(fileSystem).Publish(CreateRequest(), CreatePayload(), CancellationToken.None);
        if (permissionFailure)
            publish.Should().Throw<UnauthorizedAccessException>();
        else
            publish.Should().Throw<IOException>();

        AssertNoGeneratedArtifacts(fileSystem);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void Publish_PayloadOrMarkerPublicationFailureLeavesNoVisiblePairOrTemporaryFile(int failPublicationCall)
    {
        using InMemoryFileSystem fileSystem = new() { FailPublicationCall = failPublicationCall };

        FluentActions.Invoking(() => new ModHealthReportPublisher(fileSystem).Publish(CreateRequest(), CreatePayload(), CancellationToken.None))
            .Should().Throw<IOException>();

        AssertNoGeneratedArtifacts(fileSystem);
    }

    [TestCase(1)]
    [TestCase(2)]
    public void Publish_DirectorySyncFailureLeavesNoVisiblePairOrTemporaryFile(int failDirectorySyncCall)
    {
        using InMemoryFileSystem fileSystem = new() { FailDirectorySyncCall = failDirectorySyncCall };

        FluentActions.Invoking(() => new ModHealthReportPublisher(fileSystem).Publish(CreateRequest(), CreatePayload(), CancellationToken.None))
            .Should().Throw<IOException>();

        AssertNoGeneratedArtifacts(fileSystem);
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
    public void Publish_RemovesCompletePairsOlderThanThirtyDaysOnlyAfterNewPairSucceeds()
    {
        DateTimeOffset now = new(2026, 8, 26, 13, 0, 0, TimeSpan.Zero);
        using InMemoryFileSystem fileSystem = new();
        AddCompletePair(fileSystem, "SMAPI-health-20260725-120000-report-aaaaaaaaaaaaaaaa", now.AddDays(-31));
        AddCompletePair(fileSystem, "SMAPI-health-20260728-120000-report-bbbbbbbbbbbbbbbb", now.AddDays(-29));
        ModHealthReportPublisher publisher = new(fileSystem, () => now);

        publisher.Publish(CreateRequest(), CreatePayload(), CancellationToken.None);

        fileSystem.Files.Keys.Should().NotContain(name => name.Contains("report-aaaaaaaaaaaaaaaa", StringComparison.Ordinal));
        fileSystem.Files.Keys.Should().Contain(name => name.Contains("report-bbbbbbbbbbbbbbbb", StringComparison.Ordinal));
        fileSystem.Files.Keys.Count(name => name.EndsWith(".complete", StringComparison.Ordinal)).Should().Be(2);
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
    public void Publish_CleansStaleTemporaryFilesButPreservesFreshTemporaryFiles()
    {
        DateTimeOffset now = new(2026, 8, 26, 13, 0, 0, TimeSpan.Zero);
        using InMemoryFileSystem fileSystem = new();
        string stale = ".SMAPI-health-20260826-120000-report-1111111111111111.txt.tmp-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string fresh = ".SMAPI-health-20260826-120001-report-2222222222222222.json.tmp-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        fileSystem.Files[stale] = [1];
        fileSystem.Files[fresh] = [2];
        fileSystem.Timestamps[stale] = now.AddMinutes(-11);
        fileSystem.Timestamps[fresh] = now.AddMinutes(-9);

        new ModHealthReportPublisher(fileSystem, () => now).Publish(CreateRequest(), CreatePayload(), CancellationToken.None);

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

    private static void AddCompletePair(InMemoryFileSystem fileSystem, string stem, DateTimeOffset timestamp)
    {
        foreach (string extension in new[] { ".txt", ".json", ".complete" })
        {
            string name = stem + extension;
            fileSystem.Files[name] = [1];
            fileSystem.Timestamps[name] = timestamp;
        }
    }

    private static void AssertNoGeneratedArtifacts(InMemoryFileSystem fileSystem)
    {
        fileSystem.Files.Keys.Should().NotContain(name =>
            name.EndsWith(".txt", StringComparison.Ordinal)
            || name.EndsWith(".json", StringComparison.Ordinal)
            || name.EndsWith(".complete", StringComparison.Ordinal)
            || name.Contains(".tmp-", StringComparison.Ordinal)
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
        public int? FailWriteCall { get; init; }
        public Exception? WriteFailure { get; init; }
        public int? FailPublicationCall { get; init; }
        public int? FailDirectorySyncCall { get; init; }
        public bool MaintenanceLockAvailable { get; init; } = true;
        private int WriteCalls;
        private int PublicationCalls;
        private int DirectorySyncCalls;

        public void WritePrivateFile(string name, ReadOnlySpan<byte> contents)
        {
            if (++this.WriteCalls == this.FailWriteCall)
                throw this.WriteFailure ?? new IOException("injected write failure");
            this.Files.Add(name, contents.ToArray());
            this.Timestamps[name] = new DateTimeOffset(2026, 8, 26, 13, 0, 0, TimeSpan.Zero);
        }

        public bool TryPublishNoReplace(string temporaryName, string finalName)
        {
            if (++this.PublicationCalls == this.FailPublicationCall)
                throw new IOException("injected publication failure");
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

    [DllImport("libc", SetLastError = true)]
    private static extern uint umask(uint mask);
}
