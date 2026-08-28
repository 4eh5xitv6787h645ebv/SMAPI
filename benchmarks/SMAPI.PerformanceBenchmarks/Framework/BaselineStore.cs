using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SMAPI.PerformanceBenchmarks.Framework;

/// <summary>Read and validate committed performance baselines.</summary>
internal static class BaselineStore
{
    /// <summary>The supported baseline schema.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The shared deterministic JSON options.</summary>
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Read and validate a baseline file.</summary>
    public static PerformanceBaseline Read(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"The baseline file '{path}' does not exist.");

        PerformanceBaseline? baseline = JsonSerializer.Deserialize<PerformanceBaseline>(File.ReadAllText(path), BaselineStore.JsonOptions);
        if (baseline is null)
            throw new InvalidOperationException($"The baseline file '{path}' is empty.");
        BaselineStore.Validate(baseline);
        return baseline;
    }

    /// <summary>Validate a parsed baseline.</summary>
    public static void Validate(PerformanceBaseline baseline)
    {
        if (baseline.SchemaVersion != BaselineStore.SchemaVersion)
            throw new InvalidOperationException($"Unsupported baseline schema {baseline.SchemaVersion}; expected {BaselineStore.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(baseline.Runtime.Framework) || string.IsNullOrWhiteSpace(baseline.Runtime.RuntimeVersion) || string.IsNullOrWhiteSpace(baseline.Runtime.Rid))
            throw new InvalidOperationException("The baseline runtime must specify framework, runtimeVersion, and rid.");
        if (baseline.Scenarios.Count == 0)
            throw new InvalidOperationException("The baseline must contain at least one scenario.");

        foreach ((string id, ScenarioBaseline scenario) in baseline.Scenarios)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("A baseline scenario ID is empty.");
            if (scenario.Operations <= 0)
                throw new InvalidOperationException($"Scenario '{id}' must run at least one operation.");
            if (scenario.WarmupOperations < 0)
                throw new InvalidOperationException($"Scenario '{id}' has a negative warm-up count.");
            if (scenario.MaxAllocatedBytesPerOperation < 0)
                throw new InvalidOperationException($"Scenario '{id}' has a negative allocation budget.");
            if (scenario.MaxAllocatedBytesPerOperation > long.MaxValue / scenario.Operations)
                throw new InvalidOperationException($"Scenario '{id}' has an allocation budget which overflows its operation count.");
            if (!BaselineStore.IsDigest(scenario.ExpectedDigest))
                throw new InvalidOperationException($"Scenario '{id}' must specify a lowercase 16-character hexadecimal digest.");
            if (scenario.InformationalMedianNanosecondsPerOperation is < 0)
                throw new InvalidOperationException($"Scenario '{id}' has a negative timing reference.");
        }
    }

    /// <summary>Get whether a string is a canonical 64-bit hexadecimal digest.</summary>
    private static bool IsDigest(string value)
    {
        if (value.Length != 16)
            return false;

        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }
}
