using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using StardewModdingAPI.Framework.Performance;
using StardewModdingAPI.Toolkit.Framework.BundledModData;

namespace StardewModdingAPI.Framework.Health;

/// <summary>Safe, immutable environment values captured on the game thread for a report.</summary>
/// <remarks>This allowlisted model deliberately has no paths, user/machine identities, save data, configuration, or arbitrary environment values.</remarks>
internal sealed record ModHealthEnvironmentSnapshot(
    string SmapiVersion,
    string? SmapiCommit,
    string GameVersion,
    string RuntimeVersion,
    string ProcessArchitecture,
    int ProcessBitness,
    string? LinuxDistribution,
    string? Kernel,
    string SessionType,
    string Locale,
    int LogicalProcessorCount,
    string MultiplayerRole,
    int SplitScreenCount,
    bool StartupObserved,
    bool LifecycleTimingObserved
);

/// <summary>Maps frozen collector snapshots into the deterministic schema-v1 report contract.</summary>
internal sealed class ModHealthReportBuilder
{
    private const int NearestMarkTickDistance = 300;

    private static readonly ImmutableArray<string> IncludedIdentityFields = ImmutableArray.Create(
        "mod names",
        "mod IDs",
        "versions",
        "dependency IDs",
        "callback and exception type identities",
        "statuses"
    );

    private static readonly ImmutableArray<string> ExcludedSources = ImmutableArray.Create(
        "raw logs and stack traces",
        "absolute paths",
        "save, farm, and player data",
        "multiplayer identities and addresses",
        "command history and chat",
        "mod descriptions, authors, update keys, URLs, and configuration",
        "usernames, hostnames, machine IDs, and arbitrary environment values"
    );

    private static readonly ImmutableArray<string> Limitations = ImmutableArray.Create(
        "SMAPI observes elapsed wall-clock time only at named callback boundaries; correlation does not prove root cause.",
        "Game, SMAPI, Harmony, direct mod API, arbitrary background, native, filesystem, network, lock, GC, GPU, driver, presentation, and operating-system work can remain unattributed.",
        "Draw callback totals are separate from update ticks; complete draw, GPU, presentation, and FPS measurement is unsupported.",
        "A callback failure may also emit an error log entry, so failure and error counts must not be summed as unique incidents.",
        "The normal SMAPI log is still required for detailed exception messages and stack traces.",
        "The standalone health report is not currently parsed by smapi.io/log.",
        "No report is uploaded, transmitted, copied to the clipboard, or opened automatically."
    );

    private readonly ModHealthReportPayloadFactory PayloadFactory;

    /// <summary>Construct an instance.</summary>
    public ModHealthReportBuilder(ModHealthReportPayloadFactory? payloadFactory = null)
    {
        this.PayloadFactory = payloadFactory ?? new ModHealthReportPayloadFactory();
    }

    /// <summary>Build and format matching text/JSON payloads from one frozen request.</summary>
    public ModHealthReportPayload BuildPayload(ModHealthExportRequest request, ModHealthEnvironmentSnapshot environment, bool writeRetry = false)
    {
        return this.PayloadFactory.Create(this.Build(request, environment, writeRetry));
    }

    /// <summary>Build the immutable schema-v1 DTO from frozen source snapshots.</summary>
    public ModHealthReport Build(ModHealthExportRequest request, ModHealthEnvironmentSnapshot environment, bool writeRetry = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environment);

        ModPerformanceSnapshot? performance = request.Performance;
        ModHealthPerformanceSnapshot? health = performance?.Health;
        bool hasCapture = performance != null;
        DateTimeOffset? startedUtc = hasCapture ? ToUtc(performance!.StartedUtc) : null;
        double durationMilliseconds = hasCapture ? NonnegativeFinite(performance!.Elapsed.TotalMilliseconds) : 0;
        DateTimeOffset? endedUtc = startedUtc?.AddMilliseconds(durationMilliseconds);
        bool timingValid = hasCapture
            && performance!.TimingPartitionIsValid
            && health != null
            && health.Omissions.InvalidHistogramUpdates == 0;

        ImmutableArray<ModHealthMark> marks = request.Marks
            .Select(mark => mark with { OffsetMilliseconds = NonnegativeFinite(mark.OffsetMilliseconds) })
            .OrderBy(mark => mark.Number)
            .ThenBy(mark => mark.OffsetMilliseconds)
            .Take(ModHealthReportLimits.MaxMarks)
            .ToImmutableArray();

        ImmutableArray<ModHealthLogSummary> logs = BuildLogs(request.Ledger, hasCapture);
        ImmutableArray<ModHealthCallbackFailure> failures = BuildFailures(request.Ledger, hasCapture);
        ImmutableArray<ModHealthMod> mods = BuildMods(request.Ledger, logs, failures, health, hasCapture);
        ModHealthPerformance reportPerformance = BuildPerformance(performance, health, marks);

