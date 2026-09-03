---
layout: default
title: Linux alpha release guide
description: Verify, install, upgrade, remove, or roll back the experimental SMAPI Linux fork.
kicker: Experimental Linux x86_64 prerelease
---

This guide is for the current **unofficial experimental Linux desktop alpha 2**. It is not an official
SMAPI release and is not the default recommendation for most players. Use official SMAPI if you
want the broadly supported cross-platform release.

## Release identity

The fork uses identifiers which cannot collide with inherited official SMAPI tags or look like an
official stable release:

| Item | Published alpha 2 |
| --- | --- |
| Embedded version | `4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2` |
| Git tag | `fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2` |
| Annotated tag object | `782ae58170f4399947e03455e968775cc090666a` |
| Exact source commit | `052699e8ccba0d13f9d4f02e0bb199aa04cec605` |
| Exact source tree | `95bfb5cf8744daf15d59f4799a593fd8be7bca8d` |
| Release title | `Experimental SMAPI Linux Fork 4.5.3 alpha 2` |
| Installer | `SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip` |

Alpha 3 is currently a **non-public source candidate** with planned embedded version
`4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.3` and reserved annotated tag
`fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3`. Do not use that identity as a download or
verification claim until the exact merged commit, tag workflow, six public assets, fresh-download
qualification, and trusted-workload smoke are linked here. Alpha 2 remains the only current public
package while that sequence is incomplete.

Later alphas increment the final number. A published tag is never reused or moved. The public
release assets are built from the exact tag commit by GitHub Actions; the build records its source
commit, tree, pinned game-reference commit, runner, SDKs, package size, and SHA-256. Recorded inputs
describe how the artifact was built, but only the package/manifest hashes and independently verified
GitHub attestations establish published identity, integrity, and provenance. The workflow-run URL,
runner, pinned reference-build commit, timestamp, and .NET details are bounded informational fields;
clean-machine verification of the four primary package/metadata assets does not authenticate them.
Byte-for-byte reproducible ZIP
output is not claimed.

## Requirements and safety boundary

- Native Linux x86_64 Stardew Valley 1.6.14 or later. Android/mobile is not supported.
- A normal desktop user account. **Never run the installer with `sudo` or as root.**
- GNU Bash, GNU coreutils (`stat` and `timeout`), and GNU diffutils (`cmp`).
- The game must be closed.
- Back up saves, `Mods`, and any existing `smapi-internal/config.user.json` before changing loaders.
- Download all six files only from this repository's
  [alpha 2 release page](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2).

### Graphical package and terminal fallback

The published alpha 2 package contains a separate
`install on Linux (graphical).sh` entry point and the unchanged `install on Linux.sh` terminal
fallback in the same ZIP. The GUI is a self-contained, untrimmed Linux x86_64 application; its
single-file native runtime is extracted into a private per-run temporary directory which the
launcher normally removes after an ordinary exit or successfully settled HUP, INT, or TERM signal.
If the bounded child-settlement deadline expires, the launcher retains those private runtime files
to avoid unsafe deletion; after confirming no installer process remains, remove the leftover
directory manually. SIGKILL, power loss, or another abrupt stop can also leave that private
directory under the configured temporary root. The supported first desktop path is X11 or XWayland,
not Avalonia's experimental native Wayland backend; headless and native-Wayland-only sessions can
use the terminal launcher.

The two launchers share a package, not an installation safety model. The GUI uses the private Core
protocol with reviewed plans, receipts, journals, recovery, history, and authenticated rollback. The
console launcher runs the retained legacy `InteractiveInstaller`, which directly supports only
install and uninstall. Its exact limitations are documented below.

Alpha 2 creates one bounded private graphical-installer diagnostic session before
Avalonia, release-catalog networking, game discovery, or backend startup. Every production screen
can open a stable sanitized snapshot through **View diagnostic log**. The graphical workflow can
download a reviewed public release or verify one freshly selected local folder containing the exact
six assets. It does not infer authority from a folder path or metadata alone.

## Verify before extracting or running

Download all six alpha 2 assets into one empty directory. Do not use a mirror and never pipe a
download into a shell:

- `SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip`;
- `SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-install-manifest.json`;
- `SHA256SUMS`;
- `build-metadata.json`;
- `SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-attestation-bundle.jsonl`;
  and
- `SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-attestation-bundle.jsonl.sha256`.

