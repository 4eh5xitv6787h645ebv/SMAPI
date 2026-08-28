# Linux GUI installer architecture

This document defines the Phase 4 safety boundary for the fork's Linux desktop installer. It is deliberately narrower than the user interface: both the existing console installer and the graphical frontend must call the same installer core, and neither frontend may implement file ownership or mutation rules itself.

## Scope and compatibility

- Linux desktop only. Android and other mobile targets are out of scope.
- Normal installs run entirely as the current user. Every entry point refuses effective UID 0 before networking, logging, extraction, game discovery, or mutation; there is no `sudo`, polkit, privileged helper, or ownership-changing flow.
- The existing console and manual installation paths remain supported and documented.
- The GUI is a portable, self-contained `linux-x64` application. It does not change the game's runtime target or include the unrelated .NET 10 menu-click work.
- The first supported desktop path is X11 and XWayland on Wayland sessions. Avalonia's native Wayland backend is experimental and is not advertised as supported until it passes the same qualification matrix.

## Project boundaries

`SMAPI.Installer.Core` is a UI-independent `net6.0` library used by the console installer and GUI. It owns:

- game discovery and validation;
- release identity, package metadata, download, checksum validation, and bounded extraction;
- package manifests, installed receipts, inventory, and deterministic planning;
- operation locks, recovery backups, journals, apply/recovery/rollback, and post-verification;
- structured progress, stable error codes, and bounded private logs.

`SMAPI.Installer` remains the console adapter and the only backend process allowed to mutate a game directory. Its prompts describe a core-generated plan and then invoke the core; it must not retain an independent mutation path. It also exposes a versioned one-shot JSONL protocol for the GUI. One backend process owns the complete handshake, inspect/plan, confirmation, revalidation, apply, and result session. Standard output contains protocol JSON only, standard error is diagnostic, communication uses inherited stdin/stdout with no listener or socket, and the GUI launches it with an argument list rather than a shell.

`SMAPI.Installer.Gui` is a Linux-only `net10.0` Avalonia 12.1.1 adapter. It selects and verifies releases, stages the matching package/backend, and drives only the structured backend protocol; it never writes the game directory directly. View models are toolkit-independent where practical. The first package is self-contained, not trimmed, and not Native AOT so correctness and accessibility remain observable. Avalonia is pinned because its current Linux support includes X11, XWayland, an opt-in experimental native Wayland backend, and AT-SPI2 exposure. If packaged Orca/AT-SPI qualification fails, GTK 4 is the documented fallback rather than weakening the acceptance criteria.

## Core model

### Release and package identity

A release identity contains the fork-specific tag, semantic fork version, exact commit and tree, package filename and size, SHA-256, and build-workflow identity. Selection accepts only releases from the configured fork repository over HTTPS whose tag matches the fork namespace. A downgrade or prerelease is always labelled explicitly.

The downloader writes a unique mode-0600 `.part` file, enforces cancellation, timeout and size bounds while streaming, and discards incomplete downloads. Redirects are restricted to HTTPS GitHub release-asset hosts. The package filename, size, digest, tag, version, commit, tree, and embedded build metadata must agree before extraction. A checksum is never described as provenance; provenance is shown as verified only after an attestation has actually been verified.

Networking stays in the GUI service boundary behind an injectable transport. The core supplies the release-identity, digest, metadata-agreement, and bounded-extraction policies so protocol and console/package tests cannot disagree with the GUI.

The bounded extractor rejects absolute paths, traversal, links, devices, FIFOs, duplicate or case-colliding entries, excess entry count/depth/expanded size/compression ratio, and unexpected package layout.

### Ownership and inventory

Every package contains a versioned canonical manifest. Each installer-owned entry records its normalized relative path, kind, SHA-256, size, Unix mode, and ownership category. A successful install atomically stores a receipt with the manifest, release/package identity, launcher-backup identity, and transaction ID. The authoritative receipt and rollback generations remain in the selected game filesystem so one durable transaction can commit or recover them with the files they describe.

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

`CreatePlan` is read-only and produces a stable ordered list of creates, replacements, removals, preserved paths, conflicts, warnings, required backup bytes, and the expected final receipt. Plans distinguish install, update, repair, uninstall, create-backup, and rollback. Legacy app-data migration is a separate planned action and never an uninstall side effect.

Both frontends display the same immutable plan identifier. The executor refuses a stale plan when the game root, package identity, inventory fingerprints, or lock generation changed after planning.

Verified package content and committed recovery handles are caller-owned live authorities. Inspections and execution borrow them without taking ownership: the caller keeps each required handle alive until approval or execution completes, may retain it for a safe retry, and disposes it explicitly afterward. Disposing an inspection invalidates its plan and repair candidates only; success, failure, and cancellation never implicitly dispose either borrowed authority.

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

Cancellation is accepted during download, verification, inventory, planning, and staging. Once the short commit begins, the frontend reports “Finishing safely” and waits for commit or recovery rather than promising immediate cancellation.

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

Logs are local-only under `${XDG_STATE_HOME:-~/.local/state}/smapi-installer`, with directory mode 0700, file mode 0600, rotation by count and total bytes, and no telemetry or upload. They contain timestamps, operation/release identity, normalized desktop-session type, relative installer-owned paths, plan/apply/recovery stages, and sanitized errors.

Logs exclude authentication data, signed URL query strings, cookies, response bodies, environment dumps, report contents, save names/content, and mod identities. Full home/game paths are redacted by default. “Copy sanitized diagnostics” creates a separately labelled bounded summary; the UI never implies that a raw local log is safe to publish.

## Blocking verification

The core test suite uses synthetic fixtures and injected filesystems/network/time/process probes. It covers deterministic plans and ownership states; receipt/manifest tampering; launcher ambiguity; exact release identity; journal fault injection before and after each operation; restart recovery; disk/permission/read-only/cross-device failures; concurrent and stale locks; links, hard links, path swaps, special files, and Unicode paths; exact preservation and rollback; bounded/redacted logs; interrupted/oversized/off-host downloads; mismatched metadata/digests; corrupt and hostile archives; monotonic progress; and cancellation boundaries.

GUI tests cover view-state transitions, keyboard traversal, automation names/roles/states, safe default and restored focus, error focus, scaling and narrow layouts. Packaged desktop qualification covers Xvfb/X11, GNOME and KDE on X11 and Wayland through XWayland, Orca/AT-SPI inspection, interrupted close/recovery, root refusal before side effects, and exact public-artifact lifecycle operations.

The release workflow builds an exact reviewed commit, retains the console fallback, emits the GUI package, checksums, metadata, and supported attestations, and runs fixture-free CI. The private trusted workload is used only in its isolated local qualification environment and is never committed or uploaded.

## Delivery sequence

1. Merge the reviewed shared-core/console-adapter PR into `develop` after unit, fault-injection, preservation, and existing lifecycle tests pass.
2. Build the GUI, release acquisition, desktop tests, documentation, and packaging on a new branch from that merged `develop`.
3. Address independent security/privacy and UX/accessibility review findings before packaging.
4. Merge the GUI PR, publish the next fork-specific Linux alpha from its exact reviewed commit, and verify its public artifact in a clean isolated environment.
