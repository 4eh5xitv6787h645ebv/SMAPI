using System;
using System.IO;
using FluentAssertions;
using NUnit.Framework;
using SMAPI.Tests.Framework.Health;
using StardewModdingAPI;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Health;

namespace SMAPI.Tests.Core;

/// <summary>Tests the <see cref="SCore"/> integration boundaries for mod health reporting.</summary>
[TestFixture]
internal sealed class SCoreModHealthTests
{
    [Test]
    public void ExportCompletionMessage_PresentsSuccessFallbackFailureAndIgnoresPending()
    {
        ModHealthCompletionSummary summary = ModHealthCompletionSummary.FromReport(ModHealthReportFixtureFactory.CreateCanonical());
        ModHealthExportStatus succeeded = new(
            ModHealthExportState.Succeeded,
            Guid.NewGuid(),
            IsFinal: true,
            "ErrorLogs/HealthReports/report.txt",
            "ErrorLogs/HealthReports/report.json",
            Summary: summary
        );

        (string Message, LogLevel Level)? success = SCore.GetModHealthExportCompletionMessage(succeeded, 80);
        (string Message, LogLevel Level)? fallback = SCore.GetModHealthExportCompletionMessage(succeeded with { Summary = null }, 80);
        (string Message, LogLevel Level)? failure = SCore.GetModHealthExportCompletionMessage(new(ModHealthExportState.Failed, Error: "Report export failed (IOException)."), 80);
        (string Message, LogLevel Level)? pending = SCore.GetModHealthExportCompletionMessage(new(ModHealthExportState.Queued), 80);

        success.Should().NotBeNull();
        success!.Value.Level.Should().Be(LogLevel.Info);
        success.Value.Message.Should().Contain("ErrorLogs/HealthReports/report.txt").And.Contain("at most five complete report pairs");
        fallback.Should().Be(("The mod health report was saved, but its frozen completion summary or relative paths were unavailable. Enter 'health status' for details.", LogLevel.Warn));
        failure.Should().Be(("Report export failed (IOException). Enter 'health retry' to retry the exact frozen report.", LogLevel.Error));
        pending.Should().BeNull();
    }

    [Test]
    public void PurgeNormalLogs_PreservesCrashReportDirectoryAndUnrelatedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"smapi-health-purge-{Guid.NewGuid():N}");
        string crash = Path.Combine(root, "SMAPI-crash.txt");
        string normal = Path.Combine(root, "SMAPI-latest.txt");
        string unrelated = Path.Combine(root, "other.txt");
        string reports = Path.Combine(root, "HealthReports");
        string report = Path.Combine(reports, "SMAPI-health-20260826-120000-report-0011223344556677.json");
        try
        {
            Directory.CreateDirectory(reports);
            File.WriteAllText(crash, "crash");
            File.WriteAllText(normal, "normal");
            File.WriteAllText(unrelated, "unrelated");
            File.WriteAllText(report, "report");

            SCore.PurgeNormalLogs(root, crash);

            File.Exists(normal).Should().BeFalse();
            File.Exists(crash).Should().BeTrue();
            File.Exists(unrelated).Should().BeTrue();
            File.Exists(report).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
