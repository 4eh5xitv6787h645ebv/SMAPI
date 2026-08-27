using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Commands;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Health.Viewer;
using StardewModdingAPI.Framework.Performance;

namespace SMAPI.Tests.Framework.Commands;

/// <summary>Tests for <see cref="HealthCommand"/>.</summary>
[TestFixture]
internal sealed class HealthCommandTests
{
    [Test]
    public void BareCommand_ExplainsNextStep()
    {
        (HealthCommand command, _, Mock<IMonitor> monitor, List<string> messages, _) = Create();

        command.HandleCommand([], monitor.Object);

        messages.Should().ContainSingle(message => message.Contains("health start") && message.Contains("health view"));
        command.Description.Should().Contain("health view").And.Contain("at most five complete report pairs").And.Contain("30 days");
    }

    [TestCase(ModHealthViewerActionDisposition.Queued, "next safe game update", LogLevel.Info)]
    [TestCase(ModHealthViewerActionDisposition.Coalesced, "already open or queued", LogLevel.Info)]
    [TestCase(ModHealthViewerActionDisposition.RejectedFull, "queue is full", LogLevel.Error)]
    public void View_QueuesInjectedScreenLocalOpen(ModHealthViewerActionDisposition disposition, string expectedMessage, LogLevel expectedLevel)
    {
        int calls = 0;
        (HealthCommand command, _, Mock<IMonitor> monitor, List<string> messages, _) = Create(() =>
        {
            calls++;
            return disposition;
        });

        command.HandleCommand(["view"], monitor.Object);

        calls.Should().Be(1);
        messages.Should().ContainSingle(message => message.Contains(expectedMessage));
        monitor.Verify(instance => instance.Log(It.Is<string>(message => message.Contains(expectedMessage)), expectedLevel), Times.Once);
    }

    [Test]
    public void View_WithoutLinuxViewerExplainsAvailability()
    {
        (HealthCommand command, _, Mock<IMonitor> monitor, List<string> messages, _) = Create();

        command.HandleCommand(["view"], monitor.Object);

        messages.Should().ContainSingle(message => message.Contains("only available in Linux desktop SMAPI"));
        monitor.Verify(instance => instance.Log(It.IsAny<string>(), LogLevel.Error), Times.Once);
    }

    [Test]
    public void StartReportAndStop_UseOneCaptureAndQueueInterimThenFinalSnapshots()
    {
        (HealthCommand command, ModHealthSessionCoordinator coordinator, Mock<IMonitor> monitor, List<string> messages, TestExportQueue queue) = Create();

        command.HandleCommand(["start"], monitor.Object);
        command.HandleCommand(["report"], monitor.Object);
        command.HandleCommand(["stop"], monitor.Object);

        coordinator.GetStatus().CaptureState.Should().Be(ModHealthCaptureState.StoppedRetained);
        queue.Requests.Should().HaveCount(2);
        queue.Requests[0].IsFinal.Should().BeFalse();
        queue.Requests[0].CompletionReason.Should().Be(ModHealthCompletionReason.InterimReport);
        queue.Requests[1].IsFinal.Should().BeTrue();
        queue.Requests[1].CompletionReason.Should().Be(ModHealthCompletionReason.UserStop);
        messages.Should().Contain(message => message.Contains("Recording a mod health sample"));
        messages.Should().Contain(message => message.Contains("interim timing snapshot"));
        messages.Should().Contain(message => message.Contains("Final health report queued"));
    }

    [Test]
    public void Retry_PresentsExactFrozenRetryAndNoRetryableErrors()
    {
        (HealthCommand command, _, Mock<IMonitor> monitor, List<string> messages, TestExportQueue queue) = Create();
        queue.SetRetryResult(ModHealthExportDisposition.Retried, ModHealthExportState.Queued);

        command.HandleCommand(["retry"], monitor.Object);

        messages.Should().ContainSingle(message => message.Contains("Retrying the exact frozen health report"));
        messages.Clear();
        queue.SetRetryResult(ModHealthExportDisposition.NoRetryableExport, ModHealthExportState.None);

        command.HandleCommand(["retry"], monitor.Object);

        messages.Should().ContainSingle(message => message.Contains("no failed frozen health report"));
        monitor.Verify(instance => instance.Log(It.Is<string>(message => message.Contains("no failed frozen health report")), LogLevel.Error), Times.Once);
    }