`SHA256SUMS` has exactly two sorted subjects: the canonical install manifest followed by the
installer ZIP. `build-metadata.json` records those same two subjects. The bundle sidecar detects
transport corruption of the local bundle; only successful verification of the signed bundle under
the policy below establishes provenance. Verify the exact bytes with GitHub CLI 2.92.0:

```bash
package_name='SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip'
manifest_name='SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-install-manifest.json'
bundle_name='SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-attestation-bundle.jsonl'
release_tag='fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2'
release_commit='052699e8ccba0d13f9d4f02e0bb199aa04cec605'
sha256sum --check --strict SHA256SUMS
sha256sum --check --strict "$bundle_name.sha256"
attestation_policy=(
  --bundle "$bundle_name" \
  --hostname github.com \
  --repo 4eh5xitv6787h645ebv/SMAPI \
  --predicate-type https://slsa.dev/provenance/v1 \
  --cert-oidc-issuer https://token.actions.githubusercontent.com \
  --cert-identity "https://github.com/4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/$release_tag" \
  --signer-digest "$release_commit" \
  --source-ref "refs/tags/$release_tag" \
  --source-digest "$release_commit" \
  --deny-self-hosted-runners \
  --limit 2 \
  --format json
)
gh attestation verify "$package_name" "${attestation_policy[@]}"
gh attestation verify "$manifest_name" "${attestation_policy[@]}"
```

The expected installer SHA-256 is
`a1d8669881b8ba87c3511689b810211148430798f30bc7a42e3fd74bc5630dfd`; the expected manifest
SHA-256 is `eac8e97fbfdd437e9e165ab72ce55d782ea28449798414c8bf3e704c7a8de5a3`; and the expected
bundle SHA-256 is `7b468ab561513c2c3042ec0c9725b1522090b4483049b4d8933fe4f8b5291a4b`.
Both package/manifest checksum subjects and the bundle checksum must report `OK`, and both
attestation-verification commands must succeed against the same downloaded bundle. The successful
statement must identify this repository, the tagged `linux-alpha-release.yml` workflow, and the
selected release commit, and contain exactly the package and manifest names and locally computed
digests. A pull-request build, `develop` build, manual source build, or pre-tag workflow-dispatch candidate is non-authoritative:
the production workflow records its actual identity and does not invoke the release-manifest
creation path. The tool's tag-context check prevents accidental candidate minting, but it is not a
cryptographic provenance boundary and its environment can be reproduced by a local caller. Only
successful verification of the GitHub attestation statement, whose subjects are exactly the package
and manifest, against this repository, tagged workflow, and selected commit establishes published
release authority. Do not substitute an unattested local or candidate four-primary-asset set for the
published tagged six-asset set.

For repeatable post-publication qualification, maintainers invoke the repository wrapper with a
`GH_TOKEN` (prefer a fine-grained read-only token), the exact tag/commit/tree, a new destination,
and the official GitHub CLI 2.92.0 Linux x86_64 archive checked by the staging script:

```bash
test -n "${GH_TOKEN:-}" # export the token without placing it in shell history
build/scripts/qualify-published-linux-alpha.sh \
  'fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2' \
  '052699e8ccba0d13f9d4f02e0bb199aa04cec605' \
  '95bfb5cf8744daf15d59f4799a593fd8be7bca8d' \
  '/new/private/destination' \
  '/path/to/gh_2.92.0_linux_amd64.tar.gz'
```

The fresh alpha 2 qualification passed; its
[sanitized public evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5515036792)
records the exact inventory, identity, checks, and disposable lifecycle results. The wrapper refuses
root and an existing destination. It pins the public release ID and tag plus each
asset's ID, name, size, uploaded state, digest when GitHub supplies one, timestamp, and canonical URL
before downloading, and requires the same normalized inventory after verification. Each download
must match the pinned public size and supplied digest. The wrapper then applies the strict checksum,
metadata, manifest-authority, bundle, and two-subject attestation policy before atomically exposing
only the six verified files. It captures and removes `GH_TOKEN` from the ambient process environment
before staging or downloading. The token is never placed in a process argument: only the two
isolated, fixed-GET inventory process trees receive it as an environment variable; staging,
downloads, hashing, and attestation helpers do not. Unused token capabilities are not part of the
qualification claim. Inventory calls have a 60-second deadline, local-bundle attestation
calls have a 120-second deadline, and each download has bounded connect, total, and low-speed times.
The token is never written to the resulting directory.

## Install

Extract the verified ZIP. In an X11 or XWayland desktop session, start the graphical installer as
your normal user:

```bash
bash "install on Linux (graphical).sh"
```

