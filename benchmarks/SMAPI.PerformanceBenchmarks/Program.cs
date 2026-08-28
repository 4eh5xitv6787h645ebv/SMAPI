using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SMAPI.PerformanceBenchmarks.Framework;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>The deterministic performance-regression command-line entry point.</summary>
internal static class Program
{
    /// <summary>The registered benchmark scenarios.</summary>
    private static readonly IReadOnlyList<IPerformanceScenario> Scenarios =
    [
        new InfrastructureCanaryScenario(),
        new CanonicalPathScenario(),
        new PathNormalizationScenario(),
        new JsonStreamingScenario(),
        new AssetNameParsingScenario(),
        new ReflectorWrapperCacheHitScenario(),
        new EventDispatchScenario(),
        new InventoryChestIdleScenario(),
        new ContentCacheEnumerationScenario(),
        new ContentInvalidationScenario(),
        new TmxLayerConversionScenario()
    ];

    /// <summary>Run the benchmark command.</summary>
    public static int Main(string[] args)
    {
        try
        {
            CommandLine options = CommandLine.Parse(args);
            if (options.ShowHelp)
            {
                Program.PrintUsage();
                return 0;
            }
            if (options.ListScenarios)
            {
                foreach (IPerformanceScenario scenario in Program.Scenarios.OrderBy(value => value.Id, StringComparer.Ordinal))
                    Console.WriteLine($"{scenario.Id}: {scenario.Description}");
                return 0;
            }
            if (options.RunSelfTests)
            {
                InfrastructureSelfTests.Run();
                Console.WriteLine("Infrastructure self-tests passed.");
                return 0;
            }

            PerformanceBaseline baseline = BaselineStore.Read(options.BaselinePath!);
            RuntimeTarget runtime = RuntimeEnvironment.GetCurrent();
            PerformanceSuiteResult result = new PerformanceRunner().Run(
                availableScenarios: Program.Scenarios,
                baseline: baseline,
                runtime: runtime,
                commit: options.Commit!,
                selectedScenarioIds: options.ScenarioIds.Count == 0 ? null : options.ScenarioIds
            );
            ResultWriter.Write(options.OutputDirectory!, result);

            Console.WriteLine($"Deterministic performance regression suite: {(result.Passed ? "PASS" : "FAIL")}");
            Console.WriteLine($"JSON: {Path.GetFullPath(Path.Combine(options.OutputDirectory!, "results.json"))}");
            Console.WriteLine($"Markdown: {Path.GetFullPath(Path.Combine(options.OutputDirectory!, "comparison.md"))}");
            return result.Passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    /// <summary>Print command usage.</summary>
    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  SMAPI.PerformanceBenchmarks --baseline <file> --output <directory> --commit <sha> [--scenario <id>]...");
        Console.WriteLine("  SMAPI.PerformanceBenchmarks --self-test");
        Console.WriteLine("  SMAPI.PerformanceBenchmarks --list");
    }

    /// <summary>Parsed command-line options.</summary>
    private sealed record CommandLine(
        string? BaselinePath,
        string? OutputDirectory,
        string? Commit,
        HashSet<string> ScenarioIds,
        bool RunSelfTests,
        bool ListScenarios,
        bool ShowHelp
    )
    {
        /// <summary>Parse and validate arguments.</summary>
        public static CommandLine Parse(string[] args)
        {
            string? baseline = null;
            string? output = null;
            string? commit = null;
            HashSet<string> scenarios = new(StringComparer.Ordinal);
            bool selfTest = false;
            bool list = false;
            bool help = false;

            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                switch (argument)
                {
                    case "--baseline":
                        baseline = CommandLine.GetValue(args, ref index, argument);
                        break;
                    case "--output":
                        output = CommandLine.GetValue(args, ref index, argument);
                        break;
                    case "--commit":
                        commit = CommandLine.GetValue(args, ref index, argument);
                        break;
                    case "--scenario":
                        string id = CommandLine.GetValue(args, ref index, argument);
                        if (!scenarios.Add(id))
                            throw new InvalidOperationException($"Scenario '{id}' was selected more than once.");
                        break;
                    case "--self-test":
                        selfTest = true;
                        break;
                    case "--list":
                        list = true;
                        break;
                    case "--help" or "-h":
                        help = true;
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown argument '{argument}'.");
                }
            }

            int modes = (selfTest ? 1 : 0) + (list ? 1 : 0) + (help ? 1 : 0);
            if (modes > 1)
                throw new InvalidOperationException("Choose only one of --self-test, --list, or --help.");
            if (modes == 0)
            {
                if (string.IsNullOrWhiteSpace(baseline))
                    throw new InvalidOperationException("Missing required --baseline path.");
                if (string.IsNullOrWhiteSpace(output))
                    throw new InvalidOperationException("Missing required --output directory.");
                if (string.IsNullOrWhiteSpace(commit))
                    throw new InvalidOperationException("Missing required --commit identifier.");
            }
            else if (baseline is not null || output is not null || commit is not null || scenarios.Count > 0)
                throw new InvalidOperationException("Standalone command modes can't be combined with benchmark arguments.");

            return new CommandLine(baseline, output, commit, scenarios, selfTest, list, help);
        }

        /// <summary>Read the value following an argument.</summary>
        private static string GetValue(string[] args, ref int index, string argument)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                throw new InvalidOperationException($"Argument '{argument}' requires a value.");
            return args[index];
        }
    }
}
