# Linux GUI installer screenshot evidence

This specification defines the production screenshot set required for the Phase 4 Linux graphical installer. Every item is still pending until it is captured from the exact reviewed production build and verified as described below. The existing disconnected safe-demo screenshot is historical design evidence only: it cannot satisfy any production workflow, package, release, or lifecycle item in this document.

## Evidence classes

Each caption and provenance entry must identify one of these evidence classes.

- **Real qualification** uses the exact reviewed release package, applicable production frontend (GUI or manual console), backend, and public release artifacts in a clean isolated environment. It exercises the real protocol and filesystem lifecycle. This is required whenever the image implies that download, verification, mutation, recovery, or a public artifact succeeded.
- **Controlled fixture** uses the exact reviewed production GUI and adapter with deterministic public/synthetic data, a disposable filesystem, or an injected failure. It may document layout, validation, conflicts, confirmations, accessibility, and failure handling, but must not imply that a real package lifecycle completed.

A controlled fixture is not a mockup. Design renders, AI-generated images, manually constructed UI facsimiles, and the sealed safe-demo session are never production evidence. The private trusted modpack and save are neither required nor permitted in screenshot inputs.

All 57 entries, including controlled fixtures, must run the exact same reviewed production GUI and backend. The manifest has one top-level production identity: source commit and tree, fork-specific release tag, public package URL and SHA-256, and GUI/backend binary SHA-256 values. Every entry repeats and must exactly match that identity; fixture authority is limited to its public/synthetic inputs or injected failure and cannot substitute another build.

Each `qualification_reference` is evidence-ID-specific. A real-qualification row must link either to an exact `https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/<run-id>` URL (optionally with one exact `/attempts/<attempt>` suffix), or to a dedicated repository qualification/validation Markdown record whose `#evidence-<id>` anchor actually exists. Controlled fixtures may additionally link to their anchored row in this specification. Unanchored local paths, nonexistent or wrong-ID anchors, the generic plan for real evidence, and all other HTTPS hosts or URL shapes are invalid.

## Capture matrix

Every evidence ID must have its own distinct final PNG filename and pixel hash, so one image cannot silently satisfy semantically different rows by being copied or renamed. E2 must be one four-source contact sheet whose permission, read-only, disk-full, and cross-device source PNGs are individually retained. A contact sheet may otherwise cover the environment or scale variants within one A4, A5, A6, or A7 row only when its individual source PNGs are also retained, hashed, and recorded with their own complete environment, capture method/time, and original-resolution privacy review.

### Detection and selection

| ID | Required visible state | Minimum evidence |
| --- | --- | --- |
| D1 | <a id="evidence-d1"></a>One valid game automatically detected, with canonical validation status and the appropriate primary action | Real qualification against a clean isolated game copy |
| D2 | <a id="evidence-d2"></a>No game detected, with a useful empty state and manual-selection action | Controlled fixture |
| D3 | <a id="evidence-d3"></a>Multiple detected game folders and an explicit selected folder | Controlled fixture |
| D4 | <a id="evidence-d4"></a>Manual selection first rejecting an invalid folder and then accepting a valid folder | Controlled disposable filesystem fixture |
| D5 | <a id="evidence-d5"></a>Effective-UID-0 refusal before discovery, download, logging, or mutation | Real packaged root-refusal qualification |

### Release acquisition and verification

| ID | Required visible state | Minimum evidence |
| --- | --- | --- |
| R1 | <a id="evidence-r1"></a>Release selection showing bounded public prerelease choices and the local-package route, without claiming an installed/current, upgrade, or downgrade relationship before authenticated game-receipt inspection | Controlled fixture |
| R2 | <a id="evidence-r2"></a>Real public-package download in progress, with bounded byte progress and Cancel | Real qualification |
| R3 | <a id="evidence-r3"></a>Interrupted or cancelled download with retry guidance and no incomplete published package | Controlled transport failure through the production adapter |
| R4 | <a id="evidence-r4"></a>Checksum and release-metadata verification in progress | Real qualification |
| R5 | <a id="evidence-r5"></a>Successful checksum and attestation/provenance result which does not conflate transport integrity with provenance | Real public artifact and pinned verifier qualification |
| R6 | <a id="evidence-r6"></a>Corrupt checksum, metadata mismatch, or corrupt package blocked before extraction or mutation | Controlled tampered copy of the public artifact |
| R7 | <a id="evidence-r7"></a>Attestation, provenance, or release-identity mismatch blocked with one safe next step | Controlled tampered public evidence |

### Install, update, and repair

