using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SMAPI.PerformanceBenchmarks.Framework;

/// <summary>Write machine-readable and human-readable comparison artifacts.</summary>
internal static class ResultWriter
{
    /// <summary>Write result artifacts into a directory.</summary>
    public static void Write(string outputDirectory, PerformanceSuiteResult result)
    {
        Directory.CreateDirectory(outputDirectory);
        ResultWriter.WriteAtomic(Path.Combine(outputDirectory, "results.json"), ResultWriter.SerializeJson(result));
        ResultWriter.WriteAtomic(Path.Combine(outputDirectory, "comparison.md"), ResultWriter.FormatMarkdown(result));
    }

    /// <summary>Serialize a result deterministically.</summary>
    internal static string SerializeJson(PerformanceSuiteResult result)
    {
        return JsonSerializer.Serialize(result, BaselineStore.JsonOptions) + Environment.NewLine;
    }

    /// <summary>Format a readable comparison.</summary>
    internal static string FormatMarkdown(PerformanceSuiteResult result)
    {
        StringBuilder text = new();
        text.AppendLine("# Deterministic performance regression results");
        text.AppendLine();
        text.AppendLine($"- Result: **{(result.Passed ? "PASS" : "FAIL")}**");
        text.AppendLine($"- Commit: `{result.Commit}`");
        text.AppendLine($"- Runtime: `{result.Runtime.Framework}` / `{result.Runtime.RuntimeVersion}` / `{result.Runtime.Rid}`");
        text.AppendLine($"- Tiered compilation: `{result.Runtime.TieredCompilation.ToString().ToLowerInvariant()}`");
        text.AppendLine("- Wall-clock measurements: informational only; they are not blocking gates.");
        text.AppendLine();

        if (result.Failures.Count > 0)
        {
            text.AppendLine("## Suite failures");
            text.AppendLine();
            foreach (string failure in result.Failures)
                text.AppendLine($"- {failure}");
            text.AppendLine();
        }

        if (result.Scenarios.Count > 0)
        {
            text.AppendLine("## Scenarios");
            text.AppendLine();
            text.AppendLine("| Scenario | Gate | Digest | Median allocation | Allocation budget | Median time (informational) | Reference time |");
            text.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: |");
            foreach (ScenarioResult scenario in result.Scenarios.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                string digest = scenario.Samples.FirstOrDefault()?.Digest ?? "n/a";
                string timingReference = scenario.InformationalMedianNanosecondsPerOperation is double reference
                    ? ResultWriter.FormatNumber(reference)
                    : "n/a";
                text.AppendLine(
                    $"| `{scenario.Id}` | {(scenario.Passed ? "PASS" : "FAIL")} | `{digest}` | {ResultWriter.FormatNumber(scenario.MedianAllocatedBytesPerOperation)} B/op | {scenario.MaxAllocatedBytesPerOperation} B/op | {ResultWriter.FormatNumber(scenario.MedianNanosecondsPerOperation)} ns/op | {timingReference} ns/op |"
                );
            }
            text.AppendLine();

            foreach (ScenarioResult scenario in result.Scenarios.Where(value => value.Failures.Count > 0))
            {
                text.AppendLine($"### `{scenario.Id}` failures");
                text.AppendLine();
                foreach (string failure in scenario.Failures)
                    text.AppendLine($"- {failure}");
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    /// <summary>Format a decimal invariantly.</summary>
    private static string FormatNumber(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Replace a result file atomically.</summary>
    private static void WriteAtomic(string path, string content)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
