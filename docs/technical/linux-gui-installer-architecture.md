# Linux GUI installer architecture

This document defines the Phase 4 safety boundary for the fork's Linux desktop installer. The
graphical frontend reaches the shared installer core only through the private protocol-host mode.
The retained interactive console/headless installer is a separate legacy install/uninstall fallback;
it is not transactionally equivalent to the GUI and must not be described as another Core frontend.

## Scope and compatibility

- Linux desktop only. Android and other mobile targets are out of scope.
- Normal installs run entirely as the current user. Every entry point refuses effective UID 0 before networking, logging, extraction, game discovery, or mutation; there is no `sudo`, polkit, privileged helper, or ownership-changing flow.
- The existing console/headless install-or-uninstall fallback and last-resort raw installation path
  remain documented with their narrower safety boundary and limitations.
- The GUI is a portable, self-contained `linux-x64` application. It does not change the game's runtime target or include the unrelated .NET 10 menu-click work.
- The first supported desktop path is X11 and XWayland on Wayland sessions. Avalonia's native Wayland backend is experimental and is not advertised as supported until it passes the same qualification matrix.

## Project boundaries

`SMAPI.Installer.Core` is the implemented UI-independent `net6.0` safety library used by the
graphical workflow and its machine-readable protocol host. For that workflow, Core owns:

- game discovery and validation;
- release identity, package metadata, download, checksum validation, and bounded extraction;
- package manifests, installed receipts, inventory, and deterministic planning;
- operation locks, recovery backups, journals, apply/recovery/rollback, and post-verification;
- structured progress, stable error codes, and bounded private logs.

`SMAPI.Installer` contains two intentionally distinct modes. With exactly
`--linux-protocol-v1-jsonl`, it exposes Core's versioned, session-scoped JSONL backend exclusively
for the GUI. One such backend process owns the complete handshake, inspect/plan, confirmation,
revalidation, apply, and result session. Standard output contains protocol JSON only, standard error
is diagnostic, communication uses inherited stdin/stdout with no listener or socket, and the GUI
launches it with an argument list rather than a shell.

Without that private flag, the same apphost runs the retained `InteractiveInstaller`. That legacy
path directly detects a game folder, offers **Install** or **Uninstall**, removes its compiled list of
known SMAPI files, copies the bundled payload, backs up or restores the Unix launcher, and manages
the two bundled mods. It does not request a Core plan and does not create or authenticate a Core
manifest, receipt, journal, recovery generation, recovery-history catalog, or rollback authority.
The private JSONL flag is not a supported manual command-line interface.

The interactive console wrapper accepts no options and rejects any supplied argument with status 2.
Headless callers invoke the apphost directly with `--no-prompt`, exactly one of `--install` or
`--uninstall`, and an absolute
`--game-path`; incomplete non-interactive requests are rejected before game-folder discovery. The
legacy apphost returns 0 after its normal success path, 2 from known validation paths, and 1 when
an unexpected exception escapes; the shell or a signal can produce another status. A nonzero status
can occur after direct mutation began and never establishes unchanged or rolled-back state. Its
human-readable output can contain game paths and full exception text and must be reviewed before
sharing.

Raw `install.dat` extraction has no automatic conflict, ownership, transaction, or recovery safety.
It is documented only as a last-resort fresh install after verifying the outer six-asset release and
making a complete backup. It must never recommend recursive deletion of the game directory, `Mods`,
saves, logs, reports, or `.smapi-installer` state.

`SMAPI.Installer.Gui` is a Linux-only `net10.0` Avalonia 12.1.1 adapter. It selects and verifies releases, stages the matching package/backend, and drives only the structured backend protocol; it never writes the game directory directly. View models are toolkit-independent where practical. The published package is self-contained, untrimmed, and not Native AOT so correctness and accessibility remain observable. Avalonia is pinned because its Linux support includes X11, XWayland, an opt-in experimental native Wayland backend, and AT-SPI2 exposure. X11 and XWayland are the advertised paths; native Wayland remains experimental pending the same desktop evidence matrix.

