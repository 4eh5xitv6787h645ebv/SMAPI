using System;
using System.Collections.Immutable;
using System.Linq;

namespace StardewModdingAPI.Framework.Health;

/// <summary>Applies deterministic schema-v1 output-size pruning without changing mandatory evidence.</summary>
internal sealed class ModHealthReportPruner
{
    private const int MaxMinimalFallbackMods = 20;

    /// <summary>Try to apply the next pruning stage.</summary>
    public bool TryPrune(ModHealthReport report, out ModHealthReport pruned)
    {
        ModHealthPerformance performance = report.Performance;
        if (performance.RecentUpdates.Length > 0)
        {
            pruned = ModHealthReportPruner.AddTruncationFinding(report with
            {
                Header = report.Header with { IsTruncated = true },
                Performance = performance with { RecentUpdates = ImmutableArray<ModHealthUpdate>.Empty },
                Omissions = ModHealthReportPruner.AddOmission(report.Omissions, "recentUpdates", performance.RecentUpdates.Length)
            });
            return true;
        }

        if (performance.Callbacks.Length > 20)
        {
            ImmutableArray<ModHealthCallback> retained = performance.Callbacks.Take(20).ToImmutableArray();
            pruned = ModHealthReportPruner.AddTruncationFinding(report with
            {
                Header = report.Header with { IsTruncated = true },
                Performance = performance with { Callbacks = retained },
                Omissions = ModHealthReportPruner.AddOmission(report.Omissions, "callbacks", performance.Callbacks.Length - retained.Length)
            });
            return true;
        }

        ImmutableArray<ModHealthMod> withoutIgnored = report.Mods.Where(mod => mod.Status is not ModHealthModStatus.Ignored).ToImmutableArray();
        if (withoutIgnored.Length != report.Mods.Length)
        {
            pruned = ModHealthReportPruner.AddTruncationFinding(report with
            {
                Header = report.Header with { IsTruncated = true },
                Mods = withoutIgnored,
                ModInventory = report.ModInventory with { Retained = withoutIgnored.Length },
                Omissions = ModHealthReportPruner.AddOmission(report.Omissions, "ignoredMods", report.Mods.Length - withoutIgnored.Length)
            });
            return true;
        }

        ImmutableArray<ModHealthMod> withoutHealthyPacks = report.Mods
            .Where(mod => mod.Kind is not ModHealthModKind.ContentPack || mod.Status is not ModHealthModStatus.Loaded || mod.SessionErrorCount > 0 || mod.CallbackFailureCount > 0)
            .ToImmutableArray();
        if (withoutHealthyPacks.Length != report.Mods.Length)
        {
            pruned = ModHealthReportPruner.AddTruncationFinding(report with
            {
                Header = report.Header with { IsTruncated = true },
                Mods = withoutHealthyPacks,
                ModInventory = report.ModInventory with { Retained = withoutHealthyPacks.Length },
                Omissions = ModHealthReportPruner.AddOmission(report.Omissions, "healthyContentPacks", report.Mods.Length - withoutHealthyPacks.Length)
            });
            return true;
        }

        if (report.Environment.LinuxDistribution is not null || report.Environment.Kernel is not null || report.Environment.SmapiCommit is not null)
        {
            pruned = ModHealthReportPruner.AddTruncationFinding(report with
            {
                Header = report.Header with { IsTruncated = true },
                Environment = report.Environment with { LinuxDistribution = null, Kernel = null, SmapiCommit = null },
                Omissions = ModHealthReportPruner.AddOmission(report.Omissions, "optionalEnvironment", 1)
            });
            return true;
        }

        pruned = report;
        return false;
    }