| ID | Required visible state | Minimum evidence |
| --- | --- | --- |
| I1 | <a id="evidence-i1"></a>Fresh-install plan naming the selected game and release, creates, preserved unrelated files, and recovery capacity | Real lifecycle qualification |
| I2 | <a id="evidence-i2"></a>Fresh-install confirmation naming affected counts and the recovery path | Real lifecycle qualification |
| I3 | <a id="evidence-i3"></a>Install progress at a meaningful staging, revalidation, apply, or verification boundary | Real lifecycle qualification |
| I4 | <a id="evidence-i4"></a>Install success with the exact installed release and safe next step | Real lifecycle qualification |
| U1 | <a id="evidence-u1"></a>Update plan naming current and target releases, backup behavior, changes, and preserved files | Real lifecycle qualification after installation |
| U2 | <a id="evidence-u2"></a>Update confirmation; a downgrade variant must be explicit and default focus to Cancel | Real lifecycle qualification |
| U3 | <a id="evidence-u3"></a>Update success with the exact resulting release | Real lifecycle qualification |
| P1 | <a id="evidence-p1"></a>Repair plan for a missing receipt-owned file | Controlled disposable lifecycle state |
| P2 | <a id="evidence-p2"></a>Repair plan for a modified receipt-owned file, with an exact replacement candidate and unresolved items still blocked | Controlled disposable lifecycle state |
| P3 | <a id="evidence-p3"></a>Modified-file backup-and-replace confirmation with default focus on Cancel | Controlled disposable lifecycle state |
| P4 | <a id="evidence-p4"></a>Repair success showing repaired and preserved counts | Real lifecycle qualification using a disposable modified-file state |

### Protection, uninstall, backup, and rollback

| ID | Required visible state | Minimum evidence |
| --- | --- | --- |
| X1 | <a id="evidence-x1"></a>Unknown, legacy, hard-linked, special-file, or ambiguous-launcher collision blocking mutation | Controlled hostile disposable filesystem gallery |
| N1 | <a id="evidence-n1"></a>Uninstall plan showing owned removals and unrelated files preserved | Real lifecycle qualification |
| N2 | <a id="evidence-n2"></a>Uninstall confirmation with default focus on Cancel | Real lifecycle qualification |
| N3 | <a id="evidence-n3"></a>Uninstall success with the preserved-file result | Real lifecycle qualification |
| B1 | <a id="evidence-b1"></a>Create-backup plan with destination, capacity, and affected counts | Real lifecycle qualification |
| B2 | <a id="evidence-b2"></a>Backup success with the recovery generation identity | Real lifecycle qualification |
| B3 | <a id="evidence-b3"></a>Full recovery store or prune-required state blocking further work | Controlled disposable recovery history |
| B4 | <a id="evidence-b4"></a>Destructive backup-prune confirmation naming retained and removed generations, with default focus on Cancel | Controlled disposable recovery history |
| L1 | <a id="evidence-l1"></a>Recovery selector showing authenticated generations and the exact release or uninstalled result each would restore | Real lifecycle history |
| L2 | <a id="evidence-l2"></a>Rollback confirmation naming current and restored releases and affected counts, with default focus on Cancel | Real lifecycle qualification |
| L3 | <a id="evidence-l3"></a>Rollback progress and successful durable result | Real lifecycle qualification |

### Cancellation, errors, and recovery

| ID | Required visible state | Minimum evidence |
| --- | --- | --- |
| C1 | <a id="evidence-c1"></a>An ordinary cancellable operation with an active Cancel action | Real lifecycle qualification |
| C2 | <a id="evidence-c2"></a>Cancel requested, rollback in progress, or Finishing safely where immediate cancellation is no longer promised | Real fault-injected lifecycle qualification |
| C3 | <a id="evidence-c3"></a>Cancelled-and-rolled-back terminal state with an exact durable-state explanation | Real fault-injected lifecycle qualification |
| E1 | <a id="evidence-e1"></a>Network interruption or timeout with retry and confirmation that no incomplete package was published | Controlled transport failure |
| E2 | <a id="evidence-e2"></a>Exactly four visible failure states—permission, read-only, disk-full, and cross-device—each stating whether files changed and one safe next step | One four-source controlled real-filesystem fault contact sheet retaining all four original PNGs |
| E3 | <a id="evidence-e3"></a>Stale plan, selected-root replacement, or concurrent-installer rejection | Controlled adversarial filesystem/concurrency fixture |
| E4 | <a id="evidence-e4"></a>Backend, protocol, or writer failure before mutation | Controlled adapter failure |
| E5 | <a id="evidence-e5"></a>Interrupted mutation with the surviving GUI reporting backend state unknown and recovery required | Real fault-injected lifecycle qualification |
| E6 | <a id="evidence-e6"></a>Automatic recovery completed with a fresh inspection required | Real restart/recovery qualification |

### Logs, accessibility, desktop behavior, and fallback