        long completedUpdates = Math.Max(0, performance?.CompletedTickCount ?? 0);
        bool isShort = hasCapture
            && (durationMilliseconds < TimeSpan.FromSeconds(ModHealthReportLimits.ShortSampleSeconds).TotalMilliseconds
                || completedUpdates < ModHealthReportLimits.ShortSampleUpdates);

        ImmutableArray<ModHealthCapacity> capacities = BuildCapacities(request, health);
        ImmutableArray<ModHealthOmission> omissions = BuildOmissions(request, health, mods.Length, logs.Length, failures.Length);
        bool anyOmission = omissions.Any(entry => entry.Count > 0);
        double slowUpdateThreshold = NonnegativeFinite(health?.SlowUpdateThresholdMilliseconds ?? request.SlowUpdateThresholdMilliseconds, ModHealthReportLimits.SlowUpdateMilliseconds);

        return new ModHealthReport(
            SchemaVersion: ModHealthReportLimits.SchemaVersion,
            Header: new ModHealthReportHeader(
                ReportId: CreateReportId(request.RequestId),
                GeneratedUtc: request.RequestedUtc.ToUniversalTime(),
                IsTruncated: anyOmission,
                IsMinimalFallback: false,
                WriteRetry: writeRetry
            ),
            Completeness: new ModHealthCompleteness(
                LedgerStartedUtc: ToUtc(request.Ledger.StartedUtc),
                Boundary: "Collection began during managed SMAPI core initialization; launcher, native, and earlier managed failures are unavailable.",
                StartupObserved: environment.StartupObserved || request.Ledger.Completeness == ModHealthLedgerCompleteness.ManagedCoreInitialization,
                LifecycleTimingObserved: environment.LifecycleTimingObserved || (health?.Callbacks.Any(IsLifecycleCallback) ?? false)
            ),
            Environment: BuildEnvironment(environment),
            Capture: new ModHealthCapture(
                Mode: GetCaptureMode(request.Owner),
                CompletionReason: request.CompletionReason,
                StartedUtc: startedUtc,
                EndedUtc: endedUtc,
                DurationMilliseconds: durationMilliseconds,
                CompletedUpdateCount: completedUpdates,
                SlowUpdateThresholdMilliseconds: slowUpdateThreshold,
                IsShortSample: isShort,
                TimingValid: timingValid,
                Marks: marks
            ),
            Findings: ImmutableArray<ModHealthFinding>.Empty,
            Performance: reportPerformance,
            ModInventory: BuildInventorySummary(request.Ledger, mods.Length),
            Mods: mods,
            LogTotals: new ModHealthLogTotals(BuildSeverity(request.Ledger.LogTotalsSinceLedgerStart), hasCapture ? BuildSeverity(request.Ledger.LogTotalsDuringCapture) : EmptySeverity()),
            Logs: logs,
            CallbackFailureTotals: new ModHealthCallbackFailureTotals(Math.Max(0, request.Ledger.CallbackFailuresSinceLedgerStart), hasCapture ? Math.Max(0, request.Ledger.CallbackFailuresDuringCapture) : 0),
            CallbackFailures: failures,
            Capacities: capacities,
            Omissions: omissions,
            Privacy: new ModHealthPrivacy(true, false, IncludedIdentityFields, ExcludedSources),
            Limitations: Limitations
        );
    }

    private static ModHealthEnvironment BuildEnvironment(ModHealthEnvironmentSnapshot source)
    {
        return new ModHealthEnvironment(
            SmapiVersion: Sanitize(source.SmapiVersion, "unknown"),
            SmapiCommit: SanitizeOptional(source.SmapiCommit),
            GameVersion: Sanitize(source.GameVersion, "unknown"),
            RuntimeVersion: Sanitize(source.RuntimeVersion, "unknown"),
            ProcessArchitecture: Sanitize(source.ProcessArchitecture, "unknown"),
            ProcessBitness: source.ProcessBitness is 32 or 64 ? source.ProcessBitness : 0,
            LinuxDistribution: NormalizeLinuxDistribution(source.LinuxDistribution),
            Kernel: NormalizeKernelRelease(source.Kernel),
            SessionType: NormalizeSessionType(source.SessionType),
            Locale: Sanitize(source.Locale, "unknown"),
            LogicalProcessorCount: Math.Max(1, source.LogicalProcessorCount),
            MultiplayerRole: NormalizeMultiplayerRole(source.MultiplayerRole),
            SplitScreenCount: Math.Max(0, source.SplitScreenCount)
        );
    }

    private static ImmutableArray<ModHealthLogSummary> BuildLogs(ModHealthLedgerSnapshot ledger, bool hasCapture)
    {
        return ledger.LogSources
            .Take(ModHealthReportLimits.MaxMods)
            .Select(source => new ModHealthLogSummary(
                Source: Sanitize(source.ModId, "unknown-source"),
                SourceCategory: source.SourceCategory switch
                {
                    ModHealthLogSourceCategory.Smapi => ModHealthReportLogSourceCategory.Smapi,
                    ModHealthLogSourceCategory.Game => ModHealthReportLogSourceCategory.Game,
                    _ => ModHealthReportLogSourceCategory.Mod
                },
                SinceLedgerStart: BuildSeverity(source.SinceLedgerStart),
                DuringCapture: hasCapture ? BuildSeverity(source.DuringCapture) : EmptySeverity(),
                PeakMessagesPerSecond: Math.Max(0, source.PeakMessagesPerSecond),
                PeakCharactersPerSecond: Math.Max(0, source.PeakCharactersPerSecond),
                FirstOffsetMilliseconds: NonnegativeFinite(source.FirstOffset.TotalMilliseconds),
                LastOffsetMilliseconds: NonnegativeFinite(source.LastOffset.TotalMilliseconds)
            ))
            .OrderBy(log => log.SourceCategory)
            .ThenBy(log => log.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(log => log.Source, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<ModHealthCallbackFailure> BuildFailures(ModHealthLedgerSnapshot ledger, bool hasCapture)
    {
        return ledger.CallbackFailures
            .Take(ModHealthReportLimits.MaxCallbackFailures)
            .Select(failure => new ModHealthCallbackFailure(
                ModId: Sanitize(failure.ModId, "unknown-mod"),
                ModName: Sanitize(failure.ModName, "unknown-mod"),
                Phase: failure.Phase,
                Operation: failure.Operation,
                Callback: Sanitize(failure.CallbackIdentity, "unknown-callback", ModHealthReportLimits.MaxCallbackNameLength),
                ExceptionType: Sanitize(failure.ExceptionType, "unknown-exception", ModHealthReportLimits.MaxCallbackNameLength),
                OnBehalfOfModId: SanitizeOptional(failure.OnBehalfOfModId),
                SessionCount: Math.Max(0, failure.SinceLedgerStartCount),
                CaptureCount: hasCapture ? Math.Max(0, failure.DuringCaptureCount) : 0,
                FirstOffsetMilliseconds: NonnegativeFinite(failure.FirstOffset.TotalMilliseconds),
                LastOffsetMilliseconds: NonnegativeFinite(failure.LastOffset.TotalMilliseconds)
            ))
            .OrderBy(failure => failure.ModId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(failure => failure.ModId, StringComparer.Ordinal)
            .ThenBy(failure => failure.Operation)
            .ThenBy(failure => failure.Callback, StringComparer.Ordinal)
            .ThenBy(failure => failure.ExceptionType, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<ModHealthMod> BuildMods(ModHealthLedgerSnapshot ledger, ImmutableArray<ModHealthLogSummary> logs, ImmutableArray<ModHealthCallbackFailure> failures, ModHealthPerformanceSnapshot? health, bool hasCapture)
    {
        Dictionary<string, ModHealthLogSummary> logsById = logs
            .Where(log => log.SourceCategory == ModHealthReportLogSourceCategory.Mod)
            .GroupBy(log => log.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, long> failuresById = failures
            .GroupBy(failure => failure.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => SaturatingSum(group.Select(failure => failure.SessionCount)), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ModAggregate> performanceById = (health?.Callbacks ?? Array.Empty<ModHealthCallbackPerformanceSnapshot>())
            .GroupBy(callback => callback.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new ModAggregate(
                    NonnegativeFinite(group.Sum(callback => NonnegativeFinite(callback.TotalMilliseconds))),
                    group.Max(callback => NonnegativeFinite(callback.MaximumMilliseconds)),
                    SaturatingSum(group.Select(callback => callback.CallCount)),
                    SaturatingSum(group.Select(callback => callback.FailureCount)),
                    CountSlowUpdateParticipation(health, group.Key)
                ),
                StringComparer.OrdinalIgnoreCase
            );
        double totalObservedMilliseconds = NonnegativeFinite(performanceById.Values.Sum(value => value.TotalMilliseconds));

        return ledger.Mods
            .Take(ModHealthReportLimits.MaxMods)
            .Select(source =>
            {
                logsById.TryGetValue(source.UniqueId, out ModHealthLogSummary? log);
                failuresById.TryGetValue(source.UniqueId, out long failureCount);
                performanceById.TryGetValue(source.UniqueId, out ModAggregate observed);
                long sessionWarnings = log == null ? 0 : SaturatingAdd(log.SinceLedgerStart.WarningMessages, log.SinceLedgerStart.AlertMessages);
                long sessionErrors = log?.SinceLedgerStart.ErrorMessages ?? 0;
                long captureErrors = hasCapture ? log?.DuringCapture.ErrorMessages ?? 0 : 0;
                return new ModHealthMod(
                    Id: Sanitize(source.UniqueId, "unknown-mod"),
                    Name: Sanitize(source.DisplayName, source.UniqueId),
                    Version: Sanitize(source.Version, "unknown"),
                    Kind: GetModKind(source),
                    ParentId: SanitizeOptional(source.ParentId),
                    Status: GetModStatus(source.Status),
                    FailureCategory: GetFailureCategory(source),
                    WarningFlags: GetWarningFlags(source.WarningFlags),
                    Dependencies: source.DependencyIds
                        .Take(ModHealthReportLimits.MaxDependenciesPerMod)
                        .Select(id => Sanitize(id, "unknown-dependency"))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(id => id, StringComparer.Ordinal)
                        .ToImmutableArray(),
                    UpdateStatus: GetUpdateStatus(source.UpdateStatus),
                    SuggestedUpdateVersion: source.UpdateStatus == ModHealthUpdateStatus.UpdateAvailable ? SanitizeOptional(source.SuggestedUpdateVersion) : null,
                    SessionWarningCount: sessionWarnings,
                    SessionErrorCount: sessionErrors,
                    CaptureErrorCount: captureErrors,
                    CallbackFailureCount: failureCount,
                    ObservedCallbackMilliseconds: observed.TotalMilliseconds,
                    ObservedCallbackPeakMilliseconds: observed.PeakMilliseconds,
                    ObservedCallbackCount: observed.CallCount,
                    ObservedCallbackFailureCount: observed.FailureCount,
                    SlowUpdateParticipationCount: observed.SlowUpdateParticipationCount,
                    InstrumentedTimeShare: totalObservedMilliseconds > 0 ? observed.TotalMilliseconds / totalObservedMilliseconds : 0,
                    PeakMessagesPerSecond: log?.PeakMessagesPerSecond ?? 0,
                    PeakCharactersPerSecond: log?.PeakCharactersPerSecond ?? 0
                );
            })
            .OrderBy(mod => GetModPriority(mod.Status))
            .ThenBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.Id, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ModHealthPerformance BuildPerformance(ModPerformanceSnapshot? performance, ModHealthPerformanceSnapshot? health, ImmutableArray<ModHealthMark> marks)
    {
        if (performance == null || health == null)
            return EmptyPerformance();

        ImmutableArray<ModHealthCallback> callbacks = health.Callbacks
            .OrderByDescending(callback => NonnegativeFinite(callback.TotalMilliseconds))
            .ThenByDescending(callback => NonnegativeFinite(callback.MaximumMilliseconds))
            .ThenBy(callback => callback.ModId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(callback => callback.EventName, StringComparer.Ordinal)
            .ThenBy(callback => callback.CallbackName, StringComparer.Ordinal)
            .Take(ModHealthReportLimits.MaxCallbacks)
            .Select(callback => new ModHealthCallback(
                ModId: Sanitize(callback.ModId, "unknown-mod"),
                ModName: Sanitize(callback.ModName, callback.ModId),
                Phase: callback.Phase,
                Operation: callback.Operation,
                Event: Sanitize(callback.EventName, "unknown-event", ModHealthReportLimits.MaxCallbackNameLength),
                Callback: Sanitize(callback.CallbackName, "unknown-callback", ModHealthReportLimits.MaxCallbackNameLength),
                OnBehalfOfModId: SanitizeOptional(callback.OnBehalfOfModId),
                CallCount: Math.Max(0, callback.CallCount),
                TotalMilliseconds: NonnegativeFinite(callback.TotalMilliseconds),
                MaximumMilliseconds: NonnegativeFinite(callback.MaximumMilliseconds),
                FailureCount: Math.Max(0, callback.FailureCount)
            ))
            .ToImmutableArray();

        ImmutableArray<ModHealthUpdate> worst = health.WorstUpdates
            .Take(ModHealthReportLimits.MaxWorstUpdates)
            .Select(update => BuildUpdate(update, marks))
            .ToImmutableArray();
        ImmutableArray<ModHealthUpdate> recent = health.RecentUpdates
            .TakeLast(ModHealthReportLimits.MaxRecentUpdates)
            .Select(update => BuildUpdate(update, marks))
            .ToImmutableArray();
        ImmutableArray<ModHealthEpisode> episodes = health.Episodes
            .Take(ModHealthReportLimits.MaxEpisodes)
            .Select(episode => new ModHealthEpisode(
                episode.FirstTick,
                episode.LastTick,
                Math.Max(0, episode.QualifyingUpdateCount),
                NonnegativeFinite(episode.MaximumMilliseconds),
                NonnegativeFinite(episode.SummedQualifyingMilliseconds),
                episode.RepresentativeTick,
                FindNearestMark(episode.RepresentativeTick, marks)
            ))
            .ToImmutableArray();

        ModHealthTimingHistogramSnapshot sourceHistogram = health.Histogram;
        ModHealthHistogram histogram = new(
            Count: Math.Max(0, sourceHistogram.Count),
            SumMilliseconds: NonnegativeFinite(sourceHistogram.SumMilliseconds),
            MinimumMilliseconds: NullableNonnegativeFinite(sourceHistogram.MinimumMilliseconds),
            MaximumMilliseconds: NullableNonnegativeFinite(sourceHistogram.MaximumMilliseconds),
            P50Milliseconds: NullableNonnegativeFinite(sourceHistogram.P50Milliseconds),
            P95Milliseconds: NullableNonnegativeFinite(sourceHistogram.P95Milliseconds),
            P99Milliseconds: NullableNonnegativeFinite(sourceHistogram.P99Milliseconds),
            PercentilesApproximate: sourceHistogram.Count > 0,
            MaximumRelativeBucketError: NonnegativeFinite(sourceHistogram.MaximumRelativeBucketError),
            UnderflowCount: Math.Max(0, sourceHistogram.UnderflowCount),
            OverflowCount: Math.Max(0, sourceHistogram.OverflowCount),
            Thresholds: sourceHistogram.Thresholds
                .OrderBy(entry => entry.Milliseconds)
                .Take(ModHealthReportLimits.MaxHistogramThresholds)
                .Select(entry => new ModHealthThresholdCount(NonnegativeFinite(entry.Milliseconds), Math.Max(0, entry.Count)))
                .ToImmutableArray()
        );

        bool validPartition = performance.TimingPartitionIsValid
            && IsValidPartition(performance.TickTotalMilliseconds, performance.GameUpdateMilliseconds, performance.TickInstrumentedMilliseconds, performance.InstrumentedDuringGameUpdateMilliseconds);
        double baseGame = validPartition ? NonnegativeFinite(performance.GameUpdateExclusiveMilliseconds) : 0;
        double residual = validPartition ? NonnegativeFinite(performance.OutsideGameUpdateMilliseconds) : 0;
        return new ModHealthPerformance(
            histogram,
            validPartition ? NonnegativeFinite(performance.TickInstrumentedMilliseconds) : 0,
            baseGame,
            0,
            residual,
            Math.Max(0, health.SlowUpdateCount),
            callbacks,
            worst,
            recent,
            episodes,
            performance.CaptureGcCollectionDataIsValid ? Math.Max(0, performance.CaptureGen0Collections) : 0,
            performance.CaptureGcCollectionDataIsValid ? Math.Max(0, performance.CaptureGen1Collections) : 0,
            performance.CaptureGcCollectionDataIsValid ? Math.Max(0, performance.CaptureGen2Collections) : 0,
            performance.CaptureGcCollectionDataIsValid
        );
    }

    private static ModHealthUpdate BuildUpdate(ModHealthUpdatePerformanceSnapshot source, ImmutableArray<ModHealthMark> marks)
    {
        bool valid = source.TimingPartitionIsValid && IsValidPartition(source.TotalMilliseconds, source.GameUpdateMilliseconds, source.InstrumentedModMilliseconds, source.InstrumentedDuringGameUpdateMilliseconds);
        double total = NonnegativeFinite(source.TotalMilliseconds);
        double baseGame = valid ? NonnegativeFinite(source.GameUpdateExclusiveMilliseconds) : 0;
        double observed = valid ? NonnegativeFinite(source.InstrumentedModMilliseconds) : 0;
        double residual = valid ? NonnegativeFinite(source.ResidualMilliseconds) : 0;
        return new ModHealthUpdate(
            source.Tick,
            NonnegativeFinite(source.OffsetMilliseconds),
            total,
            baseGame,
            observed,
            0,
            residual,
            valid,
            GetTickPhase(source.Context.Phase),
            source.Context.IsFocused,
            Math.Max(0, source.Context.ScreenId),
            Math.Max(0, source.WarningCount),
            Math.Max(0, source.ErrorCount),
            Math.Max(0, source.CallbackFailureCount),
            source.GcCollectionDataIsValid ? Math.Max(0, source.Gen0Collections) : 0,
            source.GcCollectionDataIsValid ? Math.Max(0, source.Gen1Collections) : 0,
            source.GcCollectionDataIsValid ? Math.Max(0, source.Gen2Collections) : 0,
            source.GcCollectionDataIsValid,
            source.Contributors
                .OrderByDescending(contributor => NonnegativeFinite(contributor.Milliseconds))
                .ThenBy(contributor => contributor.ModId, StringComparer.OrdinalIgnoreCase)
                .Take(ModHealthReportLimits.MaxContributorsPerUpdate)
                .Select(contributor => new ModHealthContributor(Sanitize(contributor.ModId, "unknown-mod"), NonnegativeFinite(contributor.Milliseconds)))
                .ToImmutableArray(),
            FindNearestMark(source.Tick, marks)
        );
    }

    private static ImmutableArray<ModHealthCapacity> BuildCapacities(ModHealthExportRequest request, ModHealthPerformanceSnapshot? health)
    {
        ModHealthLedgerSnapshot ledger = request.Ledger;
        List<ModHealthCapacity> result =
        [
            new("modInventory", ledger.Capacities.ModInventory, ledger.Omissions.ModInventoryRecords > 0),
            new("logIdentities", ledger.Capacities.LogIdentities, ledger.Omissions.LogIdentityObservations > 0),
            new("callbackFailureSignatures", ledger.Capacities.CallbackFailureSignatures, ledger.Omissions.CallbackFailureObservations > 0),
            new("dependenciesPerMod", ledger.Capacities.DependenciesPerMod, ledger.Omissions.DependencyIds > 0),
            new("marks", ModHealthReportLimits.MaxMarks, request.Marks.Length > ModHealthReportLimits.MaxMarks)
        ];
        if (health != null)
        {
            result.Add(new("callbackIdentities", health.Capacities.CallbackIdentities, health.Omissions.CallbackInvocations > 0));
            result.Add(new("recentUpdates", health.Capacities.RecentUpdates, health.Omissions.RecentUpdates > 0));
            result.Add(new("worstUpdates", health.Capacities.WorstUpdates, health.Omissions.WorstUpdates > 0));
            result.Add(new("slowEpisodes", health.Capacities.SlowEpisodes, health.Omissions.SlowEpisodes > 0));
            result.Add(new("contributorsPerSlowUpdate", health.Capacities.ContributorsPerSlowUpdate, health.Omissions.SlowUpdateContributorIdentities > 0));
            result.Add(new("contributorIdentitiesPerUpdate", health.Capacities.ContributorIdentitiesPerUpdate, health.Omissions.ContributorObservations > 0));
            result.Add(new("histogramThresholds", ModHealthReportLimits.MaxHistogramThresholds, health.Histogram.Thresholds.Count > ModHealthReportLimits.MaxHistogramThresholds));
        }
        return result.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToImmutableArray();
    }

    private static ImmutableArray<ModHealthOmission> BuildOmissions(ModHealthExportRequest request, ModHealthPerformanceSnapshot? health, int modCount, int logCount, int failureCount)
    {
        ModHealthLedgerSnapshot ledger = request.Ledger;
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal)
        {
            ["modInventory"] = SaturatingAdd(ledger.Omissions.ModInventoryRecords, Math.Max(0, ledger.Mods.Count - modCount)),
            ["logIdentities"] = SaturatingAdd(ledger.Omissions.LogIdentityObservations, Math.Max(0, ledger.LogSources.Count - logCount)),
            ["callbackFailureSignatures"] = SaturatingAdd(ledger.Omissions.CallbackFailureObservations, Math.Max(0, ledger.CallbackFailures.Count - failureCount)),
            ["dependencyIds"] = ledger.Omissions.DependencyIds,
            ["marks"] = Math.Max(0, request.Marks.Length - ModHealthReportLimits.MaxMarks)
        };
        if (health != null)
        {
            counts["callbacks"] = SaturatingAdd(health.Omissions.CallbackInvocations, Math.Max(0, health.Callbacks.Count - ModHealthReportLimits.MaxCallbacks));
            counts["recentUpdates"] = SaturatingAdd(health.Omissions.RecentUpdates, Math.Max(0, health.RecentUpdates.Count - ModHealthReportLimits.MaxRecentUpdates));
            counts["worstUpdates"] = SaturatingAdd(health.Omissions.WorstUpdates, Math.Max(0, health.WorstUpdates.Count - ModHealthReportLimits.MaxWorstUpdates));
            counts["slowEpisodes"] = SaturatingAdd(health.Omissions.SlowEpisodes, Math.Max(0, health.Episodes.Count - ModHealthReportLimits.MaxEpisodes));
            counts["contributorObservations"] = health.Omissions.ContributorObservations;
            counts["slowUpdateContributorIdentities"] = health.Omissions.SlowUpdateContributorIdentities;
            counts["invalidHistogramUpdates"] = health.Omissions.InvalidHistogramUpdates;
            counts["histogramThresholds"] = Math.Max(0, health.Histogram.Thresholds.Count - ModHealthReportLimits.MaxHistogramThresholds);
        }
        if (request.Performance != null)
        {
            counts["invalidTimingPartitionUpdates"] = Math.Max(0, request.Performance.InvalidTimingPartitionTickCount);
            counts["invalidGcCollectionUpdates"] = Math.Max(0, request.Performance.InvalidGcCollectionTickCount);
            counts["invalidCaptureGcCollectionData"] = request.Performance.CaptureGcCollectionDataIsValid ? 0 : 1;
        }
        return counts.Select(pair => new ModHealthOmission(pair.Key, Math.Max(0, pair.Value))).ToImmutableArray();
    }

    private static ModHealthPerformance EmptyPerformance()
    {
        ModHealthHistogram histogram = new(0, 0, null, null, null, null, null, false, 0, 0, 0, ImmutableArray<ModHealthThresholdCount>.Empty);
        return new ModHealthPerformance(histogram, 0, 0, 0, 0, 0, ImmutableArray<ModHealthCallback>.Empty, ImmutableArray<ModHealthUpdate>.Empty, ImmutableArray<ModHealthUpdate>.Empty, ImmutableArray<ModHealthEpisode>.Empty, 0, 0, 0, false);
    }

    private static ModHealthLogSeveritySummary BuildSeverity(ModHealthSeverityCountsSnapshot source)
    {
        return new(
            Math.Max(0, source.TraceMessages), Math.Max(0, source.TraceCharacters),
            Math.Max(0, source.DebugMessages), Math.Max(0, source.DebugCharacters),
            Math.Max(0, source.InfoMessages), Math.Max(0, source.InfoCharacters),
            Math.Max(0, source.WarningMessages), Math.Max(0, source.WarningCharacters),
            Math.Max(0, source.ErrorMessages), Math.Max(0, source.ErrorCharacters),
            Math.Max(0, source.AlertMessages), Math.Max(0, source.AlertCharacters)
        );
    }

    private static ModHealthLogSeveritySummary EmptySeverity() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static ModHealthModInventorySummary BuildInventorySummary(ModHealthLedgerSnapshot ledger, int retained)
    {
        return new(
            Math.Max(0, ledger.TotalDiscoveredMods),
            GetStatusTotal(ledger, ModHealthLedgerModStatus.Discovered),
            GetStatusTotal(ledger, ModHealthLedgerModStatus.Loaded),
            GetStatusTotal(ledger, ModHealthLedgerModStatus.Skipped),
            GetStatusTotal(ledger, ModHealthLedgerModStatus.Ignored),
            GetStatusTotal(ledger, ModHealthLedgerModStatus.Invalid),
            GetStatusTotal(ledger, ModHealthLedgerModStatus.Failed),
            Math.Max(0, retained)
        );
    }

    private static long GetStatusTotal(ModHealthLedgerSnapshot ledger, ModHealthLedgerModStatus status)
    {
        return ledger.ModStatusTotals.TryGetValue(status, out long count) ? Math.Max(0, count) : 0;
    }

    private static long CountSlowUpdateParticipation(ModHealthPerformanceSnapshot? health, string modId)
    {
        if (health == null)
            return 0;
        return health.WorstUpdates.LongCount(update =>
            update.TotalMilliseconds >= health.SlowUpdateThresholdMilliseconds
            && update.Contributors.Any(contributor => contributor.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
        );
    }

    private static bool IsLifecycleCallback(ModHealthCallbackPerformanceSnapshot callback)
    {
        return callback.Phase == ModHealthExecutionPhase.Startup || callback.Operation is ModHealthOperationKind.Entry or ModHealthOperationKind.GetApi;
    }

    private static bool IsValidPartition(double total, double gameUpdate, double observed, double observedDuringGameUpdate)
    {
        return double.IsFinite(total) && total >= 0
            && double.IsFinite(gameUpdate) && gameUpdate >= 0
            && double.IsFinite(observed) && observed >= 0
            && double.IsFinite(observedDuringGameUpdate) && observedDuringGameUpdate >= 0
            && observedDuringGameUpdate <= gameUpdate
            && observedDuringGameUpdate <= observed
            && gameUpdate + (observed - observedDuringGameUpdate) <= total;
    }

    private static int? FindNearestMark(uint tick, ImmutableArray<ModHealthMark> marks)
    {
        ModHealthMark? nearest = null;
        ulong nearestDistance = ulong.MaxValue;
        foreach (ModHealthMark mark in marks)
        {
            ulong distance = TickDistance(tick, mark.UpdateTick);
            if (distance > NearestMarkTickDistance)
                continue;
            if (distance < nearestDistance || (distance == nearestDistance && (nearest == null || mark.OffsetMilliseconds < nearest.Value.OffsetMilliseconds || (mark.OffsetMilliseconds == nearest.Value.OffsetMilliseconds && mark.Number < nearest.Value.Number))))
            {
                nearest = mark;
                nearestDistance = distance;
            }
        }
        return nearest?.Number;
    }

    private static ulong TickDistance(uint left, uint right)
    {
        ulong direct = left >= right ? left - (ulong)right : right - (ulong)left;
        return Math.Min(direct, (ulong)uint.MaxValue + 1 - direct);
    }

    private static ModHealthCaptureMode GetCaptureMode(ModHealthCaptureOwner owner) => owner switch
    {
        ModHealthCaptureOwner.Health => ModHealthCaptureMode.Health,
        ModHealthCaptureOwner.Performance => ModHealthCaptureMode.Performance,
        _ => ModHealthCaptureMode.LedgerOnly
    };

    private static ModHealthModKind GetModKind(ModHealthModSnapshot source) => source.UsesGeneratedInvalidIdentity || source.Kind == ModHealthLedgerModKind.Unknown
        ? ModHealthModKind.Invalid
        : source.Kind == ModHealthLedgerModKind.ContentPack ? ModHealthModKind.ContentPack : ModHealthModKind.CodeMod;

    private static ModHealthModStatus GetModStatus(ModHealthLedgerModStatus status) => status switch
    {
        ModHealthLedgerModStatus.Discovered => ModHealthModStatus.Discovered,
        ModHealthLedgerModStatus.Loaded => ModHealthModStatus.Loaded,
        ModHealthLedgerModStatus.Ignored => ModHealthModStatus.Ignored,
        ModHealthLedgerModStatus.Invalid => ModHealthModStatus.Invalid,
        ModHealthLedgerModStatus.Failed => ModHealthModStatus.Failed,
        _ => ModHealthModStatus.Skipped
    };

    private static ModHealthReportUpdateStatus GetUpdateStatus(ModHealthUpdateStatus status) => status switch
    {
        ModHealthUpdateStatus.Pending => ModHealthReportUpdateStatus.Pending,
        ModHealthUpdateStatus.UpToDate => ModHealthReportUpdateStatus.UpToDate,
        ModHealthUpdateStatus.UpdateAvailable => ModHealthReportUpdateStatus.UpdateAvailable,
        ModHealthUpdateStatus.Disabled => ModHealthReportUpdateStatus.Disabled,
        ModHealthUpdateStatus.Suppressed => ModHealthReportUpdateStatus.Suppressed,
        ModHealthUpdateStatus.Unavailable => ModHealthReportUpdateStatus.Unavailable,
        _ => ModHealthReportUpdateStatus.Unknown
    };

    private static string? GetFailureCategory(ModHealthModSnapshot source)
    {
        if (source.FailureReason != ModHealthModFailureReason.None)
            return ToKebabCase(source.FailureReason.ToString());
        return source.Status == ModHealthLedgerModStatus.Discovered ? "status-incomplete" : null;
    }

    private static ImmutableArray<string> GetWarningFlags(ulong flags)
    {
        if (flags == 0)
            return ImmutableArray<string>.Empty;
        ImmutableArray<string>.Builder builder = ImmutableArray.CreateBuilder<string>();
        for (int bit = 0; bit < 64; bit++)
        {
            ulong value = 1UL << bit;
            if ((flags & value) == 0)
                continue;
            builder.Add(value <= int.MaxValue && Enum.IsDefined(typeof(ModWarning), (int)value)
                ? ToKebabCase(((ModWarning)(int)value).ToString())
                : $"unknown-flag-{bit.ToString("00", CultureInfo.InvariantCulture)}");
        }
        return builder.ToImmutable();
    }

    private static int GetModPriority(ModHealthModStatus status) => status switch
    {
        ModHealthModStatus.Loaded or ModHealthModStatus.Failed or ModHealthModStatus.Invalid => 0,
        ModHealthModStatus.Skipped or ModHealthModStatus.Discovered => 1,
        _ => 2
    };

    private static string GetTickPhase(ModHealthTickPhase phase) => phase switch
    {
        ModHealthTickPhase.LoadingSaving => "loading-saving",
        ModHealthTickPhase.Title => "title",
        ModHealthTickPhase.Cutscene => "cutscene",
        ModHealthTickPhase.Menu => "menu",
        ModHealthTickPhase.Gameplay => "gameplay",
        _ => "unknown"
    };

    private static string NormalizeSessionType(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? "unknown";
        return normalized switch
        {
            "x11" => "x11",
            "xwayland" => "xwayland",
            "wayland" => "wayland",
            _ => "unknown"
        };
    }

    private static string NormalizeMultiplayerRole(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? "unknown";
        return normalized switch
        {
            "single-player" => "single-player",
            "host" => "host",
            "client" => "client",
            _ => "unknown"
        };
    }

    /// <summary>Reduce a runtime OS description to a known non-identifying Linux distribution ID and numeric version.</summary>
    private static string? NormalizeLinuxDistribution(string? value)
    {
        return LinuxModHealthEnvironment.NormalizeDistribution(value);
    }

    /// <summary>Reduce a kernel banner to its leading numeric release.</summary>
    private static string? NormalizeKernelRelease(string? value)
    {
        return LinuxModHealthEnvironment.NormalizeKernel(value);
    }

    private static string CreateReportId(Guid requestId) => "report-" + requestId.ToString("N", CultureInfo.InvariantCulture)[..16];

    private static DateTimeOffset ToUtc(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return new DateTimeOffset(utc);
    }

    private static string Sanitize(string? value, string fallback, int maxLength = ModHealthReportLimits.MaxIdentityLength)
    {
        string result = ModHealthTextSanitizer.SanitizeIdentity(value ?? "", maxLength);
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    private static string? SanitizeOptional(string? value, int maxLength = ModHealthReportLimits.MaxIdentityLength)
    {
        return string.IsNullOrWhiteSpace(value) ? null : Sanitize(value, "unknown", maxLength);
    }

    private static double NonnegativeFinite(double value, double fallback = 0) => double.IsFinite(value) && value >= 0 ? value : fallback;
    private static double? NullableNonnegativeFinite(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 ? value : null;

    private static long SaturatingSum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (long value in values)
            total = SaturatingAdd(total, Math.Max(0, value));
        return total;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0)
            return left;
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private static string ToKebabCase(string value)
    {
        var result = new System.Text.StringBuilder(value.Length + 4);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsUpper(character) && index > 0)
                result.Append('-');
            result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }

    private readonly record struct ModAggregate(double TotalMilliseconds, double PeakMilliseconds, long CallCount, long FailureCount, long SlowUpdateParticipationCount);
}
