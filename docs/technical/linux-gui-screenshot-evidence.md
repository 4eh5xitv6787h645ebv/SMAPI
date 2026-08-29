# Linux GUI installer screenshot evidence

This specification defines the production screenshot set required for the Phase 4 Linux graphical installer. Every item is still pending until it is captured from the exact reviewed production build and verified as described below. The existing disconnected safe-demo screenshot is historical design evidence only: it cannot satisfy any production workflow, package, release, or lifecycle item in this document.

## Evidence classes

Each caption and provenance entry must identify one of these evidence classes.

- **Real qualification** uses the exact reviewed release package, applicable production frontend (GUI or manual console), backend, and public release artifacts in a clean isolated environment. It exercises the real protocol and filesystem lifecycle. This is required whenever the image implies that download, verification, mutation, recovery, or a public artifact succeeded.
- **Controlled fixture** uses the exact reviewed production GUI and adapter with deterministic public/synthetic data, a disposable filesystem, or an injected failure. It may document layout, validation, conflicts, confirmations, accessibility, and failure handling, but must not imply that a real package lifecycle completed.

A controlled fixture is not a mockup. Design renders, AI-generated images, manually constructed UI facsimiles, and the sealed safe-demo session are never production evidence. The private trusted modpack and save are neither required nor permitted in screenshot inputs.

## Capture matrix

One image may cover adjacent states when every state remains legible and the caption is exact. A contact sheet may cover environment or scale variants only when its individual source PNGs are also retained, hashed, and recorded.

### Detection and selection

| ID | Required visible state | Minimum evidence |
| --- | --- | --- |
| D1 | One valid game automatically detected, with canonical validation status and the appropriate primary action | Real qualification against a clean isolated game copy |
| D2 | No game detected, with a useful empty state and manual-selection action | Controlled fixture |
| D3 | Multiple detected game folders and an explicit selected folder | Controlled fixture |
| D4 | Manual selection first rejecting an invalid folder and then accepting a valid folder | Controlled disposable filesystem fixture |
| D5 | Effective-UID-0 refusal before discovery, download, logging, or mutation | Real packaged root-refusal qualification |

### Release acquisition and verification

| ID | Required visible state | Minimum evidence |
| --- | --- | --- |
| R1 | Release selection showing the installed/current release plus applicable upgrade, prerelease, downgrade, and local-package labels | Controlled fixture |
| R2 | Real public-package download in progress, with bounded byte progress and Cancel | Real qualification |
| R3 | Interrupted or cancelled download with retry guidance and no incomplete published package | Controlled transport failure through the production adapter |
| R4 | Checksum and release-metadata verification in progress | Real qualification |
| R5 | Successful checksum and attestation/provenance result which does not conflate transport integrity with provenance | Real public artifact and pinned verifier qualification |
| R6 | Corrupt checksum, metadata mismatch, or corrupt package blocked before extraction or mutation | Controlled tampered copy of the public artifact |
| R7 | Attestation, provenance, or release-identity mismatch blocked with one safe next step | Controlled tampered public evidence |

### Install, update, and repair

| ID | Required visible state | Minimum evidence |
| --- | --- | --- |
| I1 | Fresh-install plan naming the selected game and release, creates, preserved unrelated files, and recovery capacity | Real lifecycle qualification |
| I2 | Fresh-install confirmation naming affected counts and the recovery path | Real lifecycle qualification |
| I3 | Install progress at a meaningful staging, revalidation, apply, or verification boundary | Real lifecycle qualification |
| I4 | Install success with the exact installed release and safe next step | Real lifecycle qualification |
| U1 | Update plan naming current and target releases, backup behavior, changes, and preserved files | Real lifecycle qualification after installation |
| U2 | Update confirmation; a downgrade variant must be explicit and default focus to Cancel | Real lifecycle qualification |
| U3 | Update success with the exact resulting release | Real lifecycle qualification |
| P1 | Repair plan for a missing receipt-owned file | Controlled disposable lifecycle state |
| P2 | Repair plan for a modified receipt-owned file, with an exact replacement candidate and unresolved items still blocked | Controlled disposable lifecycle state |
| P3 | Modified-file backup-and-replace confirmation with default focus on Cancel | Controlled disposable lifecycle state |
| P4 | Repair success showing repaired and preserved counts | Real lifecycle qualification using a disposable modified-file state |

