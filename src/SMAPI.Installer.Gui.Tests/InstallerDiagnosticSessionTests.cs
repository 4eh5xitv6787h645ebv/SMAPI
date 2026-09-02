using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Privacy;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;
using StardewModdingAPI.Installer.Gui.Diagnostics;

namespace StardewModdingAPI.Installer.Gui.Tests;

[TestFixture]
internal sealed class InstallerDiagnosticSessionTests
{
    private string? TemporaryDirectory;

    [TearDown]
    public void TearDown()
    {
        if (this.TemporaryDirectory is not null && Directory.Exists(this.TemporaryDirectory))
            Directory.Delete(this.TemporaryDirectory, recursive: true);
        this.TemporaryDirectory = null;
    }

    [Test]
    public async Task ConstructorAndDispose_WriteOnlyFixedTypedRecords()
    {
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId);
        string path = log.Path;
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        InstallerDiagnosticSession session = new(log, operationId, () => now);
        await using (session)
        {
            session.Record(InstallerDiagnosticCode.ReleaseCatalogLoading);
            session.Record(InstallerDiagnosticCode.ReleaseFailed, ProtocolPrePlanErrorCode.PackageRejected, "fork-linux-v1-alpha.2");
            session.MarkCompleted();
        }

        string[] lines = File.ReadAllLines(path);
        lines.Should().HaveCount(4);
        lines[0].Should().Contain("session.started");
        string middle = string.Join('\n', lines[1], lines[2]);
        middle.Should().Contain("release.failed");
        middle.Should().Contain("protocol.preplan.package-rejected");
        middle.Should().Contain("fork-linux-v1-alpha.2");
        middle.Should().Contain("release.catalog.loading");
        lines[3].Should().Contain("session.completed");
        lines.Should().OnlyContain(line => !line.Contains(operationId.ToString(), StringComparison.OrdinalIgnoreCase) || line.Contains(operationId.ToString("N"), StringComparison.Ordinal));
        session.CreateSanitizedCopyText().Should().NotContain("fork-linux-v1-alpha.2", "release identifiers are excluded from the viewer projection");
    }

    [Test]
    public async Task ProgressFlood_IsNonblockingBoundedAndTerminalHasPriority()
    {
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId, maximumFileBytes: 64 * 1024);
        await using InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch);

        Action flood = () =>
        {
            for (int index = 0; index < 1_000_000; index++)
                session.RecordProgress(InstallerDiagnosticCode.ExecutionProgress);
        };
        flood.ExecutionTime().Should().BeLessThan(TimeSpan.FromSeconds(5));
        session.Record(InstallerDiagnosticCode.ExecutionTerminal, ProtocolTerminalErrorCode.IoFailure);
        await session.DisposeAsync();

        session.Entries.Count.Should().BeLessThanOrEqualTo(InstallerDiagnosticSession.MaximumDisplayEntries);
        session.Entries.Should().Contain(entry => entry.EventCode == "execution.terminal" && entry.StableErrorCode == "protocol.terminal.io-failure");
        new FileInfo(log.Path).Length.Should().BeLessThanOrEqualTo(64 * 1024);
    }

    [Test]
    public async Task SingleLaneStateFlood_UsesOneBoundedWriterWakeAndSettles()
    {
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId, maximumFileBytes: 64 * 1024);
        InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch);

        Action flood = () =>
        {
            for (int index = 0; index < 1_000_000; index++)
                session.Record(InstallerDiagnosticCode.ReleaseCatalogLoading);
        };
        flood.ExecutionTime().Should().BeLessThan(TimeSpan.FromSeconds(5));
        session.MarkCompleted();
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        session.Entries.Count.Should().BeLessThanOrEqualTo(InstallerDiagnosticSession.MaximumDisplayEntries);
        session.CoalescedEventCount.Should().BeGreaterThan(0);
        File.ReadLines(log.Path).Last().Should().Contain("session.completed");
    }

    [Test]
    public async Task ConcurrentRecordAndDispose_NeverLeaksAncillaryWakeFailures()
    {
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId, maximumFileBytes: 64 * 1024);
        InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch);
        ConcurrentQueue<Exception> failures = new();

        Task producer = Task.Run(() =>
        {
            try
            {
                for (int index = 0; index < 250_000; index++)
                    session.Record(InstallerDiagnosticCode.ReleaseCatalogLoading);
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
        });
        Task disposer = Task.Run(async () => await session.DisposeAsync());

        await Task.WhenAll(producer, disposer).WaitAsync(TimeSpan.FromSeconds(5));

        failures.Should().BeEmpty();
    }

    [Test]
    public async Task EnsureReadyForMutation_WritesSynchronouslyBeforeAdmission()
    {
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId);
        string path = log.Path;
        await using InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch);

        session.EnsureReadyForMutation();

        File.ReadAllText(path).Should().Contain("diagnostics.mutation-ready");
        session.IsAvailable.Should().BeTrue();
    }

    [Test]
    public async Task EnsureReadyForMutation_LogLeafReplacement_FailsClosedWithoutTouchingReplacement()
    {
        Assume.That(OperatingSystem.IsLinux(), Is.True);
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId);
        string path = log.Path;
        await using InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch);
        string captured = path + ".captured";
        File.Move(path, captured);
        string outside = Path.Combine(this.TemporaryDirectory!, "outside");
        File.WriteAllText(outside, "preserve");
        File.CreateSymbolicLink(path, outside);

        Action action = session.EnsureReadyForMutation;

        action.Should().Throw<InstallerDiagnosticsUnavailableException>()
            .WithMessage("Private installer diagnostics are unavailable; no operation was started.");
        session.IsAvailable.Should().BeFalse();
        File.ReadAllText(outside).Should().Be("preserve");
    }

    [Test]
    public async Task EnsureReadyForMutation_FullBoundedLogFailsClosed()
    {
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId, maximumFileBytes: 2048, maximumEntryCount: 1);
        await using InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch);

        Action action = session.EnsureReadyForMutation;

        action.Should().Throw<InstallerDiagnosticsUnavailableException>();
        session.IsAvailable.Should().BeFalse();
        File.ReadAllText(log.Path).Should().Contain("log.truncated");
    }

    [Test]
    public async Task UndefinedProtocolError_IsRejectedBeforeItCanReachTheWriter()
    {
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId);
        await using InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch);

        Action action = () => session.Record(InstallerDiagnosticCode.ExecutionTerminal, (ProtocolTerminalErrorCode)999);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ClassifiedPackageFailuresUseStableCodesAndExcludePrivateAuthorityFromShareableViews()
    {
        const string privateReleaseAuthority = "PRIVATE-/home/alex/secret-package.zip-https://token.example";
        (ProtocolPrePlanErrorCode Code, string StableCode)[] cases =
        [
            (ProtocolPrePlanErrorCode.PackageIntegrityRejected, "protocol.preplan.package-integrity-rejected"),
            (ProtocolPrePlanErrorCode.PackageMetadataRejected, "protocol.preplan.package-metadata-rejected"),
            (ProtocolPrePlanErrorCode.PackageArchiveRejected, "protocol.preplan.package-archive-rejected"),
            (ProtocolPrePlanErrorCode.PackageProvenanceRejected, "protocol.preplan.package-provenance-rejected"),
            (ProtocolPrePlanErrorCode.PackageReleaseIdentityRejected, "protocol.preplan.package-release-identity-rejected")
        ];
        foreach ((ProtocolPrePlanErrorCode code, string stableCode) in cases)
        {
            Guid operationId = Guid.NewGuid();
            InstallerLog log = this.CreateLog(operationId);
            await using InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch);

            session.Record(InstallerDiagnosticCode.ReleaseFailed, code);
            session.MarkCompleted();
            await session.DisposeAsync();

            session.Entries.Select(entry => entry.StableErrorCode).Should().Contain(stableCode);
            session.Entries.Should().OnlyContain(entry => !entry.Message.Contains(privateReleaseAuthority, StringComparison.Ordinal));
            string shareable = session.CreateSanitizedCopyText();
            shareable.Should().Contain(stableCode)
                .And.NotContain(privateReleaseAuthority)
                .And.NotContain("/home/alex")
                .And.NotContain("token.example");
        }
    }

    [Test]
    [TestCase(InstallerDiagnosticCode.ReleaseNetworkUnavailable, "release.network.unavailable")]
    [TestCase(InstallerDiagnosticCode.ReleaseNetworkTimedOut, "release.network.timeout")]
    [TestCase(InstallerDiagnosticCode.ReleaseDownloadInterrupted, "release.download.interrupted")]
    public async Task ReleaseNetworkFailuresUseStageTruthfulStableCodes(
        InstallerDiagnosticCode diagnosticCode,
        string stableCode
    )
    {
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId);
        await using InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch);

        session.Record(diagnosticCode);
        session.MarkCompleted();
        await session.DisposeAsync();

        session.Entries.Select(entry => entry.EventCode).Should().Contain(stableCode);
        session.CreateSanitizedCopyText().Should().Contain(stableCode);
    }

    [Test]
    public async Task UndefinedPrePlanError_IsRejectedBeforeItCanReachTheWriter()
    {
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId);
        await using InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch);

        Action action = () => session.Record(InstallerDiagnosticCode.ReleaseFailed, (ProtocolPrePlanErrorCode)999);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SnapshotAndSanitizedCopy_AreBoundedAndExcludeStorageAuthority()
    {
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId);
        string privatePath = log.Path;
        await using InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch);

        for (int index = 0; index < 400; index++)
            session.EnsureReadyForMutation();

        InstallerDiagnosticSnapshot snapshot = session.Snapshot;
        string copy = session.CreateSanitizedCopyText();

        snapshot.Health.Should().Be(InstallerDiagnosticHealth.Truncated);
        snapshot.Entries.Should().HaveCount(InstallerDiagnosticSession.MaximumDisplayEntries);
        snapshot.OmittedEntryCount.Should().BeGreaterThan(0);
        Encoding.UTF8.GetByteCount(copy).Should().BeLessThanOrEqualTo(InstallerDiagnosticSession.MaximumSanitizedCopyBytes);
        copy.Should().Contain("Review this text before sharing it.");
        copy.Should().Contain("Bounded omissions:");
        copy.Should().NotContain(privatePath).And.NotContain(operationId.ToString("N"));
        copy.Split('\n').Count(line => line.Contains("diagnostics.mutation-ready", StringComparison.Ordinal))
            .Should().BeLessThanOrEqualTo(InstallerDiagnosticSession.MaximumSanitizedCopyEntries);
    }

    [Test]
    public async Task DisposeWithoutNormalCompletion_WritesFixedUnexpectedSessionTerminal()
    {
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId);
        string path = log.Path;
        InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch);
        await session.DisposeAsync();

        using JsonDocument terminal = JsonDocument.Parse(File.ReadLines(path).Last());
        terminal.RootElement.GetProperty("eventCode").GetString().Should().Be("session.ended-unexpectedly");
    }

    [Test]
    public async Task NormalSessionTerminal_SurvivesOrdinaryLogTruncation()
    {
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId, maximumFileBytes: 2048, maximumEntryCount: 1);
        string path = log.Path;
        await using (InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch))
        {
            session.Record(InstallerDiagnosticCode.ReleaseCatalogLoading);
            session.Record(InstallerDiagnosticCode.ReleaseCatalogReady);
            session.MarkCompleted();
        }

        string[] lines = File.ReadAllLines(path);
        lines.Count(line => line.Contains("log.truncated", StringComparison.Ordinal)).Should().Be(1);
        using JsonDocument terminal = JsonDocument.Parse(lines[^1]);
        terminal.RootElement.GetProperty("eventCode").GetString().Should().Be("session.completed");
    }

    [Test]
    public async Task TypedProgress_PreservesDistinctReviewedStagesWithoutPrivateData()
    {
        Guid operationId = Guid.NewGuid();
        InstallerLog log = this.CreateLog(operationId);
        string path = log.Path;
        await using (InstallerDiagnosticSession session = new(log, operationId, () => DateTimeOffset.UnixEpoch))
        {
            IProductionInstallerDiagnosticSink sink = session;
            sink.RecordProgress(InstallerDiagnosticCode.ExecutionProgress, TransactionStage.Staging);
            sink.RecordProgress(InstallerDiagnosticCode.ExecutionProgress, TransactionStage.Applying);
            session.MarkCompleted();
        }

        string contents = File.ReadAllText(path);
        contents.Should().Contain("staging reviewed changes");
        contents.Should().Contain("applying reviewed changes");
    }

    private InstallerLog CreateLog(Guid operationId, int maximumFileBytes = 1024 * 1024, int maximumEntryCount = 2048)
    {
        this.TemporaryDirectory ??= Path.Combine(Path.GetTempPath(), $"smapi-gui-diagnostics-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TemporaryDirectory);
        string state = Path.Combine(this.TemporaryDirectory, "state");
        return new InstallerLog(
            new(state, MaximumFileBytes: maximumFileBytes, MaximumEntryCount: maximumEntryCount),
            operationId,
            DateTimeOffset.UnixEpoch
        );
    }
}