Production composition now connects the reviewed release-verification, game-discovery, plan-review, explicit confirmation, execution, rollback, interrupted-recovery, and recovery-prune controllers. Launch policy still classifies no arguments as production and exact `--demo` as the sealed synthetic demo. The bridge opens only the exact packaged `SMAPI.Installer` sibling through the core's anchored no-follow executable authority and launches the retained inode through the parent's `/proc/<pid>/fd/<fd>` path, never through `PATH`, a shell, or a later pathname lookup. One session-owned standard-output pump permits one correlated response at a time, rejects already-buffered extra frame bytes before publishing a result, exposes a bounded generic fault for later unsolicited output, and retains verified package/release identity only inside that live backend session. This boundary requires procfs and fails closed when descriptor launch is unavailable; the multi-file backend apphost resolves its companion files from the packaged directory, and a malicious same-UID actor is outside the defensible process boundary.

## Core model

### Release and package identity

A release identity contains the fork-specific tag, semantic fork version, exact commit and tree, package filename and size, SHA-256, and build-workflow identity. Selection accepts only releases from the configured fork repository over HTTPS whose tag matches the fork namespace. A downgrade or prerelease is always labelled explicitly.

The downloader writes a unique mode-0600 sibling staging file, enforces cancellation, timeout and size bounds while streaming, and discards incomplete downloads. Redirects are restricted to HTTPS GitHub release-asset hosts. A tagged release uses an exact six-asset public set: four primary package/metadata assets (the finalized installer ZIP, canonical install-manifest companion, `SHA256SUMS`, and plural-artifact build metadata), plus the GitHub attestation bundle and its checksum sidecar. The package and companion filenames, sizes, digests, tag, version, commit, tree, workflow identity, and artifact records must agree before extraction. Workflow-run URL, runner, reference-build commit, timestamp, and .NET details are bounded informational metadata and are not authenticated by clean-machine verification of the four primary assets. Both checksum subjects are attested. A checksum is never described as provenance; provenance is shown as verified only after the attestation statement and both exact subjects have been verified against the configured repository, tagged workflow, and selected commit.

Core provides a separate acquisition-only authority which accepts only a reviewed catalog candidate and downloads its exact six assets sequentially into a fresh retained same-user mode-0700 Linux workspace. It publishes only exact-size mode-0600 single-link files through anchored no-follow/no-replace handles, retains an opaque lease, and cleans only exact owned identities without recursive or pathname-fallback deletion. The production controller fetches the refreshed tag reference after all six downloads, resolves that exact candidate, and keeps the lease alive until package-open settles. Process-descriptor projections stay inside the reviewed acquisition/backend boundary.

Networking stays in the GUI service boundary behind an injectable transport. Core supplies the
release-identity, digest, metadata-agreement, and bounded-extraction policies so the GUI and its
protocol/package tests cannot disagree. The legacy console installer performs no release download,
checksum, metadata, or attestation verification itself, so users must verify the complete release set
before extracting or running it.

The bounded extractor rejects absolute paths, traversal, links, devices, FIFOs, duplicate or case-colliding entries, excess entry count/depth/expanded size/compression ratio, and unexpected package layout.

### Ownership and inventory

Every accepted tagged package is paired with a separately downloaded, checksummed, metadata-bound, versioned canonical manifest; the manifest is not embedded inside the ZIP whose exact digest and size it binds. Each installer-owned entry records its normalized relative path, kind, SHA-256, size, Unix mode, and ownership category. Production invokes manifest creation only from the exact tagged workflow, and its environment check prevents accidental candidate minting; that check is not cryptographic and a local caller can reproduce it. Pull-request, `develop`, manual-source, and pre-tag dispatch candidates retain their truthful build identity and are not published as authoritative assets. The installer accepts published release authority only after both the ZIP and manifest GitHub attestations are verified against the configured repository, tagged workflow, and selected commit. A successful install atomically stores a receipt with the manifest, release/package identity, launcher-backup identity, and transaction ID. The authoritative receipt and rollback generations remain in the selected game filesystem so one durable transaction can commit or recover them with the files they describe.

