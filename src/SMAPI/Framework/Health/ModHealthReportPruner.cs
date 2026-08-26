using System;
using System.Collections.Immutable;
using System.Linq;

namespace StardewModdingAPI.Framework.Health;

/// <summary>Applies deterministic schema-v1 output-size pruning without changing mandatory evidence.</summary>
internal sealed class ModHealthReportPruner
{
    /// <summary>Try to apply the next pruning stage.</summary>
    public bool TryPrune(ModHealthReport report, out ModHealthReport pruned)
    {
        ModHealthPerformance performance = report.Performance;
        if (performance.RecentUpdates.Length > 0)
        {
            pruned = report with
            {
                Header = report.Header with { IsTruncated = true },
                Performance = performance with { RecentUpdates = ImmutableArray<ModHealthUpdate>.Empty },
                Omissions = ModHealthReportPruner.AddOmission(report.Omissions, "recentUpdates", performance.RecentUpdates.Length)
            };
            return true;
        }

        if (performance.Callbacks.Length > 20)
        {
            ImmutableArray<ModHealthCallback> retained = performance.Callbacks.Take(20).ToImmutableArray();
            pruned = report with
            {
                Header = report.Header with { IsTruncated = true },
                Performance = performance with { Callbacks = retained },
                Omissions = ModHealthReportPruner.AddOmission(report.Omissions, "callbacks", performance.Callbacks.Length - retained.Length)
            };
            return true;
        }

        ImmutableArray<ModHealthMod> withoutIgnored = report.Mods.Where(mod => mod.Status is not ModHealthModStatus.Ignored).ToImmutableArray();
        if (withoutIgnored.Length != report.Mods.Length)
        {
            pruned = report with
            {
                Header = report.Header with { IsTruncated = true },
                Mods = withoutIgnored,
                Omissions = ModHealthReportPruner.AddOmission(report.Omissions, "ignoredMods", report.Mods.Length - withoutIgnored.Length)
            };
            return true;
        }

        ImmutableArray<ModHealthMod> withoutHealthyPacks = report.Mods
            .Where(mod => mod.Kind is not ModHealthModKind.ContentPack || mod.Status is not ModHealthModStatus.Loaded || mod.SessionErrorCount > 0 || mod.CallbackFailureCount > 0)
            .ToImmutableArray();
        if (withoutHealthyPacks.Length != report.Mods.Length)
        {
            pruned = report with
            {
                Header = report.Header with { IsTruncated = true },
                Mods = withoutHealthyPacks,
                Omissions = ModHealthReportPruner.AddOmission(report.Omissions, "healthyContentPacks", report.Mods.Length - withoutHealthyPacks.Length)
            };
            return true;
        }

        if (report.Environment.LinuxDistribution is not null || report.Environment.Kernel is not null || report.Environment.SmapiCommit is not null)
        {
            pruned = report with
            {
                Header = report.Header with { IsTruncated = true },
                Environment = report.Environment with { LinuxDistribution = null, Kernel = null, SmapiCommit = null },
                Omissions = ModHealthReportPruner.AddOmission(report.Omissions, "optionalEnvironment", 1)
            };
            return true;
        }

        pruned = report;
        return false;
    }

    /// <summary>Create the bounded minimal fallback DTO.</summary>
    public ModHealthReport CreateMinimalFallback(ModHealthReport report)
    {
        long omittedMods = report.Mods.Length;
        long omittedCallbacks = report.Performance.Callbacks.Length;
        long omittedTicks = report.Performance.RecentUpdates.Length + report.Performance.WorstUpdates.Length;
        return report with
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
            Mods = ImmutableArray<ModHealthMod>.Empty,
            Logs = report.Logs.Take(8).ToImmutableArray(),
            Omissions = ModHealthReportPruner.AddOmission(
                ModHealthReportPruner.AddOmission(
                    ModHealthReportPruner.AddOmission(report.Omissions, "minimalFallbackMods", omittedMods),
                    "minimalFallbackCallbacks",
                    omittedCallbacks
                ),
                "minimalFallbackUpdates",
                omittedTicks
            )
        };
    }

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

        return omissions.SetItem(index, omissions[index] with { Count = omissions[index].Count + count });
    }
}
