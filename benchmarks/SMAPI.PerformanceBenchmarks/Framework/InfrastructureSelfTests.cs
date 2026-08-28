using System;
using System.Collections.Generic;

namespace SMAPI.PerformanceBenchmarks.Framework;

/// <summary>Dependency-free checks for the benchmark comparison and writer infrastructure.</summary>
internal static class InfrastructureSelfTests
{
    /// <summary>Run all infrastructure checks, throwing if one fails.</summary>
    public static void Run()
    {
        InfrastructureCanaryScenario scenario = new();
        ScenarioBaseline baseline = new(
            Operations: 10,
            WarmupOperations: 1,
            ExpectedDigest: "000000000000002a",
            MaxAllocatedBytesPerOperation: 0,
            InformationalMedianNanosecondsPerOperation: 1
        );
        PerformanceSample passingSample = new(1, "000000000000002a", 0, 0, 1_000_000, 100_000);

        ScenarioResult passing = BaselineComparer.CompareScenario(scenario, baseline, [passingSample]);
        InfrastructureSelfTests.Require(passing.Passed, "A matching deterministic sample should pass.");

        ScenarioResult badDigest = BaselineComparer.CompareScenario(
            scenario,
            baseline,
            [passingSample with { Digest = "000000000000002b" }]
        );
        InfrastructureSelfTests.Require(!badDigest.CorrectnessPassed && !badDigest.Passed, "A digest regression should fail.");

        ScenarioResult badAllocation = BaselineComparer.CompareScenario(
            scenario,
            baseline,
            [passingSample with { AllocatedBytes = 1, AllocatedBytesPerOperation = 0.1 }]
        );
        InfrastructureSelfTests.Require(!badAllocation.AllocationPassed && !badAllocation.Passed, "An allocation regression should fail.");

        ScenarioResult executionFailure = BaselineComparer.CreateExecutionFailure(scenario, baseline, new InvalidOperationException("probe"));
        InfrastructureSelfTests.Require(!executionFailure.Passed && executionFailure.Failures.Count == 1, "An execution exception should produce a failed scenario result.");

        InfrastructureSelfTests.Require(passing.MedianNanosecondsPerOperation > baseline.InformationalMedianNanosecondsPerOperation, "The timing test fixture should be slower than its reference.");
        InfrastructureSelfTests.Require(passing.Passed, "Informational timing must never fail a required gate.");

        RuntimeTarget runtime = new("net6.0", "6.0.36", "linux-x64", ".NET 6.0.36", "Linux", "x64", false, false);
        InfrastructureSelfTests.Require(BaselineComparer.CompareRuntime(new BaselineRuntime("net6.0", "6.0.36", "linux-x64"), runtime).Count == 0, "An exact runtime should pass.");
        InfrastructureSelfTests.Require(BaselineComparer.CompareRuntime(new BaselineRuntime("net6.0", "6.0.35", "linux-x64"), runtime).Count == 1, "A runtime patch mismatch should fail.");

        Dictionary<string, ScenarioBaseline> continuationBaselines = new(StringComparer.Ordinal)
        {
            ["self-test.pass"] = baseline,
            ["self-test.throw"] = baseline
        };
        PerformanceSuiteResult continuation = new PerformanceRunner().Run(
            availableScenarios: [new SelfTestScenario("self-test.throw", shouldThrow: true), new SelfTestScenario("self-test.pass", shouldThrow: false)],
            baseline: new PerformanceBaseline(1, new BaselineRuntime("net6.0", "6.0.36", "linux-x64"), continuationBaselines),
            runtime: runtime,
            commit: "self-test"
        );
        InfrastructureSelfTests.Require(!continuation.Passed, "A scenario exception should fail the suite.");
        InfrastructureSelfTests.Require(continuation.Scenarios.Count == 2, "The runner should continue after a scenario exception.");
        InfrastructureSelfTests.Require(continuation.Scenarios[0].Passed, "A passing scenario after sorting should retain its result.");
        InfrastructureSelfTests.Require(!continuation.Scenarios[1].Passed, "A throwing scenario should produce a failed result.");

        PerformanceSuiteResult result = new(1, "self-test", DateTimeOffset.UnixEpoch, runtime, true, [], [passing]);
        InfrastructureSelfTests.Require(ResultWriter.SerializeJson(result) == ResultWriter.SerializeJson(result), "JSON output should be deterministic.");
        InfrastructureSelfTests.Require(ResultWriter.FormatMarkdown(result) == ResultWriter.FormatMarkdown(result), "Markdown output should be deterministic.");
    }

    /// <summary>Throw if a self-test condition isn't true.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Infrastructure self-test failed: {message}");
    }

    /// <summary>A fixed-result scenario for exercising runner behavior.</summary>
    private sealed class SelfTestScenario : IPerformanceScenario
    {
        /// <inheritdoc />
        public string Id { get; }

        /// <inheritdoc />
        public string Description => "Infrastructure self-test fixture.";

        /// <summary>Whether execution should fail.</summary>
        private readonly bool ShouldThrow;

        /// <summary>Construct an instance.</summary>
        public SelfTestScenario(string id, bool shouldThrow)
        {
            this.Id = id;
            this.ShouldThrow = shouldThrow;
        }

        /// <inheritdoc />
        public void Setup()
        {
        }

        /// <inheritdoc />
        public ulong Execute(int operations)
        {
            if (this.ShouldThrow)
                throw new InvalidOperationException("intentional self-test failure");
            return 42;
        }

        /// <inheritdoc />
        public void Cleanup()
        {
        }
    }
}