### Protection, uninstall, backup, and rollback

| ID | Required visible state | Minimum evidence |
| --- | --- | --- |
| X1 | Unknown, legacy, hard-linked, special-file, or ambiguous-launcher collision blocking mutation | Controlled hostile disposable filesystem gallery |
| N1 | Uninstall plan showing owned removals and unrelated files preserved | Real lifecycle qualification |
| N2 | Uninstall confirmation with default focus on Cancel | Real lifecycle qualification |
| N3 | Uninstall success with the preserved-file result | Real lifecycle qualification |
| B1 | Create-backup plan with destination, capacity, and affected counts | Real lifecycle qualification |
| B2 | Backup success with the recovery generation identity | Real lifecycle qualification |
| B3 | Full recovery store or prune-required state blocking further work | Controlled disposable recovery history |
| B4 | Destructive backup-prune confirmation naming retained and removed generations, with default focus on Cancel | Controlled disposable recovery history |
| L1 | Recovery selector showing authenticated generations and the exact release or uninstalled result each would restore | Real lifecycle history |
| L2 | Rollback confirmation naming current and restored releases and affected counts, with default focus on Cancel | Real lifecycle qualification |
| L3 | Rollback progress and successful durable result | Real lifecycle qualification |

### Cancellation, errors, and recovery

| ID | Required visible state | Minimum evidence |
| --- | --- | --- |
| C1 | An ordinary cancellable operation with an active Cancel action | Real lifecycle qualification |
| C2 | Cancel requested, rollback in progress, or Finishing safely where immediate cancellation is no longer promised | Real fault-injected lifecycle qualification |
| C3 | Cancelled-and-rolled-back terminal state with an exact durable-state explanation | Real fault-injected lifecycle qualification |
| E1 | Network interruption or timeout with retry and confirmation that no incomplete package was published | Controlled transport failure |
| E2 | Permission/read-only, disk-full, and cross-device failures, each stating whether files changed and one safe next step | Controlled real-filesystem fault gallery |
| E3 | Stale plan, selected-root replacement, or concurrent-installer rejection | Controlled adversarial filesystem/concurrency fixture |
| E4 | Backend, protocol, or writer failure before mutation | Controlled adapter failure |
| E5 | Interrupted mutation requiring recovery after restart | Real fault-injected lifecycle qualification |
| E6 | Automatic recovery completed with a fresh inspection required | Real restart/recovery qualification |

### Logs, accessibility, desktop behavior, and fallback

| ID | Required visible state | Minimum evidence |
| --- | --- | --- |
| G1 | Detailed local log view with relative installer-owned paths, stable operation/error codes, and rotation/status information | At least one real successful lifecycle log |
| G2 | Expanded technical error details and Copy sanitized diagnostics | Controlled error fixture |
| G3 | Privacy/redaction behavior for hostile tokens, signed URLs, usernames, and full paths | Controlled hostile-string fixture plus privacy scan |
| A1 | Readable keyboard focus on the primary action | Controlled fixture using the packaged GUI |
| A2 | A destructive dialog whose initial visible focus is Cancel | Controlled fixture using the packaged GUI |
| A3 | Narrow 420-DIP layout at 200% scale with every action reachable and no horizontal page scroll | Controlled fixture using the packaged GUI |
| A4 | Usable 100%, 125%, 150%, and 200% scale variants | Four controlled captures or one contact sheet retaining all sources |
| A5 | Light, dark, and high-contrast focus and error states | Three controlled captures or one contact sheet retaining all sources |
| A6 | Packaged GUI on GNOME and KDE under X11 | Real desktop qualification; a contact sheet is acceptable |
| A7 | Packaged GUI on GNOME and KDE through XWayland in Wayland sessions | Real desktop qualification; a contact sheet is acceptable |
| A8 | Representative screen-reader/live-status state | Controlled fixture linked to separate AT-SPI/Orca evidence |
| M1 | GUI manual-installation help with exact non-GUI steps and limitations | Packaged production GUI |
| M2 | Manual console fallback launched from the same verified public package | Real clean isolated package qualification |
| M3 | Manual install or rollback completion | Real clean isolated lifecycle qualification |

