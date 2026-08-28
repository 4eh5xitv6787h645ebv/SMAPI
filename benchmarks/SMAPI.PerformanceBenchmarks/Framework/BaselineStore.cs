using System;
using System.Collections.Generic;
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

        return BaselineStore.Parse(File.ReadAllText(path));
    }

    /// <summary>Parse and validate one baseline document.</summary>
    internal static PerformanceBaseline Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        BaselineStore.ValidateProperties(root, "baseline", "schemaVersion", "runtime", "scenarios");
        if (root.TryGetProperty("runtime", out JsonElement runtime))
            BaselineStore.ValidateProperties(runtime, "baseline runtime", "framework", "runtimeVersion", "rid");
        if (root.TryGetProperty("scenarios", out JsonElement scenarios) && scenarios.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> scenarioIds = new(StringComparer.Ordinal);
            foreach (JsonProperty scenario in scenarios.EnumerateObject())
            {
                if (!scenarioIds.Add(scenario.Name))
                    throw new InvalidOperationException($"The baseline scenarios contain duplicate scenario ID '{scenario.Name}'.");

                BaselineStore.ValidateProperties(
                    scenario.Value,
                    $"scenario '{scenario.Name}'",
                    "operations",
                    "warmupOperations",
                    "expectedDigest",
                    "maxAllocatedBytesPerOperation",
                    "informationalMedianNanosecondsPerOperation"
                );
            }
        }

        PerformanceBaseline? baseline = JsonSerializer.Deserialize<PerformanceBaseline>(json, BaselineStore.JsonOptions);
        if (baseline is null)
            throw new InvalidOperationException("The baseline document is empty.");
        BaselineStore.Validate(baseline);
        return baseline;
    }

    /// <summary>Validate a parsed baseline.</summary>
    public static void Validate(PerformanceBaseline baseline)
    {
        if (baseline.Runtime is null)
            throw new InvalidOperationException("The baseline runtime is missing.");
        if (baseline.Scenarios is null)
            throw new InvalidOperationException("The baseline scenarios are missing.");
        if (baseline.SchemaVersion != BaselineStore.SchemaVersion)
            throw new InvalidOperationException($"Unsupported baseline schema {baseline.SchemaVersion}; expected {BaselineStore.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(baseline.Runtime.Framework) || string.IsNullOrWhiteSpace(baseline.Runtime.RuntimeVersion) || string.IsNullOrWhiteSpace(baseline.Runtime.Rid))
            throw new InvalidOperationException("The baseline runtime must specify framework, runtimeVersion, and rid.");
        if (!BaselineStore.IsThreePartVersion(baseline.Runtime.RuntimeVersion))
            throw new InvalidOperationException("The baseline runtimeVersion must contain exactly three numeric components.");
        if (baseline.Scenarios.Count == 0)
            throw new InvalidOperationException("The baseline must contain at least one scenario.");

        foreach ((string id, ScenarioBaseline scenario) in baseline.Scenarios)
        {
            if (!BaselineStore.IsScenarioId(id))
                throw new InvalidOperationException($"Baseline scenario ID '{id}' isn't canonical.");
            if (scenario is null)
                throw new InvalidOperationException($"Scenario '{id}' is null.");
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

    /// <summary>Require the exact property set and reject duplicate fields before model deserialization.</summary>
    private static void ValidateProperties(JsonElement element, string description, params string[] requiredProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"The {description} must be a JSON object.");

        HashSet<string> required = new(requiredProperties, StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new InvalidOperationException($"The {description} contains duplicate property '{property.Name}'.");
            if (!required.Contains(property.Name))
                throw new InvalidOperationException($"The {description} contains unknown property '{property.Name}'.");
        }

        foreach (string property in required)
        {
            if (!seen.Contains(property))
                throw new InvalidOperationException($"The {description} is missing required property '{property}'.");
        }
    }

    /// <summary>Get whether a string is a canonical 64-bit hexadecimal digest.</summary>
    private static bool IsDigest(string? value)
    {
        if (value is null || value.Length != 16)
            return false;

        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    /// <summary>Get whether a scenario ID matches the baseline schema's canonical dotted/dashed form.</summary>
    private static bool IsScenarioId(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        bool needsAlphanumeric = true;
        foreach (char character in value)
        {
            bool isAlphanumeric = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (isAlphanumeric)
                needsAlphanumeric = false;
            else if ((character is '.' or '-') && !needsAlphanumeric)
                needsAlphanumeric = true;
            else
                return false;
        }
        return !needsAlphanumeric;
    }

    /// <summary>Get whether a runtime patch is three dot-separated numeric components.</summary>
    private static bool IsThreePartVersion(string value)
    {
        string[] parts = value.Split('.');
        if (parts.Length != 3)
            return false;

        foreach (string part in parts)
        {
            if (part.Length == 0)
                return false;
            foreach (char character in part)
            {
                if (character is not (>= '0' and <= '9'))
                    return false;
            }
        }
        return true;
    }
}