    [TestCase("start")]
    [TestCase("status")]
    [TestCase("view")]
    [TestCase("report")]
    [TestCase("stop")]
    [TestCase("retry")]
    public void Actions_RejectExtraArguments(string action)
    {
        (HealthCommand command, ModHealthSessionCoordinator coordinator, Mock<IMonitor> monitor, _, TestExportQueue queue) = Create();

        command.HandleCommand([action, "unexpected"], monitor.Object);

        coordinator.GetStatus().CaptureState.Should().Be(ModHealthCaptureState.Inactive);
        queue.Requests.Should().BeEmpty();
        monitor.Verify(instance => instance.Log(It.Is<string>(message => message.StartsWith("Usage: health")), LogLevel.Error), Times.Once);
    }

    [Test]
    public void UnknownAction_ListsValidActionsWithoutChangingState()
    {
        (HealthCommand command, ModHealthSessionCoordinator coordinator, Mock<IMonitor> monitor, _, TestExportQueue queue) = Create();

        command.HandleCommand(["private-unknown"], monitor.Object);

        coordinator.GetStatus().CaptureState.Should().Be(ModHealthCaptureState.Inactive);
        queue.Requests.Should().BeEmpty();
        monitor.Verify(instance => instance.Log(It.Is<string>(message => message.Contains("Unknown health action") && message.Contains("reset confirm")), LogLevel.Error), Times.Once);
    }

    [Test]
    public void ScreenArgument_IsConsumedByCommandManagerAndUsesTheSingleGlobalSession()
    {
        int[] previousScreenIds = [.. Context.ActiveScreenIds];
        try
        {
            Context.ActiveScreenIds.Clear();
            Context.ActiveScreenIds.Add(0);
            Context.ActiveScreenIds.Add(1);
            (HealthCommand health, ModHealthSessionCoordinator coordinator, Mock<IMonitor> monitor, _, _) = Create();
            CommandManager commands = new(monitor.Object);
            commands.Add(health, monitor.Object);

            commands.TryParse("health start screen=1", out string? firstName, out string[]? firstArgs, out Command? first, out int firstScreen).Should().BeTrue();
            first!.Callback(firstName!, firstArgs!);
            commands.TryParse("health status screen=0", out string? secondName, out string[]? secondArgs, out Command? second, out int secondScreen).Should().BeTrue();
            second!.Callback(secondName!, secondArgs!);

            firstScreen.Should().Be(1);
            secondScreen.Should().Be(0);
            coordinator.GetStatus().CaptureState.Should().Be(ModHealthCaptureState.Active);
            coordinator.GetStatus().Owner.Should().Be(ModHealthCaptureOwner.Health);
        }
        finally
        {
            Context.ActiveScreenIds.Clear();
            foreach (int screenId in previousScreenIds)
                Context.ActiveScreenIds.Add(screenId);
        }
    }

    [Test]
    public void Mark_RejectsFreeTextWithoutAddingMark()
    {
        (HealthCommand command, ModHealthSessionCoordinator coordinator, Mock<IMonitor> monitor, _, _) = Create();
        command.HandleCommand(["start"], monitor.Object);

        command.HandleCommand(["mark", "my save name"], monitor.Object);

        coordinator.GetStatus().MarkCount.Should().Be(0);
        monitor.Verify(instance => instance.Log(It.Is<string>(message => message.Contains("free text")), LogLevel.Error), Times.Once);
    }

