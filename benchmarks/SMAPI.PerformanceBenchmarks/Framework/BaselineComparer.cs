using System;
using System.Collections.Generic;
using System.Linq;

namespace SMAPI.PerformanceBenchmarks.Framework;

/// <summary>Apply deterministic blocking gates to measured results.</summary>
internal static class BaselineComparer
{
    /// <summary>Validate that the current runtime exactly matches the baseline target.</summary>
    public static IReadOnlyList<string> CompareRuntime(BaselineRuntime expected, RuntimeTarget actual)
    {
        List<string> failures = [];
        if (!string.Equals(expected.Framework, actual.Framework, StringComparison.Ordinal))
            failures.Add($"Framework mismatch: baseline '{expected.Framework}', actual '{actual.Framework}'.");
        if (!string.Equals(expected.RuntimeVersion, actual.RuntimeVersion, StringComparison.Ordinal))
            failures.Add($"Runtime version mismatch: baseline '{expected.RuntimeVersion}', actual '{actual.RuntimeVersion}'.");
        if (!string.Equals(expected.Rid, actual.Rid, StringComparison.Ordinal))
            failures.Add($"Runtime identifier mismatch: baseline '{expected.Rid}', actual '{actual.Rid}'.");
        return failures;
    }

    /// <summary>Compare raw samples against one scenario baseline.</summary>
    public static ScenarioResult CompareScenario(IPerformanceScenario scenario, ScenarioBaseline baseline, IReadOnlyList<PerformanceSample> samples)
    {
        List<string> failures = [];
        bool correctnessPassed = true;
        bool allocationPassed = true;

        foreach (PerformanceSample sample in samples)
        {
            if (!string.Equals(sample.Digest, baseline.ExpectedDigest, StringComparison.Ordinal))
            {
                correctnessPassed = false;
                failures.Add($"Sample {sample.Index} digest '{sample.Digest}' did not match '{baseline.ExpectedDigest}'.");
            }

            if (sample.AllocatedBytes > checked(baseline.MaxAllocatedBytesPerOperation * baseline.Operations))
            {
                allocationPassed = false;
                failures.Add($"Sample {sample.Index} allocated {sample.AllocatedBytesPerOperation:F3} bytes/op, above the {baseline.MaxAllocatedBytesPerOperation} bytes/op budget.");
            }
        }

        return new ScenarioResult(
            Id: scenario.Id,
            Description: scenario.Description,
            Operations: baseline.Operations,
            WarmupOperations: baseline.WarmupOperations,
            ExpectedDigest: baseline.ExpectedDigest,
            MaxAllocatedBytesPerOperation: baseline.MaxAllocatedBytesPerOperation,
            InformationalMedianNanosecondsPerOperation: baseline.InformationalMedianNanosecondsPerOperation,
            Samples: samples,
            MedianAllocatedBytesPerOperation: BaselineComparer.Median(samples.Select(sample => sample.AllocatedBytesPerOperation)),
            MaximumAllocatedBytesPerOperation: samples.Max(sample => sample.AllocatedBytesPerOperation),
            MedianNanosecondsPerOperation: BaselineComparer.Median(samples.Select(sample => sample.NanosecondsPerOperation)),
            CorrectnessPassed: correctnessPassed,
            AllocationPassed: allocationPassed,
            Failures: failures
        );
    }

    /// <summary>Create a failed result when a scenario couldn't complete measurement.</summary>
    public static ScenarioResult CreateExecutionFailure(IPerformanceScenario scenario, ScenarioBaseline baseline, Exception exception)
    {
        return new ScenarioResult(
            Id: scenario.Id,
            Description: scenario.Description,
            Operations: baseline.Operations,
            WarmupOperations: baseline.WarmupOperations,
            ExpectedDigest: baseline.ExpectedDigest,
            MaxAllocatedBytesPerOperation: baseline.MaxAllocatedBytesPerOperation,
            InformationalMedianNanosecondsPerOperation: baseline.InformationalMedianNanosecondsPerOperation,
            Samples: [],
            MedianAllocatedBytesPerOperation: 0,
            MaximumAllocatedBytesPerOperation: 0,
            MedianNanosecondsPerOperation: 0,
            CorrectnessPassed: false,
            AllocationPassed: false,
            Failures: [$"Execution failed with {exception.GetType().Name}: {exception.Message}"]
        );
    }

    /// <summary>Calculate a median.</summary>
    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
            throw new InvalidOperationException("Can't calculate a median for an empty sequence.");
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
    }
}