Choose the reviewed alpha 2 release or freshly select the folder containing the same six verified
files. Select a detected game folder or browse to one, review the read-only **Install** plan, then
confirm it on the separate final screen before using **Run operation**. A successful verification or
plan does not itself change game files. See the [graphical workflow guide](linux-gui-shell.md) for
every operation and error state.

For a terminal-only session, use the fallback from that same verified package as the normal desktop
user. Do not use `sudo`:

```bash
cd "/path/to/extracted/SMAPI installer"
bash "install on Linux.sh"
```

Konsole, Alacritty, GNOME Terminal, and xterm are detected by the launcher when it was opened outside
an existing terminal. That launch result is not a reliable automation result. The published alpha 2
wrapper does not forward command-line options, so a headless or scripted installation must invoke
the packaged apphost directly with one action and an absolute game path:

```bash
cd "/path/to/extracted/SMAPI installer"
./internal/linux/SMAPI.Installer \
  --no-prompt --install --game-path "/absolute/path/to/Stardew Valley"
```

For alpha 2, `--no-prompt` is reliably non-interactive only when `--install` or `--uninstall` and a
valid absolute `--game-path` are supplied. Exit `0` means the requested legacy action reached its
normal success return. Exit `2` means a known validation path returned false, including root use,
missing installer files, conflicting install/uninstall flags, a missing required action or option
value, or an invalid game folder. Unknown or positional arguments are ignored, so use only the exact
commands shown here. Exit `1` means an unexpected exception, filesystem failure, or runtime failure
escaped the legacy flow. Shell and signal exits can produce other statuses. Exit `1` or a signal can
occur after direct mutation began; no exit status alone proves unchanged state or successful
rollback. Treat every nonzero exit as failure and inspect the game folder before launching or
retrying. Console output can include the selected game path and full exception text, so review it
before sharing.

The current unreleased source adds two fail-closed safeguards for the next prerelease: the wrapper
rejects supplied options with status 2 instead of silently dropping them, and a prompt-free request
without `--game-path` returns status 2 before game discovery. Those safeguards do not change the
published alpha 2 package.

The private `--linux-protocol-v1-jsonl` mode is reserved for the graphical frontend. It is not a
supported manual or scripting interface.

## Update or repair

Close the game, back up saves and `Mods`, and verify the selected release. In the GUI, choose the
same game folder and inspect the operation before confirming:

- use **Update** when a receipt-authenticated managed installation should move to a different
  verified release. An older target is labelled as a downgrade and needs explicit confirmation;
- use **Repair** only when the authenticated current and target releases match. Missing managed
  files can be restored, while modified managed files stay blocked unless each eligible replacement
  is explicitly selected.

Modified, legacy, unknown, linked, special, and ambiguous launcher entries are never silently
replaced. The plan names preserved unrelated files and blocks unresolved conflicts. Custom mods,
saves, current SMAPI logs, and local Mod Health Reports are not uninstall targets.

The two bundled mods, Console Commands and Save Backup, are updated in place. An uninstall
intentionally leaves them in `Mods`, just as the upstream installer does.

The legacy terminal installer has no separate Update or Repair operation. Selecting Install first
removes its compiled list of known SMAPI files and then copies the new payload. Repeating Install is
therefore not a receipt-authenticated update or repair: there is no read-only Core plan,
authenticated current-version relationship, exact per-file approval, transaction journal, or
automatic rollback. Back up first. If the legacy action fails after mutation begins, do not assume
either the old or new installation is intact.

## Uninstall or roll back

Keep the verified alpha installer used for the current installation. To return to vanilla, inspect
and explicitly confirm **Uninstall** in the GUI. It restores the authenticated launcher state and
removes receipt-owned fork files while preserving unrelated game files, `Mods`, saves, logs, and
reports.

The legacy terminal fallback can uninstall with:

```bash
./internal/linux/SMAPI.Installer \
  --no-prompt --uninstall --game-path "/absolute/path/to/Stardew Valley"
```

That command removes the legacy installer's hard-coded known-file list and restores
`StardewValley-original` when that backup exists. It does not authenticate a GUI/Core receipt or
recovery generation and has no journal, crash recovery, or rollback. It is not equivalent to the
GUI's reviewed **Uninstall** operation. Preserve custom mods, saves, logs, reports, and backups; on a
nonzero result, inspect the folder instead of recursively deleting it.

