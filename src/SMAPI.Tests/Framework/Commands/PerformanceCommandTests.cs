using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Commands;
using StardewModdingAPI.Framework.Performance;

namespace SMAPI.Tests.Framework.Commands;

/// <summary>Unit tests for <see cref="PerformanceCommand"/>.</summary>
[TestFixture]
internal sealed class PerformanceCommandTests
{
    [Test]
    public void Start_WithThreshold_EnablesSamplingAndTickLogging()
    {
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0);
        PerformanceCommand command = new(manager);
        Mock<IMonitor> monitor = new();

        command.HandleCommand(["start", "12.5"], monitor.Object);

        ModPerformanceSnapshot snapshot = manager.GetSnapshot();
        snapshot.IsTracking.Should().BeTrue();
        snapshot.LogIndividualTicks.Should().BeTrue();
        snapshot.TickLogThresholdMilliseconds.Should().Be(12.5);
        monitor.Verify(instance => instance.Log(It.Is<string>(message => message.Contains("Started a fresh")), LogLevel.Info), Times.Once);
    }

    [TestCase("-1")]
    [TestCase("NaN")]
    [TestCase("not-a-number")]
    public void Start_WithInvalidThreshold_DoesNotEnableSampling(string threshold)
    {
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0);
        PerformanceCommand command = new(manager);
        Mock<IMonitor> monitor = new();

        command.HandleCommand(["start", threshold], monitor.Object);

        manager.IsTracking.Should().BeFalse();
        monitor.Verify(instance => instance.Log(It.IsAny<string>(), LogLevel.Error), Times.Once);
    }

    [Test]
    public void Stop_StopsAndWritesFinalReport()
    {
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0);
        PerformanceCommand command = new(manager);
        List<string> messages = [];
        Mock<IMonitor> monitor = new();
        monitor.Setup(instance => instance.Log(It.IsAny<string>(), It.IsAny<LogLevel>()))
            .Callback<string, LogLevel>((message, _) => messages.Add(message));
        manager.Start();
        manager.RecordHandler("Example.Mod", "Example Mod", "GameLoop.UpdateTicked", "Example.OnTick", 5, failed: false);

        command.HandleCommand(["stop", "5"], monitor.Object);

        manager.IsTracking.Should().BeFalse();
        messages.Should().ContainSingle(message => message.Contains("Example Mod (Example.Mod)"));
    }
}
