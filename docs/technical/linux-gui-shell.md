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

## Private diagnostics and troubleshooting

Every production workflow screen has a **View diagnostic log** action. It opens a bounded,
read-only snapshot of the current graphical-installer session. The snapshot is captured when the
window opens, so close and reopen it to include later events. **Copy sanitized diagnostics** writes
that exact bounded snapshot to the clipboard only after an explicit action; the installer never
reads clipboard contents. If the desktop has not confirmed the write, the viewer leaves Copy
disabled after a three-second deadline and reports that the original write may still finish.
Closing and reopening cannot start another write until that original provider settles; after it
settles, a fresh explicit Copy attempt is available.

Keyboard shortcuts are consistent across the production flow: Alt+D opens diagnostics, Alt+Y
copies the sanitized snapshot, and Alt+X or Escape closes the viewer. On release verification,
Alt+W starts **Download and verify**. Closing the viewer restores focus to **View diagnostic log**.

The production GUI creates its private log before Avalonia, catalog networking, game discovery, or
backend startup. On Linux it uses `$XDG_STATE_HOME/smapi-installer/logs` when `XDG_STATE_HOME` is an
absolute path, or `~/.local/state/smapi-installer/logs` otherwise, with private directories and
files. The on-screen snapshot is intentionally narrower than the local JSONL log:
it excludes local paths, URLs, credentials, backend prose, release/package identifiers, digests,
and private workload names. Review even the sanitized snapshot before sharing it. Neither surface
is uploaded automatically.

Common safe next steps are:

| Symptom | Safe next step |
| --- | --- |
| The launcher refuses root or `sudo` | Run it again as the normal desktop user. No diagnostic log, network request, or game access starts on the refused path. |
| No compatible graphical release is listed | The published alpha.1 lacks the six-asset authority required by the production GUI. Use the verified terminal package flow below; do not substitute an Actions candidate for a public release. |
| Catalog, download, or verification fails | Retry from the visible action after checking connectivity. A failed verification does not authorize game-file mutation. |
| The backend or protocol session fails | Close the GUI, review the private diagnostic snapshot, and start a fresh session. The GUI does not reconstruct backend authority after a failed session. |
| Diagnostic logging cannot start or cannot prove readiness | Close any other graphical installer session. Do not remove its lock, use root, or loosen file permissions broadly. Check free space and that the normal user owns the XDG state location, then start a fresh session. New mutating work remains blocked when readiness cannot be recorded. |
| The native Wayland path does not start reliably | Use an X11 or XWayland session, or use the retained terminal launcher. Native Wayland remains experimental. |

The same package candidate retains `install on Linux.sh` as the non-GUI terminal fallback. The
current published alpha and its exact verification, terminal, headless, rollback, and last-resort
manual extraction instructions remain in the [Linux alpha release guide](linux-alpha-release.md).

## Current boundary and packaging status

The view model accepts only the exact internal sealed `DemoInstallerFrontendSession` type. Its fixed constants are bounded and validated against control, bidirectional-format, and surrogate characters. It returns only synthetic choices and an unchanged durable state without touching operating-system services. There is no public extension point which can be mistaken for a reviewed production authority boundary.

Production composition now connects reviewed release acquisition to the exact packaged sibling backend and transfers single-owner authorities through discovery, plan review, explicit confirmation, execution, rollback, interrupted recovery, and recovery-history cleanup. Those routes still leave path ownership, trust, confirmation, transactions, backup, rollback, and recovery policy in Core.

The current focused packaging work adds the self-contained GUI to a non-authoritative Actions candidate while retaining the existing console launcher. Production now creates a bounded private diagnostic session before desktop or network startup and exposes its sanitized snapshot from all five workflow screens. This does not make the historical safe-demo screenshot production evidence, publish a tagged GUI release, or satisfy clean-machine lifecycle, trusted-workload, X11/XWayland, AT-SPI, detailed-log real-lifecycle/screenshot qualification, or screenshot requirements. Release relationship/local-package UX and the remaining qualification work are separate blockers before the next public alpha is frozen.
