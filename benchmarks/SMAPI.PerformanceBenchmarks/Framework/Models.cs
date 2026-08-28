using System;
using System.Collections.Generic;

namespace SMAPI.PerformanceBenchmarks.Framework;

/// <summary>A committed deterministic performance baseline.</summary>
internal sealed record PerformanceBaseline(
    int SchemaVersion,
    BaselineRuntime Runtime,
    Dictionary<string, ScenarioBaseline> Scenarios
);

/// <summary>The exact runtime required by a baseline.</summary>
internal sealed record BaselineRuntime(string Framework, string RuntimeVersion, string Rid);

/// <summary>The gates and informational reference for one scenario.</summary>
internal sealed record ScenarioBaseline(
    int Operations,
    int WarmupOperations,
    string ExpectedDigest,
    long MaxAllocatedBytesPerOperation,
    double? InformationalMedianNanosecondsPerOperation
);

/// <summary>The runtime which executed a result bundle.</summary>
internal sealed record RuntimeTarget(
    string Framework,
    string RuntimeVersion,
    string Rid,
    string FrameworkDescription,
    string OperatingSystem,
    string ProcessArchitecture,
    bool ServerGarbageCollection,
    bool TieredCompilation
);

/// <summary>One raw measured scenario sample.</summary>
internal sealed record PerformanceSample(
    int Index,
    string Digest,
    long AllocatedBytes,
    double AllocatedBytesPerOperation,
    long ElapsedNanoseconds,
    double NanosecondsPerOperation
);

/// <summary>The measured and compared result for one scenario.</summary>
internal sealed record ScenarioResult(
    string Id,
    string Description,
    int Operations,
    int WarmupOperations,
    string ExpectedDigest,
    long MaxAllocatedBytesPerOperation,
    double? InformationalMedianNanosecondsPerOperation,
    IReadOnlyList<PerformanceSample> Samples,
    double MedianAllocatedBytesPerOperation,
    double MaximumAllocatedBytesPerOperation,
    double MedianNanosecondsPerOperation,
    bool CorrectnessPassed,
    bool AllocationPassed,
    IReadOnlyList<string> Failures
)
{
    /// <summary>Get whether all blocking gates passed.</summary>
    public bool Passed => this.CorrectnessPassed && this.AllocationPassed && this.Failures.Count == 0;
}

/// <summary>A complete machine-readable performance-regression result.</summary>
internal sealed record PerformanceSuiteResult(
    int SchemaVersion,
    string Commit,
    DateTimeOffset GeneratedAtUtc,
    RuntimeTarget Runtime,
    bool Passed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<ScenarioResult> Scenarios
);