## Real lifecycle boundary

The following cannot be satisfied by fixture-only rendering: D1 and D5; R2, R4, and R5; successful install, update, repair, uninstall, backup, and rollback; cancellation and restart recovery; at least one real detailed log; and the manual fallback lifecycle. Checksum or provenance success must never be synthetic. Failure captures may use disposable tampered copies of public artifacts, injected transport failures, and disposable hostile filesystems when the production GUI, adapter, and relevant Core policy are exercised.

Plans, selectors, conflicts, confirmations, visual layout, scaling, themes, focus, and deterministic error rendering may use controlled fixtures. Their captions must say so and must not claim that a package or filesystem operation completed. Screenshots are illustrative evidence and do not replace protocol, filesystem, accessibility, privacy, or lifecycle tests.

## Publication structure

The main user guide should keep roughly 18–22 representative images: detection and manual selection; release acquisition and verification; plan, confirmation, progress, and success; each operation's distinctive state; collision protection; cancellation and recovery; logs; accessibility; and manual fallback. A linked qualification gallery should carry the remaining environment, scale, fault, and destructive-confirmation captures.

Every individual source image must remain available even when the guide displays a contact sheet. The repository should contain a machine-readable manifest for automated hash and coverage checks plus a readable provenance record. Both the repository guide and GitHub Pages must use descriptive alt text and captions which identify the visible state and evidence class.

## Privacy and provenance

Capture only the application window unless packaged root refusal or a manual console fallback specifically requires a terminal. Use a dedicated generic isolated account and public release data. No capture may expose a desktop background, panel, notification, terminal history, username, real home/game/Mods/save path, mod or save name, private fixture identity, authentication token, cookie, signed query URL, clipboard, or raw environment.

The product must sanitize logs and paths before display. Cosmetic redaction of an unsafe raw log is not evidence that the product is privacy-safe. Prefer a clean recapture over pixel editing. Lossless cropping to the application window is permitted when recorded; app pixels must otherwise remain unaltered. Design renders, reconstructed screens, and generated images must not be substituted for application captures.

The manifest or provenance record for every PNG must include:

- screenshot ID, filename, descriptive alt text, caption, and evidence class;
- exact source commit and tree;
- exact release tag and package SHA-256 for real package evidence;
- GUI and backend binary hashes;
- fixture or fault-injection description, without private fixture data;
- operation and durable state before and after a real lifecycle capture;
- distribution, architecture, desktop environment, session type, X11/XWayland backend, display scale, theme, and resolution;
- Avalonia, .NET SDK, and .NET runtime versions;
- capture timestamp with timezone, capture tool and command, PNG dimensions, and final PNG SHA-256;
- crop/edit statement and original-source hash when a contact sheet or lossless crop is used;
- original-resolution privacy inspection and the reviewer;
- qualification run/log reference and public release link where applicable.

Strip incidental image metadata before the final hash. If privacy inspection finds personal or private data, discard the image and recapture it; do not commit the unsafe source. Captions and alt text should describe what is visible and avoid claiming that pixels prove filesystem safety, provenance, or accessibility.

## Completion gates

The production screenshot work is complete only when every matrix row is either represented by a verified image or deliberately combined with a clearly equivalent adjacent state, all real-evidence rows link to clean isolated qualification, all controlled images are labelled accurately, privacy review is clean, automated manifest/hash checks pass, the user guide and qualification gallery are published, GitHub Pages builds successfully, and every important page and image returns HTTP 200.
