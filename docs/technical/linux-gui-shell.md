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

The GUI follows the desktop's light or dark theme by default, including live theme changes. For an
explicit maximum-contrast presentation, launch the same package as your normal desktop user with
`SMAPI_INSTALLER_HIGH_CONTRAST=1`; for example:

```sh
SMAPI_INSTALLER_HIGH_CONTRAST=1 bash "install on Linux (graphical).sh"
```

Only the exact value `1` enables this override. It changes presentation colors and focus/error
outlines only: it doesn't grant installer authority, bypass verification or confirmation, alter
diagnostics, or require `sudo`. Remove the variable (or set any other value) to follow the system
theme again. The light, dark, and explicit high-contrast palettes have automated checks for visible
keyboard focus, error text/boundaries, and the manual terminal-help card; real X11/XWayland and
AT-SPI qualification remain part of exact-package desktop testing.

## Use the production workflow

Close Stardew Valley and back up saves and `Mods` before beginning. From one complete verified
public package, run `bash "install on Linux (graphical).sh"` as the normal desktop user. The GUI is
deliberately staged; reaching a later screen never means an earlier read-only action changed files.

1. **Verify a release.** Choose a reviewed public prerelease and select **Download and verify**, or
   choose **Use local package folder…** and freshly select one folder containing the exact six
   release files. Integrity and GitHub provenance are separate visible results. A failed or cancelled
   check authorizes no game discovery or mutation.
2. **Choose the game.** Review automatically detected folders, or use **Browse for game folder** for a
   manual selection. Validation is read-only. Continue with **Review plan** only after the folder is
   reported as valid.
3. **Inspect one operation.** Select **Install**, **Update**, **Repair**, **Uninstall**, or **Backup**,
   then choose **Inspect plan**. The plan shows observed state, authenticated current/target release
   relationship, exact operations, risks, and conflicts. If modified receipt-owned files are
   eligible for replacement, every additive approval starts unchecked; there is no Select all.
   Applying those choices only refreshes the read-only plan.
4. **Confirm, then run.** **Confirm reviewed plan** still changes no files and opens a separate final
   screen. Recheck the safety boundary, leave Cancel selected if anything is unexpected, and use the
   explicit **Run** action once. Do not start the game, replace the selected folder, or run another
   installer while a mutation is active. The final screen distinguishes the durable result from
   backend-settlement warnings and offers recovery when the exact recorded state requires it.

Operation-specific expectations:

| Operation | When to use it | What to verify in the read-only plan |
| --- | --- | --- |
| Install | No managed fork installation is present. | A fresh target release, launcher handling, created files, and no unresolved collision. |
| Update | A receipt-authenticated fork installation should move to the verified target. | Current and target releases, upgrade or explicit downgrade risk, retained unrelated files, and every requested replacement. |
| Repair | The receipt-authenticated target release has missing or modified managed files. | Missing files to restore; modified files remain blocked unless individually approved. |
| Uninstall | Remove receipt-owned SMAPI files and restore the observed launcher where possible. | Destructive removal list, launcher restoration, and preserved unrelated game files, Mods, saves, and reports. |
| Backup | Create a user checkpoint of the authenticated managed installation. | The checkpoint source release and bounded receipt-owned contents; it is not a backup of Mods or saves. |
| Rollback | Restore one exact authenticated recovery generation. | Use **Load or refresh history**, select one point, and **Inspect rollback**. No point is selected automatically. Confirm only after reviewing the restored release/state and downgrade risk. |

If startup detects an interrupted mutation, follow the offered authenticated recovery before asking
for a fresh plan. Recovery preparation can be cancelled while that action remains visible; after the
backend admits recovery, it must settle and is no longer cancellable. Recovery history cleanup is
also a separate reviewed flow: select generations individually, preview cleanup,
confirm with Cancel focused by default, and then run it. The sanitized diagnostic viewer is useful
for safe next steps, but the local private log and every displayed path or report should still be
reviewed before sharing.

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

The same package candidate retains `install on Linux.sh` as the non-GUI terminal fallback. Close
the game, verify the complete public release set, extract the installer ZIP, and run
`bash "install on Linux.sh"` as the normal desktop user from that same package. Never use `sudo`.
The current published alpha and its exact verification, terminal, headless, rollback, limitations,
and last-resort manual extraction instructions remain in the
[Linux alpha release guide](linux-alpha-release.md).

## Current boundary and packaging status

The view model accepts only the exact internal sealed `DemoInstallerFrontendSession` type. Its fixed constants are bounded and validated against control, bidirectional-format, and surrogate characters. It returns only synthetic choices and an unchanged durable state without touching operating-system services. There is no public extension point which can be mistaken for a reviewed production authority boundary.

Production composition now connects reviewed release acquisition to the exact packaged sibling backend and transfers single-owner authorities through discovery, plan review, explicit confirmation, execution, rollback, interrupted recovery, and recovery-history cleanup. Those routes still leave path ownership, trust, confirmation, transactions, backup, rollback, and recovery policy in Core.

The current alpha 2 preparation adds the self-contained GUI to a non-authoritative Actions
candidate while retaining the existing console launcher. Production creates a bounded private
diagnostic session before desktop or network startup and exposes its sanitized snapshot from every
workflow screen. It can select reviewed public releases or import one freshly selected local folder
containing the exact six release files; both routes reach the same authenticated package authority.
Installed/current, upgrade, and downgrade relationships are shown only after the selected game and
its receipt have been inspected, never inferred from an unauthenticated catalog label.

This does not make the historical safe-demo screenshot production evidence or publish a tagged GUI
release. The remaining blockers are exact-commit alpha 2 qualification and publication, clean public
package lifecycle and trusted-workload qualification, genuine X11/XWayland and AT-SPI checks, and the
complete authenticated screenshot evidence set.
