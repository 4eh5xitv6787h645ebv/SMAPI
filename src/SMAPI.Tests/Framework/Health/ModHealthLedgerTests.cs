using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Health;

namespace SMAPI.Tests.Framework.Health;

/// <summary>Unit tests for <see cref="ModHealthLedger"/>.</summary>
[TestFixture]
internal sealed class ModHealthLedgerTests
{
    [Test]
    public void RegisterMod_InvalidManifestUsesGeneratedIdentityAndDropsUnsafeFields()
    {
        ModHealthLedger ledger = new(startedUtc: new DateTime(2026, 8, 26, 1, 2, 3, DateTimeKind.Utc), timestampFrequency: 1000, getTimestamp: () => 0);

        ledger.RegisterMod(new ModHealthModObservation(
            HasValidManifest: false,
            UniqueId: "/home/alice/private/mod",
            DisplayName: "secret-folder",
            Version: "secret-version",
            Kind: ModHealthLedgerModKind.CodeMod,
            ParentId: "secret-parent",
            DependencyIds: ["secret-dependency"],
            Status: ModHealthLedgerModStatus.Invalid,
            FailureReason: ModHealthModFailureReason.InvalidManifest,
            WarningFlags: 7,
            UpdateStatus: ModHealthUpdateStatus.Unknown,
            SuggestedUpdateVersion: null
        ));

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();
        snapshot.StartedUtc.Should().Be(new DateTime(2026, 8, 26, 1, 2, 3, DateTimeKind.Utc));
        snapshot.Completeness.Should().Be(ModHealthLedgerCompleteness.ManagedCoreInitialization);
        ModHealthModSnapshot mod = snapshot.Mods.Should().ContainSingle().Subject;
        mod.UniqueId.Should().Be("invalid-mod-0001");
        mod.DisplayName.Should().Be("Invalid mod #1");
        mod.Version.Should().BeNull();
        mod.Kind.Should().Be(ModHealthLedgerModKind.Unknown);
        mod.ParentId.Should().BeNull();
        mod.DependencyIds.Should().BeEmpty();
        mod.UsesGeneratedInvalidIdentity.Should().BeTrue();
        snapshot.ToString().Should().NotContain("alice").And.NotContain("secret");
    }

    [Test]
    public void RegisterMod_PrioritizesProblemRecordsAndOrdersDeterministically()
    {
        ModHealthLedger ledger = new(modCapacity: 3, timestampFrequency: 1000, getTimestamp: () => 0);
        ledger.RegisterMod(CreateMod("z.mod", ModHealthLedgerModStatus.Discovered));
        ledger.RegisterMod(CreateMod("ignored.mod", ModHealthLedgerModStatus.Ignored));
        ledger.RegisterMod(CreateMod("b.failed", ModHealthLedgerModStatus.Failed));
        ledger.RegisterMod(CreateMod("a.loaded", ModHealthLedgerModStatus.Loaded));

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();

        snapshot.TotalDiscoveredMods.Should().Be(4);
        snapshot.Mods.Select(mod => mod.UniqueId).Should().Equal("a.loaded", "b.failed", "ignored.mod");
        snapshot.Omissions.ModInventoryRecords.Should().Be(1);
        snapshot.ModStatusTotals[ModHealthLedgerModStatus.Discovered].Should().Be(1);
        snapshot.ModStatusTotals[ModHealthLedgerModStatus.Loaded].Should().Be(1);
        snapshot.ModStatusTotals[ModHealthLedgerModStatus.Failed].Should().Be(1);
        snapshot.ModStatusTotals[ModHealthLedgerModStatus.Ignored].Should().Be(1);
    }

    [Test]
    public void UpdateMod_ReconsidersPreviouslyOmittedProblemRecordAndUpdatesTotals()
    {
        ModHealthLedger ledger = new(modCapacity: 1, timestampFrequency: 1000, getTimestamp: () => 0);
        ledger.RegisterMod(CreateMod("ignored.mod", ModHealthLedgerModStatus.Ignored));
        ModHealthModKey key = ledger.RegisterMod(CreateMod("later.mod", ModHealthLedgerModStatus.Discovered));

        ledger.UpdateMod(key, ModHealthLedgerModStatus.Discovered, CreateMod("later.mod", ModHealthLedgerModStatus.Failed));

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();
        snapshot.Mods.Should().ContainSingle().Which.UniqueId.Should().Be("later.mod");
        snapshot.ModStatusTotals[ModHealthLedgerModStatus.Discovered].Should().Be(0);
        snapshot.ModStatusTotals[ModHealthLedgerModStatus.Failed].Should().Be(1);
        snapshot.ModStatusTotals[ModHealthLedgerModStatus.Ignored].Should().Be(1);
        snapshot.Omissions.ModInventoryRecords.Should().Be(1);
    }

