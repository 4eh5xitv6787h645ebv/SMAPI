# Linux graphical installer shell

The first Linux desktop shell is an Avalonia application which presents the existing installer operations without copying installation rules into the UI. Its frontend session boundary is designed for a future adapter around `LinuxInstallerProtocolService`, the same strict Core protocol used for installation planning and execution.

This initial slice is **always a safe demo**. It uses deterministic synthetic folder and release options, holds its log in memory, doesn't connect the production backend, and performs no filesystem discovery, package download, network access, or game/Mods/save mutation. The execute control is intentionally disabled; previewing an operation never claims it completed.

![The Linux installer shell showing synthetic selections, a safe-demo warning, disconnected state, disabled execution, and an in-memory demo log.](../screenshots/linux-installer-safe-demo.png)

## Build and run on Linux

Install the .NET 10 SDK and the normal desktop libraries required by Avalonia for your distribution. From the repository root:

```sh
dotnet restore src/SMAPI.Installer.Gui/SMAPI.Installer.Gui.csproj
dotnet build src/SMAPI.Installer.Gui/SMAPI.Installer.Gui.csproj --configuration Release --no-restore
dotnet run --project src/SMAPI.Installer.Gui/SMAPI.Installer.Gui.csproj --configuration Release --no-build -- --demo
```

The supported first desktop path is X11, including XWayland on Wayland sessions; Avalonia's experimental native Wayland path isn't advertised as supported yet. The window uses native controls, explicit access keys and tab order, a visible focus ring, a scrolling minimum-size layout, and device-independent sizing for desktop scaling.

Run the focused tests with:

```sh
dotnet test src/SMAPI.Installer.Gui.Tests/SMAPI.Installer.Gui.Tests.csproj --configuration Release
```

The tests cover the disconnected view model, synthetic session invariants, exact Core operation coverage, launch argument safety, automation names, keyboard order, disabled execution, and the scaling-friendly scrolling layout.

## Current boundary and next integration

`IInstallerFrontendSession` is the sole boundary consumed by the view model. The demo implementation returns synthetic choices and an unchanged durable state without touching operating-system services. A production implementation must translate UI requests into the versioned Core protocol and display its authoritative plan/progress/terminal events; it must not reimplement path ownership, trust, confirmation, transaction, backup, rollback, or recovery policy in the GUI.

Until that adapter and its security/integration tests land, this shell is a visual and accessibility preview only. The existing non-GUI/manual installation path remains the supported path and is unchanged by this project.
