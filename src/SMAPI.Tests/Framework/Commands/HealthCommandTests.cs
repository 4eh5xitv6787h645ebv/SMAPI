using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Commands;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Performance;

namespace SMAPI.Tests.Framework.Commands;

/// <summary>Tests for <see cref="HealthCommand"/>.</summary>
[TestFixture]
internal sealed class HealthCommandTests
{
    [Test]
    public void BareCommand_ExplainsNextStep()
    {
        (HealthCommand command, _, Mock<IMonitor> monitor, List<string> messages) = Create();

        command.HandleCommand([], monitor.Object);

        messages.Should().ContainSingle(message => message.Contains("health start"));
    }

    [Test]
    public void Mark_RejectsFreeTextWithoutAddingMark()
    {
        (HealthCommand command, ModHealthSessionCoordinator coordinator, Mock<IMonitor> monitor, _) = Create();
        command.HandleCommand(["start"], monitor.Object);

        command.HandleCommand(["mark", "my save name"], monitor.Object);

        coordinator.GetStatus().MarkCount.Should().Be(0);
        monitor.Verify(instance => instance.Log(It.Is<string>(message => message.Contains("free text")), LogLevel.Error), Times.Once);
    }

    [Test]
    public void Reset_WithoutConfirmOnlyExplainsDestructiveAction()
    {
        (HealthCommand command, ModHealthSessionCoordinator coordinator, Mock<IMonitor> monitor, _) = Create();
        command.HandleCommand(["start"], monitor.Object);

        command.HandleCommand(["reset"], monitor.Object);

        coordinator.GetStatus().CaptureState.Should().Be(ModHealthCaptureState.Active);
        monitor.Verify(instance => instance.Log(It.Is<string>(message => message.Contains("health reset confirm")), LogLevel.Warn), Times.Once);
    }

    [Test]
    public void Status_SeparatesSessionLedgerAndTimedCapture()
    {
        (HealthCommand command, _, Mock<IMonitor> monitor, List<string> messages) = Create();

        command.HandleCommand(["status"], monitor.Object);

        messages.Should().ContainSingle(message => message.Contains("Session ledger:") && message.Contains("Timed capture:"));
    }

    private static (HealthCommand Command, ModHealthSessionCoordinator Coordinator, Mock<IMonitor> Monitor, List<string> Messages) Create()
    {
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: _ => 0);
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: () => 0);
        TestExportQueue queue = new();
        ModHealthSessionCoordinator coordinator = new(manager, ledger, queue);
        List<string> messages = [];
        Mock<IMonitor> monitor = new();
        monitor.Setup(instance => instance.Log(It.IsAny<string>(), It.IsAny<LogLevel>()))
            .Callback<string, LogLevel>((message, _) => messages.Add(message));
        return (new HealthCommand(coordinator), coordinator, monitor, messages);
    }

    private sealed class TestExportQueue : IModHealthExportQueue
    {
        public ModHealthExportQueueResult Enqueue(ModHealthExportRequest request)
        {
            ModHealthExportStatus status = new(ModHealthExportState.Queued, request.RequestId, request.IsFinal);
            return new ModHealthExportQueueResult(ModHealthExportDisposition.Queued, status);
        }

        public ModHealthExportQueueResult Retry() => new(ModHealthExportDisposition.NoRetryableExport, ModHealthExportStatus.None);
        public void DiscardRetryable() { }
        public ModHealthExportStatus GetStatus(System.Guid? requestId = null) => ModHealthExportStatus.None;
    }
}