    [Test]
    public void RegisterMod_CopiesAndCapsDependencies()
    {
        List<string> dependencies = ["z.mod", "a.mod", "extra.mod"];
        ModHealthLedger ledger = new(dependencyCapacity: 2, timestampFrequency: 1000, getTimestamp: () => 0);
        ledger.RegisterMod(CreateMod("example.mod", ModHealthLedgerModStatus.Loaded) with { DependencyIds = dependencies });
        dependencies[0] = "changed.after.registration";

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();

        snapshot.Mods.Single().DependencyIds.Should().Equal("a.mod", "z.mod");
        snapshot.Omissions.DependencyIds.Should().Be(1);
    }

    [Test]
    public void UpdateMod_RetainsOnlySafeImmutableUpdateState()
    {
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: () => 0);
        ModHealthModObservation initial = CreateMod("example.mod", ModHealthLedgerModStatus.Loaded);
        ModHealthModKey key = ledger.RegisterMod(initial);

        ledger.UpdateMod(key, ModHealthLedgerModStatus.Loaded, initial with
        {
            UpdateStatus = ModHealthUpdateStatus.UpdateAvailable,
            SuggestedUpdateVersion = "2.0.0\r\n"
        });

        ModHealthModSnapshot mod = ledger.GetSnapshot().Mods.Single();
        mod.UpdateStatus.Should().Be(ModHealthUpdateStatus.UpdateAvailable);
        mod.SuggestedUpdateVersion.Should().Be("2.0.0");
    }

    [Test]
    public void ObserveLog_CountsEverySeverityAndCaptureDeltaWithoutText()
    {
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: () => 0);
        foreach (LogLevel level in Enum.GetValues<LogLevel>())
            ledger.ObserveLog(CreateLog("Example.Mod", level, messageLength: (int)level + 10));
        ModHealthLedgerBaseline baseline = ledger.CreateCaptureBaseline();

        ledger.ObserveLog(CreateLog("example.mod", LogLevel.Info, 21));
        ledger.ObserveLog(CreateLog("example.mod", LogLevel.Error, 34));

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot(baseline);
        ModHealthLogSourceSnapshot source = snapshot.LogSources.Should().ContainSingle().Subject;
        foreach (LogLevel level in Enum.GetValues<LogLevel>())
            source.SinceLedgerStart.GetMessages(level).Should().Be(level is LogLevel.Info or LogLevel.Error ? 2 : 1);
        source.DuringCapture.GetMessages(LogLevel.Info).Should().Be(1);
        source.DuringCapture.GetCharacters(LogLevel.Info).Should().Be(21);
        source.DuringCapture.GetMessages(LogLevel.Error).Should().Be(1);
        source.DuringCapture.GetCharacters(LogLevel.Error).Should().Be(34);
        source.DuringCapture.GetMessages(LogLevel.Trace).Should().Be(0);
        snapshot.CaptureBaselineSequence.Should().Be(baseline.Sequence);
    }

    [Test]
    public void RegisteredLogCounter_TracksBoundedOneSecondPeakRates()
    {
        long timestamp = 0;
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: () => timestamp);
        IModHealthLogCounter counter = ledger.RegisterLogSource("example.mod", "Example Mod", ModHealthLogSourceCategory.Mod);

        counter.Record(LogLevel.Info, 10, 1, ModHealthLogObservationCategory.Normal);
        timestamp = 999;
        counter.Record(LogLevel.Info, 20, 1, ModHealthLogObservationCategory.Normal);
        timestamp = 1000;
        counter.Record(LogLevel.Info, 50, 1, ModHealthLogObservationCategory.Normal);

        ModHealthLogSourceSnapshot source = ledger.GetSnapshot().LogSources.Single();
        source.PeakMessagesPerSecond.Should().Be(2);
        source.PeakCharactersPerSecond.Should().Be(50);
    }

    [Test]
    public void RegisteredLogCounter_TracksConcurrentPeakWithoutTextOrAllocatingBuckets()
    {
        const int count = 20_000;
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: static () => 500);
        IModHealthLogCounter counter = ledger.RegisterLogSource("example.mod", "Example Mod", ModHealthLogSourceCategory.Mod);

        Parallel.For(0, count, _ => counter.Record(LogLevel.Trace, 3, Environment.CurrentManagedThreadId, ModHealthLogObservationCategory.Normal));

        ModHealthLogSourceSnapshot source = ledger.GetSnapshot().LogSources.Single();
        source.PeakMessagesPerSecond.Should().Be(count);
        source.PeakCharactersPerSecond.Should().Be(count * 3);
    }

    [Test]
    public void ObserveLog_ClassifiesCoreAndGameAndExcludesReporterMessages()
    {
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: () => 0);
        long smapiSequence = ledger.ObserveLog(new ModHealthLogObservation("wrong", "wrong", ModHealthLogSourceCategory.Smapi, LogLevel.Warn, 5, 1));
        long gameSequence = ledger.ObserveLog(new ModHealthLogObservation("wrong", "wrong", ModHealthLogSourceCategory.Game, LogLevel.Error, 7, 2));
        long reporterSequence = ledger.ObserveLog(new ModHealthLogObservation("SMAPI", "SMAPI", ModHealthLogSourceCategory.Reporter, LogLevel.Error, 999, 3));

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();
        snapshot.LogSources.Should().HaveCount(2);
        snapshot.LogSources.Should().Contain(source => source.SourceCategory == ModHealthLogSourceCategory.Smapi && source.ModId == "SMAPI");
        snapshot.LogSources.Should().Contain(source => source.SourceCategory == ModHealthLogSourceCategory.Game && source.ModId == "game");
        reporterSequence.Should().Be(gameSequence);
        snapshot.CutoffSequence.Should().Be(gameSequence).And.BeGreaterThan(smapiSequence);
        snapshot.LogTotalsSinceLedgerStart.GetMessages(LogLevel.Error).Should().Be(1);
    }

    [Test]
    public void ObserveLog_PreservesGlobalTotalsWhenIdentityCapacityIsReached()
    {
        ModHealthLedger ledger = new(logIdentityCapacity: 1, timestampFrequency: 1000, getTimestamp: () => 0);
        ledger.ObserveLog(CreateLog("first.mod", LogLevel.Info, 10));
        ledger.ObserveLog(CreateLog("second.mod", LogLevel.Warn, 20));

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();

        snapshot.LogSources.Should().ContainSingle().Which.ModId.Should().Be("first.mod");
        snapshot.LogTotalsSinceLedgerStart.GetMessages(LogLevel.Info).Should().Be(1);
        snapshot.LogTotalsSinceLedgerStart.GetMessages(LogLevel.Warn).Should().Be(1);
        snapshot.Omissions.LogIdentityObservations.Should().Be(1);
    }

    [Test]
    public void ObserveCallbackFailure_RemainsSeparateFromErrorsAndSupportsCaptureDelta()
    {
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: () => 0);
        ledger.ObserveLog(CreateLog("example.mod", LogLevel.Error, 100));
        ledger.ObserveCallbackFailure(CreateFailure("example.mod", "Example.Mod.OnTick"));
        ModHealthLedgerBaseline baseline = ledger.CreateCaptureBaseline();
        ledger.ObserveCallbackFailure(CreateFailure("EXAMPLE.MOD", "Example.Mod.OnTick"));

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot(baseline);

        snapshot.LogTotalsSinceLedgerStart.GetMessages(LogLevel.Error).Should().Be(1);
        snapshot.CallbackFailuresSinceLedgerStart.Should().Be(2);
        snapshot.CallbackFailuresDuringCapture.Should().Be(1);
        ModHealthCallbackFailureSnapshot failure = snapshot.CallbackFailures.Should().ContainSingle().Subject;
        failure.SinceLedgerStartCount.Should().Be(2);
        failure.DuringCaptureCount.Should().Be(1);
        failure.ExceptionType.Should().Be("System.InvalidOperationException");
    }

    [Test]
    public void ObserveCallbackFailure_PreservesTotalWhenSignatureCapacityIsReached()
    {
        ModHealthLedger ledger = new(failureCapacity: 1, timestampFrequency: 1000, getTimestamp: () => 0);
        ledger.ObserveCallbackFailure(CreateFailure("first.mod", "First.Callback"));
        ledger.ObserveCallbackFailure(CreateFailure("second.mod", "Second.Callback"));

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();
        snapshot.CallbackFailures.Should().ContainSingle();
        snapshot.CallbackFailuresSinceLedgerStart.Should().Be(2);
        snapshot.Omissions.CallbackFailureObservations.Should().Be(1);
    }

    [Test]
    public void Observations_HaveMonotonicSequenceAndOffsets()
    {
        long timestamp = 100;
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: () => timestamp);
        timestamp = 150;
        long first = ledger.ObserveLog(CreateLog("example.mod", LogLevel.Info, 1));
        timestamp = 350;
        long second = ledger.ObserveLog(CreateLog("example.mod", LogLevel.Info, 1));

        ModHealthLogSourceSnapshot source = ledger.GetSnapshot().LogSources.Single();
        first.Should().Be(1);
        second.Should().Be(2);
        source.FirstSequence.Should().Be(first);
        source.LastSequence.Should().Be(second);
        source.FirstOffset.Should().Be(TimeSpan.FromMilliseconds(50));
        source.LastOffset.Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Test]
    public void IdentityFields_AreCappedAndStructurallySanitized()
    {
        string longName = new('x', 400);
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: () => 0);
        ledger.RegisterMod(CreateMod("C:\\Users\\Alice\\secret", ModHealthLedgerModStatus.Loaded) with
        {
            DisplayName = "Unsafe\r\n\t\u001bName",
            Version = longName,
            ParentId = "/home/alice/private"
        });
        ledger.ObserveCallbackFailure(new ModHealthCallbackFailureObservation(
            "example.mod",
            "Example Mod",
            ModHealthExecutionPhase.Update,
            ModHealthOperationKind.Event,
            new string('c', 1200),
            "Bad\nException",
            "/home/alice/pack",
            4
        ));

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();
        ModHealthModSnapshot mod = snapshot.Mods.Single();
        mod.UniqueId.Should().Be("<redacted-path>");
        mod.DisplayName.Should().NotContain("\r").And.NotContain("\n").And.NotContain("\t").And.NotContain("\u001b");
        mod.Version.Should().HaveLength(256);
        mod.ParentId.Should().Be("<redacted-path>");
        ModHealthCallbackFailureSnapshot failure = snapshot.CallbackFailures.Single();
        failure.CallbackIdentity.Should().HaveLength(1024);
        failure.ExceptionType.Should().NotContain("\n");
        failure.OnBehalfOfModId.Should().Be("<redacted-path>");
    }

    [Test]
    public void ObserveLog_IsThreadSafeUnderConcurrentProducers()
    {
        const int count = 20_000;
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: () => 0);

        Parallel.For(0, count, _ => ledger.ObserveLog(CreateLog("example.mod", LogLevel.Trace, 3)));

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();
        snapshot.LogTotalsSinceLedgerStart.GetMessages(LogLevel.Trace).Should().Be(count);
        snapshot.LogTotalsSinceLedgerStart.GetCharacters(LogLevel.Trace).Should().Be(count * 3);
        snapshot.LogSources.Should().ContainSingle().Which.SinceLedgerStart.GetMessages(LogLevel.Trace).Should().Be(count);
        snapshot.CutoffSequence.Should().Be(count);
    }

    [Test]
    public void RegisteredLogCounter_IsAllocationFreeAfterWarmup()
    {
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: static () => 0);
        IModHealthLogCounter counter = ledger.RegisterLogSource("example.mod", "Example Mod", ModHealthLogSourceCategory.Mod);
        for (int i = 0; i < 100; i++)
            counter.Record(LogLevel.Trace, 3, 1, ModHealthLogObservationCategory.Normal);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            counter.Record(LogLevel.Info, 7, 1, ModHealthLogObservationCategory.Normal);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
        ledger.GetSnapshot().LogTotalsSinceLedgerStart.GetMessages(LogLevel.Info).Should().Be(10_000);
    }

    [Test]
    public void RegisteredLogCounter_ConcurrentlyCountsTraceAndInfoWithExactSnapshotCutoff()
    {
        const int count = 20_000;
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: static () => 0);
        IModHealthLogCounter counter = ledger.RegisterLogSource("example.mod", "Example Mod", ModHealthLogSourceCategory.Mod);

        Parallel.For(0, count, index => counter.Record(index % 2 == 0 ? LogLevel.Trace : LogLevel.Info, 3, Environment.CurrentManagedThreadId, ModHealthLogObservationCategory.Normal));

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();
        snapshot.CutoffSequence.Should().Be(count);
        snapshot.LogTotalsSinceLedgerStart.GetMessages(LogLevel.Trace).Should().Be(count / 2);
        snapshot.LogTotalsSinceLedgerStart.GetMessages(LogLevel.Info).Should().Be(count / 2);
        Enum.GetValues<LogLevel>().Sum(level => snapshot.LogSources.Single().SinceLedgerStart.GetMessages(level)).Should().Be(count);
    }

    [Test]
    public async Task ReporterSuppression_FlowsAcrossAsyncAndDoesNotAdvanceCutoff()
    {
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: static () => 0);
        IModHealthLogCounter counter = ledger.RegisterLogSource("SMAPI", "SMAPI", ModHealthLogSourceCategory.Smapi);
        counter.Record(LogLevel.Info, 5, 1, ModHealthLogObservationCategory.Normal);

        using (ledger.SuppressReporterLogs())
        {
            await Task.Yield();
            ModHealthReporterLogScope.IsActive.Should().BeTrue();
            counter.Record(LogLevel.Error, 99, 1, ModHealthLogObservationCategory.Reporter);
        }

        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();
        snapshot.CutoffSequence.Should().Be(1);
        Enum.GetValues<LogLevel>().Sum(level => snapshot.LogTotalsSinceLedgerStart.GetMessages(level)).Should().Be(1);
        ModHealthReporterLogScope.IsActive.Should().BeFalse();
    }

    [Test]
    public void Snapshot_DoesNotExposeMutableLedgerCollections()
    {
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: () => 0);
        ledger.RegisterMod(CreateMod("example.mod", ModHealthLedgerModStatus.Loaded) with { DependencyIds = ["dependency.mod"] });
        ModHealthLedgerSnapshot snapshot = ledger.GetSnapshot();

        snapshot.Mods.Should().BeAssignableTo<IReadOnlyList<ModHealthModSnapshot>>();
        snapshot.Mods.Should().NotBeAssignableTo<ModHealthModSnapshot[]>();
        snapshot.Mods.Single().DependencyIds.Should().NotBeAssignableTo<string[]>();
        snapshot.ModStatusTotals.Should().NotBeAssignableTo<Dictionary<ModHealthLedgerModStatus, long>>();
    }

    private static ModHealthModObservation CreateMod(string id, ModHealthLedgerModStatus status)
    {
        return new ModHealthModObservation(
            HasValidManifest: true,
            UniqueId: id,
            DisplayName: id + " name",
            Version: "1.0.0",
            Kind: ModHealthLedgerModKind.CodeMod,
            ParentId: null,
            DependencyIds: [],
            Status: status,
            FailureReason: status is ModHealthLedgerModStatus.Failed or ModHealthLedgerModStatus.Invalid
                ? ModHealthModFailureReason.LoadFailed
                : ModHealthModFailureReason.None,
            WarningFlags: 0,
            UpdateStatus: ModHealthUpdateStatus.Unknown,
            SuggestedUpdateVersion: null
        );
    }

    private static ModHealthLogObservation CreateLog(string id, LogLevel level, int messageLength)
    {
        return new ModHealthLogObservation(id, id + " name", ModHealthLogSourceCategory.Mod, level, messageLength, Thread.CurrentThread.ManagedThreadId);
    }

    private static ModHealthCallbackFailureObservation CreateFailure(string id, string callback)
    {
        return new ModHealthCallbackFailureObservation(
            id,
            id + " name",
            ModHealthExecutionPhase.Update,
            ModHealthOperationKind.Event,
            callback,
            "System.InvalidOperationException",
            null,
            Thread.CurrentThread.ManagedThreadId
        );
    }
}