| ID | Required visible state | Minimum evidence |
| --- | --- | --- |
| G1 | <a id="evidence-g1"></a>Bounded local diagnostic snapshot with fixed typed events, stable event/error codes, and visible omission/truncation health; the private raw-log and disk-rotation boundary must remain explicit | At least one real successful lifecycle log |
| G2 | <a id="evidence-g2"></a>Safe technical next steps, stable error details, and Copy sanitized diagnostics without raw backend prose or exceptions | Controlled error fixture |
| G3 | <a id="evidence-g3"></a>Privacy/redaction behavior for hostile tokens, signed URLs, usernames, and full paths | Controlled hostile-string fixture plus privacy scan |
| A1 | <a id="evidence-a1"></a>Readable keyboard focus on the primary action | Controlled fixture using the packaged GUI |
| A2 | <a id="evidence-a2"></a>A destructive dialog whose initial visible focus is Cancel | Controlled fixture using the packaged GUI |
| A3 | <a id="evidence-a3"></a>Narrow 420-DIP layout at 200% scale with every action reachable and no horizontal page scroll | Controlled fixture using the packaged GUI |
| A4 | <a id="evidence-a4"></a>Usable 100%, 125%, 150%, and 200% scale variants | Four controlled captures or one contact sheet retaining all sources |
| A5 | <a id="evidence-a5"></a>Light, dark, and high-contrast focus and error states | Three controlled captures or one contact sheet retaining all sources |
| A6 | <a id="evidence-a6"></a>Packaged GUI on GNOME and KDE under X11 | Real desktop qualification; a contact sheet is acceptable |
| A7 | <a id="evidence-a7"></a>Packaged GUI on GNOME and KDE through XWayland in Wayland sessions | Real desktop qualification; a contact sheet is acceptable |
| A8 | <a id="evidence-a8"></a>Representative screen-reader/live-status state | Controlled fixture linked through its qualification reference to a separate AT-SPI/Orca validation record or exact Actions run; the generic screenshot-plan anchor is insufficient |
| M1 | <a id="evidence-m1"></a>GUI manual-installation help with exact non-GUI steps and limitations | Packaged production GUI |
| M2 | <a id="evidence-m2"></a>Manual console fallback launched from the same verified public package | Real clean isolated package qualification |
| M3 | <a id="evidence-m3"></a>Manual install or rollback completion | Real clean isolated lifecycle qualification |

## Real lifecycle boundary

The following cannot be satisfied by fixture-only rendering: D1 and D5; R2, R4, and R5; successful install, update, repair, uninstall, backup, and rollback; cancellation and restart recovery; at least one real detailed log; and the manual fallback lifecycle. Checksum or provenance success must never be synthetic. Failure captures may use disposable tampered copies of public artifacts, injected transport failures, and disposable hostile filesystems when the production GUI, adapter, and relevant Core policy are exercised.

Plans, selectors, conflicts, confirmations, visual layout, scaling, themes, focus, and deterministic error rendering may use controlled fixtures. Their captions must say so and must not claim that a package or filesystem operation completed. Screenshots are illustrative evidence and do not replace protocol, filesystem, accessibility, privacy, or lifecycle tests.

## Publication structure

The main user guide should keep roughly 18–22 representative images: detection and manual selection; release acquisition and verification; plan, confirmation, progress, and success; each operation's distinctive state; collision protection; cancellation and recovery; logs; accessibility; and manual fallback. A linked qualification gallery should carry the remaining environment, scale, fault, and destructive-confirmation captures.

Every individual source image must remain available even when the guide displays a contact sheet. The repository should contain a machine-readable manifest for automated hash and coverage checks plus a readable provenance record. Both the repository guide and GitHub Pages must use descriptive alt text and captions which identify the visible state and evidence class.

The evidence assets directory is a closed inventory. It contains only `README.md`, `manifest.schema.json`, `manifest.json`, and each referenced final or retained-original PNG directly in that directory. Nested directories, orphan files, undeclared PNGs, and other file types are forbidden.

## Capture staging boundary

Use the fixture-neutral `build/scripts/stage-linux-gui-screenshot.py` helper only to capture or import
an authentic application-window PNG into a private mode-`0700` staging directory outside the
repository. Direct capture verifies one visible X11/XWayland client, exact expected window title
and process ID, and the retained process executable's reviewed GUI SHA-256 before and after
invoking ImageMagick. Import mode records the supplied path-free capture method without claiming
that raster inspection proves which application produced it.

X window properties and `_NET_WM_PID` are advisory and can be spoofed by another client with access
to the same display. Use a controlled isolated display with no unrelated clients, and independently
inspect the original-resolution image to confirm the exact production application, title, and
visible state. The PID/current-user check and bounded executable hash narrow accidental selection;
they do not make the pixels self-authenticating.

