using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Logging;
using StardewModdingAPI.Internal.ConsoleWriting;

namespace SMAPI.Tests.Framework.Health;

[TestFixture]
internal sealed class MonitorHealthLogTests
{
    [Test]
    public void LogImpl_PreservesCallbackAndRecordsOnlySafeNormalizedMetadata()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-health-{Guid.NewGuid():N}.txt");
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: static () => 0);
        LogLevel? callbackLevel = null;
        using (LogFileManager logFile = new(path))
        {
            Monitor monitor = new("example.mod", "Example Mod", logFile, new Mock<IConsoleWriter>().Object, static () => null,
                (_, _, level) => callbackLevel = level,
                ledger.RegisterLogSource("example.mod", "Example Mod", ModHealthLogSourceCategory.Mod))
            {
                WriteToConsole = false
            };

            monitor.LogFatal("not retained raw text");
        }

        callbackLevel.Should().Be(LogLevel.Error);
        ModHealthLogSourceSnapshot source = ledger.GetSnapshot().LogSources.Single();
        source.SinceLedgerStart.GetMessages(LogLevel.Error).Should().Be(1);
        source.SinceLedgerStart.GetCharacters(LogLevel.Error).Should().Be("not retained raw text".Length);
        ledger.GetSnapshot().ToString().Should().NotContain("not retained raw text");
        File.Delete(path);
    }

    [Test]
    public void DeferredUserInputAndNewline_AreExcludedAndReporterScopeSuppressesNormalLog()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-health-{Guid.NewGuid():N}.txt");
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: static () => 0);
        using (LogFileManager logFile = new(path))
        {
            Monitor monitor = new("SMAPI", "SMAPI", logFile, new Mock<IConsoleWriter>().Object, static () => null,
                healthLogCounter: ledger.RegisterLogSource("SMAPI", "SMAPI", ModHealthLogSourceCategory.Smapi))
            {
                WriteToConsole = false
            };

            monitor.LogDeferred(1, static _ => "deferred", LogLevel.Info);
            monitor.LogUserInput("private input");
            monitor.Newline();
            using (ledger.SuppressReporterLogs())
                monitor.Log("report completion", LogLevel.Info);
        }

        ledger.GetSnapshot().LogTotalsSinceLedgerStart.GetMessages(LogLevel.Info).Should().Be(0);
        ledger.GetSnapshot().LogSources.Should().BeEmpty();
        File.Delete(path);
    }
}
