# Mod Health Report implementation plan

## Status and scope

This document is the authoritative design, implementation, and validation contract. The original Linux desktop report shipped through issue #159 / PR #160, the issue #156 timing boundary shipped through PR #165, and the private in-game viewer is implemented as the issue #166 follow-up. Historical future-tense wording below records the contract used to build and review those phases; it is not a claim that the feature remains unimplemented.

The feature is for Linux desktop SMAPI. Android and other mobile code paths are excluded. Changing the default .NET runtime, solving runtime-specific input problems, automatic web uploads, and modifying the public mod API are also excluded.

Register the `health` workflow and its persistent setting only when `Constants.Platform` is Linux. Shared internal models may remain portable, and all existing desktop targets must continue compiling, but Linux environment probes must be guarded and no support claim is added for Windows/macOS in v1.

The issue #166 viewer adds `health view` without changing schema v1 or adding another collector, analyzer, or report interpretation. It displays the one immutable sanitized model prepared for an exact request ID; preparation and file work remain off the game thread. The menu is explicit and never opens automatically, refuses unsafe parent menus before it queues work, and keeps the persistent local-only privacy notice visible. Its eight bounded sections cover overview, findings, capture quality, mods needing attention, performance, errors/logging, inventory, and context/limitations. Mouse, keyboard, and controller input use one screen-local game-thread action queue, while large rows are paged instead of represented by a persistent component per item. Android/mobile, historical on-disk browsing, uploads/sharing actions, and the unrelated .NET 10 menu-click issue remain excluded.

### Issue #166 viewer extension contract

`health view` opens only the latest exact model prepared during the current process. It does not deserialize an on-disk report, accept a path, or browse history.

| Current state | Required behavior |
| --- | --- |
| Active capture | Queue an interim snapshot without stopping or resetting capture. |
| Stopped retained capture | Queue or reuse its exact frozen final model. |
| No timed capture | Queue a ledger-only model. |
| Report queued or writing | Open a non-blocking Preparing state tied to that exact request ID. |
| Model built but write failed | Keep the model viewable with a prominent not-saved banner and exact retry action. |
| Rejected, superseded, canceled, disposed, or failed before build | Preserve that exact terminal state and show only a safe next action; never silently substitute content. |
| Unsafe parent menu, minigame, save/load, fade, or warp state | Refuse before report work is queued and never replace the existing menu. |

Opening an already prepared model must not export it again. Available footer actions are derived from the exact viewer/capture state and may include Start capture, Add mark, Stop capture, Refresh and save snapshot, Retry exact save, View newer report, and Close. Confirmed reset discards the exact unsaved/retryable prepared model but never deletes a saved artifact.

The viewer maps the canonical frozen DTO into eight sections without re-analysis: overview/privacy, findings, capture quality, needs attention, performance, errors/logging, mod inventory, and context/limitations. It preserves finding order, severity, confidence, evidence, suggested action, affected mod ID, and limitation. It must distinguish ledger-only, short, invalid, truncated, complete, measured-zero, and unavailable states; invalid timing suppresses percentages and valid-looking partition conclusions. It uses the exact SMAPI dispatch label and explains that elapsed wall-clock time is neither total SMAPI CPU nor proof of cause, base-game time can include Harmony/direct mod work, unavailable SMAPI timing is folded into residual, GC values are process-wide correlation, update ticks are not FPS, and draw/GPU coverage is incomplete.

The persistent notice says the model and artifacts are private/local, no upload occurred, the user should inspect them before sharing, the normal SMAPI log remains necessary for detailed exceptions, and `smapi.io/log` does not parse a standalone report. Viewer data is restricted to sanitized DTO fields and stable relative artifact paths. It must not access or expose raw logs/stacks, absolute paths, saves or identities, multiplayer/network details, host/user/machine identity, command/chat history, manifest descriptions/authors/update fields, configuration, or arbitrary extension data. It offers no network/upload, clipboard, browser/file-manager, mutation/removal, or report deletion/alteration action.

Visible chrome uses centralized translation-ready keys with default-English fallback; schema-v1 finding prose stays canonical until a separate localization-equivalence design exists. Headings, text, and icons convey meaning without relying on color alone. Required input is mouse click/wheel; keyboard arrows, Page Up/Down, Home/End, Enter, Tab, Escape, I, and P; and controller D-pad, shoulder tabs, A/B/X/Y/back. Direct Close exits the viewer, while Escape/B backs out expanded text and row details first. Layout and hit targets recompute for window, viewport, UI-scale, and split-screen changes, with 1280×720 as the minimum validation resolution. Large sections are virtual/paged and materialize only the visible bounded row window.

One bounded prepared-report store publishes only after build, sanitation, analysis, and deterministic pruning. It retains at most one model plus bounded exact-request terminal tombstones and releases replaced content deterministically. One per-screen controller/session owns a bounded typed action queue. Menu callbacks enqueue actions; `SCore` drains them at the next safe pre-base-update boundary. UI creation, input, update, and draw stay on the game thread; worker work never accesses game/menu/graphics/live metadata, and the menu never waits on it. While closed, the fast path performs one integer-backed pending-action check with no controller lookup, polling, UI allocation, update, or draw work.

Viewer validation covers canonical, empty, ledger-only, short, invalid, truncated, maximum-capacity, GC, status/failure, and all timing-availability models; exact request/state transitions and final-over-interim priority; reset/shutdown/concurrent reads; privacy and viewer/text/JSON equivalence; mouse/keyboard/controller/focus/resize/scale/split-screen ownership; safe refusal and close while preparing; queue dispositions; pagination; and the allocation-free closed path. Packaged validation uses both Linux hosts, the complete trusted PR #158 fixture and Blossom save in fresh isolated roots, Xvfb/PTTY, non-English locale, full inventory, required input types, exact write failure/retry, request succession, small/common resolution behavior, schema/privacy checks, normal exit, and unchanged source/live state. Raw validation captures and fixture inventory are never committed; a privacy-reviewed, curated screenshot subset may be committed for user documentation.

The implementation should build on the bounded performance diagnostics added in issue #154 and pull request #155. It must not recreate the unbounded experimental profiler removed in SMAPI 3.8.3 because of its memory, performance, and interpretation problems.

At implementation time, inspect the current state of these related open artifacts before branching or duplicating work:

