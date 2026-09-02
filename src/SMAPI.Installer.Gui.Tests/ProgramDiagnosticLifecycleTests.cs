using FluentAssertions;
using StardewModdingAPI.Installer.Core.Privacy;
using StardewModdingAPI.Installer.Gui.Diagnostics;

namespace StardewModdingAPI.Installer.Gui.Tests;

[TestFixture]
[NonParallelizable]
internal sealed class ProgramDiagnosticLifecycleTests
{
    private DiagnosticSessionFixture? SessionFixture;

    [TearDown]
    public void TearDown()
    {
        this.SessionFixture?.Dispose();
        this.SessionFixture = null;
    }

    [Test]
    public void Production_CreatesExactlyOneDiagnosticSessionBeforeDesktopStarts()
    {
        this.SessionFixture = new();
        List<string> events = [];
        InstallerDiagnosticSession? created = null;
        InstallerDiagnosticSession? received = null;
        using StringWriter diagnostics = new();

        int exit = Program.StartSelectedModeWithDiagnostics(
            GuiLaunchMode.Production,
            () =>
            {
                events.Add("factory");
                return created = this.SessionFixture.CreateSession();
            },
            (mode, session) =>
            {
                events.Add("desktop");
                mode.Should().Be(GuiLaunchMode.Production);
                received = session;
                session.Should().NotBeNull();
                session!.EnsureReadyForMutation();
                return 17;
            },
            diagnostics
        );

        exit.Should().Be(17);
        events.Should().Equal("factory", "desktop");
        received.Should().BeSameAs(created);
        diagnostics.ToString().Should().BeEmpty();
    }

    [Test]
    public void Demo_DoesNotCreateDiagnosticsAndPassesNullSessionToDesktop()
    {
        int factoryCalls = 0;
        int desktopCalls = 0;
        using StringWriter diagnostics = new();

        int exit = Program.StartSelectedModeWithDiagnostics(
            GuiLaunchMode.Demo,
            () =>
            {
                factoryCalls++;
                throw new InvalidOperationException("The demo must not create production diagnostics.");
            },
            (mode, session) =>
            {
                desktopCalls++;
                mode.Should().Be(GuiLaunchMode.Demo);
                session.Should().BeNull();
                return 23;
            },
            diagnostics
        );

        exit.Should().Be(23);
        factoryCalls.Should().Be(0);
        desktopCalls.Should().Be(1);
        diagnostics.ToString().Should().BeEmpty();
    }

    [Test]
    public void InvalidMode_DoesNotCreateDiagnosticsOrStartDesktop()
    {
        int factoryCalls = 0;
        int desktopCalls = 0;
        using StringWriter diagnostics = new();

        int exit = Program.StartSelectedModeWithDiagnostics(
            (GuiLaunchMode)999,
            () =>
            {
                factoryCalls++;
                throw new InvalidOperationException("The invalid mode must be rejected before diagnostics.");
            },
            (_, _) =>
            {
                desktopCalls++;
                return 0;
            },
            diagnostics
        );

        exit.Should().Be(2);
        factoryCalls.Should().Be(0);
        desktopCalls.Should().Be(0);
        diagnostics.ToString().Should().Be("The graphical installer launch mode is invalid." + Environment.NewLine);
    }

    [Test]
    public void DiagnosticCreationFailure_IsGenericAndDoesNotStartDesktop()
    {
        int factoryCalls = 0;
        int desktopCalls = 0;
        using StringWriter diagnostics = new();

        int exit = Program.StartSelectedModeWithDiagnostics(
            GuiLaunchMode.Production,
            () =>
            {
                factoryCalls++;
                throw new IOException("sensitive filesystem detail");
            },
            (_, _) =>
            {
                desktopCalls++;
                return 0;
            },
            diagnostics
        );

        exit.Should().Be(1);
        factoryCalls.Should().Be(1);
        desktopCalls.Should().Be(0);
        diagnostics.ToString().Should().Be(
            "The graphical installer couldn't create its private local diagnostic log safely. No network request or game access was started."
            + Environment.NewLine
        );
        diagnostics.ToString().Should().NotContain("sensitive filesystem detail");
    }

    [Test]
    public void NormalDesktopReturn_DisposesDiagnosticSession()
    {
        this.SessionFixture = new();
        InstallerDiagnosticSession session = this.SessionFixture.CreateSession();
        using StringWriter diagnostics = new();

        int exit = Program.StartSelectedModeWithDiagnostics(
            GuiLaunchMode.Production,
            () => session,
            (_, received) =>
            {
                received.Should().BeSameAs(session);
                received!.EnsureReadyForMutation();
                return 31;
            },
            diagnostics
        );

        exit.Should().Be(31);
        Action useAfterReturn = session.EnsureReadyForMutation;
        useAfterReturn.Should().Throw<InstallerDiagnosticsUnavailableException>();
        diagnostics.ToString().Should().BeEmpty();
    }

    [Test]
    public void DesktopFailure_StillDisposesDiagnosticSessionAndPreservesException()
    {
        this.SessionFixture = new();
        InstallerDiagnosticSession session = this.SessionFixture.CreateSession();
        InvalidOperationException expected = new("desktop failed");
        using StringWriter diagnostics = new();

        Action start = () => Program.StartSelectedModeWithDiagnostics(
            GuiLaunchMode.Production,
            () => session,
            (_, received) =>
            {
                received.Should().BeSameAs(session);
                received!.EnsureReadyForMutation();
                throw expected;
            },
            diagnostics
        );

        start.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(expected);
        Action useAfterFailure = session.EnsureReadyForMutation;
        useAfterFailure.Should().Throw<InstallerDiagnosticsUnavailableException>();
        diagnostics.ToString().Should().BeEmpty();
    }

    private sealed class DiagnosticSessionFixture : IDisposable
    {
        private readonly string TemporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"smapi-gui-program-diagnostics-tests-{Guid.NewGuid():N}"
        );
        private InstallerDiagnosticSession? Session;

        public InstallerDiagnosticSession CreateSession()
        {
            if (this.Session is not null)
                throw new InvalidOperationException("This fixture creates exactly one diagnostic session.");

            Guid operationId = Guid.NewGuid();
            string stateRoot = Path.Combine(this.TemporaryDirectory, "state");
            InstallerLog log = new(new(stateRoot), operationId, DateTimeOffset.UnixEpoch);
            try
            {
                return this.Session = new(log, operationId, () => DateTimeOffset.UnixEpoch);
            }
            catch
            {
                log.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            this.Session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(this.TemporaryDirectory))
                Directory.Delete(this.TemporaryDirectory, recursive: true);
        }
    }
}