    /// <summary>Create the bounded minimal fallback DTO.</summary>
    public ModHealthReport CreateMinimalFallback(ModHealthReport report)
    {
        ImmutableArray<ModHealthMod> selectedMods = report.Mods
            .OrderBy(mod => ModHealthReportPruner.IsProblemMod(mod) ? 0 : 1)
            .ThenBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.Id, StringComparer.Ordinal)
            .Take(ModHealthReportPruner.MaxMinimalFallbackMods)
            .ToImmutableArray();
        long omittedDependencies = selectedMods.Sum(mod => (long)mod.Dependencies.Length);
        long omittedWarningFlags = selectedMods.Sum(mod => (long)mod.WarningFlags.Length);
        ImmutableArray<ModHealthMod> retainedMods = selectedMods
            .Select(mod => mod with
            {
                Dependencies = ImmutableArray<string>.Empty,
                WarningFlags = ImmutableArray<string>.Empty
            })
            .ToImmutableArray();
        long omittedMods = report.Mods.Length - retainedMods.Length;
        long omittedCallbacks = report.Performance.Callbacks.Length;
        long omittedTicks = report.Performance.RecentUpdates.Length + report.Performance.WorstUpdates.Length;
        long omittedLogs = Math.Max(0, report.Logs.Length - 8);
        long omittedCallbackFailures = report.CallbackFailures.Length;
        return ModHealthReportPruner.AddTruncationFinding(report with
        {
            Header = report.Header with { IsTruncated = true, IsMinimalFallback = true },
            Environment = report.Environment with { SmapiCommit = null, LinuxDistribution = null, Kernel = null },
            Performance = report.Performance with
            {
                Callbacks = ImmutableArray<ModHealthCallback>.Empty,
                WorstUpdates = ImmutableArray<ModHealthUpdate>.Empty,
                RecentUpdates = ImmutableArray<ModHealthUpdate>.Empty,
                Episodes = ImmutableArray<ModHealthEpisode>.Empty
            },
            ModInventory = report.ModInventory with { Retained = retainedMods.Length },
            Mods = retainedMods,
            Logs = report.Logs.Take(8).ToImmutableArray(),
            CallbackFailures = ImmutableArray<ModHealthCallbackFailure>.Empty,
            Omissions = ModHealthReportPruner.AddOmission(
                ModHealthReportPruner.AddOmission(
                    ModHealthReportPruner.AddOmission(
                        ModHealthReportPruner.AddOmission(
                            ModHealthReportPruner.AddOmission(
                                ModHealthReportPruner.AddOmission(
                                    ModHealthReportPruner.AddOmission(report.Omissions, "minimalFallbackMods", omittedMods),
                                    "minimalFallbackDependencies",
                                    omittedDependencies
                                ),
                                "minimalFallbackWarningFlags",
                                omittedWarningFlags
                            ),
                            "minimalFallbackLogs",
                            omittedLogs
                        ),
                        "minimalFallbackCallbackFailures",
                        omittedCallbackFailures
                    ),
                    "minimalFallbackCallbacks",
                    omittedCallbacks
                ),
                "minimalFallbackUpdates",
                omittedTicks
            )
        });
    }

    private static ModHealthReport AddTruncationFinding(ModHealthReport report)
    {
        if (report.Findings.Any(finding => finding.RuleId.Equals("capacity-reached", StringComparison.Ordinal)))
            return report;

        ModHealthFinding finding = new(
            "capacity-reached",
            ModHealthFindingSeverity.Check,
            ModHealthFindingConfidence.Factual,
            null,
            "Some report detail was omitted by a safety limit.",
            "One or more bounded collections reached capacity or were pruned for output size.",
            "Use the aggregate counts and omissions section when interpreting this report.",
            "Omitted detail can hide individual examples, so conclusions should remain cautious."
        );
        ImmutableArray<ModHealthFinding> findings = report.Findings
            .Take(Math.Max(0, ModHealthReportLimits.MaxFindings - 1))
            .Append(finding)
            .OrderBy(entry => ModHealthReportPruner.GetSeverityOrder(entry.Severity))
            .ThenBy(entry => entry.RuleId, StringComparer.Ordinal)
            .ThenBy(entry => entry.ModId, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return report with { Findings = findings };
    }

    private static bool IsProblemMod(ModHealthMod mod)
    {
        return mod.Status is not ModHealthModStatus.Loaded
            || mod.SessionErrorCount > 0
            || mod.CaptureErrorCount > 0
            || mod.CallbackFailureCount > 0;
    }

    private static int GetSeverityOrder(ModHealthFindingSeverity severity) => severity switch
    {
        ModHealthFindingSeverity.ActionNeeded => 0,
        ModHealthFindingSeverity.Performance => 1,
        ModHealthFindingSeverity.Check => 2,
        _ => 3
    };

    private static ImmutableArray<ModHealthOmission> AddOmission(ImmutableArray<ModHealthOmission> omissions, string section, long count)
    {
        if (count <= 0)
            return omissions;

        int index = -1;
        for (int i = 0; i < omissions.Length; i++)
        {
            if (omissions[i].Section.Equals(section, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return omissions.Add(new ModHealthOmission(section, count));

        long existing = Math.Max(0, omissions[index].Count);
        long combined = existing > long.MaxValue - count
            ? long.MaxValue
            : existing + count;
        return omissions.SetItem(index, omissions[index] with { Count = combined });
    }
}