- [issue #156](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/156), which specifies separating base-game update, observed mod callbacks, and SMAPI/other update time plus GC collection signals;
- [pull request #157](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/157), which implements that diagnostic split and should be independently reviewed and integrated as a prerequisite when safe;
- [pull request #158](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/158), whose benchmark workspace and associated `benchmark-mods-2026-08-26` release provide the complete real modpack, a 308-entry mod list, and the deliberately shared `Blossom_389524656` save.

Their state may change before implementation. Use the merged `develop` result if already merged; if still open, inspect their exact commits, comments, checks, and diffs. Do not blindly duplicate or merge conflicting code. Record whether each was merged, incorporated, superseded, or used only as a fixture.

The reviewed heads are PR #157 at `66b806b6ab702ba0008ddf72ea01c9b1d3adcd5a` and PR #158 at `599c8b786215c7cfa5bf395fa4b726c0d1c61805`. At the latter commit, `Blossom_389524656.tar.xz` has SHA-256 `6f707c73d02a05eed42adee0d9d9b434e92dff84a841e2f00047456921ae4bca`, with four regular files, two directories, 84,028,043 expanded bytes, and an 82,522,715-byte largest member. The complete matching Mods snapshot is the `Mods-2026-08-26.tar.zst` asset on [`benchmark-mods-2026-08-26`](https://github.com/adventurexplore/SMAPI/releases/tag/benchmark-mods-2026-08-26): 746,198,040 compressed bytes, SHA-256 `337d157bb2cf7283eaf6796c259a21d6e2e71baf134cb67a1f2a655e31cb312c`, 25,226 members, and 1,018,793,776 total regular-file bytes. The user states this modpack came from their wife and explicitly authorizes extracting and running it for this work without per-mod review.

## User problem

The existing `performance` command exposes useful technical counters, but an ordinary player still needs to know when to start it, how to read callback timings, what “unattributed” means, and which files to share. It also has important gaps for a support report:

- starting a performance sample clears warning and error evidence collected during startup;
- skipped or invalid mods are not retained in a report-oriented session model;
- stopping does not freeze an explicit end timestamp, so a later snapshot's elapsed duration continues increasing;
- the latest-600-tick ring can overwrite an earlier freeze during a long reproduction;
- rendering callbacks and update-tick wall time are not separated clearly enough for user-facing conclusions;
- there is no stable, private, shareable text/JSON artifact;
- warnings, errors, failed callbacks, log floods, update state, and timing evidence are not combined into cautious suggested actions.

The health report should solve those problems without claiming that elapsed time proves a mod caused the underlying slowdown.

## Product goals

1. Give a player one clear workflow to record a problem and obtain a report suitable for support.
2. Preserve useful session evidence from launch independently of an optional deep timing capture.
3. Identify mod-owned work only at boundaries SMAPI can actually observe.
4. Clearly distinguish observed mod work from game, SMAPI, Harmony, background, native, GPU/driver, I/O, GC, and operating-system work.
5. Retain bounded evidence from the entire capture, including an early freeze.
6. Produce deterministic human-readable text and versioned JSON from one frozen model.
7. Avoid collecting or transmitting personal data that is not needed for troubleshooting.
8. Keep disabled timing overhead and memory behavior effectively unchanged.
9. Fail safely when the sample is incomplete, storage is unavailable, or counters reach capacity.
10. Let Linux desktop players inspect the current exact sanitized report in-game without upload, history browsing, or another interpretation layer.

## Non-goals for the first release

- Automatically upload a report or open a browser.
- Automatically disable, delete, move, update, or edit a mod.
- Recommend removing a mod without explaining dependency/save risks.
- Assign a single health score.
- Include raw log messages, stack traces, chat, command history, saves, or mod configuration files.
- Time Harmony patch bodies, arbitrary background tasks, direct mod-to-mod calls, native code, or per-mod memory.
- Claim to measure GPU time or complete presented FPS.
- Add web-parser support or a ZIP support bundle. The in-game viewer is the separate issue #166 extension and remains in-memory/current-process only.
- Add adaptive always-on deep profiling or periodic report checkpointing.
- Change Android/mobile projects or behavior.

## User workflow

Add a built-in `health` command as the ordinary-user facade. Keep `performance` as the advanced diagnostic interface.

```text
health
health start
health status
health view
health mark
health report
health stop
health retry
health reset confirm
```

Behavior:

- Bare `health` explains the current state and the next action.
- `health start` begins a fresh, quiet timing capture with a 33.333 ms default slow-update threshold. It says: “Recording a mod health sample. Reproduce the problem, then enter `health stop`.”
- `health status` shows capture duration, tick count, retained slow moments, errors, and capacity warnings.
- `health view` prepares or reuses the current process's exact sanitized report model without stopping/resetting capture or browsing on-disk reports. It refuses unsafe parent menus before queuing report work.
- `health mark` marks the current reproduction point without accepting free text. This avoids copying save names, secrets, or other accidental personal data into a shareable file.
- `health report` freezes an interim snapshot and saves a report without stopping capture.
- `health stop` stops timing first, freezes the end time and data, saves the report, logs a compact summary, and prints the report locations.
- `health retry` retries the exact frozen DTO from the most recent failed export; it never rebuilds the report from newer ledger data.
- Bare `health reset` only explains that reset discards timed evidence. `health reset confirm` clears the timed capture and any retryable frozen export. If health timing is active it immediately starts a new health window; if a stopped sample was retained it transitions to inactive/no sample. It never clears the session-wide ledger.
- `health stop` with no active or retained timing capture still writes the session health report and clearly says that no deep timing window was available.
- Treat `inactive/no sample` and `stopped/retained sample` as distinct states. A stopped sample retains its immutable timing snapshot, owner, ledger cutoff, and export state. `health report` or a repeated `health stop` exports that retained timing instead of incorrectly producing a ledger-only report.
- `health start` must never silently discard an active or unexported/failed retained health sample. It may replace a stopped health sample only after that sample was exported successfully, or after the user explicitly runs `health reset confirm`. The existing advanced `performance start` reset behavior remains compatible only when the current session is performance-owned; it must refuse to replace a health-owned capture.
- A report shorter than 30 seconds or 600 update ticks may still show direct failures and peaks, but it must be labelled a short sample and must not conclude that no issue exists.
- `health mark` without an active capture explains that `health start` is required. `health report` without an active or retained capture writes a ledger-only report. `health status` always labels session counts and timed-capture counts separately.
- Report creation is asynchronous: the initiating command prints `queued`, `health status` exposes `queued`, `writing`, `succeeded`, or `failed`, and the final paths are logged only after both files are committed.
- While an export is queued or writing, interim report requests are rejected or coalesced explicitly. A final `health stop` export replaces a pending interim export, but never the one already writing. Reset is refused while an export is queued/writing; after a failed export, `health retry` preserves it and `health reset confirm` explicitly discards it.

The two command surfaces must share one atomic session controller. Starting `health` while advanced `performance` sampling is active, or vice versa, must produce explicit behavior instead of resetting data unexpectedly. If an advanced command stops a health-owned capture, the health report must still be finalized.

Optional discoverability aliases may be added as `performance diagnose` and `performance export`, but they must delegate to the same coordinator and must not create a second collector.

The required interaction matrix is:

| Current state | Command | Required result |
| --- | --- | --- |
| Inactive, no retained sample | `health start` | Start a health-owned timing window. |
| Stopped health sample, export succeeded | `health start` | Start a fresh health-owned timing window and explicitly say the previously exported sample was replaced. |
| Stopped health sample, not yet exported or export failed | `health start` | Refuse without changing data; show `health report`/`health retry` or `health reset confirm`. |
| Inactive or stopped performance-owned sample | `performance start` | Preserve the current advanced reset behavior and start a performance-owned window. |
| Health timing active | either start command | Refuse without changing data and show the health report/stop/reset command. |
| Performance timing active | `health start` | Refuse without changing data and show the advanced report/stop/reset command. |
| Performance timing active | `performance start` | Preserve the current intentional behavior: start a fresh advanced sample and state that the previous advanced sample was reset. |
| Health timing active | `performance report` or `performance ticks` | Preserve advanced reporting/tick-log behavior without changing ownership. |
| Health timing active | `performance reset` | Refuse and require `health reset confirm`, since silent discard would violate the health workflow. |
| Health timing active | `performance stop` | Stop/freeze the window and queue the health export as well as the current advanced summary. |
| Performance timing active | `health report` | Queue a health-format interim snapshot without stopping or changing ownership. |
| Performance timing active | `health stop` | Stop/freeze the advanced window and queue a health export from it. |
| Performance timing active | `health reset confirm` | Refuse; advanced reset remains under the `performance` command. |
| Stopped retained timing sample | `health report` or `health stop` | Export the immutable retained timing sample using its frozen end time and ledger cutoff; never relabel it as ledger-only. If the same completed export already exists, report its paths instead of creating an accidental duplicate. |
| Inactive, no retained timing sample | `health report` or `health stop` | Queue a ledger-only report and say that deep timing was unavailable. |

For Linux launches without an interactive terminal, add `EnableModHealthReportOnLaunch`, disabled by default. It starts a health timing window after the ledger/core initializes and automatically finalizes it on normal shutdown. Document that it cannot recover evidence from an abrupt process kill or failures before initialization.

All configuration-driven state changes must also pass through the session coordinator. `SCore.ReloadSettings` must not call `ModPerformanceManager.ApplySettings` behind the coordinator. A config reload during a manual capture may update non-destructive live tick logging, but start/stop/reset changes are queued until the manual session ends and are reported to the console/log. If health-on-launch and persistent advanced tracking are both enabled, health-on-launch owns the single capture and the report records that choice.

## Two data lifetimes

### Session health ledger

Maintain a low-cost bounded ledger from its earliest safe initialization before `LogManager`, through process exit. It is not reset by `performance start`, `health start`, or timing resets. The report must include `ledgerStartedUtc` and a completeness value which states that launcher/native failures before this boundary are not captured; it must not claim complete process-launch evidence.

It should contain:

- total counts for every discovered mod/content-pack status plus retained safe identity records up to the documented deterministic capacity;
- loaded, skipped, ignored, invalid, and failed states;
- structured fail reasons, warning flags, and dependency IDs where available;
- mod `Entry` and API failures;
- available update versions, or an explicit disabled/pending/unavailable state;
- message and character counts by mod and severity;
- warning/error counts, first/last monotonic occurrence offsets, and whether they occurred during the timing window;
- SMAPI and game warnings/errors in separate unattributed groups;
- structured callback failure counts without merging them into error counts;
- omission/capacity counters for every bounded ledger collection.

Do not retain raw mod log messages in the default report. If repeated error signatures are implemented, derive them from structured exception types and safe categories where SMAPI catches the exception. A later opt-in detailed mode may consider sanitized message fingerprints only after a separate privacy review.

The observer path must cover public mod logging through `Monitor.LogImpl`. The internal `LogDeferred` distinction should be documented and tested so deferred SMAPI messages are not accidentally described as mod evidence.

### Timed diagnostic capture

Maintain a separately resettable opt-in timing window:

- monotonic and UTC start/end timestamps;
- immutable stopped duration;
- capture owner/mode and completion reason;
- existing exclusive callback aggregates;
- separate execution phase (`startup`, `update`, `draw`, `background`, or `unscoped`) and operation kind (`event`, `content-load`, `content-edit`, `console`, `entry`, `get-api`, or other) for every observed callback;
- recent update-tick ring;
- worst update ticks from the whole capture;
- clustered slow-update episodes;
- fixed-size tick-duration histogram and threshold counts;
- top contributors for retained slow moments;
- user marks;
- coarse game phase and split-screen context;
- optional process/GC start/end and low-frequency samples;
- truncation and omitted-entry counters.

Starting or resetting timing takes a session-ledger baseline so the report can show both “since launch” and “during capture” counts without deleting startup evidence.

## Observation and attribution contract

Observed mod boundaries are:

- SMAPI-managed event callbacks;
- content load and edit callbacks;
- mod console commands;
- `Mod.Entry` and `Mod.GetApi` when timing was active;
- any other boundary explicitly added and named in the report schema.

Nested timings remain exclusive so child callback time is not counted again in its parent. Elapsed time is wall-clock time observed while the callback boundary was active, not CPU time and not proof of root cause.

Update-tick attribution is main-update-thread only. `BeginTick` records the owning managed thread, and callback/log work is added to that tick only when it occurs on the same thread. Background-thread callbacks remain in their phase/operation aggregates and must not inflate update denominators merely because the global update window was open. A same-tick log association means temporal overlap on that thread, not causation.

Incorporate the safe boundary from issue #156 / PR #157 so update reports show an internally consistent split:

- base-game update time, exclusive of observed nested mod callbacks;
- observed mod callback time included in the update;
- separately measured SMAPI dispatch/other time when an owned boundary exists;
- otherwise, residual time outside the measured game/callback boundaries remains explicitly unattributed so the report never implies SMAPI ownership without a measurement.

Record GC generation collection deltas at update boundaries and for the capture, labelled process-wide correlation rather than mod attribution, with validity flags so unavailable evidence is never presented as zero observed collections. If PR #157's implementation is not safe after review, fix it or reproduce its tested behavior in the health feature; use owned attribution buckets only where a corresponding boundary is actually measured. Impossible values such as same-thread instrumented time exceeding its enclosing update must set an invalid-data flag and suppress percentage findings instead of being silently clamped into a valid-looking denominator.

The report must label these as unobserved or unattributed:

- Stardew Valley and SMAPI core work;
- Harmony patch bodies;
- direct mod API calls outside an observed boundary;
- arbitrary tasks/background threads;
- native calls, filesystem/network waits, and locks;
- GC pauses, GPU/driver work, presentation/vsync waits, and OS scheduling.

Content load/edit operations should retain both the executing framework mod and `OnBehalfOf` content-pack identity when SMAPI knows it. A report may say a framework callback ran on behalf of a pack; it must not assert that the pack caused all framework time.

Rendering callback totals must have a separate execution domain and must never be divided by or compared as part of update-tick wall time. The first release may state that full draw/present timing is unsupported. Adding bounded outer draw-loop timing around `SGame._draw` is a follow-up phase; it must remain distinct per screen and must still disclaim GPU/presentation attribution.

## Bounded capture model

The implementation should use hard-coded safe caps initially, record every omission, and avoid exposing a large configuration surface before real-world evidence exists.

Recommended starting caps:

- latest 600 update ticks;
- worst 100 update ticks across the capture;
- 50 clustered slow-update episodes;
- top five mod contributors per retained slow tick;
- 8,192 distinct callback identities;
- 4,096 mod/log identities;
- 1,024 structured failure signatures if signatures are implemented;
- 100 user marks;
- top 500 callback rows in exported detail, with aggregate and omitted counts preserved;
- 100 findings and suggested actions;
- 256 dependencies per mod record;
- 256 characters for IDs/names/environment labels and 1,024 for callback/type fields after structural sanitization;
- five retained report pairs;
- five MiB maximum for each output file.

Apply caps before building large strings or serializing. Every nested collection and mod-controlled string must have an explicit limit, deterministic priority, and omission count; the five-MiB check alone is not a memory bound. When the mod inventory must be truncated, prioritize loaded and failed/problem mods, then skipped/ignored entries, with stable ID ordering inside each group. Report the total discovered count and omitted count so junk folders or insertion order cannot hide important entries.

The streaming update histogram should support approximate median, p95, and p99 plus exact counts over at least:

- 16.667 ms;
- 33.333 ms;
- 50 ms;
- 100 ms;
- 250 ms;
- 500 ms;
- 1 second.

Use a documented fixed 256-bucket logarithmic histogram spanning 0.125 ms through 8,192 ms with 16 sub-buckets per power-of-two range plus explicit underflow/overflow accounting. Store exact count, sum, minimum, and maximum. Percentiles return the selected bucket's upper bound, set `approximate: true`, and publish the maximum relative bucket error. Do not infer percentiles from the seven threshold counters alone.

Each retained slow update should contain:

- update tick number and offset from capture start;
- total, instrumented, and unattributed milliseconds;
- up to five observed mod contributors;
- warnings, errors, and callback failures associated with that tick;
- phase such as title, loading, gameplay, menu, saving, cutscene, or transition;
- focused/unfocused state;
- split-screen number without player identity;
- nearby user mark;
- capacity or timing-validity flags.

Cluster adjacent qualifying ticks into bounded episodes so one freeze cannot fill the worst-tick collection with near-identical entries.

Initial deterministic episode rules are: a tick qualifies at 33.333 ms; an episode may bridge at most one below-threshold tick and closes after two consecutive below-threshold ticks or capture stop; its representative is the highest-duration member; episodes rank by maximum duration, then summed qualifying duration, then earliest tick; retain the worst 50. The worst-100 tick list remains independent and may reference ticks which also belong to an episode. A mark is associated with the nearest episode/tick only within 300 ticks, with an earlier mark winning a tie. Coarse phase is sampled once at tick start on the main thread with precedence `loading/saving`, `title`, `cutscene`, `menu`, `gameplay`, then `unknown`, and is labelled an estimate.

Default health capture must not log every tick. Live tick logging remains an explicit advanced option because its I/O can distort the measurement.

## Report data contract

Build one immutable, implementation-independent DTO and generate both formats from it. Do not serialize private runtime models or emit .NET type metadata.

### Header and completeness

- integer `schemaVersion`, initially 1;
- short collision-resistant report ID;
- generated UTC timestamp;
- capture start/end/duration and completion reason;
- capture mode and thresholds;
- completed update count;
- whether startup/lifecycle timing was observed;
- short-sample, counter-capacity, truncation, invalid-timing, and write-retry flags.

### Environment

- SMAPI/fork build version and existing informational commit value if available;
- Stardew Valley version;
- actual CLR/runtime version and process architecture;
- Linux distribution/kernel at a non-identifying level;
- normalized X11/XWayland/Wayland/unknown session type when reliably available;
- process bitness and logical processor count;
- game locale;
- single-player, host, client, and split-screen state without identities;
- process/GC start/end/delta values only when obtained through reliable low-cost APIs.

Do not include exact CPU/GPU model in v1. Do not infer GPU memory ownership.

### Mod inventory

For every retained discovered mod/content pack:

- unique ID and display name;
- installed version;
- code-mod/content-pack classification and parent ID;
- loaded/skipped/ignored/failed status;
- safe structured warning/failure/dependency fields;
- already-known suggested update version or explicit unknown state;
- callback/log/failure aggregates, if any;
- informational Harmony patch-owner counts only if they can be gathered at report time without implying those patches were timed.

Exclude authors, descriptions, update URLs/keys, folder paths, arbitrary manifest extension data, and configuration contents.

Privacy is enforced through a strict source-field allowlist, not a claim that arbitrary text can be understood semantically. SMAPI must never read prohibited sources for this report. Allowed identity fields such as mod ID, display name, version, dependency ID, and callback/type name are mod-controlled; they are length-capped and structurally sanitized, but may themselves contain personal-looking text chosen by a mod author. Invalid-manifest entries must use a generated placeholder instead of a folder-derived display name. The report and help must explain this limitation and require inspection before sharing.

### Performance sections

- update histogram, mean, approximate p50/p95/p99, maximum, and threshold counts;
- total observed mod callback time and unattributed update time with valid denominators;
- ranked mods by exclusive observed time, peak, calls, failures, slow-tick participation, and percentage of instrumented time;
- callback hotspots with domain, event/operation, fully qualified callback, total/average/peak, call count, failures, and optional over-budget count;
- worst updates and clustered episodes;
- recent updates for context;
- resource deltas labelled process-wide, never per-mod;
- all capacity and omission data.

Call update measurements “update ticks,” never frames or FPS.

### Errors and logging

- messages and approximate characters by severity and mod;
- warning/error counts since launch and during capture;
- first/last occurrence offsets;
- callback failures by operation and exception type when safely structured;
- SMAPI/game issues separately;
- no raw messages or stack traces;
- explicit note that one callback failure may also emit one error, so columns must not be summed as unique incidents.

### Privacy notice and limitations

Every text/JSON report and the in-game viewer must state:

- it contains the installed mod names, IDs, versions, and statuses;
- the user should inspect it before sharing;
- it contains no automatic upload;
- the normal SMAPI log is still required for full exception details;
- `smapi.io/log` does not support this standalone report until web support is implemented;
- attribution and unsupported-boundary limitations.

Privacy tests must place fixture secrets in prohibited source fields and verify those sources are never collected. Separate sanitation tests should place path-like/control content in allowlisted identity fields and verify redaction of obvious absolute paths plus control/length safety, without promising detection of every arbitrary semantic secret.

## Plain-language findings

Generate deterministic findings with stable rule IDs. Each finding contains severity, confidence, evidence, affected mod ID when applicable, a suggested action, and a limitation.

Initial rule families:

- failed/skipped mod load;
- failed callback;
- high mod error volume;
- available update for a mod with issues;
- logging flood by message/character rate;
- repeated slow updates;
- one observed mod dominating instrumented work in several slow updates;
- extreme callback peak;
- mostly unattributed slow updates;
- sample too short;
- capacity/truncation reached;
- no clear mod-owned bottleneck observed.

Use explicit named constants for initial thresholds and document them. Treat a capture under 30 seconds or 600 updates as short for sustained conclusions. Direct failures and individual peaks remain factual in short samples.

Initial conservative values are: at least three slow updates for a repeated-slow finding; any failed callback for a direct-failure finding; at least five mod errors during capture or 20 since ledger start for high error volume; at least 100 messages or 64 KiB of text in a one-second saturating bucket for a log-flood check; and at least a 100 ms observed callback for an extreme peak. A mod-dominance finding requires at least three slow updates, the mod to be the largest observed contributor in at least half, and at least half of instrumented slow-update time; confidence cannot exceed `likely` unless instrumented work also represents at least half of total slow-update time. At least 75% residual/unattributed time across three slow updates makes the mostly-unattributed finding primary. These values must be named, fixture-tested, documented as initial heuristics, and recalibrated only with recorded real reports.

Use wording such as:

> During 18 of 24 slow update ticks, Example Mod (`Example.Mod`) was the largest callback contributor SMAPI observed. Those callbacks used 312 ms; another 690 ms was unattributed. This correlation does not prove the mod caused the entire delay.

If unattributed time dominates, that must be the primary conclusion. Do not present a tiny observed callback as the culprit. Never use “caused your lag,” never produce a composite score, and never report “healthy” from an insufficient capture. Prefer “no clear issue was observed during this capture.”

Suggested actions may include:

- update the named mod when an update is known;
- after reviewing both for private information, share the health `.txt` report and the normal SMAPI log; provide the health `.json` only when a maintainer or tool requests it;
- reproduce for longer;
- temporarily test without a mod only after backing up and checking dependencies/save implications;
- use an external process/system profiler when most time is unattributed.

## Text report layout

1. `SMAPI Mod Health Report` header and privacy notice.
2. `What SMAPI observed` with `[ACTION NEEDED]`, `[PERFORMANCE]`, `[CHECK]`, and `[INFO]` labels.
3. `Suggested next steps`.
4. `Capture quality and scope`.
5. `Slow update overview`.
6. `Mods needing attention`.
7. `Top observed mods and callbacks`.
8. `Slow episodes and worst updates`.
9. `Errors, failures, and logging volume`.
10. `Installed mod and content-pack inventory`.
11. `Environment`.
12. `Attribution and privacy limitations`.

Use headings and words rather than color alone, ASCII-friendly formatting, terminal-width summary lines, invariant units, stable tie-breaking, and mod IDs beside names. Explain “callback” and “unattributed” on first use. Keep collection classes free of presentation prose so later localization remains possible.

The console should show only the top findings and a stable relative location such as `ErrorLogs/HealthReports/<filename>`. It should not print a potentially identifying absolute path, the complete report, or block the game while formatting it.

## JSON requirements

- UTF-8 and invariant numeric/date values;
- raw numeric values, not preformatted strings;
- deterministic arrays and tie-breaking;
- stable property names and enum strings;
- top-level `schemaVersion`;
- `completeness`, `capacities`, `omissions`, and `limitations` objects;
- readers must tolerate unknown future fields;
- no `TypeNameHandling` or runtime type metadata;
- golden schema fixture checked into tests;
- an actual checked-in JSON Schema v1 document defining required, nullable, enum, and capacity fields, with runtime fixtures validated against it;
- text and JSON generated from the same DTO and cross-checked in tests.

## Storage, retention, and failure behavior

Store report pairs under:

```text
ErrorLogs/HealthReports/SMAPI-health-<UTC timestamp>-<report ID>.txt
ErrorLogs/HealthReports/SMAPI-health-<UTC timestamp>-<report ID>.json
```

The subdirectory is required because `SCore.PurgeNormalLogs` deletes direct `ErrorLogs` children whose names begin with `SMAPI-`.

Writing rules:

- capture an immutable DTO on the game thread under the shortest practical lock;
- stop timing before final snapshot generation;
- sort, analyze, format, serialize, and write outside collector locks and off the update thread;
- write both random temporary files in the final directory and flush/dispose them before publishing either one, then rename with create-new semantics;
- publish a small completion marker only after both final files exist; the marker defines a valid report pair because two filesystem renames cannot be atomic as one transaction;
- treat the pair as successful only after the completion marker exists; on failure remove files published by that attempt when possible, and safely clean unmatched generated files/markers on the next writer startup;
- never derive filenames from mod or user text;
- sanitize CR/LF, tabs, ANSI escapes, controls, and overlong mod-controlled strings;
- deterministically prune optional DTO rows in this order until both regenerated UTF-8 payloads fit five MiB: recent context ticks, lower-ranked callback detail beyond the required top set, ignored mod inventory, healthy content-pack inventory, then nonessential environment detail; preserve findings, problem mods, aggregates, capacities, and per-section omission counts;
- if the mandatory minimal DTO still exceeds the limit, write a small valid fallback text/JSON report which says generation was truncated and retains only completeness/capacity/error metadata;
- handle same-second reports and concurrent game instances without overwriting;
- retain at most five report pairs and remove reports older than 30 days;
- prune only after a new complete pair is committed successfully, disclose retention in help/success output, and skip pruning rather than waiting when another process holds the report-directory maintenance lock;
- delete only exact generated filename patterns in the dedicated directory;
- preserve unrelated files and nested directories;
- never accept an arbitrary output path in v1.

On Linux, create `HealthReports` with owner-only `0700` permissions and temporary/final files and completion markers with `0600` permissions at creation time, independent of a permissive umask. Failure to establish private permissions must fail the export safely rather than publish a more permissive shareable file. Cleanup must hold the same bounded cross-process maintenance lock and must preserve fresh incomplete pairs/temporary files for a generous grace period (at least ten minutes) so another live process is never mistaken for stale work.

If writing fails, keep the frozen in-memory model so the user can retry, log a short actionable error, and never crash or stop the game. A normal shutdown must attempt a bounded best-effort final report when a health capture is active. Fatal-crash export, periodic checkpoints, and crash recovery are follow-up work and must never become dependencies of the crash handler.

Use one bounded single-consumer export worker. At most one DTO may be writing, one may be pending, and one failed frozen DTO may be retained for retry; these slots must never grow into an unbounded task queue. Further `health report` requests are rejected or coalesced with an explicit status, and a final stop snapshot has priority over a pending interim snapshot. On normal shutdown, an active health capture is frozen with completion reason `normal-shutdown`, queued, and the writer is given at most two seconds to finish. Stop accepting exports before writer disposal, dispose/drain the writer before `LogManager`, cancel and clean temporary files after the timeout, and never log after `LogManager.Dispose`. An abrupt kill or `Environment.Exit` may leave only temporary/uncommitted files, which are ignored and cleaned after the grace period on a later launch. Multi-process publish/prune coordination must use a bounded-wait lock; unique report IDs prevent overwrite even when pruning is skipped.

No report generation path may perform a network request, upload, clipboard write, or browser launch.

## Architecture and integration points

Use separate components with narrow responsibilities:

- `ModHealthLedger`: always-on bounded session evidence and safe mod inventory/status data.
- `ModPerformanceManager`: existing opt-in callback/update timing, extended without changing its disabled fast path.
- `ModDiagnosticSessionCoordinator`: atomic ownership and start/stop/reset/export transitions shared by `health` and `performance`.
- `ModHealthReportBuilder`: combines frozen ledger, timing, environment, and registry snapshots into the stable DTO.
- `ModHealthInsightAnalyzer`: pure deterministic evidence-to-finding rules.
- `ModHealthReportTextFormatter`: human report.
- `ModHealthReportJsonSerializer`: schema-versioned JSON.
- `ModHealthReportWriter`: asynchronous atomic output, retention, retry, and disposal.
- `HealthCommand`: player workflow and concise console feedback.

Data flow:

```text
mod loading/logging ──> session ledger ───────────┐
                                                  ├─> frozen report DTO
observed callbacks ──> opt-in timing capture ─────┘          │
                                                            ├─> insight analyzer
                                                            ├─> text formatter
                                                            └─> JSON serializer
                                                                       │
                                                               atomic report writer
```

Relevant integration points include `SCore`, `SGame._draw` only in the later draw phase, `ModRegistry`, the transient skipped-mod loading path, `LogManager`, `Monitor.LogImpl`, `ManagedEvent`, content load/edit boundaries, `PerformanceCommand`, `Constants.LogDir`, shutdown/disposal, and the existing Linux runtime dispatcher tests.

Feed manifest-resolution results into the ledger immediately after discovery and before ignored/invalid entries are filtered, then record explicit validation/load/status transitions. Feed already-immutable update-check results into it when the asynchronous update task completes. Neither the report builder nor writer may read live `IModMetadata` or directory-derived invalid names from a background thread.

The asynchronous writer receives only immutable DTOs and must never read live Stardew Valley objects. Give ledger observations a monotonic sequence/epoch; a snapshot freezes a precise ledger cutoff and excludes any currently open partial update tick. A retry reuses the exact frozen model. Snapshot registry/game context on the game thread separately from collector copying, and never hold a collector lock while traversing the full mod inventory. Report-related messages use an internal reporter category/suppression scope so they cannot recursively become mod or log-flood evidence.

## Performance and resource budget

- Preserve the lock-free disabled `IsTracking` branch.
- No new per-callback allocation after warm-up when timing is disabled.
- No ordinary mod log text retention.
- Always-on ledger updates must be O(1), bounded, and limited to work proportional to an actual log/load/failure event.
- All-severity message/character counting must pass only ID, severity, monotonic sequence/timestamp, internal category, and `message.Length` to the ledger, never the message. After monitor registration it should use per-monitor saturating atomic counters rather than a global dictionary lock on every Trace/Info line.
- Active timing must not retain a per-invocation object history.
- Allocate contributor arrays only for ticks selected for retention.
- Resource telemetry, if implemented, samples at most once per second into a bounded ring.
- Snapshot under a lock; release it before ranking, analysis, formatting, JSON serialization, or file I/O.
- Measure report formatting separately from disk writing.
- Record enabled and disabled allocation/timing evidence in the technical document.
- Use stable allocation assertions as CI gates. Treat tight wall-clock percentages as informational because shared CI timing is noisy.

Benchmark/check scenarios should cover warmed no-listener, one-, ten-, and hundred-listener event raises; disabled/enabled existing and new callback identities; nested calls; tick completion; ordinary and error logging; concurrent producers; maximum-capacity snapshots; formatting; and file writing.

## Test plan

### State, ledger, and collector tests

- startup/skipped-mod/log evidence survives timing start, stop, and reset;
- stop freezes end time and snapshot contents;
- start/duplicate-start/stop/restart/reset/export transitions are atomic;
- callback timing remains exclusive when nested;
- callback failures and emitted errors remain separate but correlatable;
- mod/game/SMAPI logs are classified separately;
- all severities and character counts are bounded correctly;
- case-insensitive mod IDs have deterministic display identity;
- skipped, ignored, invalid-manifest, duplicate-ID, missing-dependency, and content-pack records;
- on-behalf-of content pack attribution;
- latest-ring rollover, whole-capture worst retention, and deterministic episode clustering;
- histogram/percentile/threshold boundaries, empty and single-tick samples;
- negative/invalid timestamp handling, counter overflow, and tick-number wrap;
- every capacity sets an omission flag/count;
- snapshots expose no mutable internal collections;
- concurrent logs, callbacks, stops, and snapshots do not deadlock or corrupt data.
- background-thread callbacks/logs overlapping an open update remain aggregate-only and cannot corrupt update denominators;
- config-owned startup/reload transitions obey the same ownership matrix;
- frozen snapshots use a precise ledger sequence cutoff and exclude open partial ticks.

### Analyzer tests

- direct load/callback failures;
- short and no-data samples;
- repeated errors and log floods;
- observed-mod-dominant slow updates;
- mostly unattributed slow updates;
- isolated peaks versus sustained issues;
- update-available, update-unknown, and update-disabled states;
- capacity/truncation warnings;
- stable rule IDs, ranking, confidence, and tie-breaking;
- no causation wording, invalid denominator, health score, or unsupported FPS claim;
- render-domain totals never appear in update denominators.

### Formatter, schema, and privacy tests

- text/JSON equivalence from one DTO;
- JSON round-trip and schema v1 golden fixture;
- validation against the checked-in JSON Schema v1 document;
- invariant UTC/number formatting under several process cultures;
- empty, normal, error-heavy, mostly unattributed, truncated, and maximum-size fixtures;
- duplicate names and missing/malformed metadata;
- quotes, slashes, Unicode, CR/LF, tabs, ANSI escapes, controls, and overlong fields;
- NaN/infinity normalization;
- deterministic output ordering;
- fixture usernames, hostnames, absolute paths, save/farm/player names, IPs, secrets, raw messages, descriptions, update keys, and configs never appear.

### Writer and command tests

- create missing report directory;
- atomic success and same-second collision handling;
- concurrent writers and multiple game instances;
- Linux directory/file/marker modes remain `0700`/`0600` under a permissive umask;
- permission, disk, serialization, rename, and cancellation failures never create a completion marker or make an incomplete pair visible as successful;
- temporary-file cleanup and retry from retained frozen data;
- one-writing/one-pending/one-retryable queue caps, stop priority, `health retry`, and reset/shutdown interactions;
- exact-pattern retention never deletes unrelated files;
- startup normal-log purge does not remove reports;
- all command actions, optional aliases, invalid/extra arguments, marks, duplicate start, no-data stop, and interim report;
- `screen=X` does not create conflicting global sessions;
- success/error console messages use the correct stable relative path;
- console/log output uses only the stable relative report path;
- existing `performance` behavior and tests remain compatible.

### Linux runtime validation

Create a disposable diagnostic probe mod that exercises fast, slow, nested, failing, logging-heavy, command, content load/edit, background-overlap, and on-behalf-of callbacks. Optionally add one Harmony patch only to demonstrate that it remains unattributed.

Validate a packaged disposable Linux game installation through PTY console input and Xvfb:

1. Start a health capture, exercise the probe, mark the problem, export an interim report, and stop.
2. Verify both files exist, are within size limits, share one report ID/model, and validate against schema v1.
3. Verify expected mod IDs, versions, timings, errors, failures, slow ticks, context, findings, and limitations.
4. Verify injected paths, names, IPs, secrets, raw errors, and control sequences are absent.
5. Verify a deliberate unobserved stall produces an unattributed finding instead of blaming the probe mod.
6. Run a capture longer than 600 ticks and confirm an early freeze remains in the worst list.
7. Repeat under non-English locale and isolated home/XDG directories.
8. Repeat using both supported Linux desktop host launchers; compare behavior/schema, not timing values.
9. Test shutdown/cancellation deterministically through an injected slow writer; keep one real Ctrl-C run as best-effort evidence.
10. Exercise cross-process collision/retention with a small writer harness; two simultaneous full game instances are optional extra evidence.

Use the complete modpack/save fixture associated with PR #158. Pin the workspace to commit `599c8b786215c7cfa5bf395fa4b726c0d1c61805`, the save to SHA-256 `6f707c73d02a05eed42adee0d9d9b434e92dff84a841e2f00047456921ae4bca`, and the [`Mods-2026-08-26.tar.zst`](https://github.com/adventurexplore/SMAPI/releases/download/benchmark-mods-2026-08-26/Mods-2026-08-26.tar.zst) release asset to exactly 746,198,040 bytes and SHA-256 `337d157bb2cf7283eaf6796c259a21d6e2e71baf134cb67a1f2a655e31cb312c`. Fail the fixture run on a missing or mismatched asset instead of silently substituting another corpus. The user states this modpack came from their wife and explicitly authorizes extracting and running it for this SMAPI validation; do not perform a per-mod trust, provenance, licensing, or code review and do not pause for another approval. This authorization applies to this supplied fixture only, not unrelated downloads.

Keep fixture handling separate from the user's live environment. Inventory each archive without executing its contents, accept only regular files/directories, and reject absolute paths, `..` traversal, symlinks, hardlinks, devices, FIFOs, sockets, sparse/unsupported entries, or realized paths outside the temporary root. Ignore stored ownership and modes. Keep the save archive's limits at 16 entries, 100 MiB per file, 128 MiB expanded, and eight path components. Enforce modpack limits of 26,000 entries, 32 MiB per file, 1.25 GiB total expanded bytes, and 12 path components; the pinned archive contains 25,226 members, 21,984 regular files, 3,242 directories, a 20,696,901-byte largest file, 1,018,793,776 total regular-file bytes, and a maximum of ten path components. Extract into a fresh disposable game `Mods` directory and isolated XDG/save roots. Never overwrite the source archives, live Mods directory, live `Blossom_389524656` save, or source game installation. This is fixture integrity/isolation, not a review of the trusted mods.

Use `mods.json` only through an allowlisted projection which drops its folder paths. The archive deliberately contains a farm/save identity, so generated reports must prove that identity is not collected from game/save state. The complete 308-mod release asset, save, metadata workload, and purpose-built probe are mandatory runtime validation inputs. Record any mod that cannot load because of a missing external prerequisite, but do not replace the supplied corpus by autonomously downloading unrelated files. Do not commit, mirror, re-upload, package, or publish the modpack in SMAPI build/CI artifacts; it is an external user-authorized test fixture and mod authors retain their rights. Synthetic maximum-capacity fixtures remain the deterministic required gate.

Final checks:

```text
dotnet test src/SMAPI.Tests/SMAPI.Tests.csproj -c Release --no-restore -p:AllowMissingPrunePackageData=true
dotnet build src/SMAPI.slnx -c Release --no-restore -p:AllowMissingPrunePackageData=true
git diff --check
```

Run whitespace verification only for modified files, or compare against clean `develop` and require no new formatting failures. The repository has known unrelated whole-solution formatting failures; do not modify unrelated files to make that command green.

Publish both Linux hosts and run the existing runtime-dispatcher tests as packaging checks. Android/mobile files must remain unchanged.

## Delivery phases

### Phase 1: contract and trustworthy state

- Create the GitHub feature issue with this scope and acceptance criteria.
- Define stable report DTO/schema, capacities, privacy rules, finding IDs, example text, and JSON fixture.
- Separate session ledger from timed capture.
- Capture skipped/invalid mod state and make stopped snapshots immutable.
- Add state, privacy, and deterministic-schema tests first.

### Phase 2: actionable bounded evidence

- Add histogram, thresholds, whole-capture worst ticks, episode clustering, top contributors, context, log-volume counters, marks, and safe failure categories.
- Preserve existing exclusive timing and disabled-path behavior.
- Add analyzer rules and plain-language output.

### Phase 3: report writing and command workflow

- Implement text/JSON generation from the same DTO.
- Add asynchronous atomic writer, retention, retry, and shutdown behavior.
- Add `health` and optional advanced aliases with one shared session coordinator.
- Update help and technical/player documentation.

### Phase 4: validation and rollout

- Run focused, full, privacy, concurrency, capacity, and performance checks.
- Validate a packaged Linux installation with the probe mod and both supported desktop hosts.
- Dogfood with the complete pinned 308-mod release asset, PR #158 metadata, and save in an isolated disposable installation; record load failures and the exact loaded/skipped/invalid corpus before interpreting timings.
- Review generated artifacts manually for privacy and misleading claims.
- Commit focused changes, push a feature branch, open a PR linked to the issue, review the complete diff and runtime evidence, fix all findings, merge into `develop`, and verify the issue closes and the branch is clean.

### Follow-up issues after schema v1 stabilizes

- outer CPU draw-loop measurement with split-screen separation;
- resource/GC time-series correlation if v1 data shows value;
- best-effort crash finalization and checkpoint recovery;
- optional `smapi.io` parser/view support with explicit upload consent;
- broader localization of canonical schema-v1 finding prose, subject to a localization-equivalence design;
- automatic low-overhead alerts after false-positive and overhead evaluation.

## Definition of done

The feature is complete only when:

- the launch ledger and timed capture are separate and bounded;
- startup/skipped-mod evidence survives capture resets;
- inactive/no-sample and stopped/retained-sample states are distinct, stopped duration/data are immutable, and a retained sample cannot be silently lost or misreported as ledger-only;
- the ordinary `health start` → reproduce → `health stop` flow works;
- an early freeze survives a long session;
- reports contain cautious findings, mod inventory/status, update timing, errors/failures, environment, completeness, privacy, and limitations;
- text and JSON are deterministic, versioned, bounded, private by contract, and written atomically;
- write failures retain retryable in-memory data and never crash the game;
- existing `performance` behavior and disabled hot path remain compatible;
- no report is automatically transmitted and no mod is changed;
- focused/full tests, build, format, packaging, and Linux runtime validation pass;
- disabled/enabled overhead evidence is documented;
- Android/mobile files are unchanged;
- issue #156 / PR #157 were reviewed and their safe timing split/GC behavior is integrated without duplicate collectors;
- the complete pinned 308-mod release asset plus PR #158's save and metadata was used from an isolated disposable location, with hash verification, extraction containment, source Mods/save protection, corpus completeness, and report privacy verified;
- the GitHub issue and PR contain the final evidence, the PR is merged, the issue is closed, and `develop` is clean and synchronized.
- `health view` implements the exact request-state table without replacing unsafe menus or silently switching reports;
- the viewer preserves schema-v1 semantics, privacy, relative paths, bounded paging, and screen-local game-thread ownership;
- mouse, keyboard, controller, resize/scale, split-screen, and large-list tests pass with no normal closed-path work;
- both supported Linux hosts pass the isolated full-corpus viewer validation.

## Historical base-feature `/goal` prompt

The prompt below records the completed base-report implementation goal. It is retained for provenance and should not be rerun as the issue #166 viewer goal.

```text
Implement a private, bounded, shareable Mod Health Report for the Linux desktop SMAPI fork in /home/jake/Downloads/SMAPI-audit, following docs/technical/mod-health-report-plan.md as the authoritative specification.

Use agents for independent architecture, privacy/security, UX-language, test, performance, fixture, and final-diff reviews whenever those tasks can run in parallel. Read any repository instructions first. Restrict source changes to SMAPI-audit, preserve unrelated user changes, and do not modify /home/jake/Downloads/smap, the source game installation, or live saves. Isolated `mktemp` directories and the explicitly disposable SMAPI-game-audit copy may be used for runtime validation. The untracked docs/technical/mod-health-report-plan.md is an intentional user-authored input: preserve it and add its finalized version to the feature branch/PR.

Scope is Linux desktop SMAPI only. Do not change Android/mobile code paths. Do not make .NET runtime migration or the intermittent .NET 10 menu-click issue part of this goal. Keep both currently supported Linux desktop host launchers buildable and use them only as the validation matrix.

Before implementation, inspect the current develop branch and the diagnostics added by issue #154 / PR #155. Also inspect issue #156, PR #157 (`feature/tick-time-split-diagnostics` at planning time), and PR #158 (the benchmark workspace from `adventurexplore/SMAPI`, at planning time). Check their current GitHub state, exact commits, comments, diffs, tests, and mergeability instead of assuming they are unchanged. Independently review PR #157's base-game/SMAPI/mod update split and GC signals, and safely integrate it as a prerequisite or incorporate/fix its behavior without duplicating conflicting code. Record whether #156/#157 were merged, fixed, superseded, or incorporated. Use PR #158 at pinned head `599c8b786215c7cfa5bf395fa4b726c0d1c61805` together with its complete matching modpack release asset, 308-entry mod list, and deliberately shared Blossom save as required real fixtures as detailed below.

Create a detailed GitHub feature issue containing the user problem, workflow, privacy contract, attribution limitations, capacities, non-goals, implementation phases, performance budget, real-fixture plan, and acceptance criteria. Create a focused feature branch from current origin/develop after resolving the prerequisite order.

Build the health report as a coordinator around the existing bounded ModPerformanceManager. Separate a low-cost ledger initialized at the earliest safe core boundary from the resettable opt-in timing capture so health/performance start and reset cannot erase startup warnings, errors, skipped/invalid mods, or load failures. Include the ledger initialization timestamp/completeness boundary. Fix capture state so stop freezes the end timestamp, ledger sequence cutoff, and immutable data. Route console and persistent-config start/stop/reset/reload transitions through one coordinator. Preserve existing advanced performance behavior when performance owns the session and preserve its lock-free disabled fast path; refuse advanced operations which would silently destroy a health-owned capture.

Register on Linux the user workflow `health`, `health start`, `health status`, `health mark`, `health report`, `health stop`, `health retry`, bare `health reset` help, and destructive `health reset confirm`. `health mark` must not accept free text. Implement the exact ownership/state matrix in the plan, including performance-owned duplicate-start compatibility, config precedence, distinct inactive/no-sample and stopped/retained-sample states, retained export/retry semantics, no-data reports, and queued/writing/succeeded/failed status. Use one atomic coordinator shared with existing performance commands; never silently discard a health-owned capture or misreport retained timing as ledger-only. Add the disabled-by-default no-terminal health-on-launch setting and bounded normal-shutdown finalization. Keep deep timing opt-in and quiet by default with the documented 33.333 ms slow threshold.

Extend the bounded timing model with orthogonal execution phase and operation-kind fields, main-update-thread-affine tick association, the issue #156/PR #157 base-game-exclusive versus observed-mod split, explicitly unattributed residual time outside measured boundaries, and a reserved SMAPI/other bucket used only when separately measured; add per-tick/sample GC generation deltas with validity flags, the specified fixed histogram and threshold counts, latest 600 updates, worst 100 updates, deterministic 50-episode algorithm, five contributors per retained slow update, estimated game-phase precedence, fixed user marks, and explicit omission/capacity data. Background callbacks/logs overlapping an update must remain aggregate-only and impossible denominators must invalidate findings instead of being clamped. Preserve exclusive nested callback timing and do not retain per-invocation history. Retain executing framework and on-behalf-of content-pack identity where known.

Collect bounded session evidence for every retained discovered loaded/skipped/ignored/invalid mod and content pack using deterministic problem-first priority and omitted counts, safe structured load/dependency/warning state, already-known update availability, all-level message/character volume, separate mod/game/SMAPI warning and error counts, first/last occurrence offsets, and structured callback failures. Capture discovery before ignored/invalid filtering and receive immutable update-result transitions; background writers must never inspect live mod metadata. Pass only message length/category/sequence/severity to per-monitor saturating counters and do not retain raw log messages or stack traces. Keep callback failures and logged errors separate because one incident may create both. Exclude reporter-generated internal messages from ledger evidence.

Create one stable immutable schema-v1 DTO, a checked-in JSON Schema v1 document, deterministic sanitized example fixtures, and a deterministic insight analyzer using the plan's initial named thresholds. Generate findings with stable rule IDs, evidence, severity, confidence, suggested action, and limitations. Use cautious “SMAPI observed” wording, make mostly-unattributed time primary when applicable, never claim a mod caused lag, never compute a health score, never call update ticks FPS, and never recommend removing a mod without dependency/save warnings.

Generate matching UTF-8 text and versioned JSON reports from the same DTO under ErrorLogs/HealthReports using collision-safe UTC/report-ID filenames, both same-directory temporary payloads, create-new renames, and a completion marker committed last. Consumers/retention recognize only marked pairs; stale incomplete cleanup uses the bounded cross-process maintenance lock and at least a ten-minute grace period. Use Linux `0700` directory and `0600` file/marker modes at creation time or fail privately. Prune exact completed pairs only after a successful new commit, retaining at most five pairs/30 days; never delete unrelated or fresh in-progress files.

Enforce explicit caps for every nested collection/string and one writing + one pending + one failed-retryable frozen DTO. A final stop replaces only a pending interim export. Deterministically prune the shared DTO and regenerate both payloads until each fits five MiB; emit a valid minimal fallback if mandatory content cannot fit. Snapshot briefly under lock, then analyze, format, serialize, and write outside collector locks and off the update thread. On write/serialization/permission/disk failure, publish no completion marker, retain the exact frozen model for `health retry`, log a concise error, and never crash or stop the game. Drain before LogManager disposal for at most two seconds on normal shutdown. Never accept arbitrary output paths, upload, access the clipboard, launch a browser, or make a network request. Print only relative `ErrorLogs/HealthReports/<filename>` paths.

Enforce privacy with a strict source-field allowlist: reports intentionally contain installed mod names, IDs, versions, safe dependency IDs/callback identities, and statuses and must warn users to inspect them before sharing. Health-report collection must not inspect or serialize prohibited sources including usernames, hostnames, machine IDs, absolute home/game/mod/save paths, save/farm/player names, multiplayer IDs/IPs, arbitrary environment values, command history, raw logs/stacks, descriptions/authors/configs, update keys/URLs, chat, or save contents; loading the isolated benchmark save normally through the game does not authorize collecting those values. Use generated placeholders for invalid-manifest folder names. Sanitize/cap allowlisted mod-controlled identity strings and redact obvious path/control/ANSI content, while honestly stating such arbitrary identity text cannot be semantically guaranteed secret-free. State that the normal SMAPI log is still needed for detailed exceptions and smapi.io does not yet parse standalone health reports.

Include low-sensitivity environment and completeness data: SMAPI/fork and game versions, actual runtime/process architecture, non-identifying Linux/session type, locale, logical processor count, multiplayer role and split-screen count without identities, capture timestamps/duration/counts, whether lifecycle timing was observed, and all truncation/unsupported-boundary flags. Keep render callback totals separate from update denominators; full draw/GPU measurement, web parser/UI work, crash checkpointing, automatic alerts/uploads, Harmony timing, per-mod memory, and public API changes are follow-up non-goals.

Add comprehensive unit and integration coverage for the exact coordinator/config/export state machine, ledger cutoff/completeness, frozen stop data, exclusive timing, same-thread versus overlapping-background attribution, issue #156 split/GC values, case-insensitive IDs, prioritized inventory truncation, skipped/invalid/content-pack/on-behalf-of cases, latest/worst rollover, specified histogram/percentile and episode rules, context/marks/caps, concurrency, findings/wording, JSON Schema and text equivalence, cultures, sanitation/privacy allowlist, NaN/infinity, Linux modes, completion markers, fresh/stale cleanup, cross-process collisions/retention, deterministic size pruning/minimal fallback, queue/retry/stop priority, permission/disk/rename/serialization/cancellation failures, split screen, config reloads, and existing performance compatibility.

Create a disposable Linux probe mod and packaged-game audit that exercises fast/slow/nested/failing/logging-heavy/command/content/background-overlap/on-behalf-of callbacks plus a deliberately unattributed stall. Validate health start/mark/interim/stop/retry through PTY/Xvfb, the health text and JSON artifacts and schema, privacy exclusions, an early freeze after 600+ ticks, issue #156 splits, unattributed wording, clean shutdown, non-English locale, and both supported hosts without comparing timings. Test cancellation with an injected slow writer and cross-process collision with a small harness; a real Ctrl-C and two simultaneous full games are best-effort evidence.

Use PR #158's benchmark workspace even if it remains unmerged. Pin its head to `599c8b786215c7cfa5bf395fa4b726c0d1c61805`; inspect its README, `mod-list.md`, `mods.json`, and `Blossom_389524656.tar.xz`. Download the matching `Mods-2026-08-26.tar.zst` only from `https://github.com/adventurexplore/SMAPI/releases/download/benchmark-mods-2026-08-26/Mods-2026-08-26.tar.zst`; require exactly 746,198,040 bytes and SHA-256 `337d157bb2cf7283eaf6796c259a21d6e2e71baf134cb67a1f2a655e31cb312c`, and require save SHA-256 `6f707c73d02a05eed42adee0d9d9b434e92dff84a841e2f00047456921ae4bca`. Fail the fixture run on a missing/mismatched file rather than substituting another corpus. The modpack came from the user's wife and is explicitly trusted and authorized for this test: extract and run it without per-mod trust/provenance/licensing/code review and without asking again. Do not extend that authorization to unrelated downloads, and do not commit, mirror, re-upload, package, or publish the asset in SMAPI builds or CI artifacts.

Use a fresh disposable game install, Mods directory, and `mktemp`/isolated XDG/save roots; never touch live Mods or saves. Accept only regular files/directories, reject absolute/`..` paths, links and special/unsupported entries, ignore stored owners/modes, and verify realized paths remain below the temporary root. Enforce the plan's pinned save limits and modpack limits of 26,000 entries, 32 MiB per file, 1.25 GiB expanded, and 12 path components. Treat these checks as containment/integrity, not mod review. Project `mods.json` through an allowlist that drops folder paths. Run the complete 308-mod corpus with the Blossom save, record loaded/skipped/invalid/missing-prerequisite results, and prove the farm/save identity, archive paths, and config values are not collected. Do not fill gaps by downloading unrelated mods. Record the PR head, release tag/hash, SMAPI commit, native Linux host/runtime, scenario, and corpus completeness with the results. Add fixture failure coverage for missing, hash-mismatched, malformed, oversized, or unsafe archives. The purpose-built probe and synthetic maximum-capacity coverage remain mandatory.

Measure and document parent-versus-feature disabled/enabled overhead and warmed allocations, including high-volume concurrent Trace/Info counters. Require no new disabled per-callback allocations, no ordinary-log text retention, bounded collections/output/tasks, no sorting/formatting/I/O under collector locks, and off-update-thread file work. Run focused tests, full SMAPI tests, Release solution build, git diff checks, both host publishes, and runtime-dispatcher tests. Verify whitespace only on modified files or prove no new failures relative to clean develop; do not repair unrelated formatting. Treat noisy wall-clock thresholds as informational and stable allocation assertions as gates.

Update docs/technical/mod-health-report-plan.md to reflect final decisions and measured evidence, update the Linux audit, command help, release notes, and add sanitized example/schema fixtures. Manually inspect generated artifacts for privacy and misleading attribution.

Create focused commits, push the branch, open a PR linked with `Closes #<issue>`, perform independent reviews of architecture, privacy, UX language, tests, performance, and the complete final diff, fix every in-scope finding, verify the PR is mergeable, merge it into develop, confirm the issue closes, synchronize local develop with origin/develop, and verify the final tree is clean. Do not stop at a plan, partial implementation, unmerged PR, or unverified report; continue until every definition-of-done item in the specification is genuinely satisfied or an external blocker has repeated enough to meet the goal-blocked policy.
```