For an authenticated GUI rollback, select **Load or refresh history**, select one generation,
choose **Inspect rollback**, review the exact restored release/state and downgrade risk, then use
**Confirm reviewed plan** and **Run rollback**. No recovery point is selected automatically. These
recovery generations contain installer-managed state, not backups of `Mods` or saves.

To roll back to official SMAPI 4.5.2 or an earlier verified fork package:

1. Copy `smapi-internal/config.user.json` and the current `ErrorLogs` folder (including
   `HealthReports`) somewhere outside the game and app-data folders. Official SMAPI 4.5.2 still
   removes the current `ErrorLogs` folder during its install pass; restore the reviewed backup
   afterwards.
2. Run this alpha's uninstaller first. The official 4.5.2 uninstaller does not know the fork-only
   net6/net10 host filenames and must not be used directly over the alpha.
3. Verify and install the selected older package.
4. Restore only compatible user configuration values if needed, and restore the reviewed log/report
   backup if you need to retain it.
5. Launch once and confirm the displayed SMAPI version.

## Manual installation path

Raw extraction is an unsupported last resort for a fresh installation when neither packaged launcher
can run. It is not a manual form of the GUI transaction. It does not verify the release, detect or
approve modified or unknown files, create a manifest or receipt, journal changes, roll back a
partial copy, recover after interruption, create authenticated history, or support authenticated
rollback.

