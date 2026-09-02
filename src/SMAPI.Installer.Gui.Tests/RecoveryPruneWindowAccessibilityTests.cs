using System.Threading.Channels;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed class RecoveryPruneWindowAccessibilityTests
{
    [AvaloniaTest]
    public async Task WindowStartsExplicitReadOnlyUnselectedAndKeepsPrivatePathOutOfAutomation()
    {
        const string privatePath = "/home/private-user/secret-game";
        FakePruneSession session = new(privatePath, Points(2));
        RecoveryPruneViewModel viewModel = new(new RecoveryPruneController(session));
        RecoveryPruneWindow window = new(viewModel);
        window.Show();

        Button load = window.FindControl<Button>("LoadButton")!;
        ListBox list = window.FindControl<ListBox>("HistoryList")!;
        Border status = window.FindControl<Border>("StatusRegion")!;
        Border boundary = window.FindControl<Border>("SafetyBoundary")!;
        TextBlock game = window.FindControl<TextBlock>("GameDetail")!;
        Border gameContext = window.FindControl<Border>("GameContextRegion")!;
        await WaitUntilAsync(() => load.IsFocused);

        session.ListCalls.Should().Be(0, "opening the cleanup window must not read recovery history");
        session.InspectCalls.Should().Be(0);
        session.ConfirmCalls.Should().Be(0);
        session.ExecuteCalls.Should().Be(0);
        list.SelectedItem.Should().BeNull("no destructive retention boundary is selected automatically");
        viewModel.SelectedChoice.Should().BeNull();
        ControlAutomationPeer.CreatePeerForElement(status).GetLiveSetting().Should().Be(AutomationLiveSetting.Polite);
        ControlAutomationPeer.CreatePeerForElement(boundary).GetName().Should()
            .Contain("changes no files").And.Contain("separate explicit actions").And.Contain("Cancel");
        AutomationProperties.GetName(gameContext).Should().Be(viewModel.GameAccessibleName).And.NotContain(privatePath);
        ControlAutomationPeer.CreatePeerForElement(game).GetName().Should().NotContain(privatePath);
        game.Text.Should().NotContain(privatePath);
        viewModel.LiveAnnouncement.Should().NotContain(privatePath);

        Control[] accessKeyControls =
        [
            list,
            window.FindControl<Button>("LoadButton")!,
            window.FindControl<Button>("InspectButton")!,
            window.FindControl<CheckBox>("ConsentCheckBox")!,
            window.FindControl<Button>("CancelButton")!,
            window.FindControl<Button>("ConfirmButton")!,
            window.FindControl<Button>("RunButton")!,
            window.FindControl<Button>("ExitButton")!
        ];
        string?[] keys = accessKeyControls.Select(AutomationProperties.GetAccessKey).ToArray();
        keys.Should().OnlyHaveUniqueItems().And.NotContainNulls();
        window.MinWidth.Should().Be(420);

        Press(window, Key.Escape);
        await WaitUntilAsync(() => !window.IsVisible);
        session.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task ExactBoundaryRequiresUncheckedConsentThenSeparateConfirmAndNonDefaultRun()
    {
        FakePruneSession session = new("/games/Stardew Valley", Points(2), InstallerBackendSettlement.Unconfirmed);
        RecoveryPruneViewModel viewModel = new(new RecoveryPruneController(session));
        RecoveryPruneWindow window = new(viewModel);
        window.Show();
        await WaitUntilAsync(() => window.FindControl<Button>("LoadButton")!.IsFocused);

        await viewModel.ListCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        ListBox list = window.FindControl<ListBox>("HistoryList")!;
        Button inspect = window.FindControl<Button>("InspectButton")!;
        Button cancel = window.FindControl<Button>("CancelButton")!;
        Button confirm = window.FindControl<Button>("ConfirmButton")!;
        Button run = window.FindControl<Button>("RunButton")!;
        CheckBox consent = window.FindControl<CheckBox>("ConsentCheckBox")!;

        viewModel.Choices.Should().HaveCount(2);
        list.SelectedItem.Should().BeNull();
        inspect.IsEffectivelyEnabled.Should().BeFalse();
        list.ItemsPanel.Should().NotBeNull();
        list.SelectedItem = viewModel.Choices[0];
        await WaitUntilAsync(() => inspect.IsEffectivelyEnabled);
        session.InspectCalls.Should().Be(0, "selection is local and read-only");

        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.IsPlanVisible.Should().BeTrue();
        AutomationProperties.GetName(list).Should().Contain("Oldest recovery point to keep");
        viewModel.PlanRows.Select(row => row.Label).Should().Contain(["Retained recovery points", "Older points removed"]);
        consent.IsChecked.Should().BeFalse("destructive consent must never be preselected");
        confirm.IsVisible.Should().BeTrue();
        confirm.IsEffectivelyEnabled.Should().BeFalse();
        run.IsVisible.Should().BeFalse();
        cancel.TabIndex.Should().BeLessThan(confirm.TabIndex).And.BeLessThan(run.TabIndex);

        consent.IsChecked = true;
        await WaitUntilAsync(() => confirm.IsEffectivelyEnabled);
        await viewModel.ConfirmCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        session.ConfirmCalls.Should().Be(1);
        session.ExecuteCalls.Should().Be(0, "confirmation must not run cleanup");
        run.IsVisible.Should().BeTrue();
        await WaitUntilAsync(() => cancel.IsFocused);
        window.FindControl<Border>("StatusRegion")!.Focus();
        Press(window, Key.Enter);
        session.ExecuteCalls.Should().Be(0, "Run has no default Enter activation");

        await viewModel.RunCommand.ExecuteAsync();
        await WaitUntilAsync(() => viewModel.IsResultVisible);
        session.ExecuteCalls.Should().Be(1);
        viewModel.ResultRows.Should().NotBeEmpty();
        Border settlementWarning = window.FindControl<Border>("SettlementWarningRegion")!;
        settlementWarning.IsVisible.Should().BeTrue();
        AutomationProperties.GetName(settlementWarning).Should()
            .Contain("did not confirm a clean close")
            .And.Contain("fresh verified installer session");

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task EscapeDuringActiveLoadRequestsCancellationOnceAndKeepsWindowOpenUntilSettlement()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePruneSession session = new("/games/Stardew Valley", Points(1))
        {
            RecoveryCatalog = async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new AssertionException("The synthetic recovery-history request wasn't cancelled.");
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            }
        };
        RecoveryPruneViewModel viewModel = new(new RecoveryPruneController(session));
        RecoveryPruneWindow window = new(viewModel);
        window.Show();
        await WaitUntilAsync(() => window.FindControl<Button>("LoadButton")!.IsFocused);

        Task load = viewModel.ListCommand.ExecuteAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Press(window, Key.Escape);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        window.IsVisible.Should().BeTrue("the window must remain present while cancellation settles");
        await load.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.IsExitVisible);
        Press(window, Key.Escape);
        await WaitUntilAsync(() => !window.IsVisible);
        session.ListCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task EveryVisibleStateHasOneActiveLiveRegionAndResponsiveLayoutRemainsScrollable()
    {
        FakePruneSession session = new("/games/Stardew Valley", Points(2));
        RecoveryPruneViewModel viewModel = new(new RecoveryPruneController(session));
        RecoveryPruneWindow window = new(viewModel);
        window.Width = 420;
        window.Height = 600;
        window.ApplyResponsiveLayout(420);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        Control[] liveRegions =
        [
            window.FindControl<Border>("StatusRegion")!,
            window.FindControl<Border>("ErrorRegion")!,
            window.FindControl<Border>("StageLiveRegion")!,
            window.FindControl<Border>("ReviewRegion")!,
            window.FindControl<Border>("ResultRegion")!
        ];
        int ActiveLiveRegionCount() => liveRegions.Count(control =>
            control.IsEffectivelyVisible
            && ControlAutomationPeer.CreatePeerForElement(control).GetLiveSetting() != AutomationLiveSetting.Off
        );

        ActiveLiveRegionCount().Should().Be(1);
        await viewModel.ListCommand.ExecuteAsync();
        viewModel.SelectedChoice = viewModel.Choices[0];
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        ActiveLiveRegionCount().Should().Be(1, "plan review is announced by one region");

        viewModel.IsDestructiveConsentChecked = true;
        await viewModel.ConfirmCommand.ExecuteAsync();
        await viewModel.RunCommand.ExecuteAsync();
        await WaitUntilAsync(() => viewModel.IsResultVisible);
        Dispatcher.UIThread.RunJobs();
        ActiveLiveRegionCount().Should().Be(1, "the exact terminal result is announced once");

        window.IsNarrowLayout.Should().BeTrue();
        window.FindControl<ScrollViewer>("PageScrollViewer")!.HorizontalScrollBarVisibility.Should().Be(ScrollBarVisibility.Disabled);
        window.CaptureRenderedFrame().Should().NotBeNull();

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    private static BoundInstallerRecoveryPoint[] Points(int count)
    {
        ProtocolReleaseIdentity release = GameDiscoveryControllerTests.Release();
        return Enumerable.Range(1, count)
            .Select(ordinal => new BoundInstallerRecoveryPoint(
                ordinal,
                ordinal == 1,
                false,
                InstallerOperation.Update,
                new BoundInstallerRecoveryReleaseTarget(release.Tag, release.EmbeddedVersion)
            ))
            .ToArray();
    }

    private static BoundInstallerRecoveryPrunePlanSuccess Plan(int retainNewest, int catalogCount) => new(
        retainNewest,
        retainNewest,
        catalogCount - retainNewest,
        catalogCount - retainNewest,
        false,
        1,
        [ProtocolPlanRisk.RecoveryPrune],
        ProtocolRecommendedDefault.Cancel,
        true
    )
    {
        Confirmation = new BoundInstallerRecoveryPruneConfirmation()
    };

    private static InstallerRecoveryPruneOperation SuccessfulOperation(
        int removedCount,
        InstallerBackendSettlement settlement = InstallerBackendSettlement.ConfirmedClosed
    )
    {
        Channel<InstallerRecoveryPruneProgress> progress = Channel.CreateBounded<InstallerRecoveryPruneProgress>(1);
        progress.Writer.TryWrite(new InstallerRecoveryPruneProgress(TransactionStage.CleaningRecovery, 1, 1));
        progress.Writer.TryComplete();
        InstallerRecoveryPruneTerminalResult terminal = new(
            ProtocolPruneOutcome.Succeeded,
            ProtocolDurableState.PruneApplied,
            null,
            ProtocolRecoveryDisposition.NotRequired,
            ProtocolNextAction.ListRecoveries,
            new InstallerRecoveryPruneSummary(removedCount, removedCount, 0, false),
            settlement
        );
        return new(progress.Reader, Task.FromResult<InstallerRecoveryPruneResult>(terminal), () => Task.CompletedTask);
    }

    private static void Press(RecoveryPruneWindow window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        if (window.IsVisible)
            window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected recovery-cleanup window state wasn't reached.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    private sealed class FakePruneSession : IPlanInspectionSession
    {
        private readonly IReadOnlyList<BoundInstallerRecoveryPoint> Points;
        private readonly InstallerBackendSettlement Settlement;
        private int listCalls;
        private int inspectCalls;
        private int confirmCalls;
        private int executeCalls;
        private int disposeCalls;

        public FakePruneSession(
            string privatePath,
            IReadOnlyList<BoundInstallerRecoveryPoint> points,
            InstallerBackendSettlement settlement = InstallerBackendSettlement.ConfirmedClosed
        )
        {
            this.Game = new(privatePath, "Stardew Valley");
            this.Points = points;
            this.Settlement = settlement;
        }

        public ProtocolReleaseIdentity Release { get; } = GameDiscoveryControllerTests.Release();
        public VerifiedGamePresentation Game { get; }
        public TaskCompletionSource<InstallerProtocolClientException> Fault { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;
        public int ListCalls => Volatile.Read(ref this.listCalls);
        public int InspectCalls => Volatile.Read(ref this.inspectCalls);
        public int ConfirmCalls => Volatile.Read(ref this.confirmCalls);
        public int ExecuteCalls => Volatile.Read(ref this.executeCalls);
        public int DisposeCalls => Volatile.Read(ref this.disposeCalls);
        public Func<CancellationToken, Task<BoundInstallerRecoveryCatalogResult>>? RecoveryCatalog { get; init; }

        public Task<BoundInstallerRecoveryCatalogResult> ListRecoveriesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref this.listCalls);
            if (this.RecoveryCatalog is not null)
                return this.RecoveryCatalog(cancellationToken);
            return Task.FromResult<BoundInstallerRecoveryCatalogResult>(new BoundInstallerRecoveryCatalogSuccess(this.Points));
        }

        public Task<BoundInstallerRecoveryPrunePlanResult> InspectRecoveryPruneAsync(
            BoundInstallerRecoveryPoint oldestPointToKeep,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref this.inspectCalls);
            return Task.FromResult<BoundInstallerRecoveryPrunePlanResult>(Plan(oldestPointToKeep.Ordinal, this.Points.Count));
        }

        public Task<IConfirmedRecoveryPruneSession> ConfirmRecoveryPruneAsync(
            BoundInstallerRecoveryPruneConfirmation confirmation,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref this.confirmCalls);
            return Task.FromResult<IConfirmedRecoveryPruneSession>(new Confirmed(this));
        }

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(InstallerOperation operation, CancellationToken cancellationToken = default)
            => throw new AssertionException("Ordinary plan inspection wasn't expected.");

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref this.disposeCalls);
            return ValueTask.CompletedTask;
        }

        private sealed class Confirmed(FakePruneSession owner) : IConfirmedRecoveryPruneSession
        {
            public ProtocolReleaseIdentity Release => owner.Release;
            public VerifiedGamePresentation Game => owner.Game;
            public Task<InstallerProtocolClientException> SessionFaulted => owner.SessionFaulted;

            public Task<InstallerRecoveryPruneOperation> ExecuteAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref owner.executeCalls);
                return Task.FromResult(SuccessfulOperation(1, owner.Settlement));
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.disposeCalls);
                return ValueTask.CompletedTask;
            }
        }
    }
}
