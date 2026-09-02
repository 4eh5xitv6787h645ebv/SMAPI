# Linux graphical installer shell

The Linux desktop frontend is an Avalonia application around the shared installer behavior; it does not copy filesystem ownership or mutation rules into the UI. The repository now contains both the original sealed safe-demo mode and the separately reviewed production workflow for release verification, game discovery, plan review, execution, rollback, interrupted recovery, and recovery-history cleanup.

The screenshot below is **historical safe-demo evidence**, not the production installer. Exact `--demo` still uses deterministic synthetic folder and release options, holds its app log in memory, does not connect the production backend, and performs no installer-controlled game/package discovery or mutation and no app-initiated download or network request. Its execute control is intentionally disabled; previewing an operation never claims it completed.

Avalonia and the operating-system desktop stack still make their normal infrastructure accesses. Depending on the session, those can include X11, D-Bus/AT-SPI, fonts, and Mesa or toolkit caches. The safety boundary here is the absent installer backend and fixed synthetic app data, not a claim that the GUI process makes no system calls or reads/writes no runtime state.

![The Linux installer shell showing synthetic selections, a safe-demo warning, disconnected state, disabled execution, and an in-memory demo log.](../screenshots/linux-installer-safe-demo.png)

[Review the exact source, capture environment, checksum, and privacy record for this screenshot.](../screenshots/linux-installer-safe-demo.provenance.html)

## Build and run on Linux

Install the .NET 10 SDK and the normal desktop libraries required by Avalonia for your distribution. From the repository root:

```sh
dotnet restore src/SMAPI.Installer.Gui/SMAPI.Installer.Gui.csproj
dotnet build src/SMAPI.Installer.Gui/SMAPI.Installer.Gui.csproj --configuration Release --no-restore
dotnet run --project src/SMAPI.Installer.Gui/SMAPI.Installer.Gui.csproj --configuration Release --no-build -- --demo
```

The supported first desktop path is X11, including XWayland on Wayland sessions; Avalonia's experimental native Wayland path isn't advertised as supported yet. The window uses native controls, unique access keys and an explicit forward/reverse tab order, a visible focus ring, automation headings and a concise live result, and device-independent sizing. Selection and state cards stack below 850 device-independent pixels; the page remains vertically scrollable without horizontal page scrolling at the tested 100%, 125%, 150%, and 200% scale models.

Run the focused tests with:

```sh
dotnet test src/SMAPI.Installer.Gui.Tests/SMAPI.Installer.Gui.Tests.csproj --configuration Release
build/scripts/test-linux-gui-demo-smoke.sh
```

The tests cover the disconnected view model, synthetic session invariants, fixed display bounds and control/bidirectional-character rejection, exact Core operation coverage, launch argument safety, real access-key and Tab/Shift+Tab invocation, automation names/headings/live status, disabled execution, contrast, and arranged/rendered layouts at the scale models and narrow viewport. The process smoke runs the built shell for five seconds under Xvfb with disposable `HOME` and XDG directories; it isn't a general operating-system sandbox or a substitute for the later installer integration tests.

## Current boundary and packaging status

The view model accepts only the exact internal sealed `DemoInstallerFrontendSession` type. Its fixed constants are bounded and validated against control, bidirectional-format, and surrogate characters. It returns only synthetic choices and an unchanged durable state without touching operating-system services. There is no public extension point which can be mistaken for a reviewed production authority boundary.

Production composition now connects reviewed release acquisition to the exact packaged sibling backend and transfers single-owner authorities through discovery, plan review, explicit confirmation, execution, rollback, interrupted recovery, and recovery-history cleanup. Those routes still leave path ownership, trust, confirmation, transactions, backup, rollback, and recovery policy in Core.

The current focused packaging work adds the self-contained GUI to a non-authoritative Actions candidate while retaining the existing console launcher. It does not make the historical safe-demo screenshot production evidence, publish a tagged GUI release, or satisfy clean-machine lifecycle, trusted-workload, X11/XWayland, AT-SPI, or screenshot requirements. The detailed local-log viewer and release relationship/local-package UX are separate blockers before the next public alpha is frozen.