Receipts and manifests are untrusted input. Validation rejects absolute or parent-relative paths, invalid separators, links and special files, duplicates and case collisions, and paths outside compiled installer-owned namespaces. A manifest owns listed entries only; it never implicitly owns an entire `smapi-internal` or bundled-mod directory.

Inventory classifies each candidate as:

1. absent;
2. receipt-owned and unchanged;
3. receipt-owned but modified;
4. recognised legacy SMAPI candidate without ownership proof;
5. unknown collision; or
6. unrelated/user-owned and preserved.

Only absent and unchanged receipt-owned entries are safe to replace automatically. Modified, legacy, or unknown entries block the operation unless an exact review screen records an explicit backup-and-replace decision. For repair, the core exposes only deterministic, nonconstructible candidates minted from descriptor-anchored observations of exact modified receipt-owned files; the frontend selects those candidate objects from their still-live source inspection and never opens, stats, or hashes a game path itself. Selection revalidates root identity, operation generation, package authority, file type, size, mode, and digest before the core replans, and partial selection leaves every unselected conflict blocked. Ambiguous `StardewValley-original` state always blocks. Repair changes only missing or explicitly approved corrupt receipt-owned files for the same verified release.

### Deterministic planning

`CreatePlan` is read-only and produces a stable ordered list of creates, replacements, removals, preserved paths, conflicts, the core-authenticated current and expected-result release, an observed-state classification, and exact recovery capacity. Plans distinguish install, update, repair, uninstall, create-backup, and rollback. A full recovery store blocks before confirmation with a stable prune-required conflict. Legacy app-data migration is a separate planned action and never an uninstall side effect.

Both frontends display the same immutable plan identifier. The executor refuses a stale plan when the game root, package identity, inventory fingerprints, or lock generation changed after planning.

Verified package content and committed recovery handles are caller-owned live authorities. Inspections and execution borrow them without taking ownership: the caller keeps each required handle alive until approval or execution completes, may retain it for a safe retry, and disposes it explicitly afterward. Disposing an inspection invalidates its plan and repair candidates only; success, failure, and cancellation never implicitly dispose either borrowed authority. Recovery catalogs and handles expose the authenticated release restored by each generation, including an explicit uninstalled result, so frontends never parse private receipt state or infer a rollback target.

### Transaction and recovery

For every mutating operation, the executor:

1. canonicalizes and opens the selected game root, then acquires an exclusive per-game lock;
2. recovers or rolls back any incomplete prior transaction;
3. validates the immutable plan against a fresh inventory;
4. stages payload and rollback data on the game filesystem;
5. creates a journal and durably records each intent before mutation;
6. moves replaced entries to transaction backup rather than deleting them;
7. installs temporary entries and atomically renames them into place;
8. verifies content hashes, modes, launcher state, and final receipt;
9. atomically commits the receipt and marks the journal complete; and
10. reverses the journal on error or cancellation before reporting the result.

Linux operations are anchored to the canonical game directory and use no-follow relative operations where the runtime permits. Any managed fallback revalidates parent identity immediately before mutation and rejects inode/device changes. Leaf symbolic links are unlinked without traversing their targets. Hard links and special files are conflicts, not install destinations.

