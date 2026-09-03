# Linux GUI hard-state qualification

This protocol defines how to qualify the Linux graphical installer's hardest production states
without modifying the release package or adding a user-visible test mode. It covers the four
[E2 filesystem failures](linux-gui-screenshot-evidence.md#evidence-e2),
[C2 safe finishing](linux-gui-screenshot-evidence.md#evidence-c2),
[C3 cancelled and rolled back](linux-gui-screenshot-evidence.md#evidence-c3),
[E5 interrupted mutation](linux-gui-screenshot-evidence.md#evidence-e5), and
[E6 completed restart recovery](linux-gui-screenshot-evidence.md#evidence-e6).

The supervisor described here is external qualification equipment, not shipped product code. It is
absent from the installer package, default-inert, and permitted to operate only inside a disposable
qualification VM and its disposable game roots. A normal installer launch cannot activate it. The
supervisor never patches, replaces, debugs, or rewrites the GUI or backend; sends no private Protocol
V1 request; and does not construct controller snapshots, accessibility nodes, or pixels. For the
C2/C3 and E5/E6 scheduling cases only, it may preload the separately reviewed external barrier into
the verified process environment. That barrier calls the real `fsync`/`fdatasync` first, never changes
the syscall result or journal bytes, and may hold only the calling backend worker after it observes a
complete durable `Applied` record. This disclosed scheduling aid is not part of the package and is
not performance evidence. The supervisor may otherwise arrange real operating-system boundaries
before launch, operate the real packaged GUI through its accessibility surface, observe the
supervisor-owned filesystem and processes, and pause or terminate only processes it launched for a
case. The later completed qualification supervisor may also stage an authentic window through the
separately reviewed screenshot tool; the implemented preflight does not capture pixels.

This is a qualification specification, not evidence that any case has passed. A checkbox, caption,
or release claim remains pending until the exact public package has completed this protocol and the
result has been independently reviewed.

## Implemented preflight boundary

The checked-in helpers currently implement a fail-closed **preflight**, not the completed screenshot
qualification described by this document:

- `run-linux-gui-hard-state-case.py` creates one private mount namespace and one root-owned direct
  cgroup-v2 child, starts the root-only boundary controller there, and drops the supervisor and every
  product/UI process to the admitted desktop user with no effective capabilities and `no_new_privs`
  set. It copies the admitted contract and single-use boundary request into separately fully sealed
  anonymous files before passing them to the children; neither child reopens the caller pathname;
- `qualify-linux-gui-hard-states.py` binds the package and process identities, drives the packaged UI
  through `drive-linux-gui-hard-states-atspi.py`, coordinates the real filesystem or scheduling
  boundary, inventories the disposable root, and performs bounded process and mount cleanup;
- the AT-SPI driver's signed `capture-ready` hold currently proves only that the exact accessibility
  milestone was observed and held for the supervisor's acknowledgement. It does not prove pixels or
  that a painted frame persisted. The supervisor does **not** yet invoke
  `stage-linux-gui-screenshot.py`, and pixels are not evidence from this preflight; and
- a successful current run emits one case-level `status: "preflighted"` record with
  `exactWindowCaptured: false` and the fixed `capturePending`, `durableClassificationPending`, and
  `publicAuthorityVerificationPending` fields set to true. It never emits `passed` or `qualified`.

The current contract has exactly `schema_version`, `scenario`, `release`, `package`, `game_marker`,
`binaries`, `isolation`, and `timeouts_seconds`. It intentionally has no caller-provided display,
visible-state, durable-state, or screenshot-provenance prose. A later closed capture schema and
state classifier must be reviewed before a PNG can be staged or a qualification checkbox checked.

## Trust and isolation boundary

Run each case from a reverted VM snapshot or a newly created disposable VM. The host's game
installation, Mods directory, saves, home directory, and desktop session must never be mounted into
the guest. Do not use the private trusted modpack or save: these cases require only public package
bytes and small synthetic, redistribution-safe game fixtures. Disable shared folders, clipboard
sharing, drag-and-drop, host SSH agents, credential stores, and unrelated network access. Permit
network access only long enough to download and verify the pinned public release, or transfer the
already verified six public assets through a read-only channel.

Create one generic unprivileged desktop account for the run. Its home, state, cache, temporary,
download, and game roots are new and contain no personal name. Root may be used by the fixed broker
and separate setup controller only to create the private mount namespace, loop filesystems,
ownership, and fault boundaries. The broker drops the supervisor to the generic account before any
product or UI process starts; the controller remains root only inside that same namespace and never
drives the UI. The GUI, backend, terminal fallback, AT-SPI client, screenshot tool, and all child
processes must have that account's real and effective UID and must never have ambient, inheritable,
or effective capabilities. A root-owned controller must not join the desktop D-Bus session or send
installer actions.

Use a dedicated graphical session containing no unrelated client windows. X11 and XWayland are the
supported capture paths; native Wayland is not inferred from them. Record distribution, architecture,
desktop, session type, display backend, scale, theme, resolution, compositor, Avalonia version, .NET
runtime, and whether an SDK is installed or used. The environment record is private until reviewed.

## Exact production identity

Pin the public release before any case starts. Freshly download its exact six-asset inventory and
verify the immutable annotated tag, peeled source commit and tree, canonical package URL, asset sizes,
SHA-256 checksums, release metadata, install manifest, local attestation bundle and sidecar, and GitHub
attestation where available. Extract the verified ZIP once into a new mode-`0700` directory owned by
the generic account. Do not rebuild or substitute a candidate artifact.

Record and recheck all of these bindings for every case:

- release tag, source commit and tree, public package URL and SHA-256;
- GUI and backend executable relative package names, sizes, modes, device/inode identities, and
  SHA-256 values before launch and after settlement;
- GUI and backend PID, process start time, real/effective UID, process group, cgroup, executable
  device/inode identity, and SHA-256 read from the bounded `/proc/<pid>/exe` identity;
- the backend's verified descendant relationship to the launched GUI session and the absence of an
  unexpected sibling process; and
- the one visible application window's exact production title, `_NET_WM_PID`, X client identity,
  display, geometry, and screenshot-time GUI PID.

The package hashes are authority; a PID, window title, X property, AT-SPI application node, and
parent/child relationship are supporting observations and can be spoofed by another process in the
same session. The isolated display, exact executable identity, before-and-after hashing, and a human
original-resolution review are all required. If the process exits and a PID is reused, or any
identity changes, discard the case instead of rebinding it.

## Supervisor admission and bounds

The implemented preflight supervisor accepts the exact eight-field contract listed above. It rejects
unknown fields, duplicate keys, host/live paths, symlinks, relative traversal, an existing output
directory, an unverified caller-bound package identity, a non-disposable root, and any UID-zero
application command. Scenarios are enums; no manifest field is interpreted as a shell fragment. The
contract and fixed helper executables are reviewed before the VM starts. The future qualification
contract must additionally bind the desktop environment, fixture profile, operation, expected
visible state, and expected durable result through closed enums, not free text.

The completed qualification implementation must enforce explicit bounds no weaker than these:

- one case and one application process group at a time, with at most 32 observed descendants;
- one private mode-`0700` run root, at most eight supervisor-created mounts, and no mount outside its
  dedicated mount namespace and validated run root;
- a 30-minute hard deadline per case, including capture and settlement, and a two-minute cleanup
  deadline;
- at most 1 GiB of private logs, inventories, traces, and images per case, failing closed before the
  limit is exceeded; and
- one final capture per evidence row, except E2's exactly four retained source captures. Repeated
  diagnostic attempts remain private and cannot silently replace the admitted source.

The current preflight enforces the process/mount/deadline/entry/per-file bounds and rejects a private
output tree above 1 GiB at final inspection. It does not yet place the output on a quota-limited
filesystem, so it does not claim that aggregate writes are stopped before crossing 1 GiB. That
pre-write aggregate resource gate remains part of the later qualification work.

The supervisor records its own monotonic event sequence. Timeouts, bound violations, ambiguous
process/window/accessibility identity, missed trigger windows, incomplete cleanup, or unexpected
extra processes fail the case. They are not converted into an installer success or failure.

## Disposable Ubuntu/Kubuntu preflight invocation

This invocation exercises the implemented preflight only. It cannot produce publishable screenshot
evidence or a `qualified` result.

Inside a disposable Ubuntu/Kubuntu 24.04 VM, install the external qualification dependencies:

```sh
sudo apt-get update
sudo apt-get install --no-install-recommends \
  python3-pyatspi at-spi2-core gsettings-desktop-schemas \
  imagemagick x11-utils xwayland gcc libc6-dev util-linux e2fsprogs git
```

From the generic user's logged-in graphical session, enable the desktop accessibility bus and fail
closed unless the session and direct X11/XWayland capture prerequisites are present:

```sh
gsettings set org.gnome.desktop.interface toolkit-accessibility true
test "$(id -u)" -ne 0
test -n "${DISPLAY:-}"
test -n "${DBUS_SESSION_BUS_ADDRESS:-}"
test -n "${XDG_RUNTIME_DIR:-}"
case "${XDG_SESSION_TYPE:-}" in x11|wayland) ;; *) exit 1 ;; esac
command -v python3 unshare mount umount mkfs.ext4 \
  xwininfo xprop import cc >/dev/null
timeout 10s /usr/bin/python3 -c \
  'import pyatspi; assert pyatspi.Registry.getDesktop(0) is not None'
xprop -root _NET_CLIENT_LIST >/dev/null
import -version >/dev/null
```

In the same shell, pin these values from the already reviewed public release record. Validate every
value before it is used in a path, Git command, or privileged command. The package must already have
been downloaded with exclusive creation into a fresh private runtime input directory; do not
continue with a local build, caller-invented digest, or symlink:

```sh
set -euo pipefail
: "${release_version:?exact fork prerelease version}"
: "${release_commit:?exact 40-character release commit}"
: "${release_tree:?exact 40-character release tree}"
: "${package_sha256:?exact published package SHA-256}"
: "${scenario:?one exact E2-permission/E2-read-only/E2-disk-full/E2-cross-device/C2/C3/E5/E6 value}"
[[ "$release_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+-unofficial\.4eh5xitv6787h645ebv\.linux\.alpha\.[1-9][0-9]*$ ]]
[[ "$release_commit" =~ ^[0-9a-f]{40}$ ]]
[[ "$release_tree" =~ ^[0-9a-f]{40}$ ]]
[[ "$package_sha256" =~ ^[0-9a-f]{64}$ ]]
case "$scenario" in
  E2-permission|E2-read-only|E2-disk-full|E2-cross-device|C2|C3|E5|E6) ;;
  *) exit 1 ;;
esac
test "$XDG_RUNTIME_DIR" = "/run/user/$(id -u)"
input_root="${XDG_RUNTIME_DIR}/smapi-hard-state-inputs"
test -d "$input_root"
test ! -L "$input_root"
test -O "$input_root"
test "$(stat -c %a -- "$input_root")" = 700
package_path="${input_root}/SMAPI-${release_version}-linux-x64-installer.zip"
test -f "$package_path"
test ! -L "$package_path"
test -O "$package_path"
test "$(stat -c %h -- "$package_path")" = 1
test "$(stat -c %a -- "$package_path")" = 600
test "$(sha256sum -- "$package_path" | cut -d' ' -f1)" = "$package_sha256"
```

Install the exact reviewed harness tree at the release commit beneath
`/opt/smapi-hard-state-harness`, owned by root and not writable by group or other users. These
commands are for a newly reverted VM where the target does not exist:

```sh
sudo test ! -e /opt/smapi-hard-state-harness
sudo git clone --no-checkout https://github.com/4eh5xitv6787h645ebv/SMAPI.git \
  /opt/smapi-hard-state-harness
sudo git -C /opt/smapi-hard-state-harness checkout --detach "$release_commit"
test "$(sudo git -C /opt/smapi-hard-state-harness rev-parse HEAD)" = "$release_commit"
test "$(sudo git -C /opt/smapi-hard-state-harness rev-parse 'HEAD^{tree}')" = "$release_tree"
sudo chown -R root:root /opt/smapi-hard-state-harness
sudo chmod -R go-w /opt/smapi-hard-state-harness
test -z "$(sudo git -C /opt/smapi-hard-state-harness status --short)"
```

Create a new fixed root-owned prefix and one atomically new random case root. The parent is not
writable by the desktop user, so another user process cannot pre-create or replace the generated
child between `mkdir` and `chown`. Run the prefix creation once per newly reverted VM; for each later
scenario in that VM, re-verify the existing prefix identity and repeat only the random case-root
creation with a new name:

```sh
run_uid="$(id -u)"
run_gid="$(id -g)"
test "$run_uid" -ne 0
sudo test ! -e /srv/smapi-hard-state
sudo test ! -L /srv/smapi-hard-state
sudo mkdir -m 0711 -- /srv/smapi-hard-state
sudo chown root:root /srv/smapi-hard-state
test "$(sudo stat -c '%u:%g:%a' -- /srv/smapi-hard-state)" = "0:0:711"
case_name="smapi-hard-state-$(python3 -c 'import secrets; print(secrets.token_hex(16))')"
case_root="/srv/smapi-hard-state/${case_name}"
sudo mkdir -m 0700 -- "$case_root"
sudo chown -- "${run_uid}:${run_gid}" "$case_root"
test -O "$case_root"
test "$(stat -c %a -- "$case_root")" = 700
```

Run the reviewed nonroot preparer. It stable-reads the checked-in reviewable Base64 fixture at
`build/fixtures/linux-gui-hard-state/Stardew Valley.dll.base64`, verifies its exact encoded SHA-256
`0d73da2d4c7e7c7553033e359a6e1808c0e0be5ccd5f0f6fa781c7efc23fd0cf`, decodes it, and verifies the
exact 3,584-byte managed-assembly SHA-256
`8617cb5b0132c275d2db285d7a6475ea326a2387f1fe98cb0c4d4218c6a15744`. That assembly was built from
`src/SMAPI.Installer.TestGameMarker` using the repository-pinned SDK, `Release`,
`DebugType=None`, `DebugSymbols=false`, `ContinuousIntegrationBuild=true`, and a `/source` path map;
its assembly name is `Stardew Valley`, version `1.6.15.0`, and it contains no game code or assets.
Reviewers can reproduce the decoded bytes from a clean checkout with the pinned `global.json` SDK:

```sh
dotnet clean src/SMAPI.Installer.TestGameMarker/SMAPI.Installer.TestGameMarker.csproj \
  -c Release --nologo --verbosity quiet
dotnet build src/SMAPI.Installer.TestGameMarker/SMAPI.Installer.TestGameMarker.csproj \
  -c Release --nologo --verbosity quiet \
  -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true \
  -p:PathMap="$PWD=/source"
test "$(sha256sum 'src/SMAPI.Installer.TestGameMarker/bin/Release/net6.0/Stardew Valley.dll' | cut -d' ' -f1)" = \
  8617cb5b0132c275d2db285d7a6475ea326a2387f1fe98cb0c4d4218c6a15744
cmp -- \
  <(base64 -d 'build/fixtures/linux-gui-hard-state/Stardew Valley.dll.base64') \
  'src/SMAPI.Installer.TestGameMarker/bin/Release/net6.0/Stardew Valley.dll'
```

The helper creates that immutable marker as mode `0600` inside the new private contract directory;
there is no caller-supplied game-marker argument or path. It verifies ZIP structure and equality to
the caller-supplied digest from the reviewed public record without extracting it, hashes the packaged
GUI/backend entries, binds the case-root device/inode, creates the exact mode-`0600` disposable
marker and eight-field contract, and chooses a new output basename. It never downloads a package or
accepts the private modpack, save, or a caller-supplied game assembly. Public release authority
remains pending until the separate final public-asset verification.

```sh
prepared_json="$(
  /usr/bin/python3 \
    /opt/smapi-hard-state-harness/build/scripts/prepare-linux-gui-hard-state-case.py \
    --case-root "$case_root" \
    --package "$package_path" \
    --expected-package-sha256 "$package_sha256" \
    --version "$release_version" \
    --commit "$release_commit" \
    --tree "$release_tree" \
    --scenario "$scenario"
)"
contract_path="$(printf '%s' "$prepared_json" | python3 -c \
  'import json,sys; value=json.load(sys.stdin); assert value["ok"] is True; print(value["contractPath"])')"
output_path="$(printf '%s' "$prepared_json" | python3 -c \
  'import json,sys; value=json.load(sys.stdin); assert value["ok"] is True; print(value["outputPath"])')"
test -f "$contract_path"
test ! -e "$output_path"
```

For E2, C2, C3, E5, and E6 use independent case roots and preparation records; never reuse a failed
or completed root.

Invoke the fixed broker from the graphical session so it can pass only the existing display/bus
coordinates to the dropped child:

```sh
sudo --preserve-env=DISPLAY,WAYLAND_DISPLAY,DBUS_SESSION_BUS_ADDRESS,\
AT_SPI_BUS_ADDRESS,XAUTHORITY,XDG_RUNTIME_DIR \
  /usr/bin/python3 \
  /opt/smapi-hard-state-harness/build/scripts/run-linux-gui-hard-state-case.py \
  --contract "$contract_path" \
  --output "$output_path" \
  --execute
```

The broker unshares one mount namespace before forking and admits both children into one exact
root-owned cgroup-v2 child before either can spawn. Only its controller child remains root; the
supervisor, GUI, backend, AT-SPI operator, compiler, and future screenshot stage run as the contract
owner with cleared supplementary groups, no effective capabilities, and `no_new_privs`. The broker
stable-reads each user-owned protocol file once, copies those exact bytes into a fully sealed anonymous
file, atomically consumes the matching single-use request, and binds the controller PID, command line,
script, sealed descriptor, source-request inode, socket, and mount namespace before accepting an ACK.
The root controller does not receive or connect to the desktop bus. A current `preflighted` record is
useful private diagnostics only: capture, final durable classification, exact six-public-asset
authority, and the pre-write aggregate resource gate remain pending.

## Authentic UI operation and capture

All installer choices and actions must be reached through the packaged GUI. The AT-SPI driver finds
the exact application/window subtree, records each target's role, accessible name, enabled/visible
state, and action interface, then invokes that exact exposed action. It fails closed if the action is
missing, disabled, ambiguous, or lacks its expected AT-SPI action interface. Fixed keyboard input is
used only for the verified native folder picker (`Ctrl+L`, the already admitted path, and Enter); it
is not a fallback for missing installer actions. The driver must not call view-model methods,
synthesize backend events, use a private protocol client, or bypass a confirmation. Destructive
confirmation must begin with visible focus on Cancel.

An AT-SPI transcript proves only what the accessibility tree exposed and what action the driver
requested. It does not prove pixels, filesystem effects, durable state, or that a request won a race.
Record the relevant heading/status/live-region text immediately before capture and correlate its
monotonic observation with the private supervisor timeline. Separately prove filesystem and durable
state from the before/after records below.

Capture only the exact application window with
`build/scripts/stage-linux-gui-screenshot.py` in its direct X11/XWayland mode. Supply the exact
visible production title, GUI PID, reviewed executable identity, and evidence-ID-specific
qualification reference. The tool's X properties and PID check are advisory, so the controlled
display and human original-resolution inspection remain mandatory. Do not capture the desktop,
panel, notification, terminal, or accessibility inspector. Do not crop, annotate, redact, or compose
inside the supervisor.

The supervisor may make a short, recorded `SIGSTOP`/`SIGCONT` hold of the verified GUI process only
after the required state has been painted and observed through AT-SPI. It must not stop the backend to
manufacture a longer mutation or rollback state. A held GUI image is a truthful capture of the last
painted application state, not proof that the backend remained in that state; record backend progress
and the eventual terminal separately. If the heading cannot be captured before it naturally changes,
repeat the disposable case with a larger public synthetic payload or slower isolated block device.
Do not alter product timing, inject sleeps, or publish a reconstructed image.

For E2, stage four distinct original PNGs with the exact `--fault` values `permission`, `read-only`,
`disk-full`, and `cross-device`. Each source keeps its own operation, injection, before/after durable
state, environment, capture method, and privacy review. Assemble the single E2 contact sheet only
after all four originals pass review, retain every source unchanged, and record composition as the
screenshot contract requires.

## Before, trigger, capture, and settlement

Every case follows the same order:

1. Revert the VM or create a new disposable root. Verify the public package and executable identity.
2. Record the private pre-case inventory and mount/process baseline. Launch the GUI as the generic
   account and bind its GUI, backend, AT-SPI, and window identities.
3. Reach and confirm the real operation through AT-SPI. Arm only the admitted external boundary for
   this case; do not arm another case's fault.
4. Record the trigger, the product's visible state, the exact application-window capture, and any
   durable transition on one monotonic timeline. A screenshot never substitutes for the transition
   record.
5. Resume a held GUI, allow the packaged processes to settle unless E5 requires termination, and
   record the exact terminal presentation. Close through the GUI where possible.
6. Record the post-case inventory, journal/receipt/recovery disposition, process settlement, package
   hashes, and mount state. Perform bounded cleanup, then destroy or revert the VM.
7. Emit only the fixed sanitized aggregate. Retain detailed records and images privately until their
   separate privacy and evidence reviews finish.

The private inventory recursively records only the disposable fixture and installer-state roots. It
uses no-follow traversal and includes a bounded entry count, relative generic path, file type, mode,
UID/GID, size, device/inode/link count, and SHA-256 for bounded regular files. It also records the
receipt, recovery generations, transaction/journal state, mount IDs/options/capacity, and package
identity. Record a cryptographic digest of the canonical inventory. Refuse special files, unexpected
mount crossings, entry-count overflow, oversized files, or a changed root identity. The public record
may state only fixed counts and durable-state enums; it must not include the inventory or its paths.

### E2: real filesystem failures

Use four independent disposable roots and four independent captures:

| Fault | External boundary | Required proof |
| --- | --- | --- |
| `permission` | Remove the generic account's required access at a predeclared fixture-owned leaf or parent, without changing package files. | The real operation receives an OS permission refusal; the UI states whether files changed and gives one safe next step; inventory and durable state agree. |
| `read-only` | Remount the dedicated fixture filesystem read-only in its private mount namespace before the admitted write. | The real operation receives `EROFS`; no host or package mount is changed; UI, inventory, and durable state agree. |
| `disk-full` | Use a bounded loop filesystem or quota with deliberately insufficient free blocks/inodes for the admitted operation. | The real operation receives `ENOSPC`; retained capacity evidence proves the boundary; UI, inventory, and durable state agree. |
| `cross-device` | Place the predeclared source and destination on distinct supervisor-owned mounts so the production operation encounters a real `EXDEV` boundary. | Mount IDs prove the boundary and the real operation reports it without an injected exception; UI, inventory, and durable state agree. |

The setup controller may use root to create or remount these filesystems, but it must complete or arm
the one narrowly defined transition before returning control to the nonroot desktop driver. Do not
change permissions on a live or shared path. If product policy makes a proposed `EXDEV` route
unreachable, redesign the disposable mount topology or report the case blocked; do not preload a
library, replace a syscall, throw a synthetic exception, or label another error `cross-device`.

Each fault must show its actual product-visible error class, whether files changed, and one safe next
step. A generic failure screen, a unit-test projection, or identical copied pixels cannot satisfy E2.

### C2 and C3: cancellation after durable mutation

Use a real install, update, repair, uninstall, backup, or rollback plan with enough bounded synthetic
work to expose an active Cancel action. Invoke Cancel through its visible AT-SPI action while the
operation is running. The product may observe the request before mutation, after a complete mutation
and durable `Applied` journal record, or after the final commit boundary; the supervisor must not
reclassify those outcomes.

To make the post-`Applied` race repeatable, the reviewed external barrier may hold the backend worker
only after the real journal sync returns successfully and the complete canonical record has been
observed through the exact descriptor. The supervisor must bind the shim, backend PID/start time,
executable hash, root identity, current-user private socket, operation index, and bounded release.
It invokes Cancel through AT-SPI while that worker is held, then sends the barrier's exact release.
Missing, malformed, wrong-process, wrong-root, non-`Applied`, timed-out, or disconnected control state
must leave the shim inert or release the worker; it must never forge an error or cancellation result.

For C2, capture the real visible `Cancellation requested`/`Finishing safely` state after AT-SPI shows
the operation cancellation is already requested and immediate termination is no longer promised.
The copy must allow unchanged, rolled-back, committed-after-a-late-request, and recovery-required
outcomes if rollback fails. A short GUI-only capture hold is permitted under the rule above.

For C3, admit only a run whose validated terminal is `CancelledAndRolledBack`, durable state is
`RolledBack`, recovery disposition is completed, and next action requires a fresh inspection. The
after inventory must prove all operation changes were restored and unrelated fixture files remained
unchanged. A before-mutation cancellation is truthful but does not satisfy C3; a late request that
commits must be recorded as committed and does not satisfy C3; a rollback failure must be recorded as
recovery required and does not satisfy C3. Repeat only from a reverted root.

### E5 and E6: interruption and restart recovery

For E5, start a real mutating operation through the GUI. The external controller watches only its
disposable filesystem and process tree. After it proves that a journaled mutation has occurred but
before a terminal commit or completed rollback, terminate only the exact verified backend with
`SIGKILL`. This deliberate crash is the fault boundary; it must not be described as a product
cancellation. Keeping the already bound GUI alive lets the production process client observe the
lost backend and present its truthful state-unknown/recovery-required result. Do not kill between
intent publication and an individual filesystem operation merely to force corrupt data. The
post-crash inventory must independently prove an incomplete durable transaction requiring recovery.

The same post-sync barrier may be used only to prove and hold that exact E5 boundary long enough for
the supervisor to revalidate the backend identity and send `SIGKILL`; the worker is not released in
that case. The barrier does not manufacture the interruption or alter filesystem calls—the external
signal does. Record its use in the private provenance and do not present the timing as a naturally
long application state.

Capture E5 in that surviving GUI only after it exposes the exact state-unknown heading, explains that
a fresh session is required, offers Close rather than an unusable recovery action, and the private
journal/inventory evidence agrees. Then close/reap the GUI and restart the same unchanged packaged
GUI against the same disposable root for E6. Do not edit the journal, receipt, or files between crash
and restart. Drive the normal verified selection, inspection, confirmation, and Run actions. The
fresh backend automatically recovers the incomplete journal before attempting the newly confirmed
operation and must return `AutomaticRecoveryCompletedFreshInspectionRequired` instead of continuing
with a stale plan. Capture the real completed-recovery result only after it requires a fresh
inspection. The final inventory must match the proven pre-operation state at every managed location,
preserve unrelated files, and show no incomplete transaction. If recovery fails, retain the private
failure evidence, report E6 failed, and do not claim completion from reassuring pixels.

## Private evidence and sanitized output

Everything retained as detailed evidence remains inside the private mode-`0700` run root: package
extraction, full process and AT-SPI transcripts, mount data, inventories, journals, receipts,
diagnostic JSONL, console output, exception text, original and rejected images, screenshot sidecars,
and cleanup logs. Files are mode `0600`. The controller transiently writes two root-owned mode-`0600`
ledger files beneath the root-owned mode-`0711` prefix so the unprivileged account cannot replace
them. After the controller and its cgroup settle, the broker deletes only those exact captured
inode/name/metadata pairs; an unsafe replacement or incomplete removal fails cleanup. Review retained
files before sharing because console and diagnostic material may contain full paths or exception text
even when the GUI is sanitized. Never commit or upload the run root.

The implemented supervisor emits one bounded, closed-schema record for one preflight case. A success
contains the fixed identity and observation/cleanup booleans documented under
[Implemented preflight boundary](#implemented-preflight-boundary); a failure contains only its fixed
kind/schema/status and one reviewed failure-code enum. The root broker independently validates that
closed result and returns a nonzero exit status for a failed child. The field `publicReleaseUrl`
names the release page; it must not be mislabeled as a direct package URL.

The later qualification aggregator must combine exactly the four E2 sources plus C2, C3, E5, and E6.
Its only allowed content is:

- schema version and the fixed qualification kind;
- public release tag, source commit/tree, package URL and SHA-256, GUI/backend SHA-256;
- enumerated environment values: distribution release, architecture, desktop, session/backend,
  scale, theme, and resolution;
- for each admitted case, the case ID, enumerated operation/fault/trigger, `passed` boolean, exact
  expected and observed visible-state enum, before/after durable-state enums, AT-SPI-action-observed,
  exact-window-captured, inventory-verified, package-identity-reverified, and cleanup-complete booleans;
- fixed integer counts for admitted, passed, failed, captured, and cleaned cases; and
- an overall `passed` boolean and one optional fixed failure-code enum.

Reject unknown keys and out-of-range counts. Neither the preflight record nor final aggregate may
emit timestamps precise enough to identify a
private run, host/user names, paths, PIDs, device/inode/mount IDs, cgroup names, process arguments,
environment variables, package extraction names, inventory hashes, screenshot hashes before privacy
approval, URLs other than the pinned public release/package URL, exception or backend text, log or
image content, mod/save identities, credentials, or arbitrary failure descriptions. A failed case
uses a reviewed enum such as `identity`, `timeout`, `state`, `inventory`, `capture`, `privacy`, or
`cleanup`; details remain private. Scan the serialized aggregate against the private path/token
denylist before it leaves the VM.

After original-resolution human privacy review, the unchanged staged PNGs and their path-free
sidecars may be used to create the separate screenshot manifest and dedicated anchored qualification
record. That later publication follows the
[screenshot evidence contract](linux-gui-screenshot-evidence.md#privacy-and-provenance); the
supervisor aggregate alone neither publishes nor authenticates an image.

## Cleanup

Cleanup acts only on exact recorded identities. Stop the bound nonroot process group with `SIGTERM`,
wait a bounded grace period, then use `SIGKILL` only for the same still-matching start-time/cgroup
members. The root broker finally uses `cgroup.kill`, requires `populated 0`, and removes that exact
root-owned cgroup child; failure remains failure. It removes only an exact captured residual request
and exact root-ledger objects. Never use name-based `pkill`, a broad UID kill, recursive host deletion,
an unresolved variable, glob, or path inferred from application output. Refuse PID reuse and
unexpected descendants.

Unmount the at-most-eight recorded guest mounts leaf-first by exact mount ID and validated target.
Do not use lazy or forced unmount to turn an uncertain cleanup into success. If a mount or process
does not settle within the two-minute cleanup budget, mark cleanup failed and destroy the whole VM
from the host hypervisor after closing its private evidence sink. Delete only the exact newly created
guest run root after required private evidence has been copied to its approved private retention
location. VM destruction is the final containment boundary; it is never a substitute for recording a
cleanup failure.

## Definition of pass

A hard-state qualification passes only when all of the following are true:

- the exact freshly downloaded public package and both running executables retain their reviewed
  identities before, during, and after every applicable case;
- the GUI and backend run only as the generic nonroot account, with no capabilities, against new
  disposable roots in the isolated VM and display;
- AT-SPI reaches the real packaged controls, records their accessible roles/names/states, invokes the
  real actions and confirmations, and the exact application window is captured without mocks,
  reconstruction, injected protocol events, or product modification;
- E2 retains exactly one authentic source for each real `permission`, `read-only`, `disk-full`, and
  `cross-device` failure, and every source's UI statement matches its before/after inventory and
  durable state;
- C2 shows truthful safe-finishing semantics and C3 proves an exact cancelled-and-rolled-back durable
  terminal plus restored managed state and preserved unrelated files;
- E5 proves a real externally interrupted incomplete transaction and the surviving GUI's truthful
  backend-loss/recovery-required state; E6 proves a fresh launch of the unchanged public package
  automatically completed recovery and required a fresh inspection;
- every timeout, identity, process, mount, filesystem, inventory, durable-state, accessibility,
  capture, privacy, and cleanup check passes within its bound;
- private logs, inventories, images, and sidecars remain private; the fixed aggregate passes its
  closed-schema and denylist checks; and each image separately passes original-resolution human
  privacy review, staging validation, manifest validation, and evidence-ID-specific review before
  publication; and
- an independent reviewer confirms the commands, production identity, timeline, before/after proof,
  visible wording, retained E2 sources, C2/C3 and E5/E6 classification, aggregate, and cleanup record.

Partial success is not an overall pass. A valid late cancellation commit, before-mutation
cancellation, rollback failure, recovery failure, missed short-lived screen, or inaccessible AT-SPI
action is useful private diagnostic evidence but cannot be relabelled as the required state. The
screenshots demonstrate appearance at a recorded moment; they do not establish universal desktop
compatibility, performance, absence of every filesystem race, or success on hardware and
distributions outside the recorded environment.