1. Close the game and back up the entire game folder, saves, and `Mods` to a separate location.
2. Download all six release assets into one new directory and complete
   [every checksum and attestation check](#verify-before-extracting-or-running). Verify the outer ZIP
   before extracting any part of it; `install.dat` is not independently authenticated by the legacy
   installer.
3. Extract the verified outer installer ZIP into a new staging directory. Do not run or copy from an
   Actions artifact, pull-request artifact, mirror, or mixed set of files.
4. Confirm this is a fresh target: no SMAPI host, `smapi-internal`, `unix-launcher.sh`, or
   `StardewValley-original` may already exist in the game folder. If any does, stop and use a verified
   installer or restore a known backup; do not overwrite an unknown file or launcher backup.
5. Treat `internal/linux/install.dat` as a ZIP and extract it into a second staging directory. Do not
   extract it directly over the game folder.
6. Copy the staged payload into the game folder without deleting or replacing unrelated files. Treat
   an existing destination as a collision and stop. The staged `Mods` directory contains only the two
   bundled mods; copy them only after separately backing up any same-named mod folder.
7. Copy `Stardew Valley.deps.json` to `StardewModdingAPI-net6.deps.json` without changing its bytes or
   mode.
8. Move the original `StardewValley` launcher to `StardewValley-original` without overwriting a
   destination, then rename the copied `unix-launcher.sh` in the game folder to `StardewValley`.
9. Mark `StardewValley`, `StardewModdingAPI`, both `StardewModdingAPI-net*` hosts, and every
   private-runtime `createdump` executable as mode 755. The private app-relative runtime contains
   `host/fxr` and `shared/Microsoft.NETCore.App`; it intentionally does not bundle the `dotnet` CLI.

For alpha 2, step 7 must preserve both exact bytes and Unix mode:

```bash
cp --preserve=mode -- "Stardew Valley.deps.json" "StardewModdingAPI-net6.deps.json"
```

The alpha 2 dispatcher validates that file instead of creating or refreshing it at launch.

There is no safe generic raw-extraction update, repair, uninstall, or rollback recipe. Prefer the
verified GUI; otherwise use the legacy console uninstaller with the limitations above or restore the
complete backup made before step 1. Never recursively delete the game folder, `Mods`, saves,
`ErrorLogs`, `HealthReports`, `.smapi-installer`, or other user data. Never use a broad wildcard or
recursive delete to imitate the legacy installer's known-file list.

## Diagnostics, privacy, and limitations

The alpha's `health` and `performance` tools remain local and bounded. Their reports can contain mod
names, IDs, versions, dependency IDs, callback identities, filesystem-derived details, and system
information. Inspect any report before sharing it. The installer and release workflow never upload
the private benchmark modpack or save.

### Graphical-installer diagnostics

The graphical installer stores its local JSONL diagnostics under
`$XDG_STATE_HOME/smapi-installer/logs` when `XDG_STATE_HOME` is an absolute path, or
`~/.local/state/smapi-installer/logs` otherwise. On Linux, its directories are mode `0700` and
its files are mode `0600`. Each file is bounded to 1 MiB and at most five owned log files are
retained. The session also bounds its queues, displayed entries, progress events, and sanitized
clipboard projection. It sends no telemetry and never uploads the log.

The GUI-owned session starts before Avalonia or networking and records only fixed typed events,
stage classifications, and stable error codes from the presentation controllers. It excludes full
game/home paths, URLs and signed query strings, credentials, cookies, response bodies, raw backend
messages, package/release identifiers, digests, mod/save identities, and private workload names.
The viewer captures one immutable snapshot when opened; close and reopen it to refresh. **Copy
sanitized diagnostics** writes at most 32 KiB from at most 128 recent displayed entries, never reads
the clipboard, and asks the user to review the result before sharing.

Normal mutation is fail-closed: if the private log cannot be created before startup, the GUI does
not start Avalonia, network, or game access. If it cannot durably prove readiness immediately before
a new mutating action, that action is not admitted. A logging failure does not rewrite the outcome
of work which was already admitted.

| Graphical installer symptom | Safe next step |
| --- | --- |
| Root or `sudo` refusal | Run as the normal desktop user. The refusal happens before logging, networking, discovery, or mutation. |
| No compatible release | Check connectivity and confirm the catalog is showing this repository's alpha 2. Do not substitute an Actions or pull-request artifact. The verified local-folder route and terminal fallback remain available. |
| Catalog, download, checksum, metadata, or provenance failure | Use the visible retry only after checking connectivity and the selected public release. Verification failure blocks mutation. |
| Backend or protocol failure | Review the bounded diagnostic snapshot, close the GUI, and start a fresh session. Do not infer that files changed unless the typed result says so. |
| Diagnostic log unavailable | Close any other graphical installer session, check free space and normal-user ownership of the XDG state location, then start a fresh session. Do not remove the lock, run as root, or recursively change unrelated permissions. |
| GUI unavailable in a headless or native-Wayland-only session | Use `bash "install on Linux.sh"` from the same verified package, or the documented direct headless command. |

The retained terminal and headless paths are the supported non-GUI fallback, but they run the legacy
install/uninstall implementation rather than the GUI's Core protocol. Last-resort raw extraction is
narrower still and cannot provide Core ownership, planning, receipts, journaling, recovery, history,
or authenticated rollback.

The published performance comparison describes one controlled workstation and workload. It is not
a universal FPS, power, CPU-use, or latency claim. Alpha 2 rechecks path identities to catch ordinary
concurrent changes during dispatcher validation, but same-user adversarial path replacement between
validation and process execution remains outside the nonprivileged launcher's threat boundary.
Native Wayland is experimental; the advertised GUI path is X11 or XWayland. Authentic GNOME/KDE,
desktop-session, AT-SPI, scaling, and complete production-workflow screenshot evidence is still being
captured and must not be inferred from the historical safe-demo screenshot. See the
[comparison](../upstream-comparison.md),
[validation record](linux-alpha-release-validation.md), and
[issue tracker](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues).

## Maintainer release process

The alpha 2 release-preparation pull request passed `Linux alpha release qualification` and the
repository's required performance gate. Hosted CI compiles the game-bound test assembly against pinned public reference
assemblies, then executes the fixture-free runtime-dispatcher and analyzer suites with zero-test
discovery treated as an error. Reference assemblies are deliberately non-executable, so they must
not be presented as a full game-bound test run. The complete `SMAPI.Tests` suite is run separately
against executable game assemblies in the authorized disposable environment, with only sanitized
counts and pass/fail evidence published.

After independent release, security/privacy, testing, and final-diff reviews, the pull request was
merged to `develop`. A non-authoritative candidate was built by dispatching
`linux-alpha-release.yml` with the exact 40-character merge commit, embedded version, and reserved
tag. Its metadata records the actual pre-tag workflow ref; it does not mint a companion manifest
which the installer can accept as release authority. Only after the full test suite, isolated
lifecycle, and trusted-workload qualifications passed for that exact commit was the annotated tag
created at the same commit.

The [tag-triggered alpha 2 workflow](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33669816773)
then rebuilt and qualified the finalized ZIP, created its canonical external install manifest,
emitted `SHA256SUMS` with exactly those two sorted subjects and plural-artifact build metadata, ran
the complete package/manifest authority verification before and after workflow-artifact transfer,
attested both subjects, exported the local attestation bundle and its checksum sidecar, and published
the exact six named release files. The downloaded public six-asset set—not a local package or pre-tag
candidate—then passed final clean-isolated verification and disposable lifecycle tests. These alpha
2 steps do not retroactively describe the historical alpha 1 assets.
