using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Commands;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Performance;

namespace SMAPI.Tests.Framework.Commands;

/// <summary>Unit tests for <see cref="PerformanceCommand"/>.</summary>
[TestFixture]
internal sealed class PerformanceCommandTests
{
    [Test]
    public void Description_ExplainsMeasuredSmapiUpdateDispatchWithoutCpuOrCausationClaims()
    {
        PerformanceCommand command = new(new ModPerformanceManager(timestampFrequency: 1000, getTimestamp: () => 0));

        command.Description.Should().Contain("SMAPI update dispatch observed outside the base-game update");
        command.Description.Should().Contain("not total SMAPI CPU or proof of cause");
        command.Description.Should().Contain("waiting, scheduling, and unobserved nested work");
        command.Description.Should().NotContain("SMAPI dispatch & other time");
    }

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

    [Test]
    public void Start_WithCoordinator_DoesNotReplaceHealthOwnedCapture()
    {
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: _ => 0);
        ModHealthSessionCoordinator coordinator = new(manager, new ModHealthLedger(timestampFrequency: 1000, getTimestamp: () => 0), new NoOpExportQueue());
        PerformanceCommand command = new(coordinator);
        Mock<IMonitor> monitor = new();
        coordinator.StartHealth();

        command.HandleCommand(["start"], monitor.Object);

        coordinator.GetStatus().Owner.Should().Be(ModHealthCaptureOwner.Health);
        monitor.Verify(instance => instance.Log(It.IsAny<string>(), LogLevel.Error), Times.Once);
    }

    [Test]
    public void Reset_WithCoordinator_RefusesHealthOwnedCapture()
    {
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: _ => 0);
        ModHealthSessionCoordinator coordinator = new(manager, new ModHealthLedger(timestampFrequency: 1000, getTimestamp: () => 0), new NoOpExportQueue());
        PerformanceCommand command = new(coordinator);
        Mock<IMonitor> monitor = new();
        coordinator.StartHealth();

        command.HandleCommand(["reset"], monitor.Object);

        coordinator.GetStatus().CaptureState.Should().Be(ModHealthCaptureState.Active);
        monitor.Verify(instance => instance.Log(It.IsAny<string>(), LogLevel.Error), Times.Once);
    }

    private sealed class NoOpExportQueue : IModHealthExportQueue
    {
        public ModHealthExportQueueResult Enqueue(ModHealthExportRequest request)
        {
            ModHealthExportStatus status = new(ModHealthExportState.Queued, request.RequestId, request.IsFinal);
            return new ModHealthExportQueueResult(ModHealthExportDisposition.Queued, status);
        }

        public ModHealthExportQueueResult Retry(System.Guid? requestId = null) => new(ModHealthExportDisposition.NoRetryableExport, ModHealthExportStatus.None);
        public void DiscardRetryable(System.Guid? requestId = null) { }
        public ModHealthExportStatus GetStatus(System.Guid? requestId = null) => ModHealthExportStatus.None;
        public ModHealthPreparedReportSnapshot GetPreparedReport(System.Guid? requestId = null) => ModHealthPreparedReportSnapshot.Absent;
    }
}