A recovery backup contains only entries the installer will change, their content/mode/identity, launcher state, configuration, and receipt. Its canonical snapshot binds both sides of the receipt transition: the receipt expected after the completed operation and the exact prior receipt to restore (either may be absent). This makes install, update/repair, and uninstall rollback restore ownership state in the same transaction as game files instead of inferring or discarding it. It excludes custom mods, saves, logs, and health reports. Authoritative recovery generations live under the game-local private `0700` `.smapi-installer` state so publication, game-file changes, receipt changes, and crash recovery stay on one filesystem; files are private `0600`, history depth and bytes are bounded, and nothing is uploaded. An optional future XDG export may copy an authenticated checkpoint for portability, but cannot become installation authority without a fresh verified import. Destructive tail pruning requires an exact reviewed head digest and explicit confirmation.

Cancellation is accepted during download, verification, inventory, planning, and staging. Fault-isolated progress covers inspection, recovery verification, recovery preparation, payload preparation, transaction staging, revalidation, apply, verification, commit, and rollback; indeterminate updates precede potentially large single-file work instead of claiming byte precision the core doesn't have. Once the short commit begins, the frontend reports “Finishing safely” and waits for commit or recovery rather than promising immediate cancellation.

## GUI interaction model

The GUI uses one resizable window:

- a detected/manual game-installation selector with canonical path and status;
- a release selector with prerelease, update, downgrade, and local-package labelling;
- one context-sensitive primary action: Install, Update, or Repair;
- secondary Create backup, Roll back, Uninstall, and View log actions;
- an exact plan summary grouped into safe changes, preserved items, modified files, and unknown conflicts; and
- a persistent activity area with stage, text, bounded progress announcements, details, and cancellation state.

Confirmation names the game root, current and target release, backup destination, affected counts, conflicts, and preserved data. Uninstall, rollback, downgrade, modified-file replacement, and backup pruning default focus to Cancel. Errors state the cause, whether files changed, whether recovery succeeded, one safe next step, a stable error code, and expandable technical details.

All controls must be operable with Tab/Shift+Tab, arrows, Enter/Space, and Escape. Focus order follows visual order, dialogs restore focus, errors receive focus, and focus remains visibly distinct in light, dark, and high-contrast themes. Controls expose unique accessible names, roles, states, relationships, and validation messages. Layout must remain usable at 100%, 125%, 150%, and 200% scale without hiding actions or requiring horizontal page scrolling.

## Logs and privacy

Production owns exactly one graphical diagnostic session. After the root and argument validation gates pass, it is created and durably records its first fixed event before Avalonia, catalog networking, game discovery, staging, or backend startup. Rejected root/invalid-argument paths and demo mode create no diagnostic state. The GUI observes only typed controller snapshots; the protocol host, Protocol V1 schema, standard output, standard error, and nullable legacy protocol log-path fields are unchanged.

Logs are local-only under `$XDG_STATE_HOME/smapi-installer/logs` when `XDG_STATE_HOME` is an absolute path, or `~/.local/state/smapi-installer/logs` otherwise, with directory mode 0700, file mode 0600, a 1 MiB per-file limit, at most five retained owned files, a 5 MiB aggregate limit, a 256-entry directory-enumeration cap, and no telemetry or upload. Older owned logs rotate when the next session starts. A file accepts at most 2,048 ordinary entries and separately reserves 512 bytes only for its final `session.completed` or `session.ended-unexpectedly` record. Fixed entries contain timestamps, the private session operation ID, reviewed event codes/messages, typed plan/apply/recovery stage classifications, and applicable stable error codes. Exact execution, interrupted-recovery, and recovery-prune terminals additionally map their closed typed outcome, operation class where applicable, durable state, and next action into fixed event codes/messages with truthful severity. The current production observer deliberately does not persist release/package identifiers, desktop-session labels, canonical or relative game paths, raw backend messages, response content, or protocol IDs/digests.

