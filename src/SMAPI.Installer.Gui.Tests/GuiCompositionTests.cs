using Avalonia.Headless.NUnit;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed class GuiCompositionTests
{
    [AvaloniaTest]
    public async Task DefaultCompositionMapsProductionAndDemoToTheirActualWindows()
    {
        ReleaseVerificationWindow production = GuiComposition.CreateMainWindow(GuiLaunchMode.Production)
            .Should().BeOfType<ReleaseVerificationWindow>().Subject;
        MainWindow demo = GuiComposition.CreateMainWindow(GuiLaunchMode.Demo)
            .Should().BeOfType<MainWindow>().Subject;

        await production.DisposeAsync();
        demo.DataContext.Should().BeOfType<MainWindowViewModel>();
    }

    [AvaloniaTest]
    public async Task ProductionCompositionDoesNotStartWorkAndDisposesItsOwnedResources()
    {
        TrackingReleaseService service = new();
        int productionFactoryCalls = 0;
        int demoFactoryCalls = 0;
        int protocolFactoryCalls = 0;

        ReleaseVerificationWindow CreateProduction()
        {
            productionFactoryCalls++;
            ReleaseVerificationViewModel viewModel = new(new ReleaseVerificationController(
                service,
                () =>
                {
                    protocolFactoryCalls++;
                    throw new AssertionException("Composition must not start the backend protocol client.");
                }
            ));
            return new ReleaseVerificationWindow(viewModel);
        }

        MainWindow CreateDemo()
        {
            demoFactoryCalls++;
            return new MainWindow();
        }

        ReleaseVerificationWindow window = GuiComposition.CreateMainWindow(
            GuiLaunchMode.Production,
            CreateProduction,
            CreateDemo
        ).Should().BeOfType<ReleaseVerificationWindow>().Subject;

        productionFactoryCalls.Should().Be(1);
        demoFactoryCalls.Should().Be(0);
        service.LoadCatalogCalls.Should().Be(0);
        service.PrepareCalls.Should().Be(0);
        protocolFactoryCalls.Should().Be(0);
        service.DisposeCalls.Should().Be(0);

        await Task.WhenAll(window.DisposeAsync().AsTask(), window.DisposeAsync().AsTask());
        service.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public void DemoCompositionDoesNotCreateProductionResources()
    {
        int productionFactoryCalls = 0;
        int demoFactoryCalls = 0;

        MainWindow window = GuiComposition.CreateMainWindow(
            GuiLaunchMode.Demo,
            () =>
            {
                productionFactoryCalls++;
                throw new AssertionException("Demo composition must not create production resources.");
            },
            () =>
            {
                demoFactoryCalls++;
                return new MainWindow();
            }
        ).Should().BeOfType<MainWindow>().Subject;

        window.DataContext.Should().BeOfType<MainWindowViewModel>();
        productionFactoryCalls.Should().Be(0);
        demoFactoryCalls.Should().Be(1);
    }

    private sealed class TrackingReleaseService : IReviewedReleaseService
    {
        public int LoadCatalogCalls { get; private set; }

        public int PrepareCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task<IReadOnlyList<ReviewedReleaseCandidate>> LoadCatalogAsync(CancellationToken cancellationToken = default)
        {
            this.LoadCatalogCalls++;
            throw new AssertionException("Composition must not load the network release catalog.");
        }

        public Task<IPreparedReleasePackage> PrepareAsync(
            ReviewedReleaseCandidate candidate,
            IProgress<ReviewedReleasePreparationProgress>? progress = null,
            CancellationToken cancellationToken = default
        )
        {
            this.PrepareCalls++;
            throw new AssertionException("Composition must not prepare a release package.");
        }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