The helper accepts only bounded, static, noninterlaced, 8-bit RGB/RGBA input. It removes incidental
metadata by decoding and canonically re-encoding the image, then independently decodes the result
and requires a byte-identical pixel digest. Only `IHDR`, `IDAT`, and `IEND` remain. Its mode-`0600`
sidecar binds the PNG to one matrix ID, production identity, evidence class, environment, runtime,
capture method, fault/fixture context, durable-state statements, and qualification reference without
recording the input, identity-file, private-string-file, or staging paths.

After all four E2 originals pass source-specific review, use
`build/scripts/assemble-linux-gui-e2-contact-sheet.py` in the same private staging root. It accepts
exactly one mode-`0600` source in the fixed `permission`, `read-only`, `disk-full`, `cross-device`
order, never overwrites output, and creates a deterministic two-by-two RGB/RGBA contact sheet plus a
private source-digest sidecar. The neutral gutters do not alter source pixels. The four original PNGs
remain authoritative and must still be retained, declared, and independently privacy-reviewed.

Staging is not qualification or manifest publication. The sidecar always leaves original-resolution
privacy review pending, is forbidden from the final asset directory, and must be replaced by a
reviewed manifest entry. The helper does not OCR rendered pixels, establish filesystem effects,
assemble contact sheets, crop images, generate fixtures, or fabricate screenshots. Promote the
canonical PNG later without changing its bytes and verify its staged SHA-256 before manifesting it.
See the [evidence-bundle README](../screenshots/linux-gui/README.md#private-capture-staging) for usage.

## Privacy and provenance

Capture only the application window unless packaged root refusal or a manual console fallback specifically requires a terminal. Use a dedicated generic isolated account and public release data. No capture may expose a desktop background, panel, notification, terminal history, username, real home/game/Mods/save path, mod or save name, private fixture identity, authentication token, cookie, signed query URL, clipboard, or raw environment.

The product must sanitize logs and paths before display. Cosmetic redaction of an unsafe raw log is not evidence that the product is privacy-safe. Prefer a clean recapture over pixel editing. Lossless cropping to the application window is permitted when recorded; app pixels must otherwise remain unaltered. Design renders, reconstructed screens, and generated images must not be substituted for application captures.

The manifest or provenance record for every PNG must include:

- screenshot ID, filename, descriptive alt text, caption, and evidence class;
- exact source commit and tree;
- exact release tag, public package URL, and package SHA-256 matching the top-level reviewed identity, including for controlled-fixture captures;
- GUI and backend binary hashes;
- fixture or fault-injection description, without private fixture data;
- operation and durable state before and after a real lifecycle capture;
- distribution, architecture, desktop environment, session type, X11/XWayland backend, display scale, theme, and resolution;
- Avalonia and .NET runtime versions, plus the installed .NET SDK version or an explicit
  not-installed/not-used state for a self-contained package;
- capture timestamp with timezone, capture tool and command, PNG dimensions, and final PNG SHA-256;
- crop/edit statement and each retained original source's hash, dimensions, complete environment,
  capture timestamp/method, and privacy review when a contact sheet or lossless crop is used;
- for E2, each original source's exact `fault` discriminator (`permission`, `read-only`, `disk-full`,
  or `cross-device`), with every value present exactly once and with the source-specific fixture or
  injection, operation, and durable state before and after describing that actual fault rather than
  the assembled sheet generally;
- original-resolution privacy inspection and the reviewer;
- evidence-ID-specific qualification run/log reference under the trusted anchored-record or exact fork Actions-run policy, and public release link where applicable.

Final and retained-original files must be static, noninterlaced, 8-bit RGB or RGBA PNGs within the documented dimension, pixel, file-size, and decoded-byte bounds. Only the validator's minimal structural/color chunk allowlist is permitted; strip color profiles, text, EXIF, time, custom ancillary metadata, and other incidental chunks before the final hash. The validator bounded-decompresses the complete IDAT stream and verifies its exact scanline size and end-of-stream. If privacy inspection finds personal or private data, discard the image and recapture it; do not commit the unsafe source. Captions and alt text should describe what is visible and avoid claiming that pixels prove filesystem safety, provenance, or accessibility.

The retained-source matrices are exact, not minimum examples: E2 has exactly one `permission`, `read-only`, `disk-full`, and `cross-device` fault source; A4 has exactly 100%, 125%, 150%, and 200% source environments; A5 has exactly `light`, `dark`, and `high_contrast`; A6 has exactly `GNOME` and `KDE` with session/backend `x11`/`x11`; and A7 has exactly `GNOME` and `KDE` with session/backend `wayland`/`xwayland`.

## Completion gates

The production screenshot work is complete only when every matrix row is either represented by a verified image or deliberately combined with a clearly equivalent adjacent state, all real-evidence rows link to clean isolated qualification, all controlled images are labelled accurately, privacy review is clean, automated manifest/hash checks pass, the user guide and qualification gallery are published, GitHub Pages builds successfully, and every important page and image returns HTTP 200.