    [Test]
    public void Reset_WithoutConfirmOnlyExplainsDestructiveAction()
    {
        (HealthCommand command, ModHealthSessionCoordinator coordinator, Mock<IMonitor> monitor, _, _) = Create();
        command.HandleCommand(["start"], monitor.Object);

        command.HandleCommand(["reset"], monitor.Object);

        coordinator.GetStatus().CaptureState.Should().Be(ModHealthCaptureState.Active);
        monitor.Verify(instance => instance.Log(It.Is<string>(message => message.Contains("health reset confirm")), LogLevel.Warn), Times.Once);
    }

    [Test]
    public void Status_SeparatesSessionLedgerAndTimedCapture()
    {
        (HealthCommand command, _, Mock<IMonitor> monitor, List<string> messages, _) = Create();

        command.HandleCommand(["status"], monitor.Object);

        messages.Should().ContainSingle(message => message.Contains("Session ledger:") && message.Contains("Timed capture:") && message.Contains("health view"));
    }

    [Test]
    public void BareCommand_AfterSuccessfulRetainedExportShowsPathsAndFreshStart()
    {
        (HealthCommand command, _, Mock<IMonitor> monitor, List<string> messages, TestExportQueue queue) = Create();
        command.HandleCommand(["start"], monitor.Object);
        command.HandleCommand(["stop"], monitor.Object);
        queue.SetLastStatus(ModHealthExportState.Succeeded, "ErrorLogs/HealthReports/report.txt", "ErrorLogs/HealthReports/report.json");
        messages.Clear();

        command.HandleCommand([], monitor.Object);

        messages.Should().ContainSingle(message => message.Contains("ErrorLogs/HealthReports/report.txt") && message.Contains("health start"));
        messages.Should().NotContain(message => message.Contains("health report' to save"));
    }

    private static (HealthCommand Command, ModHealthSessionCoordinator Coordinator, Mock<IMonitor> Monitor, List<string> Messages, TestExportQueue Queue) Create(Func<ModHealthViewerActionDisposition>? queueViewerOpen = null)
    {
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: _ => 0);
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: () => 0);
        TestExportQueue queue = new();
        ModHealthSessionCoordinator coordinator = new(manager, ledger, queue);
        List<string> messages = [];
        Mock<IMonitor> monitor = new();
        monitor.Setup(instance => instance.Log(It.IsAny<string>(), It.IsAny<LogLevel>()))
            .Callback<string, LogLevel>((message, _) => messages.Add(message));
        return (new HealthCommand(coordinator, queueViewerOpen), coordinator, monitor, messages, queue);
    }

    private sealed class TestExportQueue : IModHealthExportQueue
    {
        private ModHealthExportStatus Status = ModHealthExportStatus.None;
        private ModHealthExportQueueResult RetryResult = new(ModHealthExportDisposition.NoRetryableExport, ModHealthExportStatus.None);

        public List<ModHealthExportRequest> Requests { get; } = [];

        public ModHealthExportQueueResult Enqueue(ModHealthExportRequest request)
        {
            this.Requests.Add(request);
            ModHealthExportStatus status = new(ModHealthExportState.Queued, request.RequestId, request.IsFinal);
            this.Status = status;
            return new ModHealthExportQueueResult(ModHealthExportDisposition.Queued, status);
        }

        public ModHealthExportQueueResult Retry(System.Guid? requestId = null) => this.RetryResult;
        public void DiscardRetryable(System.Guid? requestId = null) { }
        public ModHealthExportStatus GetStatus(System.Guid? requestId = null) => requestId is null || requestId == this.Status.RequestId ? this.Status : ModHealthExportStatus.None;
        public ModHealthPreparedReportSnapshot GetPreparedReport(System.Guid? requestId = null) => ModHealthPreparedReportSnapshot.Absent;

        public void SetLastStatus(ModHealthExportState state, string? textPath = null, string? jsonPath = null)
        {
            this.Status = this.Status with { State = state, TextPath = textPath, JsonPath = jsonPath };
        }

        public void SetRetryResult(ModHealthExportDisposition disposition, ModHealthExportState state)
        {
            ModHealthExportStatus status = new(state, System.Guid.NewGuid(), IsFinal: true);
            this.RetryResult = new ModHealthExportQueueResult(disposition, status);
            this.Status = status;
        }
    }
}
