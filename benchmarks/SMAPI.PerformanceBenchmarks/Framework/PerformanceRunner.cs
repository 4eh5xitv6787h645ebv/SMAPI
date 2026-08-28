using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SMAPI.PerformanceBenchmarks.Framework;

/// <summary>Execute deterministic scenarios and collect raw samples.</summary>
internal sealed class PerformanceRunner
{
    /// <summary>The number of independent measured samples per scenario.</summary>
    public const int MeasurementSampleCount = 5;

    /// <summary>Run selected scenarios and compare them with the baseline.</summary>
    public PerformanceSuiteResult Run(
        IReadOnlyList<IPerformanceScenario> availableScenarios,
        PerformanceBaseline baseline,
        RuntimeTarget runtime,
        string commit,
        IReadOnlySet<string>? selectedScenarioIds = null
    )
    {
        List<string> suiteFailures = BaselineComparer.CompareRuntime(baseline.Runtime, runtime).ToList();
        if (runtime.ServerGarbageCollection)
            suiteFailures.Add("Server GC is enabled; deterministic allocation gates require workstation GC.");
        if (runtime.TieredCompilation)
            suiteFailures.Add("Tiered compilation is enabled; deterministic measurements require it to be disabled.");
        Dictionary<string, IPerformanceScenario> scenarios = new(StringComparer.Ordinal);
        foreach (IPerformanceScenario scenario in availableScenarios)
        {
            if (!scenarios.TryAdd(scenario.Id, scenario))
                suiteFailures.Add($"Duplicate registered scenario ID '{scenario.Id}'.");
        }

        IEnumerable<string> idsToRun = selectedScenarioIds is null
            ? baseline.Scenarios.Keys
            : selectedScenarioIds;
        List<string> orderedIds = idsToRun.OrderBy(id => id, StringComparer.Ordinal).ToList();
        foreach (string id in orderedIds)
        {
            if (!baseline.Scenarios.ContainsKey(id))
                suiteFailures.Add($"Scenario '{id}' has no baseline.");
            if (!scenarios.ContainsKey(id))
                suiteFailures.Add($"Baseline scenario '{id}' is not registered.");
        }

        if (selectedScenarioIds is null)
        {
            foreach (string id in scenarios.Keys)
            {
                if (!baseline.Scenarios.ContainsKey(id))
                    suiteFailures.Add($"Registered scenario '{id}' is missing from the baseline.");
            }
        }

        List<ScenarioResult> results = [];
        if (suiteFailures.Count == 0)
        {
            foreach (string id in orderedIds)
            {
                IPerformanceScenario scenario = scenarios[id];
                ScenarioBaseline scenarioBaseline = baseline.Scenarios[id];
                try
                {
                    IReadOnlyList<PerformanceSample> samples = this.Measure(scenario, scenarioBaseline);
                    results.Add(BaselineComparer.CompareScenario(scenario, scenarioBaseline, samples));
                }
                catch (Exception ex)
                {
                    results.Add(BaselineComparer.CreateExecutionFailure(scenario, scenarioBaseline, ex));
                }
            }
        }

        bool passed = suiteFailures.Count == 0 && results.All(result => result.Passed);
        return new PerformanceSuiteResult(
            SchemaVersion: BaselineStore.SchemaVersion,
            Commit: commit,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Runtime: runtime,
            Passed: passed,
            Failures: suiteFailures,
            Scenarios: results
        );
    }

    /// <summary>Measure one scenario after setup and warm-up.</summary>
    private IReadOnlyList<PerformanceSample> Measure(IPerformanceScenario scenario, ScenarioBaseline baseline)
    {
        List<PerformanceSample> samples = new(PerformanceRunner.MeasurementSampleCount);
        scenario.Setup();
        try
        {
            if (baseline.WarmupOperations > 0)
                _ = scenario.Execute(baseline.WarmupOperations);

            // Prime the measurement machinery itself so its first-use JIT and runtime initialization aren't
            // attributed to the first retained scenario sample.
            _ = this.MeasureSample(scenario, baseline, index: 0);

            for (int index = 1; index <= PerformanceRunner.MeasurementSampleCount; index++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                samples.Add(this.MeasureSample(scenario, baseline, index));
            }
        }
        finally
        {
            scenario.Cleanup();
        }
        return samples;
    }

    /// <summary>Collect one raw sample without setup, warm-up, or collection control.</summary>
    private PerformanceSample MeasureSample(IPerformanceScenario scenario, ScenarioBaseline baseline, int index)
    {
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        long timestampBefore = Stopwatch.GetTimestamp();
        ulong digest = scenario.Execute(baseline.Operations);
        long timestampAfter = Stopwatch.GetTimestamp();
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        long elapsedNanoseconds = checked((long)Math.Round((timestampAfter - timestampBefore) * (1_000_000_000d / Stopwatch.Frequency)));
        GC.KeepAlive(digest);

        return new PerformanceSample(
            Index: index,
            Digest: digest.ToString("x16"),
            AllocatedBytes: allocatedBytes,
            AllocatedBytesPerOperation: (double)allocatedBytes / baseline.Operations,
            ElapsedNanoseconds: elapsedNanoseconds,
            NanosecondsPerOperation: (double)elapsedNanoseconds / baseline.Operations
        );
    }
}
