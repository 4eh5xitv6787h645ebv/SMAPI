using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

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

        const string validBaselineJson = """
            {
              "schemaVersion": 1,
              "runtime": { "framework": "net6.0", "runtimeVersion": "6.0.36", "rid": "linux-x64" },
              "scenarios": {
                "self-test": {
                  "operations": 1,
                  "warmupOperations": 1,
                  "expectedDigest": "000000000000002a",
                  "maxAllocatedBytesPerOperation": 0,
                  "informationalMedianNanosecondsPerOperation": null
                }
              }
            }
            """;

        const string baselineWithDuplicateScenarioId = """
            {
              "schemaVersion": 1,
              "runtime": { "framework": "net6.0", "runtimeVersion": "6.0.36", "rid": "linux-x64" },
              "scenarios": {
                "self-test": {
                  "operations": 1,
                  "warmupOperations": 1,
                  "expectedDigest": "000000000000002a",
                  "maxAllocatedBytesPerOperation": 0,
                  "informationalMedianNanosecondsPerOperation": null
                },
                "self-test": {
                  "operations": 1,
                  "warmupOperations": 1,
                  "expectedDigest": "000000000000002a",
                  "maxAllocatedBytesPerOperation": 0,
                  "informationalMedianNanosecondsPerOperation": null
                }
              }
            }
            """;

        InfrastructureSelfTests.Require(BaselineStore.Parse(validBaselineJson).Scenarios.Count == 1, "A complete baseline should parse.");
        InfrastructureSelfTests.RequireThrows(
            () => BaselineStore.Parse(validBaselineJson.Replace("\"informationalMedianNanosecondsPerOperation\": null", "\"informationalMedianNanosecondsPerOperation\": null, \"unknown\": true", StringComparison.Ordinal)),
            "An unknown baseline property should be rejected."
        );
        InfrastructureSelfTests.RequireThrows(
            () => BaselineStore.Parse(baselineWithDuplicateScenarioId),
            "A duplicate scenario ID should be rejected."
        );

        string[] requiredScenarioProperties =
        [
            "operations",
            "warmupOperations",
            "expectedDigest",
            "maxAllocatedBytesPerOperation",
            "informationalMedianNanosecondsPerOperation"
        ];
        foreach (string name in requiredScenarioProperties)
        {
            InfrastructureSelfTests.RequireThrows(
                () => BaselineStore.Parse(InfrastructureSelfTests.RemoveProperty(validBaselineJson, "scenarios", "self-test", name)),
                $"Missing required scenario property '{name}' should be rejected."
            );
        }
        InfrastructureSelfTests.RequireThrows(
            () => BaselineStore.Parse(InfrastructureSelfTests.RemoveProperty(validBaselineJson, "runtime", "rid")),
            "A missing required runtime property should be rejected."
        );
        InfrastructureSelfTests.RequireThrows(
            () => BaselineStore.Parse(InfrastructureSelfTests.RemoveProperty(validBaselineJson, "schemaVersion")),
            "A missing required root property should be rejected."
        );

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

    /// <summary>Require an action to reject invalid input.</summary>
    private static void RequireThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException($"Infrastructure self-test failed: {message}");
    }

    /// <summary>Remove one nested JSON property while preserving a valid document.</summary>
    private static string RemoveProperty(string json, params string[] path)
    {
        JsonObject parent = JsonNode.Parse(json)!.AsObject();
        for (int index = 0; index < path.Length - 1; index++)
            parent = parent[path[index]]!.AsObject();
        if (!parent.Remove(path[^1]))
            throw new InvalidOperationException($"Infrastructure self-test fixture doesn't contain '{string.Join('.', path)}'.");
        return parent.ToJsonString();
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