Logs also exclude authentication data, signed URL query strings, cookies, environment dumps, report contents, save names/content, and mod identities. Home and state-root strings are redacted defensively even though controller projection excludes paths. The nonblocking runtime lanes hold at most 128 normal, 64 typed-progress, and 16 controller-terminal events; controller-terminal overflow marks diagnostics unavailable. The newest 256 durably accepted entries feed the bounded viewer. Display eviction, raw-log rejection, and progress coalescence are counted separately; any one changes snapshot health from complete to bounded-with-omissions, while an I/O failure has its own failed health. Every production window exposes **View diagnostic snapshot**, which captures one immutable, path-free in-memory snapshot when opened. The viewer visibly distinguishes that sanitized projection from the richer private rotating JSONL file, states the raw file/count/aggregate/rotation limits, and never shows or opens its resolved path. **Copy sanitized diagnostics** writes at most 32 KiB from at most 128 recent displayed entries and never reads the clipboard. Its three-second confirmation deadline and one session-wide write authority prevent a timed-out viewer or a reopened viewer from issuing another write until the original clipboard provider settles; a fresh explicit attempt is allowed after settlement. The UI says explicitly that the raw local JSONL file requires its own review and is never uploaded automatically.

Startup fails closed if the log cannot be created and written. Immediately before a new mutating action, the GUI durably records a fixed readiness event; failure prevents that admission but does not alter an operation which was already admitted. Runtime event delivery is bounded and nonblocking, progress is coalesced, the dedicated controller-terminal lane is drained first, and record/disposal races settle without exposing private exception text.

## Blocking verification

The complete fixture-free installer suite uses synthetic fixtures, real disposable Linux filesystems, bounded loopback networking, and explicit transaction/recovery fault injectors. It covers deterministic plans and ownership states; receipt/manifest tampering; launcher ambiguity; exact release identity; journal fault injection at durable boundaries; restart recovery; concurrent and stale locks; permission-mode drift; links, hard links, path swaps, special files, and Unicode paths; exact preservation and rollback; bounded/redacted logs; interrupted/oversized/off-host downloads; mismatched metadata/digests; corrupt and hostile archives; monotonic progress; and cancellation boundaries.

Acceptance tests also exercise explicit disk-full, read-only-filesystem, and cross-device failure scenarios through the integrated adapter and package boundaries.

GUI tests cover view-state transitions, keyboard traversal, automation names/roles/states, safe default and restored focus, error focus, scaling and narrow layouts. Exact-package Xvfb smoke, interrupted close/recovery, root refusal before side effects, and public-artifact lifecycle operations have passed. Authentic GNOME and KDE on X11 and Wayland through XWayland, Orca/AT-SPI inspection, scaling captures, and the complete production screenshot matrix remain pending and are not inferred from automated layout tests.

The release workflow builds an exact reviewed commit, retains the console fallback, emits the GUI package and exact six-asset public set (the four primary package/metadata assets plus the attestation bundle and checksum sidecar), verifies the complete package/manifest authority before upload and after each workflow-artifact download, and attests both checksummed subjects. It also runs fixture-free CI. The private trusted workload is used only in its isolated local qualification environment and is never committed or uploaded.

## Delivery record

The Core protocol-host integration, GUI workflow, recovery, diagnostics, local-package import,
and release-preparation slices were independently reviewed and merged into `develop`. Alpha 2 was
then published from exact reviewed commit
[`052699e8`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/052699e8ccba0d13f9d4f02e0bb199aa04cec605)
through the [tag workflow](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33669816773).
Its six freshly downloaded public assets passed inventory, checksum, metadata, manifest,
attestation, package, GUI-smoke, and disposable lifecycle qualification; see the
[sanitized record](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5515036792).
The authentic multi-desktop accessibility and screenshot evidence is the remaining delivery slice.

Alpha 3 is currently a non-public source candidate with planned embedded version
`4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.3` and reserved annotated tag
`fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3`. It does not replace the alpha 2 public delivery
record until exact-merge qualification, trusted-workload qualification, immutable annotated-tag
publication, fresh public-download verification, and a public-package trusted smoke all pass.
