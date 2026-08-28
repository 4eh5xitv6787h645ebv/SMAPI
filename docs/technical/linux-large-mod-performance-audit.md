---
layout: default
title: Linux large-mod performance audit
description: All 95 performance and correctness findings, with evidence, risk, and status.
kicker: Technical reference
---

# Linux large-mod performance and correctness audit

This document tracks SMAPI-side performance and correctness findings for Linux players with very large mod sets (for example, 200+ code mods and 400+ content packs). It focuses on work SMAPI can avoid or make incremental. SMAPI also has opt-in runtime diagnostics for the mod-owned execution boundaries it can observe.

The rankings prioritize gameplay frame pacing, then transition stalls, memory pressure, and startup
time. Expected benefits for individual findings are qualitative unless their evidence says
otherwise; projected rankings do not imply percentage gains. The whole-workload section below
reports separate measured results with explicit limitations.

Statuses used below are **confirmed**, **fixed**, **deferred**, **rejected**, and **needs runtime evidence**.

## Current whole-workload evidence

The Phase 1 Linux comparison tested official SMAPI 4.5.2 at `79f9bbbe` against the fork at
`3c98eadd` using five fixed-order A/B pairs and five paired diagnostics-control/enabled samples.
Every separate process captured at least 180 seconds of steady gameplay with the same 132 loaded
code mods, 176 loaded content packs, authorized private save, game/runtime files, configuration,
resolution, isolated session, wrapper, warm-up, and scripted save/warp scenario.

Across the five main runs, median-of-run mean update elapsed duration was 14.596 ms official and
7.228 ms fork; p95 was 26.265 ms and 18.546 ms; p99 was 35.659 ms and 26.681 ms; and main-thread
allocation per update was 1,384.6 KiB and 887.6 KiB. Mean update time was lower in every pair, with
paired differences from −53.9% to −46.1%. Enabling fork diagnostics increased paired mean update
time by 1.3%–8.3% (mean 4.0%) in the separate control series.

This is descriptive one-workstation evidence, not a universal FPS, CPU, power, or latency claim.
Official A always preceded fork B, tiered compilation was disabled, audio used a null backend, and
Xvfb used llvmpipe software rendering. Selected-core busy time was higher for the fork in every
pair. Process Gen1 collections were 2–5 higher and Farm-observed warp timing was slower in four of
five pairs, but the captures lack GC pause duration and stable transition evidence, so neither is a
confirmed fork regression. The [current comparison](../upstream-comparison.md#current-452-whole-workload-comparison)
and [sanitized result bundle](https://github.com/4eh5xitv6787h645ebv/SMAPI/tree/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results)
retain full distributions, run variation, metric semantics, runtime provenance, and limitations.

## Runtime mod diagnostics

On Linux desktop, the player-facing workflow is `health start`, reproduce the problem, optionally enter `health mark`, then enter `health stop`. Enter `health view` from normal gameplay to inspect the latest exact report in a private in-game viewer; it can also prepare an interim or ledger-only report and exposes only actions which are safe for that report state. Close another menu or minigame first. SMAPI writes a matching text and JSON report under `ErrorLogs/HealthReports` without uploading anything or changing any mod. The report and viewer contain mod names, IDs, versions, and statuses, so inspect the artifacts before sharing them. They deliberately exclude raw log messages, stack traces, absolute paths, save/player/farm names, update URLs and keys, configuration contents, and arbitrary manifest extension data. Keep the normal SMAPI log for full exception details; `smapi.io/log` does not yet parse this standalone report.

Use `health status` to inspect the current state, `health report` for an interim or ledger-only report, `health retry` after a write failure, and `health reset confirm` to explicitly discard retained timed evidence. A health report can attribute only work observed at SMAPI-managed boundaries. Harmony patches, direct calls, native work, operating-system scheduling, and other unobserved work remain unattributed, and update measurements are update ticks rather than frames or FPS.

Enter `performance start` in the SMAPI console, reproduce the slowdown, then enter `performance stop`. The final report ranks mods and individual callbacks by exclusive elapsed time, distinguishes base-game-exclusive time, instrumented mod callbacks, SMAPI update dispatch observed outside the base-game update, and uncategorized residual time, counts garbage collections observed during measured ticks, shows recent slow update ticks, and includes warning, error, and failed-callback counts.

To log individual update ticks while sampling, use `performance start <threshold-ms>`. For example, `performance start 16.667` logs ticks which miss a 60 FPS frame budget, while `performance start 0` logs every tick. Every-tick logging is intentionally opt-in because it creates substantial log traffic. Use `performance ticks off` to stop individual tick messages without ending the aggregate sample.

The advanced settings `EnableModPerformanceTracking`, `LogModPerformanceTicks`, and `ModPerformanceTickThresholdMilliseconds` can enable the same behavior automatically for a troubleshooting session. They are disabled by default so the normal event-dispatch path doesn't pay per-handler profiling costs.

Instrumented time covers SMAPI-managed event handlers, content load/edit callbacks, mod console commands, and lifecycle callbacks. Each valid tick distinguishes *base game update* (measured around the vanilla update, so it can include Harmony patches, direct mod API calls, and other unobserved work invoked by the game), exclusive observed callback time, *SMAPI update dispatch observed outside the base-game update*, and an uncategorized residual. The SMAPI dispatch category is elapsed wall-clock time inside SMAPI's owned update-dispatch boundary after excluding observed callbacks. It is not total SMAPI CPU, does not prove causation, and can include waiting, scheduling, and unobserved nested work. Residual time can include framework work outside that boundary, background work, waiting, operating-system scheduling, and other unobserved causes, so it is not assigned to SMAPI. If the owned dispatch measurement is unavailable for a tick or complete sample, its unseparated time is folded back into residual and the report marks the SMAPI category unavailable instead of presenting an observed zero. Garbage collection counts per generation are recorded per tick and per sample as an allocation-pressure signal. A high timing identifies where SMAPI observed time, not necessarily the ultimate root cause. History is bounded to the latest 600 ticks and aggregate counters for 8,192 distinct callback identities, avoiding the large per-invocation history used by SMAPI's removed experimental profiler.

## Current priority ranking

This is the current jank-first order, combining likely frame-time impact, frequency, confidence, and compatibility risk. Finding numbers link the ranking to the detailed evidence below; fixed entries remain ranked so the expected benefit of this fork is visible.

1. Finding 48 — event-driven observed chest inventory tracking — fixed.
2. Finding 2 — demand-driven world, chest, and inventory tracking — fixed.
3. Finding 1 — duplicate world-location processing — fixed.
4. Finding 23 — per-tick core update closures — fixed.
5. Finding 35 — render-stage reflection and mismatched sprite batches — fixed.
6. Finding 40 — repeated current-screen dictionary lookups — fixed.
7. Finding 24 — pressed-key polling allocation while walking — fixed.
8. Finding 26 — unused cursor snapshots while the camera scrolls — fixed.
9. Finding 47 — eager cursor coordinate derivation while walking — deprioritized for the target pack.
10. Finding 25 — held-input event snapshot allocations — fixed.
11. Finding 7 — normal-tile rendering overhead — fixed.
12. Finding 54 — unchanged active-level list hashing — fixed.
13. Finding 53 — duplicate cached-asset probes — fixed.
14. Finding 16 — managed-event and live asset-request dispatch allocations — partially fixed.
15. Finding 37 — redundant invalidation-batch cloning — fixed.
16. Finding 36 — per-parse locale delegate allocation — fixed.
17. Finding 34 — layer work repeated for every patched map tile — fixed.
18. Finding 90 — empty tile-property copies and repeated animation snapshots — fixed.
19. Finding 92 — canonical file paths are split and rejoined for every probe — fixed.
20. Finding 15 — one-tick asset-operation cache lifetime — rejected without a provider contract.
21. Finding 31 — intercepted asset-operation dispatch churn — fixed.
22. Finding 33 — asset-loader adapter closures — fixed.
23. Finding 27 — tick-cache factory and world-helper allocations — fixed.
24. Finding 55 — redundant propagation location-array projection — fixed.
25. Finding 6 — synchronous game-thread log flushing — fixed.
26. Finding 46 — synchronous log formatting on the game thread — fixed.
27. Finding 52 — eager asset-operation trace formatting — fixed.
28. Finding 5 — repeated global invalidation-propagation searches — partially fixed.
29. Finding 41 — incomplete and duplicate world-location topology — fixed.
30. Finding 43 — per-asset propagation key normalization allocations — fixed.
31. Finding 32 — per-map warp comparison sets — fixed.
32. Finding 19 — repeated propagation side effects — partially fixed.
33. Finding 4 — no first-class batched exact invalidation — fixed.
34. Finding 3 — exact invalidation performing cache scans — fixed.
35. Finding 22 — oversized sparse image-patch transfers — fixed.
36. Finding 51 — decoded-texture cache-miss metadata syscalls — fixed.
37. Finding 8 — PNG decode and conversion churn — fixed with a bounded repeat-decode cache.
38. Finding 28 — texture-propagation temporary allocations and lifetime — fixed.
39. Finding 21 — unbudgeted texture and decoded-content memory — partially fixed.
40. Finding 93 — successful JSON reads retain a full UTF-16 file copy — fixed.
41. Finding 9 — linear content-manager routing — fixed.
42. Finding 10 — repeated asset-name strings — fixed.
43. Finding 14 — retained dead disposable wrappers — fixed.
44. Finding 29 — world trackers lost across reordered transfers — fixed.
45. Finding 42 — location trackers lack source ownership — fixed.
46. Finding 30 — rectangular transformed-tile origin — fixed.
47. Finding 18 — reversed location event changes — fixed.
48. Finding 17 — swapped managed-event identifiers — fixed.
49. Finding 38 — case-sensitive Linux paint-mask matching — fixed.
50. Finding 39 — culture-sensitive and ambiguous Linux content-path comparisons — fixed.
51. Finding 11 — eager Linux case-insensitive tree indexing — partially fixed.
52. Finding 13 — repeated dependency-list scans — fixed.
53. Finding 44 — repeated loaded-assembly scans and dependency parsing — fixed.
54. Finding 12 — repeated assembly parsing and compatibility rewriting — fixed.
55. Finding 45 — incorrect overlay alpha composition — fixed.
56. Finding 49 — mod messages serialize even when no remote peer will receive them — fixed.
57. Finding 50 — public reflection cache hits allocate lookup machinery — fixed.
58. Finding 20 — .NET 6 runtime and disabled tiered compilation — deferred.
59. Finding 56 — duplicate content-cache hashing during scans and invalidation — fixed.
60. Finding 57 — managed-asset parsing and lock closures — fixed.
61. Finding 58 — localized cached loads probe their mapping twice — fixed.
62. Finding 59 — intercepted operation groups allocate wrapper objects — fixed.
63. Finding 60 — content routing state is case-sensitive on Linux — fixed.
64. Finding 61 — no-op asset interception wraps every raw asset — fixed.
65. Finding 62 — broad cache scans allocate an iterator per manager — fixed.
66. Finding 63 — texture propagation retrieves cached targets twice — fixed.
67. Finding 64 — custom-map tilesheet routing splits every path repeatedly — fixed.
68. Finding 65 — successful mod asset loads allocate type arrays — fixed.
69. Finding 66 — vanilla map inspection probes before loading — fixed.
70. Finding 67 — loader-only assets allocate editable wrappers — fixed.
71. Finding 68 — texture replacement probes before every successful load — fixed.
72. Finding 69 — asset-operation cache ignores the requested data type — fixed.
73. Finding 70 — existence checks probe irrelevant providers before cache/routing — fixed.
74. Finding 71 — successful on-behalf-of registration formats failure text — fixed.
75. Finding 72 — disconnected gamepads scan every control each update — fixed.
76. Finding 73 — every content manager repeats base-load reflection — fixed.
77. Finding 74 — every registered asset edit allocates a wrapper object — fixed.
78. Finding 75 — case variants create duplicate map tilesheets — fixed.
79. Finding 76 — explicit XNB fallback tilesheets get a double extension — fixed.
80. Finding 77 — observable watchers start with an empty baseline — fixed.
81. Finding 91 — explicit XNB map sheets are treated as different identities — fixed.
82. Finding 94 — case-insensitive lookup conflates case-distinct Linux roots — fixed.

## Detailed findings

### 1. World locations are updated and reset twice per tick

- **Affected code:** `Framework/WatcherCore.cs`, `WatcherCore.Update` and `WatcherCore.Reset`.
- **Scenario:** Every game update on Linux, with cost increasing as expansion mods add locations, buildings, and chests.
- **Root cause:** `LocationsWatcher` is included in the general `Watchers` list and then invoked explicitly a second time. Besides duplicating work, the second update can clear change flags produced by reference-list watchers for active mine and volcano locations before the snapshot is captured.
- **Impact:** Steady gameplay and correctness.
- **Expected benefit:** Removes a complete duplicate traversal and preserves temporary-location change notifications.
- **Risk:** Low. The fix must retain one update and reset in the same tick phase.
- **Status:** Fixed. `WatcherCore` now processes `LocationsWatcher` once through its general watcher collection, preserving change flags for the snapshot and avoiding the duplicate traversal.

### 2. World, chest, and player inventory tracking runs even when no related event has listeners

- **Affected code:** `Framework/SCore.cs` (`OnPlayerInstanceUpdating`), `Framework/WatcherCore.cs` (`Update`), `StateTracking/PlayerTracker.cs` (`Update`/`Reset`), `StateTracking/WorldLocationsTracker.cs` (`Update`/`Reset`), `StateTracking/LocationTracker.cs` (`Update`/`Reset`), `StateTracking/Snapshots/WorldLocationsSnapshot.cs` (`Update`), and `StateTracking/Snapshots/LocationSnapshot.cs` (`Update`).
- **Scenario:** Normal gameplay in expansion-heavy saves, including ticks where no mod subscribes to world collection or chest inventory events.
- **Root cause:** SMAPI unconditionally walks all tracked locations, copies every location collection watcher's added/removed values into snapshots, retains snapshots for unchanged locations, scans every building for indoor-location replacement, scans chest trackers, and rebuilds the player's inventory snapshots before checking whether the corresponding events have listeners.
- **Impact:** Steady gameplay and memory.
- **Expected benefit:** Lower baseline main-thread work, scaling with world size rather than charging every player the full tracking cost.
- **Risk:** High. Some tracked state is needed for internal context and newly registered listeners need a correct baseline; activation must be granular rather than disabling the entire watcher graph.
- **Status:** Fixed within the safe demand-gating boundary. The chest-inventory and player-inventory stages disable stack baselines, comparisons, and snapshot construction when their events have no listeners, with a fresh baseline on activation; unobserved chest watchers also unsubscribe from inventory notifications and skip their update/reset traversals. When player inventory changes are observed, normal item slots and stack fields now push changes through the same shared incremental tracker used for chests, so an unchanged tick traverses no player inventory slots or normal stack values. Runtime item types which override `Item.Stack` retain a narrow compatibility poll. A 10,000-iteration warmed check with a full 36-item normal inventory allocated no bytes on the track/diff/reset thread path. Player skill net fields now push a sorted dirty list, so unchanged ticks skip all six skill-watcher update, snapshot, dispatch-scan, and reset passes. The player location watcher also reads the current location once per tick instead of twice. Verbose diagnostic logging keeps player inventory tracking active when requested. Location collection watchers push one aggregate dirty notification, so updates and snapshots process only dirty locations. World snapshots copy only event families which have listeners and retain only locations with relevant changes. Building indoor references use net-field notifications and a dirty set instead of an every-tick building scan. The underlying topology collection watchers intentionally remain active because SMAPI must discover live locations independently of public event subscriptions. Observed-chest scaling is addressed separately in finding 48.

### 3. Exact asset invalidation performs a general cache scan

- **Affected code:** `Framework/ModHelpers/GameContentHelper.cs` (`InvalidateCache(IAssetName)`) and `Framework/ContentCoordinator.cs` (`InvalidateCache`).
- **Scenario:** Content frameworks invalidate known map, data, or texture names during warps, day starts, festivals, and season/context changes.
- **Root cause:** The exact-name API is implemented by passing an equivalence predicate through the general invalidation path, which scans cached keys across content managers and reparses asset names.
- **Impact:** Transitions and garbage-collection pressure.
- **Expected benefit:** Direct key lookup avoids whole-cache work when the caller already knows the affected asset.
- **Risk:** Medium. Exact matching must preserve locale, separator, and case-insensitive equivalence semantics.
- **Status:** Fixed. The exact-name helper now uses direct cache-key lookup across only game content managers, with the same localization cleanup, temporary-map handling, propagation, events, and reporting as predicate invalidation. The long-standing single-name overload also retains a scalar path: it normalizes one key and probes it directly without first allocating an array and a `HashSet` for the batch implementation. True batches still use the deduplicated set path. Exact invalidations now build and parse the live expansion-location topology only when at least one requested key was absent from every cache and may therefore be a temporary-manager map; when all requested keys were found, that scan cannot add an asset name or type and is skipped entirely.

### 4. Invalidation lacks a first-class multi-key transaction

- **Affected code:** `Framework/ContentCoordinator.cs` (`InvalidateCache` and `InvalidateCacheImpl`) and `Content/IAssetName.cs` APIs.
- **Scenario:** Content Patcher-scale context updates which invalidate dozens of related maps and data assets together.
- **Root cause:** Callers either invoke exact invalidation repeatedly or use a predicate; SMAPI has no public set-based path backed by one lookup, event, propagation, and reporting transaction.
- **Impact:** Transitions.
- **Expected benefit:** Less repeated locking, cache enumeration, event dispatch, propagation setup, and log construction during invalidation bursts.
- **Risk:** Medium to high. Event ordering and the meaning of `AssetsInvalidated` must remain compatible.
- **Status:** Fixed. `IGameContentHelper` now accepts a sequence of exact asset names and handles them through one normalized, deduplicated lookup, live-map scan, invalidation event, propagation pass, and report.

### 5. Asset propagation repeats global searches for each invalidated asset

- **Affected code:** `Metadata/CoreAssetPropagator.cs`, particularly `Propagate`, map propagation, texture propagation, NPC dialogue propagation, and NPC schedule propagation.
- **Scenario:** Day/season/warp transitions invalidating many maps, character assets, and textures in a large world.
- **Root cause:** Assets are propagated one at a time. Map propagation searches locations per map, NPC propagation searches characters per NPC asset, schedule resumption sorts every matching schedule key, and texture propagation searches all content managers per texture.
- **Impact:** Transitions.
- **Expected benefit:** A single world pass and indexed lookups can substantially reduce repeated traversal during large invalidation batches.
- **Risk:** High. Propagation has many asset-specific side effects and ordering dependencies.
- **Status:** Partially fixed. Non-caching namespaced managers are excluded from invalidation and loaded-value scans. Texture propagation reuses the exact manager targets already discovered during invalidation instead of rescanning every code mod's game content manager, with a full-scan fallback for the base-name half of localized invalidations. Multi-asset NPC dialogue/schedule bursts build one exact-name index instead of scanning every NPC for each asset. Multi-map bursts similarly index locations and spouse-room targets in one world pass instead of scanning every location for each map. Resuming an invalidated NPC schedule now finds the latest applicable entry in one linear, allocation-free pass instead of filtering and sorting every key. Single-asset world invalidations retain the cheaper direct scans. Reusing the live-location topology built during invalidation was rejected: `AssetsInvalidated` handlers run before propagation and may mutate locations, buildings, interiors, or map paths, so the pre-event topology isn't a valid propagation snapshot. Side-effect batching remains deferred.

### 6. File logging flushes synchronously on the game thread

- **Affected code:** `Framework/Logging/LogFileManager.cs` (constructor, `WriteLine`, `Flush`, and `Dispose`) and `Framework/Logging/LogManager.cs` (`WriteCrashLog`).
- **Scenario:** Large content changes producing thousands of trace messages while loading or editing assets on Linux filesystems.
- **Root cause:** The log `StreamWriter` uses `AutoFlush`, so every message writes and flushes synchronously in the caller, normally the game thread. Background mod logs can also reach the same non-thread-safe writer concurrently.
- **Impact:** Transitions and occasional steady-gameplay frame spikes.
- **Expected benefit:** Moving ordered file writes and batch flushes off the game thread removes routine filesystem latency from gameplay calls, while serializing concurrent producers prevents writer corruption.
- **Risk:** High. Crash-tail durability, ordering, shutdown, queue saturation, and recursive logging failures must be handled explicitly.
- **Status:** Fixed. File messages now enter a bounded, lossless queue drained by one ordered background writer, with periodic and size-bounded flushes, backpressure instead of dropped messages at saturation, explicit flush barriers before crash-log copies, writer failures surfaced without logging from the writer thread, and a full drain on shutdown.

### 7. Every rendered tile checks and parses transform properties

- **Affected code:** `Framework/Rendering/SDisplayDevice.cs` (`DrawImpl`).
- **Scenario:** Rendering large, multilayer custom maps where almost all tiles have no SMAPI flip or rotation metadata.
- **Root cause:** Each tile performs property dictionary lookups and potential integer parsing before using the transform-capable draw path. The transform-capable path also looked up the same properties a second time.
- **Impact:** Steady gameplay.
- **Expected benefit:** A normal-tile fast path avoids SMAPI-specific transform overhead for the common case.
- **Risk:** Low to medium. Animated tiles and maps edited at runtime must still observe changed transform properties.
- **Status:** Fixed. Tiles without SMAPI flip or rotation properties now delegate to xTile's simpler base draw path. Property-free tiles avoid hashing either transform key, while transformed tiles reuse the first lookup results instead of reading the property dictionary twice; transformed rendering behavior is unchanged.

### 8. Mod PNG loads repeat synchronous decode and pixel conversion

- **Affected code:** `Framework/ContentManagers/ModContentManager.cs` (`LoadRawImageData` and texture loading).
- **Scenario:** First access or reload of many pack textures during warps and seasonal/context changes.
- **Root cause:** Each uncached PNG load decodes through Skia, materializes an unpremultiplied pixel array and a second full-image premultiplied array, converts pixels again, creates a texture, and uploads it synchronously.
- **Impact:** Transitions, memory, and garbage collection.
- **Expected benefit:** A bounded immutable decoded-pixel cache and selective preload support can move repeat decoding out of transition-critical paths while preserving separately owned textures.
- **Risk:** High. Graphics-device access is thread-affine, returned assets may be mutable/disposable, and an unbounded cache would worsen memory pressure.
- **Status:** Fixed with a bounded repeat-decode strategy. Normal PNG decoding converts Skia's native premultiplied RGBA/BGRA span directly into the final XNA array, eliminating both full-image intermediate managed arrays. On Linux's normal RGBA path, matching packed rows are copied in bulk instead of constructing every pixel individually. A process-wide decoded-pixel LRU now admits only an unchanged file's second decode, validates file length and modification time on every hit, skips entries larger than one quarter of its budget, and returns a fresh pixel array so public mutability and Harmony post-processing remain isolated. The adaptive budget is 1/128 of available memory, clamped to 16–128 MiB.

### 9. Content-manager lookup is linear in the number of mods and packs

- **Affected code:** `Framework/ContentCoordinator.cs` (`DoesManagedAssetExist` and `LoadManagedAsset`).
- **Scenario:** Resolving namespaced content with hundreds of per-mod and per-pack content managers.
- **Root cause:** Manager ID resolution uses `FirstOrDefault` over the shared content-manager collection while holding its lock.
- **Impact:** Transitions, startup, and contention.
- **Expected benefit:** An ID-indexed dictionary makes manager resolution constant-time and enables game and namespaced managers to be traversed separately.
- **Risk:** Low to medium. Registration, disposal, and duplicate IDs must update the list and index atomically.
- **Status:** Fixed. Namespaced managers are indexed by managed asset prefix under the existing coordinator lock, while duplicate-name registration and disposal preserve the previous first-match behavior.

### 10. Asset-name normalization creates repeated transient strings

- **Affected code:** `Framework/Content/AssetName.cs` (constructor), `Framework/ContentCoordinator.cs` (`ParseAssetName`), and `SMAPI.Toolkit/Utilities/PathUtilities.cs` (`NormalizeAssetName`).
- **Scenario:** Cache scans, invalidation, and content requests involving the same asset names many times.
- **Root cause:** Normalization splits and rejoins path segments and stores a lowercase copy, while invalidation reparses cached string keys.
- **Impact:** Transitions, memory, and garbage collection.
- **Expected benefit:** Reused canonical asset-name instances, comparer-based hashing, and allocation-light separator normalization reduce transient allocations.
- **Risk:** Medium. Asset equivalence is public behavior and must remain identical for mixed separators, case, locale, and relative segments.
- **Status:** Fixed. `AssetName` no longer allocates and retains a lowercase copy for every parsed name; ordinal case-insensitive equality and hashing operate directly on the canonical name. Runtime asset normalization returns already-canonical strings unchanged and writes noncanonical paths directly into one result string instead of splitting into an array and per-segment strings. `ContentCoordinator` reuses exact parsed inputs through a thread-safe 8,192-entry insertion-bounded cache, with separate entries for locale-aware and mod-file semantics. The immutable instances are safe to share, and the cache is atomically cleared after custom language definitions load so negative locale parsing can't become stale. Localized instances also cache their immutable base name, eliminating the prior 48-byte allocation on every repeated `GetBaseAssetName` call. `AssetReadyEventArgs` and `AssetsInvalidatedEventArgs` now defer their locale-stripped projections; nonlocalized invalidations reuse the original set, while a one-name invalidation constructor fell from 416 to 240 bytes before that property is requested. After warm-up on .NET 10, two million repeated canonical parses took 263.5 million stopwatch ticks directly versus 41.2 million through the cache (about 6.4 times faster in that microbenchmark), while repeat parse cache hits allocate nothing. This does not imply the same whole-frame speedup; benefit scales with repeated content-key parsing and invalidation volume.

### 11. Linux case-insensitive compatibility indexes each mod tree independently

- **Affected code:** `Framework/Models/SConfig.cs` (`UseCaseInsensitivePaths`) and `SMAPI.Toolkit/Utilities/PathLookups/CaseInsensitiveFileLookup.cs` (`GetFile` and `GetRelativePathCache`).
- **Scenario:** Linux startup and first file access with hundreds of mod roots and many content files.
- **Root cause:** The first lookup for each root forces recursive directory enumeration before checking whether the requested path already exists with the exact casing. Normal manifest DLL and content paths therefore pay the compatibility-index cost even though they don't need case correction.
- **Impact:** Startup and memory.
- **Expected benefit:** Exact-first lookup avoids recursive enumeration for correctly cased mods; a directory-level fallback could further limit work for the smaller set of mis-cased paths while retaining Windows-style compatibility.
- **Risk:** Low for exact-first lookup. A replacement fallback is medium to high risk because runtime file creation, symlinks, case-colliding names, and invalidation need well-defined handling.
- **Status:** Partially fixed. Exact paths are now normalized and checked before the recursive compatibility index is materialized, so correctly cased mods and content packs avoid indexing their trees entirely. Mis-cased paths retain the existing fallback behavior; replacing that fallback with a reusable directory-level index remains deferred pending evidence that those uncommon lookups are a material startup cost.

### 12. Mod assemblies are parsed and rewritten again on every launch

- **Affected code:** `Framework/ModLoading/AssemblyLoader.cs` (`Load` and rewrite pipeline).
- **Scenario:** Linux startup with hundreds of code-mod DLLs which have not changed since the previous launch.
- **Root cause:** Cecil reads symbols and assemblies, compatibility handlers walk IL, and rewritten assemblies are serialized to memory each launch without a persistent result cache.
- **Impact:** Startup and temporary memory pressure.
- **Expected benefit:** A content-addressed rewrite cache can skip unchanged compatibility work.
- **Risk:** High. The key must cover SMAPI, game, platform, rewrite-handler, symbol, and configuration versions; stale rewritten code is unacceptable.
- **Status:** Fixed. SMAPI now persists content-addressed analysis/rewrite results keyed by the exact DLL and PDB bytes plus the SMAPI module identity, target platform, live game/framework module identities, paranoid/rewrite settings, and diagnostic-detail mode. Cache hits still parse the authoritative source dependency graph, replay the same warnings and trace messages, recheck currently missing references, and only load a cached rewritten image after validating its assembly identity. Entries have a SHA-256 integrity check, writes are atomic, corrupt entries fall back to a normal rewrite, obsolete environments are removed, and an unavailable cache never blocks loading. On the target Linux/Proton installation, a cold run populated 270 entries and the 250-mod `Loading mods` phase took 13 seconds; the immediately repeated warm run took 4 seconds. Both runs loaded all 250 mods, emitted the same 231 rewrite/detection diagnostics, and produced identical normalized SMAPI warning/error output (37 entries).

### 13. Mod dependency resolution repeatedly scans the mod list

- **Affected code:** `Framework/ModLoading/ModResolver.cs` (`ProcessDependencies`, `GetDependenciesFrom`, and local `FindMod`).
- **Scenario:** Startup with hundreds of manifests and dependency edges.
- **Root cause:** Dependency lookup uses repeated linear searches by unique ID rather than a prebuilt ID dictionary.
- **Impact:** Startup.
- **Expected benefit:** Constant-time dependency lookup removes avoidable quadratic scaling.
- **Risk:** Low. Duplicate-ID and ignored-mod diagnostics must preserve their current selection behavior.
- **Status:** Fixed. Dependency sorting now builds one trimmed, case-insensitive unique-ID index and resolves every dependency edge through it, while preserving the prior first-match behavior for duplicate IDs.

### 14. Uncached disposable tracking retains dead weak-reference wrappers

- **Affected code:** `Framework/ContentManagers/BaseContentManager.cs` (uncached disposable registration and disposal).
- **Scenario:** Long sessions in which mods repeatedly request privately owned disposable assets.
- **Root cause:** Dead target objects can be collected, but their `WeakReference<IDisposable>` wrappers remain in the manager list until the manager itself is disposed.
- **Impact:** Memory and eventual disposal traversal time.
- **Expected benefit:** Periodic opportunistic pruning bounds bookkeeping growth without changing asset lifetime.
- **Risk:** Low to medium. Pruning must not lose live disposables or add a costly full scan to every load.
- **Status:** Fixed. Uncached disposable registration now prunes collected weak references at an adaptive interval, keeping frequent cleanup for short-lived assets while avoiding repeated scans when many assets remain alive.

### 15. Asset operation discovery is cached for only one tick

- **Affected code:** `Framework/ContentCoordinator.cs` (`AssetOperationsByKey`) and `Framework/Utilities/TickCacheDictionary.cs`.
- **Scenario:** Repeated uncached requests for the same asset across ticks, especially when many frameworks subscribe to `AssetRequested`.
- **Root cause:** Loader/editor discovery results expire on the next process tick because SMAPI has no provider generation token that can prove a cross-tick result is still valid.
- **Impact:** Transitions and steady gameplay for mods which request uncached assets frequently.
- **Expected benefit:** Generation-based caching or keyed subscriptions can avoid dispatching unrelated providers repeatedly.
- **Risk:** High. Mods can add/remove handlers or change context dynamically, so stale negative or operation caches would break content behavior.
- **Status:** Rejected as a registration-generation cache without a new provider contract. The installed target collection contains 106 assemblies which participate in `AssetRequested`, but handlers can change whether and how they handle an asset based on arbitrary game/mod state without adding or removing the handler. A handler-registration generation therefore can't prove that a prior positive or negative result is still valid. The one-tick cache remains the longest safe lifetime under the current API; request-frequency tracing can still justify a future keyed provider API.

### 16. Event dispatch performs per-handler context operations and lazy callback allocations

- **Affected code:** `Framework/Events/ManagedEvent.cs` (`Raise`) and `Framework/Events/ManagedEventHandler.cs`.
- **Scenario:** High-frequency update/input events with many subscribers, and repeated live asset requests while walking or transitioning.
- **Root cause:** Each handler invocation pushes and pops the current mod context and enters exception-handling logic. Lazy dispatch originally created a new callback delegate for every handler on every raise; after caching those callbacks, high-frequency callers still created a capturing outer dispatch closure for each live asset request or routed network message.
- **Impact:** Steady gameplay, transitions, and garbage collection.
- **Expected benefit:** Caching lazy callbacks removes repeat dispatch allocations; a correctly scoped context fast path could further reduce framework overhead, although mod handler time will usually dominate.
- **Risk:** High. Current-mod attribution and exception isolation are correctness features and must not be weakened.
- **Status:** Partially fixed. Each registered handler owns one cached lazy-dispatch callback, and stateful raises now pass stack-held per-raise state by reference to cached static invokers. Live asset requests and routed network messages therefore avoid both the per-handler callback allocations and their per-raise capturing dispatch closure. `AssetRequestedEventArgs` now creates its loader and editor lists only when a handler actually registers that operation type; a no-op request fell from 120 to 56 allocated bytes in the warmed .NET 10 allocation check, and loader-only/editor-only requests avoid the unrelated empty list too. Context stack operations and exception boundaries remain unchanged pending runtime evidence.

### 17. World event manager identifiers for locations and buildings are swapped

- **Affected code:** `Framework/Events/EventManager.cs` (constructor assignments for `BuildingListChanged` and `LocationListChanged`).
- **Scenario:** Diagnostics, event attribution, and any internal behavior keyed by a managed event's textual name.
- **Root cause:** `BuildingListChanged` is constructed with the location-list event name and `LocationListChanged` with the building-list event name.
- **Impact:** Correctness and diagnostics; negligible direct performance impact.
- **Expected benefit:** Correct event identity in logs, attribution, and management operations.
- **Risk:** Low. The public event fields and argument types remain unchanged; only the internal name is corrected.
- **Status:** Fixed. Each managed event now uses the textual identifier matching its public world event.

### 18. Added and removed locations are reversed in event snapshots

- **Affected code:** `Framework/StateTracking/Snapshots/WorldLocationsSnapshot.cs` (`Update`).
- **Scenario:** Mods handling `LocationListChanged` as temporary mine levels, building interiors, or modded locations enter or leave the world on Linux or other platforms.
- **Root cause:** `WorldLocationsTracker.Added` is passed to the `removed` parameter of `SnapshotListDiff.Update`, and `Removed` is passed to `added`.
- **Impact:** Correctness during transitions.
- **Expected benefit:** Mods receive newly tracked locations in `Added` and departed locations in `Removed`, matching the public event contract.
- **Risk:** Low. The fix only corrects argument direction at the snapshot boundary.
- **Status:** Fixed. The call now uses named arguments to make the direction explicit, with add/remove coverage at the world snapshot boundary.

### 19. Repeated propagation side effects are not coalesced

- **Affected code:** `Metadata/CoreAssetPropagator.cs` asset-specific propagation methods.
- **Scenario:** A single context change invalidates multiple assets which each request the same registry reset or global cache rebuild.
- **Root cause:** Side effects are applied immediately per asset instead of being accumulated and executed once at the end of the invalidation transaction.
- **Impact:** Transitions.
- **Expected benefit:** Fewer repeated registry resets, warp-cache rebuilds, and global refresh calls.
- **Risk:** High. Some invalidations may require ordering or intermediate state visible to later propagation.
- **Status:** Partially fixed. Adjacent item-data routes now assign all of their new data sources and reset the shared `ItemRegistry` once at the end of that run instead of once per asset. A non-item route flushes the pending reset first, preserving the exact point at which any other propagation can observe registry state. Other unrelated global side effects remain separate until their ordering contracts are proven.

### 20. The runtime is out of support and performance features are disabled

- **Affected code:** `SMAPI.csproj` (`TargetFramework` and tiered compilation properties) and `SMAPI.Installer/assets/runtimeconfig.json`.
- **Scenario:** All Linux launches and gameplay.
- **Root cause:** SMAPI targets .NET 6 and disables tiered compilation due to Harmony/runtime compatibility constraints, preventing use of current runtime, JIT, GC, and dynamic PGO improvements.
- **Impact:** Startup, steady gameplay, transitions, and supportability.
- **Expected benefit:** .NET 10 provides a supported LTS runtime and broad JIT/runtime improvements; safely restoring tiered compilation may be more valuable than the target-framework change alone.
- **Risk:** Very high. Game integration, bundled runtime, Harmony patching, mod binary compatibility, installer packaging, and all supported platforms are affected.
- **Status:** Deferred until algorithmic fixes and Harmony compatibility are stable.

### 21. Texture and decoded-content memory pressure is not centrally budgeted

- **Affected code:** `Framework/ContentManagers/BaseContentManager.cs`, `GameContentManager.cs`, and `ModContentManager.cs` asset ownership/cache paths.
- **Scenario:** Long Linux sessions with many high-resolution content-pack textures and repeated invalidation/reload cycles.
- **Root cause:** Cache decisions do not have one byte-oriented budget covering decoded CPU buffers, live texture objects, and privately owned uncached assets.
- **Impact:** Memory, garbage collection, and possible paging-related stutter.
- **Expected benefit:** Byte-aware bounded caches and prompt release of transient decode buffers reduce memory spikes and paging risk.
- **Risk:** High. GPU allocation size is approximate, asset ownership is split, and aggressive eviction can cause reload thrashing.
- **Status:** Partially fixed for decoded CPU pixels. The installed target collection contains 46,714 readable PNGs representing about 16.0 GB of decoded RGBA data, which rules out whole-pack caching. The shared cache instead uses a 16–128 MiB adaptive byte budget, second-use admission, per-entry limits, LRU eviction, bounded first-use bookkeeping, and file metadata validation. It retains only repeatedly decoded recent sources and releases everything with the content coordinator. Live GPU textures and privately owned uncached assets still need a separate ownership model before they can be budgeted safely.

### 22. Sparse image patches transfer transparent columns

- **Affected code:** `Framework/Content/AssetDataForImage.cs` (`PatchImageImpl`).
- **Scenario:** Content Patcher-scale overlay and mask edits which place a small sprite inside a much wider transparent source area during warps or context changes.
- **Root cause:** SMAPI trims fully transparent leading and trailing rows, but keeps the original patch width. It therefore reads back, blends, and uploads transparent columns on both sides of the actual changed pixels.
- **Impact:** Transitions and temporary memory pressure.
- **Expected benefit:** Bounding the operation on all four sides reduces GPU readback/upload bytes, the rented merge buffer, and pixels visited by the blend loop for sparse patches.
- **Risk:** Medium. Source arrays can represent a packed subarea or a row window into a larger raw image, so source-to-target coordinate mapping must remain exact for overlay and mask modes.
- **Status:** Fixed. Overlay and mask patches now retain the existing fast endpoint checks, scan horizontal alpha bounds only when needed, and transfer/blend the exact nontransparent rectangle.

### 23. Core update wrappers allocate capturing callbacks every tick

- **Affected code:** `Framework/SGameRunner.cs` (`Update`) and `Framework/SGame.cs` (`Update`), with callback consumers in `Framework/SCore.cs`.
- **Scenario:** Every Linux gameplay update, including ordinary walking; the inner allocation repeats for each local split-screen instance.
- **Root cause:** Both update overrides construct a lambda which captures the current `GameTime` solely so SMAPI can let `SCore` invoke the corresponding base update inside its event and error boundaries.
- **Impact:** Steady gameplay and garbage collection.
- **Expected benefit:** Cached base-update delegates remove the two continuous closure allocations per single-player tick, plus the additional per-screen closures in split-screen, reducing Gen 0 pressure without changing update ordering.
- **Risk:** Low. The callback must receive the exact `GameTime` for its invocation and remain synchronous.
- **Status:** Fixed. The wrapper delegates now accept `GameTime` directly and each game object reuses one cached base-update callback, preserving the existing synchronous control flow without per-tick closures.

### 24. Input polling allocates and rehashes held buttons while walking

- **Affected code:** `Framework/Input/KeyboardStateBuilder.cs`, `MouseStateBuilder.cs`, and `GamePadStateBuilder.cs` (`Reset`, `FillPressedButtons`, and override handling).
- **Scenario:** Every focused Linux update, including ordinary WASD or controller walking.
- **Root cause:** SMAPI called MonoGame's parameterless `KeyboardState.GetPressedKeys`, which constructed a new exact-sized array whenever at least one key was pressed. All three builders then cleared and repopulated their own mutable button hash sets before immediately copying those buttons into SMAPI's combined set, even though the private sets are only needed when a mod overrides input.
- **Impact:** Steady gameplay and garbage collection.
- **Expected benefit:** A caller-owned key buffer removes the continuous pressed-key array allocation, while direct immutable-state enumeration removes one redundant hash-set clear/fill/enumeration layer from normal keyboard, mouse, and controller ticks.
- **Risk:** Low to medium. Reused keyboard buffers must process only their populated prefix, and the lazy mutable sets must reconstruct every digital button before applying the first override without changing analog trigger or thumbstick state.
- **Status:** Fixed. Keyboard polling grows one per-player buffer only when its simultaneous key-count high-water mark increases and fills it through MonoGame's nonallocating overload. Keyboard, mouse, and controller builders now read the original immutable state directly on normal ticks and materialize their private mutable pressed-button sets only when an override is requested. Focused tests verify the sets remain lazy and that reconstructed keyboard, mouse, digital controller, trigger, and thumbstick output is equivalent after overrides.

### 25. ButtonsChanged constructs three lists on every held-input tick

- **Affected code:** `Events/ButtonsChangedEventArgs.cs` (constructor).
- **Scenario:** Focused Linux gameplay while a mod listens to `ButtonsChanged` and the player holds a movement key or controller direction.
- **Root cause:** Every event snapshot originally created separate pressed, held, and released lists even when a category was empty; after making categories lazy, each populated category still allocated both a `List<SButton>` object and its minimum four-element backing array. An ordinary walking tick normally populates only the held category with one movement key.
- **Impact:** Steady gameplay and garbage collection when the event has listeners.
- **Expected benefit:** Lazy category allocation removes the two empty list objects from the normal held-input event and avoids all three list objects if an unusual active-state snapshot contains no categorized buttons.
- **Risk:** Low. The public properties remain `IEnumerable<SButton>` snapshots; empty categories use immutable empty enumerables instead of newly allocated empty lists.
- **Status:** Fixed. Empty categories share the runtime's allocation-free empty representation, while populated categories are now exact arrays. The normal one-key held-input snapshot therefore retains the required stable public snapshot with one allocation instead of a list object plus an over-capacity backing array; concrete dictionary enumeration avoids adding boxed enumerators for the count and fill passes.

### 26. Camera scrolling materializes unused cursor snapshots

- **Affected code:** `Framework/Input/SInputState.cs` (`TrueUpdate` and `CursorPosition`), `Framework/WatcherCore.cs` (`Update`/`Reset`), and `Framework/SCore.cs` (`OnPlayerInstanceUpdating`).
- **Scenario:** Focused Linux gameplay while walking scrolls the viewport, particularly when no mod listens for cursor movement or requests cursor coordinates on that tick.
- **Root cause:** A changed viewport changes the cursor's map-relative coordinates, so SMAPI constructs a new immutable `CursorPosition` every update. The cursor watcher and input-event block both read it unconditionally even when no observable event needs the object.
- **Impact:** Steady gameplay and garbage collection.
- **Expected benefit:** Keeping pending coordinates as value fields and materializing the immutable API object on demand removes this per-scroll-tick allocation for mod sets which don't consume cursor positions continuously.
- **Risk:** Medium. Cursor events need stable old/new objects, polling must still return current coordinates, and enabling a listener needs a fresh baseline rather than a delayed movement event.
- **Status:** Fixed. Input polling now records the same pre-update coordinate values without constructing an object, cursor-diff tracking is listener-aware with an activation baseline, and button/wheel code requests the snapshot only when it will raise an event. Direct `IInputHelper` polling still materializes and returns the current immutable snapshot on demand.

### 27. Tick-cache lookups allocate factories before checking the cache

- **Affected code:** `Framework/Utilities/TickCacheDictionary.cs` (`GetOrSet`), `Framework/ContentCoordinator.cs` (`GetAssetOperations`), and `Metadata/CoreAssetPropagator.cs` world lookup helpers.
- **Scenario:** Repeated live asset requests during gameplay and multi-asset propagation during warp, day, season, or context changes on Linux.
- **Root cause:** Callers construct capturing value-factory closures for every lookup, including cache hits, and the type-erased world cache wraps each factory in another closure. Location helpers also interpolate boolean cache keys and snapshot root locations through temporary LINQ iterator/array objects.
- **Impact:** Steady gameplay for live requests, transitions, and garbage collection.
- **Expected benefit:** Stateful static factories make cache hits allocation-free and reduce temporary objects when a propagation miss must enumerate the world.
- **Risk:** Low. Factory state must be ignored on hits exactly like the old closure, and typed values in the type-erased cache must retain the existing cast diagnostics.
- **Status:** Fixed. Tick caches now accept explicit factory state, asset requests and world helpers use cached static delegates and stable keys, the derived cache no longer creates an adapter closure, and location enumeration fills its result directly without LINQ or root-list snapshots.

### 28. Texture propagation allocates a lazy factory and can strand its temporary texture

- **Affected code:** `Metadata/CoreAssetPropagator.cs` (`PropagateTexture`).
- **Scenario:** Linux transitions or Content Patcher context changes which invalidate loaded textures, especially batches containing many texture keys or an incompatible target texture that throws during the in-place copy.
- **Root cause:** Each localized/base-name propagation pass constructs a `Lazy<Texture2D?>` and capturing factory even though the surrounding loop is already the lazy boundary. The replacement texture is disposed only after every target copy completes, so an exception caught by the outer per-asset propagation handler skips disposal of that temporary GPU resource.
- **Impact:** Transitions, garbage collection, and memory on propagation failures.
- **Expected benefit:** Direct delayed loading removes the lazy object and closure from each propagated texture pass, while guaranteed disposal prevents failed in-place copies from retaining temporary GPU resources until the propagation content manager is disposed.
- **Risk:** Low. The replacement is still loaded at most once and only after a matching cached target is found; all targets receive the same replacement instance and successful propagation order is unchanged.
- **Status:** Fixed. Texture propagation now uses an explicit one-shot load guard and disposes the temporary replacement in a `finally` block.

### 29. Add-before-remove world transfers can drop tracked locations

- **Affected code:** `Framework/StateTracking/WorldLocationsTracker.cs` (`Update`).
- **Scenario:** A Linux game or expansion mod moves a building between locations, or a temporary location between the main, active-mine, and active-volcano lists, by adding it to the destination before removing it from the source in the same update.
- **Root cause:** SMAPI processes each changed source independently as remove-then-add. If the destination source is visited first, its addition is ignored because the object is still tracked; the later source removal then deletes the only tracker. Building indoor locations can consequently disappear from world event tracking even though the building remains in the world.
- **Impact:** Steady-gameplay and transition correctness.
- **Expected benefit:** Stable tracking across either transfer order prevents subsequent world and building-interior changes from being missed in large expansion saves.
- **Risk:** Low. Removals and additions retain their existing order within each source, but all changed sources now complete removal before any source is allowed to add.
- **Status:** Fixed. Top-level location sources and changed building collections are processed in two phases, with all removals preceding all additions.

### 30. Rectangular transformed tiles use the horizontal origin on both axes

- **Affected code:** `Framework/Rendering/SDisplayDevice.cs` (`DrawImpl`).
- **Scenario:** Linux gameplay on a custom map containing a non-square tile with SMAPI rotation or flip metadata.
- **Root cause:** SMAPI calculates the correct two-dimensional center point, but offsets both the X and Y draw coordinates by `origin.X`. A rectangular source therefore shifts vertically by half its width instead of half its height before rotation.
- **Impact:** Steady-gameplay rendering correctness.
- **Expected benefit:** Rotated and flipped rectangular tiles stay centered at their intended map position instead of visibly jumping along the vertical axis.
- **Risk:** Low. Square tiles are numerically unchanged, normal tiles still use xTile's base fast path, and only the transformed rectangular-tile Y offset changes.
- **Status:** Fixed. The vertical draw offset now uses `origin.Y`.

### 31. Intercepted asset reloads repeatedly allocate dispatch helpers

- **Affected code:** `Framework/ContentManagers/BaseContentManager.cs`, `GameContentManager.cs` (`LoadExact`, `ApplyLoader`, `ApplyEditors`, and `AssertMaxOneRequiredLoader`), `Framework/ContentCoordinator.cs` (predicate invalidation), `Framework/SCore.cs` (`RequestAssetOperations`), and `Framework/Utilities/ContextHash.cs` (`Track`).
- **Scenario:** Linux gameplay transitions and context changes which reload many assets intercepted by Content Patcher-scale handler sets.
- **Root cause:** Every uncached intercepted load created a capturing recursive-load closure. Loader validation built a filtered array even in the normal zero-or-one-exclusive-loader case, selecting the winning loader used a LINQ maximum pass, and every application of a cached editor group rebuilt a stable LINQ ordering pipeline. Asset metadata also created a new bound normalization delegate per instance—including once per cached asset inspected by predicate invalidation—and the editor method emitted a captured rollback frame on successful edits even though rollback was only used for invalid mod output. Assets requested through a general type like `object` also rediscovered, specialized, and reflectively invoked the generic editor method on every application.
- **Impact:** Transitions and garbage collection, with cost repeated per reloaded asset and content manager.
- **Expected benefit:** Removes routine helper objects and redundant ordering work around mod loaders/editors, leaving more of the transition frame budget for the actual content edits and texture/map work.
- **Risk:** Low. The highest-priority loader still wins with registration order breaking ties, editor ordering remains stable for equal priorities, recursive-load cleanup still runs through `finally`, and the conflict diagnostics are unchanged.
- **Status:** Fixed. Recursive-load tracking now accepts explicit state and a cached static callback, the normal exclusive-loader check and winning-loader selection use direct list scans, and editor operations are stably ordered once when their tick-cached operation group is created and enumerated directly thereafter. Each content manager now creates one stable asset-name normalizer delegate for all metadata and editable-asset wrappers. Predicate invalidation reuses that delegate for every inspected cache entry, and the normal editor path no longer emits the captured rollback frame; Release output contains neither its display class nor local-function method. Object-typed dictionaries, lists, textures, and maps now create one open typed editor delegate per concrete type and invoke it directly thereafter without reflection or argument arrays.

### 32. Map propagation allocates two warp sets per reloaded location

- **Affected code:** `Metadata/CoreAssetPropagator.cs` (`PropagateMap`).
- **Scenario:** Linux warps, season changes, and Content Patcher context updates which invalidate a batch of map assets used by many loaded locations.
- **Root cause:** Before and after reloading every matching location map, SMAPI constructs a fresh hash set of warp and door destinations so it can decide whether to rebuild the global NPC warp-route cache. A multi-map expansion update therefore creates two separate set objects and backing storage per reloaded location.
- **Impact:** Transitions and garbage collection.
- **Expected benefit:** Bounds the temporary set allocation for an entire propagation transaction to at most two reusable sets, and stops collecting later warp snapshots once one changed route has already proven that the global cache must be rebuilt.
- **Risk:** Low. The same case-sensitive destination sets, count check, membership check, map update order, and final cache-rebuild decision are retained.
- **Status:** Fixed. The map propagation pass now clears and reuses one old/new target-set pair across matching locations, and skips further comparisons after detecting the first route change.

### 33. Asset loader registrations create adapter closures

- **Affected code:** `Events/AssetRequestedEventArgs.cs` (`LoadFrom` and `LoadFromModFile`), `Framework/Content/AssetLoadOperation.cs`, and `Framework/ContentManagers/GameContentManager.cs` (`ApplyLoader`).
- **Scenario:** Linux asset requests and context-driven reloads where many Content Patcher-style handlers register conditional loaders, including mod-file loads.
- **Root cause:** SMAPI wraps a caller's existing parameterless load delegate in a second captured delegate solely to discard an unused `IAssetInfo` argument. Mod-file loaders similarly capture the requesting mod and relative path in a new closure on every matching request.
- **Impact:** Transitions and garbage collection.
- **Expected benefit:** Removes one framework-created closure per applicable load registration while retaining the one operation object needed to record priority, attribution, and loader state.
- **Risk:** Low. The public callbacks, exception boundary, priority selection, mod attribution, file API, and invocation timing remain unchanged; only the internal representation differs.
- **Status:** Fixed. Delegate-backed operations now retain the caller's `Func<object>` directly, while a generic mod-file operation stores its mod and path in fields and invokes `ModContent.Load<TAsset>` without a captured adapter. Release IL no longer contains the two `AssetRequestedEventArgs` display classes.

### 34. Map patching repeats layer-wide work for every tile

- **Affected code:** `Framework/Content/AssetDataForMap.cs` (`PatchMap`).
- **Scenario:** Linux map loads and context changes where large content packs overlay or replace substantial map regions with multiple layers.
- **Root cause:** Inside the nested width-by-height tile loop, SMAPI iterates source layers, hashes each source layer through a dictionary lookup, and copies the layer's entire property collection. Layer creation can only be needed once and layer properties apply to the whole layer, so both operations are invariant across every tile in the patch.
- **Impact:** Transitions and warp-time stalls, scaling with patch area, layer count, and layer-property count.
- **Expected benefit:** Reduces layer-property copies from `width × height × layers` to `layers` and removes the source-to-target dictionary lookup from the innermost tile loop. For example, a 100×100 five-layer patch performs five property copies instead of 50,000.
- **Risk:** Low. Source/target layer pairing, missing-layer creation, property results, tile order, patch-mode behavior, and target-only layer clearing are retained. Zero-sized patches explicitly keep their previous behavior of making no layer changes.
- **Status:** Fixed. Source and target layers are paired once before tile traversal, missing layers and properties are handled once per source layer, and the tile loop iterates those stable pairs directly. Target-only layer tracking is allocated only for full `Replace` mode.

### 35. Render events reflect private sprite-batch state several times per frame

- **Affected code:** `Framework/Extensions/SpriteBatchExtensions.cs` (`IsOpen`) and `Framework/SCore.cs` (`RaiseRenderEvent`).
- **Scenario:** Every Linux draw with mods subscribed to generic or stage-specific rendering events; the check can repeat for world, menu, HUD, step, and final rendered events.
- **Root cause:** Each event checks MonoGame's private `_beginCalled` field through the general `Reflector`, which formats a cache key, constructs a capturing lookup delegate, allocates a reflected-field wrapper, invokes `FieldInfo.GetValue`, and boxes the Boolean result even when the metadata is cached. The temporary-open path also begins `Game1.spriteBatch` but ends the method's passed `spriteBatch`, which can corrupt batch state if they differ.
- **Impact:** Steady frame pacing, garbage collection, and render correctness.
- **Expected benefit:** Makes the repeated open-state check a direct typed delegate call with no per-check framework allocation or Boolean boxing.
- **Risk:** Low to medium. The accessor relies on MonoGame's private field name, as the old reflection path already did; initialization validates that the field still exists with the expected Boolean type.
- **Status:** Fixed. SMAPI creates one visibility-skipping typed field accessor at initialization and reuses it for every render-stage check and draw-error recovery check. Temporary rendering now begins and ends the same passed sprite batch.

### 36. Localized asset parsing allocates a bound delegate per call

- **Affected code:** `Framework/ContentCoordinator.cs` (`ParseAssetName`).
- **Scenario:** Every localized asset-name parse during large content-pack loads, cache invalidations, and asset propagation.
- **Root cause:** The `allowLocales` path creates a new bound `Func<string, LanguageCode?>` which captures the coordinator for every parse, even though the callback behavior is stable for the coordinator's lifetime.
- **Impact:** Content-load and transition CPU/GC pressure, proportional to asset-name parsing volume.
- **Expected benefit:** Removes the callback and closure allocation from each localized parse by creating the bound method delegate once.
- **Risk:** Low. Locale lookup and lazy locale-table behavior are unchanged.
- **Status:** Fixed. `ContentCoordinator` now caches one typed locale parser and reuses it for all localized asset-name parses.

### 37. Invalidation propagation clones the entire batch immediately before reading it

- **Affected code:** `Framework/ContentCoordinator.cs` (`ProcessInvalidatedAssets`) and `Metadata/CoreAssetPropagator.cs` (`Propagate`).
- **Scenario:** Large Content Patcher invalidation batches during day changes, warps, token changes, and other context updates.
- **Root cause:** The coordinator owns a fresh invalidated-assets dictionary, but copies every entry into another dictionary immediately before passing it to propagation, which only reads the collection.
- **Impact:** Transition CPU, temporary memory, and GC pressure proportional to invalidation-batch size.
- **Expected benefit:** Removes one full dictionary allocation, entry copy, rehash, and subsequent collection from every nonempty invalidation transaction.
- **Risk:** Low. The propagator now expresses its existing read-only contract; iteration and asset identity are unchanged.
- **Status:** Fixed. The coordinator passes its existing batch directly through an `IReadOnlyDictionary` contract.

### 38. Linux paint-mask propagation uses a case-sensitive suffix check

- **Affected code:** `Metadata/CoreAssetPropagator.cs` (`PropagateTexture`).
- **Scenario:** A mod supplies or invalidates a building paint mask whose `_PaintMask` suffix differs only in casing on Linux.
- **Root cause:** The directory check uses SMAPI's case-insensitive asset-name semantics, but the final raw string suffix check uses the platform-neutral case-sensitive overload.
- **Impact:** Linux correctness and stale visual state; the changed texture can be reloaded while live buildings keep the old painted texture.
- **Expected benefit:** Ensures valid building paint-mask invalidations propagate consistently regardless of filename casing.
- **Risk:** Low. This aligns the suffix test with SMAPI's documented case-insensitive asset-name comparisons.
- **Status:** Fixed. The paint-mask suffix uses `StringComparison.OrdinalIgnoreCase`.

### 39. Linux content paths use culture-sensitive allocation and ambiguous prefix matching

- **Affected code:** `Framework/ContentManagers/BaseContentManager.cs` (`PrenormalizeRawAssetName`) and `Framework/ContentManagers/ModContentManager.cs` (`LoadExact`, `FixTilesheetPaths`).
- **Scenario:** Loading mod files and XNB maps with mixed-case extensions or tilesheet paths on Linux.
- **Root cause:** File dispatch lowercases every extension using the current culture; legacy `.xnb` stripping is case-sensitive; and eager tilesheet-prefix removal uses the default `StartsWith` overload without requiring a directory boundary, so a `Maps` prefix can incorrectly match `Maps2`.
- **Impact:** Mod-content load CPU/allocations and Linux path correctness. Ambiguous prefix removal can rewrite a valid tilesheet path or slice past a path that only shares the same leading characters.
- **Expected benefit:** Removes one string allocation from every mod-file dispatch and makes extension/path handling deterministic, case-insensitive, and segment-aware.
- **Risk:** Low. Comparisons now use explicit ordinal asset-path semantics, and prefix removal only applies to a complete directory prefix.
- **Status:** Fixed. Extension dispatch uses allocation-free ordinal-ignore-case comparisons, `.xnb` pre-normalization accepts any casing, and eager map prefixes include the normalized directory separator.

### 40. Current-screen state repeats cleanup checks and dictionary hashing

- **Affected code:** `Utilities/PerScreen.cs` (`Value`, `GetValueForScreen`, `SetValueForScreen`, and screen removal).
- **Scenario:** Every update and draw where SMAPI or mods repeatedly query per-screen values such as `Context.IsWorldReady`, load stage, peer state, command queues, or mod-owned `PerScreen<T>` instances.
- **Root cause:** Each read checks the global removed-screen marker and hashes the current screen ID into a dictionary, even though calls normally occur in long runs for the same active screen.
- **Impact:** Steady walking/draw CPU overhead, multiplied by SMAPI and mod call volume.
- **Expected benefit:** Repeated reads for the current screen become three scalar comparisons and a field read, with no dictionary hashing or cleanup method traversal.
- **Risk:** Low to medium. Split-screen switching, explicit screen access, writes, removals, and full reset must keep the fast cache coherent.
- **Status:** Fixed. `PerScreen<T>` retains the most recently accessed screen/value, uses it only while the removed-screen generation matches, writes through on assignment, and invalidates it when that screen is removed or all screens reset.

### 41. Propagation uses an incomplete and duplicate world-location topology

- **Affected code:** `Metadata/CoreAssetPropagator.cs` (`GetLocationsWithInfo`) and `Framework/ContentCoordinator.cs` (live-map invalidation discovery).
- **Scenario:** Map, NPC, building, and texture invalidations in saves with expansion locations, nested building interiors, active mine levels, or volcano dungeon levels.
- **Root cause:** Propagation combines `Game1.locations` and `SaveGame.loaded.locations` without reference de-duplication, follows only one interior level, and omits `MineShaft.activeMines` and `VolcanoDungeon.activeLevels`. Exact invalidation's live-map fallback scans only `Game1.locations`. `WorldLocationsTracker` already models more of this topology, but the implementations are separate.
- **Impact:** Duplicate propagation work and side effects for overlapping roots, plus stale generated/nested locations that are never updated.
- **Expected benefit:** One deterministic reference-deduplicated recursive traversal shared by invalidation, propagation, and world tracking.
- **Risk:** Medium to high. Ordering, building ownership metadata, generated-level lifetime, and save-load overlap must be preserved.
- **Status:** Fixed. A shared root-first traversal now reference-deduplicates `Game1` and loaded-save roots, includes active mine and volcano levels, recursively follows interiors to any depth, and retains every building which references a shared interior. Propagation caches this topology per tick, while exact and predicate invalidation use the same live-location coverage.

### 42. Location trackers don't retain source ownership

- **Affected code:** `Framework/StateTracking/WorldLocationsTracker.cs` (`Add` and `Remove`).
- **Scenario:** A location is reachable from multiple roots, transfers between root/building/generated-location sources across ticks, or temporarily appears in overlapping source lists.
- **Root cause:** `Add(GameLocation)` forcibly removes any existing tracker before recreating it, while `Remove(GameLocation)` has no source token or reference count. Removing one source can therefore dispose the only tracker even while another source still owns the same live location.
- **Impact:** Walking-time change detection correctness and avoidable tracker churn; live locations can stop reporting content changes.
- **Expected benefit:** Stable one-tracker-per-location ownership across transfers and overlapping roots, with less disposal/recreation work.
- **Risk:** Medium to high. Source identities and same-tick transfer behavior need an explicit contract to prevent leaks or delayed removals.
- **Status:** Fixed. Reference-identity owner counts now cover both locations and buildings. Same-update additions are applied before removals, so transfers and overlapping root/parent sources retain one tracker, one nested tracker graph, and one building net-field handler until the last owner disappears.

### 43. Core propagation allocates normalized key strings per asset

- **Affected code:** `Metadata/CoreAssetPropagator.cs` (`PropagateTexture` and `PropagateOther`).
- **Scenario:** Every large invalidation/propagation transaction.
- **Root cause:** Dispatch calls `ToLower().Replace("\\", "/")` for each propagated key. `AssetName` already normalizes separators, so replacement is redundant; lowercasing remains a culture-sensitive full-string allocation.
- **Impact:** Transition CPU and GC pressure proportional to invalidated asset count.
- **Expected benefit:** Allocation-free ordinal-ignore-case dispatch over the already normalized asset name.
- **Risk:** Low to medium. A replacement dispatch table must preserve every alias and the exact precedence of current cases.
- **Status:** Fixed. Static ordinal-ignore-case route tables now dispatch all five texture keys and 37 non-texture keys without transforming the requested name. Focused coverage locks every legacy switch label, alias, case-insensitive comparison, normalized separator, and dynamic fallback route.

### 44. Assembly loading repeats global scans and reparses visited local dependencies

- **Affected code:** `Framework/ModLoading/AssemblyLoader.cs` (`Load` and `GetReferencedLocalAssemblies`).
- **Scenario:** Startup with hundreds of code mods that share bundled dependencies.
- **Root cause:** Each mod rebuilds a set by scanning all loaded AppDomain assemblies. Recursive local dependency discovery reads the full DLL, initializes symbol handling, and parses its metadata before checking whether that assembly name was already visited.
- **Impact:** Startup disk I/O, allocations, and Cecil parsing time, especially for dependency-heavy packs.
- **Expected benefit:** Maintain loaded-name state across the launch and skip already-visited canonical local files before reading/parsing them.
- **Risk:** Medium. Assembly identity, duplicate-copy diagnostics, resolver search paths, and mods that load assemblies dynamically must remain correct.
- **Status:** Fixed. `AssemblyLoader` now builds one live concurrent simple-name index, updates it through `AppDomain.AssemblyLoad`, and maintains separate per-root identity and platform-aware canonical-path sets. Existing local dependencies and cycles are rejected before byte reads, PDB probing, stream allocation, or Cecil parsing; root assemblies are still parsed before duplicate-mod diagnostics so filename mismatches retain the old behavior.

### 45. Image overlay composition calculates the wrong output alpha

- **Affected code:** `Framework/Content/AssetDataForImage.cs` (`PatchImage` overlay loop).
- **Scenario:** Content packs overlay translucent pixels onto translucent target pixels.
- **Root cause:** RGB channels use premultiplied source-over composition, but alpha uses `Math.Max(sourceAlpha, targetAlpha)`. Two 50%-opaque pixels therefore remain about 50% alpha instead of composing to about 75%.
- **Impact:** Visual correctness. Incorrect alpha can also cause later overlays to produce progressively wrong results.
- **Expected benefit:** Mathematically consistent source-over output for layered translucent content-pack patches.
- **Risk:** Medium to high. Some packs may have authored around the old behavior, so representative visual comparison is required.
- **Status:** Fixed. Premultiplied RGB and alpha now use the same source-over calculation, with focused coverage for symmetric and asymmetric translucent pixels plus transparent and opaque edge values.

### 46. Async log writing still formats every record on the game thread

- **Affected code:** `Framework/Monitor.cs` (`LogImpl` and `GenerateMessagePrefix`).
- **Scenario:** Trace-heavy mods during walking, asset loading, and invalidation reports; trace output may be hidden from the console but is still written to the log.
- **Root cause:** The async writer removed synchronous file flushes, but each caller still formats the timestamp/prefix, full file line, and console line before enqueueing. Hidden trace lines pay for console text that is never displayed.
- **Impact:** Potential frame-time spikes and allocation bursts under high log volume.
- **Expected benefit:** Queue structured records, format file text on the writer thread, and only build console text when the level is visible.
- **Risk:** Medium to high. Timestamp ordering, crash-time draining, mutable messages, and console/file consistency need explicit handling.
- **Status:** Fixed. `Monitor` captures only the timestamp, cached level text, screen ID, source, and message for file output. `LogFileManager` formats those fields on its writer thread, raw lines no longer allocate a newline-appended copy on callers, and console text is only created when that level is actually visible. Invalidation reports now also queue their privately owned result state, so filtering, case-insensitive sorting, joining, and report-string construction happen on the writer thread when trace output is hidden. Visible console output is still formatted immediately, while explicit flush ordering and bounded backpressure remain unchanged.

### 47. Cursor coordinate derivation remains eager while walking

- **Affected code:** `Framework/Input/SInputState.cs` (`TrueUpdate`, `UpdateCursorPosition`, and `CursorPosition`).
- **Scenario:** Every tick where the player tile or camera-relative cursor position changes, even when no mod reads `ICursorPosition`.
- **Root cause:** Snapshot object creation is lazy, but SMAPI still calculates screen pixels, tile coordinates, radius checks, and `GetGrabTile` eagerly on the update path.
- **Impact:** Possible steady walking CPU overhead in packs where cursor state is rarely consumed.
- **Expected benefit:** Defer the coordinate/radius/grab calculations until the snapshot is actually requested.
- **Risk:** Medium. The getter must reproduce the pre-game-update snapshot, so it must capture viewport, zoom, mouse, player position/tile/facing, and any other inputs instead of reading later live state.
- **Status:** Deprioritized for the installed target pack. Of 283 compiled mod assemblies, only three contain actual cursor API calls or `CursorMoved` subscriptions, but Context-Sensitive Gift Cursor subscribes to `CursorMoved` globally. That listener makes SMAPI materialize the cursor snapshot on walking ticks anyway, so deferring the coordinate fields would not remove this work while that mod is enabled. The change may still benefit other mod sets, but it isn't a target-pack priority.

### 48. Observed chest inventories scan every chest and item stack each tick

- **Affected code:** `Framework/StateTracking/WorldLocationsTracker.cs`, `LocationTracker.cs`, `ChestTracker.cs`, `FieldWatchers/InventoryWatcher.cs`, and `Snapshots/WorldLocationsSnapshot.cs`.
- **Scenario:** Normal walking with any mod subscribed to `ChestInventoryChanged`, particularly expansion saves with many placed chests and large automated storage inventories.
- **Root cause:** Once the event had a listener, SMAPI updated and reset every location and chest each tick. Snapshot comparison then read every tracked item's virtual `Stack` property even when no chest changed. Inventory slot notifications already existed, and normal game item quantities are backed by `NetInt`, but neither signal reached the aggregate dirty-location path.
- **Impact:** Steady frame time scales with all loaded chests and stored item stacks instead of the number which changed; the scan can amplify walking jank even on idle inventory ticks.
- **Expected benefit:** Idle ticks with ordinary items perform no location, chest, or item-stack traversal for this event. A changed normal stack pushes exactly its chest and location into the snapshot path, and only changed stack baselines are compared/reset. Work therefore scales with changes rather than world inventory size.
- **Risk:** Medium. Mods can override the virtual `Item.Stack` implementation without using the game's net field, and inventory add/remove, listener activation, duplicate net callbacks, and multiple changes within a tick must preserve the old snapshot baseline.
- **Status:** Fixed with a compatibility fallback. Standard inherited `Item.Stack` implementations subscribe to the item's reference-identified net field; inventory slot changes and stack changes push a dirty chest/location once per reset. Location snapshots inspect and reset only changed chests. Runtime item types which override `Stack` remain in a dedicated narrow polling set, so custom mod items retain the previous detection semantics without keeping the global scan.

### 49. Mod messages serialize even when no remote peer will receive them

- **Affected code:** `Framework/SMultiplayer.cs` (`BroadcastModMessage`).
- **Scenario:** A mod sends frequent messages in single-player, or targets only the local player while network-traffic logging is disabled.
- **Root cause:** After recipient filtering, SMAPI still creates the remote-player array, converts the payload to a `JToken`, and serializes the full message to JSON before delivering the already constructed model locally. The serialized string is only needed for remote transmission or network logging.
- **Impact:** Avoidable JSON traversal, string allocation, and GC pressure on the calling thread. Whether this contributes to walking jank depends entirely on the installed mods' message rate and payload sizes.
- **Expected benefit:** Build the model for local delivery, but defer JSON serialization until at least one remote peer needs it or network logging will consume it.
- **Risk:** Low to medium. Local delivery must retain the same payload conversion, error timing, recipient metadata, and logging behavior.
- **Status:** Fixed. Local delivery still constructs the same `JToken`-backed model, but SMAPI now serializes its JSON envelope only when a reachable remote peer or enabled network-traffic log will consume it. Remote messages are still serialized before local event delivery, preserving payload ordering and mutation semantics; invalid-recipient and disconnected-host paths avoid serialization too. A static string scan found 50 target-pack assemblies containing `SendMessage` or `IMultiplayerHelper` metadata, but that does not establish call frequency; frame-time impact remains workload-dependent.

### 50. Public reflection cache hits allocate lookup machinery

- **Affected code:** `Framework/Reflection/Reflector.cs` (`GetFieldFromHierarchy`, `GetPropertyFromHierarchy`, `GetMethodFromHierarchy`, and `GetCached`).
- **Scenario:** Mods repeatedly use SMAPI's reflection API from update or draw callbacks.
- **Root cause:** A metadata-cache hit still formats a string key, constructs a capturing fetch delegate, and creates a new reflected field/property/method wrapper. Finding 35 removed this work from SMAPI's confirmed render-stage hot path, but the public API retains it for mod callers.
- **Impact:** Potential per-frame allocations and dictionary churn proportional to hot-loop reflection calls.
- **Expected benefit:** Use a structured cache key, non-capturing lookup, and weak target-bound wrapper cache so repeated calls reach the cached member without allocating or retaining game objects.
- **Risk:** Medium to high. Wrapper instances bind target objects, cache intervals intentionally expire stale lookups, and broad changes affect a public compatibility API.
- **Status:** Fixed. Metadata uses a structural key containing the actual `Type`, avoiding both formatted key strings and collisions between same-named types from different assemblies. A state-passing cache overload removes capturing fetch delegates, bitwise flag checks avoid enum boxing, and a `ConditionalWeakTable` reuses field/property/method wrappers without keeping target objects alive. Wrapper entries reset at the existing daily reflection-cache interval. After warm-up, a 10,000-call .NET 10 allocation check for a cached instance field fell from 368 bytes per call on the parent commit to zero bytes per call; the result measures the reflection lookup itself, not arbitrary reflected member access or mod handler time.

### 51. Decoded-texture cache misses refresh file metadata before checking the cache

- **Affected code:** `Framework/Content/DecodedTextureCache.cs` (`TryGetCopy`).
- **Scenario:** First and second loads of uncached content-pack PNGs on Linux, including large asset-load and invalidation bursts.
- **Root cause:** The lookup refreshes `FileInfo` and reads the source file's length and modification time before checking whether the decoded-pixel cache contains that path. Second-use admission deliberately leaves most first loads uncached, so routine misses issue a synchronous metadata query which cannot validate or return any cached pixels.
- **Impact:** Transition and load-time filesystem latency, proportional to PNG cache-miss volume.
- **Expected benefit:** Cache misses return after one in-memory dictionary probe without touching the filesystem; real hits still refresh and validate file metadata before returning a private pixel copy.
- **Risk:** Low. The entry reference is revalidated after the metadata query, so concurrent disposal, eviction, or replacement produces a safe miss instead of returning stale pixels.
- **Status:** Fixed. `TryGetCopy` checks for an existing entry under the cache lock, refreshes metadata only for that candidate, then rechecks that the same entry is still current before validating its stamp and updating LRU order.

### 52. Successful asset operations format trace messages on the game thread

- **Affected code:** `Framework/ContentManagers/GameContentManager.cs` (`ApplyLoader` and `ApplyEditors`).
- **Scenario:** Content Patcher-scale asset loads and invalidation bursts which apply many loaders and editors while trace output is hidden from the console.
- **Root cause:** Each successful loader and editor eagerly interpolates mod, asset, and content-pack text before calling the monitor. The file writer is asynchronous, but the transition thread still pays for those strings one operation at a time.
- **Impact:** Transition CPU and temporary allocations proportional to applied asset operations.
- **Expected benefit:** Hidden trace messages retain only stable names on the caller and construct their final text on the log-writer thread, leaving more of the transition frame for the content edits themselves.
- **Risk:** Low. Warnings and errors remain immediate, visible trace output is still formatted synchronously, and queued messages capture immutable string values so later metadata changes can't alter the log.
- **Status:** Fixed. Successful loader and editor traces use cached static deferred-message factories with stable mod, asset, and content-pack names; text and queue ordering are unchanged.

### 53. Cached game-content loads probe the same cache repeatedly

- **Affected code:** `Framework/ContentManagers/GameContentManager.cs` (`LoadExact`), `Framework/ContentManagers/BaseContentManager.cs` (`TryGetCachedAsset` and `RawLoad`), and `Framework/ContentCoordinator.cs` (`GetLoadedValues`).
- **Scenario:** Mods repeatedly load an already-cached game asset from update or draw callbacks, and Content Patcher-style code requests all loaded instances during content updates.
- **Root cause:** `LoadExact` first asks whether the cache contains the key, then invokes MonoGame's generic content load to probe and retrieve that same entry. `GetLoadedValues` adds another presence check before calling `LoadExact`, reaching at least three probes per returned manager value.
- **Impact:** Steady update/draw CPU for repeated cached loads and transition CPU across multiple game content managers.
- **Expected benefit:** Compatible cached loads return from one dictionary lookup, while loaded-value discovery performs one lookup per manager and directly reuses the value it found.
- **Risk:** Low to medium. A cached value must satisfy the requested generic type; incompatible values still fall through to MonoGame's existing load path so its error behavior is retained.
- **Status:** Fixed. `GameContentManager` uses `TryGetCachedAsset` and returns type-compatible hits directly, and `GetLoadedValues` adds the object returned by that same single probe without routing back through `LoadExact<object>`.

### 54. Active mine and volcano lists are rehashed every unchanged tick

- **Affected code:** `Framework/StateTracking/FieldWatchers/ComparableListWatcher.cs`, `WatcherFactory.cs`, and `WorldLocationsTracker.cs`.
- **Scenario:** Every gameplay update while generated mine or volcano levels remain active, including ordinary walking in expansion-heavy or multiplayer saves.
- **Root cause:** The only polled world-topology lists clear and refill a pooled hash set, scan the previous set for removals, then scan the new set for additions on every tick. The lists normally retain the exact same ordered object references for long runs, so all of that hashing proves an unchanged state repeatedly.
- **Impact:** Steady update CPU proportional to active generated levels.
- **Expected benefit:** An unchanged list performs one count check and one direct reference comparison per entry, with no hash-set clear, insertion, or membership passes. Hash diffing runs only after the ordered sequence actually changes.
- **Risk:** Low. The ordered sequence is only a fast-path hint; public changes remain set-based, so reorder-only and duplicate-count changes keep their previous behavior.
- **Status:** Fixed. Reference-list watchers retain the prior ordered sequence, bypass hashing when it matches, and refresh that hint after a slow-path comparison. Focused tests cover no-rehash unchanged updates, reorders, real add/remove diffs, and duplicates.

### 55. World propagation projects its cached topology into a redundant array

- **Affected code:** `Metadata/CoreAssetPropagator.cs` (`UpdateBuildingPaintMask`, `GetCharacters`, `GetFarmAnimals`, and world-location helpers).
- **Scenario:** Content Patcher-scale reloads of character, farm-animal, or building paint-mask assets in an expansion save with many live locations.
- **Root cause:** Propagation first builds and caches a reference-deduplicated list of location records, then allocates and fills a second `GameLocation[]` containing the same location references before scanning characters, animals, or buildings.
- **Impact:** Transition CPU and temporary allocation proportional to the live world topology.
- **Expected benefit:** Those propagation paths iterate the existing topology directly, avoiding one full location projection and array allocation per propagation tick that needs it.
- **Risk:** Low. Location order, reference deduplication, active mine and volcano coverage, building-interior selection, and entity loops are unchanged; only the intermediate array is removed.
- **Status:** Fixed. Character, farm-animal, and paint-mask propagation read each `Location` from the already cached `WorldLocationInfo` sequence, and the redundant `GetLocations` projection is removed.

### 56. Content-cache scans and invalidations hash every key twice

- **Affected code:** `Framework/Content/ContentCache.cs`, `Framework/ContentManagers/BaseContentManager.cs` (`GetCachedAssets` and `InvalidateCache`), and `Framework/ContentCoordinator.cs` (predicate invalidation scans).
- **Scenario:** Content Patcher-style predicate invalidations scan every cached asset across game content managers, while exact and predicate invalidations remove matched entries.
- **Root cause:** Cache enumeration walked the dictionary's keys and then indexed the dictionary again for every value, repeating the hash lookup. Removal first called `ContainsKey`, then called a removal method which performs the same lookup and already returns whether it succeeded.
- **Impact:** Transition CPU proportional to scanned cache entries and invalidated manager/asset pairs.
- **Expected benefit:** Predicate scans traverse each dictionary entry directly with no per-entry re-hash, and each removal uses one dictionary probe instead of two.
- **Risk:** Low. Enumeration order and mutation behavior remain those of the same underlying dictionary, disposal still occurs only after successful removal, and the returned success flag is unchanged.
- **Status:** Fixed. Content managers return the underlying cache entry enumeration, and invalidation directly returns the cache removal result.

### 57. Managed mod assets split paths and allocate lock closures on every resolution

- **Affected code:** `Framework/ContentCoordinator.cs` (`TryParseManagedAssetKey`, managed content-manager lookup, creation, invalidation, and disposal) and `Framework/Extensions/ReaderWriterLockSlimExtensions.cs`.
- **Scenario:** Custom maps and content packs load managed `SMAPI/mod-id/path` assets, including tilesheets reloaded during warps and context changes; large mod sets also create and dispose hundreds of content managers.
- **Root cause:** Each managed key used `Split` to allocate a segment array and three substrings, then `Path.Combine` allocated the manager ID. The subsequent indexed lookup created a capturing closure and delegate solely to enter a read lock. Other content-manager lifecycle and loaded-value lock calls had the same closure pattern, and invalidation closures captured several mutable transaction values. Prefix matching also allowed a partial `SMAPIFoo` segment and the manager index was case-sensitive on Linux.
- **Impact:** Transition and managed-asset load CPU, temporary allocation, and Linux path correctness.
- **Expected benefit:** A managed key now allocates only the manager ID and relative path which downstream APIs retain, while warmed state-passing lock calls allocate nothing. Invalidation avoids its transaction closure, and lifecycle/loaded-value paths reuse cached static callbacks.
- **Risk:** Low to medium. Parser coverage locks valid slash and backslash forms, malformed/partial prefixes, empty IDs and paths, platform-normalized manager IDs, and case-insensitive routing. Every lock path retains `finally`-based release and existing first-manager precedence.
- **Status:** Fixed. Managed keys are scanned by separator index without split arrays; prefix matching, the namespaced index, duplicate-manager promotion, and the mod-manager access check all use the same ordinal-ignore-case identity. Explicit-state read/write lock overloads replace capturing callbacks. A warmed 20,000-operation read/write regression check allocates zero bytes.

### 58. Non-English cached loads probe their localized-name mapping twice

- **Affected code:** `Framework/ContentManagers/BaseContentManager.cs` (`LoadLocalized`).
- **Scenario:** Every repeated localized game-content load after SMAPI has resolved the asset's language-specific, international, or base key, including loads issued from update and draw callbacks.
- **Root cause:** `LoadLocalized` called `TryGetValue` to determine whether the stable base-to-localized mapping existed, discarded the returned string, then indexed the same dictionary again to retrieve it on the overwhelmingly common cache-hit path.
- **Impact:** Steady content-load and transition CPU for non-English players.
- **Expected benefit:** Each cached localized-name resolution uses one dictionary hash/probe instead of two, with no added allocation.
- **Risk:** Low. Cache misses retain the same localized and `_international` existence checks and early returns; the no-variant branch stores and reuses the same base name.
- **Status:** Fixed. The first `TryGetValue` retains its mapped raw name, and the miss branch assigns that same local when caching the base-name fallback.

### 59. Every intercepted asset operation group allocates a wrapper object

- **Affected code:** `Framework/Content/AssetOperationGroup.cs`, `Framework/SCore.cs` (`RequestAssetOperations`), `Framework/ContentCoordinator.cs`, and `Framework/ContentManagers/GameContentManager.cs`.
- **Scenario:** Large Content Patcher reloads request many assets which register at least one loader or editor and retain the resulting operation group in SMAPI's one-tick cache.
- **Root cause:** The internal immutable operation group was a reference record, so SMAPI allocated a separate heap object per intercepted asset solely to carry two existing list references.
- **Impact:** Transition allocation and garbage collection proportional to intercepted asset count.
- **Expected benefit:** Operation groups are stored and returned inline with no wrapper-object allocation; loader and editor list ownership is unchanged.
- **Risk:** Low. The type is internal, no usage relies on reference identity, nullable group access remains the same, and equality still compares the same two list references.
- **Status:** Fixed. `AssetOperationGroup` is a readonly record struct, while the existing nullable cache and call sites retain their current null/property patterns.

### 60. Localized mappings and recursive-load guards are case-sensitive on Linux

- **Affected code:** `Framework/ContentCoordinator.cs` (`LocalizedAssetNames` and `ForgetLocalizedAssetNames`) and `Framework/ContentManagers/GameContentManager.cs` (`AssetsBeingLoaded`).
- **Scenario:** A non-English mod requests the same asset using different casing, or an asset loader recursively requests its current asset with case differences on Linux.
- **Root cause:** Asset names use ordinal-ignore-case identity, but localized base-to-target mappings and the active-load context set used default case-sensitive string comparers. Mixed-case localized requests repeated existence checks and retained duplicate mappings; invalidation compared a mapped target case-sensitively and could retain stale state. A recursive mixed-case load could bypass SMAPI's loop guard and recurse until failure.
- **Impact:** Steady localized-load and transition CPU, cache correctness, and recursive-load stability on Linux.
- **Expected benefit:** Equivalent mixed-case requests share one localization mapping and invalidation removes it reliably, while recursive loaders are stopped at the first equivalent key instead of repeating the load pipeline.
- **Risk:** Low. The comparers now match the established `IAssetName` identity contract; canonical asset paths and case-distinct mod-file paths outside the game-content namespace are unaffected.
- **Status:** Fixed. Localized mappings and target comparisons use ordinal-ignore-case semantics, and active game-asset loads use an ordinal-ignore-case context set with focused nested-key cleanup coverage.

### 61. No-op global asset handlers still wrap every raw-loaded asset

- **Affected code:** `Framework/ContentManagers/GameContentManager.cs` (`LoadExact`) and `Framework/Content/AssetDataForObject.cs`.
- **Scenario:** Global `AssetRequested` listeners such as Content Patcher inspect an uncached or invalidated asset but register no applicable loader or editor, which is the normal result for most unrelated assets in a large pack.
- **Root cause:** After handler discovery returned no operation group, SMAPI still raw-loaded the asset, allocated an `AssetDataForObject` wrapper, called `ApplyEditors` only for it to return immediately on a null list, then read the wrapper's `Data` back out.
- **Impact:** Transition allocation and dispatch CPU proportional to unrelated uncached/reloaded assets observed by global handlers.
- **Expected benefit:** The no-operation path performs the raw load and returns its value directly, with no editable-asset wrapper or no-op editor call.
- **Risk:** Low. The load remains inside the same recursive-load context, while outer cache tracking, first-load handling, asset-loaded callbacks, exception behavior, and actual loader/editor paths are unchanged.
- **Status:** Fixed. A null operation group now takes an immediate raw-load fast path; non-null groups retain the existing loader, wrapper, validation, and editor pipeline.

### 62. Predicate cache scans allocate one iterator object per content manager

- **Affected code:** `Framework/Content/ContentCache.cs` (`GetEntries`), `Framework/ContentManagers/IContentManager.cs` and `BaseContentManager.cs` (`GetCachedAssets`), and `Framework/ContentCoordinator.cs` (predicate invalidation).
- **Scenario:** Broad Content Patcher invalidations scan cached assets across roughly one game content manager per code mod, including the many managers whose cache is empty.
- **Root cause:** Although entry enumeration no longer re-hashed each key, it still crossed a `yield`/`IEnumerable` boundary which created an iterator state machine for every manager before examining its cache.
- **Impact:** Transition allocation and garbage collection proportional to game content-manager count.
- **Expected benefit:** Empty and populated manager scans obtain the dictionary's value-type enumerator directly, with no iterator object or boxed enumerator.
- **Risk:** Low. The wrapper enumerates the same underlying dictionary in the same order and retains its mutation/version behavior; the API is internal.
- **Status:** Fixed. Cache enumeration returns a concrete readonly view whose pattern-based `GetEnumerator` exposes the dictionary's struct enumerator. A warmed 10,000-pass regression check allocates zero bytes.

### 63. Texture propagation retrieves each cached target twice

- **Affected code:** `Metadata/CoreAssetPropagator.cs` (`PropagateTexture`).
- **Scenario:** Texture invalidations during Content Patcher reloads and context changes, multiplied by the content managers retaining an in-place texture reference.
- **Root cause:** Each candidate manager first probed its exact cache key with `IsLoaded`, then retrieved the same object through `LoadLocalized`, which repeated cache and localization routing work.
- **Impact:** Transition CPU proportional to invalidated textures and their retaining content managers.
- **Expected benefit:** Each normal cached target is found and retrieved with one dictionary lookup, without localized-name routing or a check/use gap.
- **Risk:** Low to medium. Localized and base-name propagation passes are unchanged. The incompatible-type fallback deliberately retains the old localized load path and its exception behavior.
- **Status:** Fixed. Propagation uses `TryGetCachedAsset` once and copies directly into the exact cached `Texture2D`; only an impossible/mismatched cache entry takes the compatibility fallback.

### 64. Custom-map tilesheet routing repeatedly splits normalized paths

- **Affected code:** `Framework/ContentManagers/ModContentManager.cs` (`FixTilesheetPaths`, `TryGetTilesheetAssetName`, and `GetContentKeyForTilesheetImageSource`).
- **Scenario:** Loading or reloading expansion maps with many tilesheets during warps and content-pack context changes.
- **Root cause:** After canonicalizing separators, SMAPI still split each tilesheet path into substring arrays to validate parent traversal, check its leading segment, and route game-content fallbacks. The broad leading `StartsWith("..")` check also treated a valid local folder such as `..foo/` as parent traversal.
- **Impact:** Transition allocation and garbage collection proportional to custom-map tilesheet count, plus incorrect Linux fallback routing for dot-prefixed folder names.
- **Expected benefit:** Exact segment checks now scan the canonical string without arrays or substrings, eliminating two or three split arrays per tilesheet while resolving `..foo/` locally as intended.
- **Risk:** Low to medium. Focused coverage locks single/multiple/non-leading traversal, exact `..` segment identity, `Maps` routing, case-insensitive PNG removal, and dot-prefixed normal folders.
- **Status:** Fixed. Directory traversal validation and content-key routing use allocation-free exact segment scans over normalized `/`-separated paths.

### 65. Successful mod asset loads allocate temporary type arrays

- **Affected code:** `Framework/ContentManagers/ModContentManager.cs` (`LoadFont`, `LoadImageFile`, `LoadMapFile`, and `AssertValidType`).
- **Scenario:** Every unpacked PNG, TMX/TBIN map, and bitmap-font load from a mod, directly proportional to large content-pack reload volume.
- **Root cause:** Type validation accepted a `params Type[]`, so the compiler allocated a one- or two-element array before every successful load even though the valid type count is fixed at each call site.
- **Impact:** Short-lived transition allocation and garbage collection proportional to mod asset loads.
- **Expected benefit:** Successful validation performs only direct type comparisons with no array, enumeration, or diagnostic formatting allocation.
- **Risk:** Very low. Fixed one- and two-type overloads retain the same assignability checks and produce the same allowed-type information on the exceptional invalid-type path.
- **Status:** Fixed. Successful font, image, and map validations use fixed-arity overloads; error strings are still created only when validation fails.

### 66. Vanilla map inspection probes each XNB before loading it

- **Affected code:** `Framework/ContentCoordinator.cs` (`TryLoadVanillaAsset` and `GetVanillaTilesheetIds`).
- **Scenario:** The first replacement of each distinct vanilla map, when SMAPI loads the original map to retain its ordered tilesheet IDs.
- **Root cause:** The unmodified vanilla content manager first performed an existence/type lookup and then immediately performed the actual load under the same catch-all failure handling.
- **Impact:** An avoidable content/filesystem lookup on the map replacement transition path.
- **Expected benefit:** Each inspected map takes one content-pipeline load attempt instead of an existence probe followed by that load.
- **Risk:** Low. Missing, corrupt, and wrong-type assets still take the same catch path and return no vanilla map; successful assets return the same loaded value.
- **Status:** Fixed. Vanilla inspection directly loads inside the existing guarded block.

### 67. Loader-only assets allocate an unused editable wrapper

- **Affected code:** `Framework/ContentManagers/GameContentManager.cs` (`LoadExact` and loader application).
- **Scenario:** Custom assets supplied by Content Patcher or another loader with no applicable edit operations, including batches discovered during content-pack reloads.
- **Root cause:** A successful loader always returned its data inside `AssetDataForObject`; the editor stage immediately returned that wrapper unchanged when the operation group had no editors, and `LoadExact` then read `Data` back out.
- **Impact:** One short-lived wrapper object per loader-only asset plus needless editor dispatch on the transition path.
- **Expected benefit:** Loader-only assets return their validated typed value directly with no editable wrapper; wrappers are created only when at least one editor will consume them.
- **Risk:** Medium. Exclusive-loader conflict handling, loader exceptions, null/type validation, vanilla fallback, map repair, recursive-load tracking, and actual editor paths retain their existing order and behavior.
- **Status:** Fixed. Loader application uses a typed try pattern, and `LoadExact` creates `AssetDataForObject` only for nonempty edit-operation groups.

### 68. Texture replacement probes existence before every successful load

- **Affected code:** `Metadata/CoreAssetPropagator.cs` (`PropagateTexture`).
- **Scenario:** Each invalidated texture which still has an in-place cached target during a Content Patcher reload or context change.
- **Root cause:** Before loading the fresh replacement, propagation always called `DoesAssetExist` and then performed the actual uncached localized load. Existing assets—the normal case—therefore traversed provider/filesystem existence logic immediately before traversing the load pipeline.
- **Impact:** Transition CPU and synchronous content/filesystem lookup work proportional to propagated textures.
- **Expected benefit:** Successful replacements take one real load attempt and no preliminary existence probe.
- **Risk:** Low to medium. On load failure, propagation still probes existence: genuinely removed assets keep the focused warning, while a present but broken provider rethrows into the existing per-asset propagation error handler.
- **Status:** Fixed. The real load is attempted first; existence classification runs only on its exceptional path.

### 69. Asset-operation cache ignores the requested data type

- **Affected code:** `Framework/ContentCoordinator.cs` (`AssetOperationsByKey`, `GetAssetOperations`, and invalidation) and `Framework/Utilities/TickCacheDictionary.cs`.
- **Scenario:** The same asset name is queried under different generic types in one tick, such as an existence probe followed by a real load, while an `AssetRequested` handler selects operations using `IAssetInfo.DataType`.
- **Root cause:** Per-tick loader/editor results were keyed only by `IAssetName` even though the requested type is part of the handler-visible request identity. Whichever type queried first supplied operations reused for every later type that tick.
- **Impact:** Wrong or missing loader/editor selection, fallback/retry work, and potential content-load failures whose order depends on same-tick probes.
- **Expected benefit:** Each `(asset name, requested type)` pair discovers operations once and reuses only compatible results.
- **Risk:** Medium. Name invalidation must remove all cached type variants; focused coverage verifies composite-key identity and predicate removal of every matching variant while retaining unrelated entries.
- **Status:** Fixed. The tick cache uses a value-type name/type key, and invalidation removes every entry whose asset name matches without allocating a captured predicate.

### 70. Existence checks probe irrelevant providers before cache and managed routing

- **Affected code:** `Framework/ContentManagers/BaseContentManager.cs`, `GameContentManager.cs`, and `ModContentManager.cs` (`DoesAssetExist`).
- **Scenario:** Repeated existence checks for already-cached game assets and uncached managed `SMAPI/mod-id/path` assets, including localized and custom-map tilesheet resolution during content-pack transitions.
- **Root cause:** Base existence logic queried MonoGame's content provider before its dictionary cache. The Linux game implementation builds a `StringBuilder`, normalizes the full path, appends `.xnb`, materializes a new string, and queries the content manifest. Game managers also sent managed mod keys through that guaranteed-useless vanilla probe before routing them, while mod managers hashed a cache which is deliberately always empty.
- **Impact:** Redundant allocation, linear path normalization, manifest lookup, and cache hashing on content existence bursts.
- **Expected benefit:** Cached game assets return after one dictionary probe; managed keys route directly to the owning mod; mod-file checks go directly to path resolution and file metadata.
- **Risk:** Low to medium. Cached values still report present even if their source was deleted, matching prior behavior. Non-managed cache misses retain vanilla-provider then custom-loader ordering, and mod content remains explicitly non-caching.
- **Status:** Fixed. Existence checks now separate cache, managed routing, vanilla provider, custom loader, and mod-file paths so only relevant sources are queried.

### 71. Successful content-pack operation registration formats failure text

- **Affected code:** `Framework/SCore.cs` (`GetOnBehalfOfContentPack`).
- **Scenario:** Every successful `AssetRequested` load or edit operation registered on behalf of a content pack, multiplied across Content Patcher-scale operation discovery.
- **Root cause:** The helper eagerly interpolated a full error prefix before normal registry and ownership checks, then returned the valid pack without ever using that string.
- **Impact:** One short-lived failure-message string per successful on-behalf-of operation registration, adding transition GC pressure.
- **Expected benefit:** Valid content-pack operations perform registry/ownership validation with no diagnostic string allocation.
- **Risk:** Very low. The identical warning text is now formatted only inside the missing-pack and invalid-owner branches which emit it.
- **Status:** Fixed. Failure diagnostics are constructed lazily at their two use sites; the successful path returns directly.

### 72. Disconnected gamepads scan every control each focused update

- **Affected code:** `Framework/Input/GamePadStateBuilder.cs` (`FillPressedButtons`).
- **Scenario:** Every focused update while walking or playing with keyboard and mouse on Linux and no physical controller connected.
- **Root cause:** The builder still read and tested the D-pad, eleven digital buttons, two triggers, and both thumbsticks even when the immutable gamepad state reported disconnected.
- **Impact:** Small but unconditional steady update CPU for the common keyboard/mouse configuration.
- **Expected benefit:** Disconnected unmodified state returns after two field/scalar checks, skipping all controller property reads, comparisons, stick length calculation, and set branches.
- **Risk:** Low. Overrides initialize the mutable pressed-button set before changing digital or analog state, so virtual controller input still bypasses the early return. Focused tests cover both disconnected/no-override and disconnected digital/trigger/stick overrides.
- **Status:** Fixed. `FillPressedButtons` exits immediately only for disconnected state with no initialized overrides.

### 73. Every content manager repeats generic base-load method discovery

- **Affected code:** `Framework/ContentManagers/BaseContentManager.cs` (`RawLoad`).
- **Scenario:** The first cached load of each asset type through every game content manager; large code-mod sets create roughly one such manager per mod helper.
- **Root cause:** Each manager independently reflected `ContentManager.Load`, specialized the generic method for the same common type, obtained its method pointer, and only then constructed the manager-bound nonvirtual delegate.
- **Impact:** Repeated reflection and generic method specialization during startup and first-use content transitions, multiplied by manager and common asset-type counts.
- **Expected benefit:** Method discovery, specialization, and pointer resolution occur once per asset type for the process; each manager retains only the necessary target-bound delegate construction and its isolated cache.
- **Risk:** Medium-low. The nonvirtual base-call mechanism and per-manager delegate targets are unchanged; only the stable specialized method pointer is shared through a generic static holder.
- **Status:** Fixed. A generic static holder resolves the base-load pointer once per `T`, while each manager still owns its bound delegate and asset cache.

### 74. Every registered asset edit allocates a wrapper object

- **Affected code:** `Framework/Content/AssetEditOperation.cs`, `Events/AssetRequestedEventArgs.cs`, `Framework/SCore.cs`, and `Framework/ContentManagers/GameContentManager.cs`.
- **Scenario:** Every successful edit registered during `AssetRequested` discovery, multiplied across large Content Patcher reload batches.
- **Root cause:** The immutable four-field edit carrier was a reference record, so each registration allocated a separate object in addition to the lazily created operation list.
- **Impact:** Transition allocation, GC pressure, and pointer indirection proportional to registered edit count.
- **Expected benefit:** Edit records are stored inline in their list with no per-operation wrapper object and improved iteration locality.
- **Risk:** Low. Usages copy, sort, and read the immutable fields and never rely on null or reference identity; the extra value copies are four machine-word fields.
- **Status:** Fixed. `AssetEditOperation` is a readonly record struct, with focused registration coverage asserting value-container semantics and retained callback identity.

### 75. Case variants create duplicate tilesheets during map patches

- **Affected code:** `Framework/Content/AssetDataForMap.cs` (`PatchMap` and tilesheet path normalization).
- **Scenario:** Content-pack map patches whose same-ID source and target tilesheets use equivalent paths with different casing, separators, `Maps/` prefixes, or `.png` casing; repeated reloads compound the result.
- **Root cause:** SMAPI normalized separators/prefix/extensions but compared the resulting paths with case-sensitive string inequality despite ordinal-ignore-case asset identity.
- **Impact:** Incorrectly added `z_<id>`, `z_<id>_2`, and further duplicate tilesheets, causing extra texture loads/retention and transition/render memory growth on Linux.
- **Expected benefit:** Equivalent same-ID sheets are reused across patches and reloads instead of growing redundant map and texture state.
- **Risk:** Low. Only normalized path equality changes to the established asset-name comparer; genuinely different paths still take the existing disambiguation path.
- **Status:** Fixed. Normalized tilesheet paths use ordinal-ignore-case equality, with focused case/separator/prefix/extension and negative-path coverage.

### 76. Explicit XNB fallback tilesheets get a double extension

- **Affected code:** `Framework/ContentManagers/ModContentManager.cs` (`GetContentKeyForTilesheetImageSource`).
- **Scenario:** A custom map references a game-content fallback tilesheet with an explicit legacy `.xnb` extension, including parent-relative paths such as `../TileSheets/Furniture.xnb`.
- **Root cause:** Tilesheet routing removed `.png` from physical image paths before creating a game asset key, but retained `.xnb`; MonoGame then appended its implicit `.xnb` and looked for `file.xnb.xnb`.
- **Impact:** Failed fallback load, exception handling, and potentially missing custom-map tilesheets rather than broad frame-time cost.
- **Expected benefit:** Explicit `.xnb` and `.XNB` paths resolve to the same canonical game asset as extensionless paths, avoiding the failed load/retry route.
- **Risk:** Low. SMAPI's public game-content normalization already treats `.xnb` as a legacy removable extension, and local mod tilesheets still resolve physical `.xnb` files before fallback.
- **Status:** Fixed. Game-content tilesheet keys strip `.png` or `.xnb` ordinal-ignore-case, with focused lower/upper-case coverage.

### 77. Observable collection watchers start with an empty baseline

- **Affected code:** `Framework/StateTracking/FieldWatchers/ObservableCollectionWatcher.cs` and `WorldLocationsTracker.cs` initialization.
- **Scenario:** A watcher is constructed after its source collection already contains values, notably a late or split-screen world tracker after game locations are populated.
- **Root cause:** The watcher subscribed to future notifications but left its ordered `PreviousValues` baseline empty. Removing or replacing an initial item could index outside that list; reset could not report initial removals; and the world tracker never discovered unchanged preexisting locations.
- **Impact:** Potential update exception, incomplete world tracking, and missed location/content changes rather than a direct speed cost.
- **Expected benefit:** Nonempty collections are safely tracked from construction and world locations are discovered on the first update without waiting for another collection change.
- **Risk:** Medium-low. Initial values follow the existing comparable-list watcher contract by appearing as added until the first reset, then serving as the stable baseline. Focused tests cover initial discovery, removal, replacement, and reset/clear.
- **Status:** Fixed. Construction seeds the ordered baseline and initial added set before subscribing to changes.

### 78. Predicate invalidation repeats matching for every manager copy

- **Affected code:** `Framework/ContentCoordinator.cs` (`InvalidateCache(Func<IAssetInfo, bool>)`).
- **Scenario:** Content Patcher invalidates assets after a context change or warp in a large mod set, where the same game asset can be cached by the main game and several mod-owned game content managers.
- **Root cause:** SMAPI parsed and constructed an `IAssetInfo`, then invoked the public predicate once for every cached manager copy. The public callback can't observe the content manager, so equal asset names and data types necessarily produce the same match decision; repeating Content Patcher's dependency checks only adds work.
- **Impact:** Transition CPU and short-lived asset-info allocation proportional to duplicate cached copies. In the target-pack trace, the farmhouse-to-farm warp spent 289.7 ms in Content Patcher while its predicate invalidation rebuilt six assets, including three propagated textures.
- **Expected benefit:** Each distinct `(asset name, data type)` evaluates the public predicate once per invalidation transaction. Every matching manager copy is still retained or invalidated exactly as before.
- **Risk:** Low. Asset-name identity remains ordinal-ignore-case, data types remain distinct, and the internal manager-aware predicate overload is unchanged. Public invalidation predicates receive no manager identity and are contractually match functions rather than per-entry notifications.
- **Status:** Fixed. Public predicate invalidation caches positive and negative results for the duration of one transaction, with focused coverage for case-equivalent names and distinct data types.

### 79. Batched invalidation rescans the operation cache for every name

- **Affected code:** `Framework/ContentCoordinator.cs` (`ProcessInvalidatedAssets`) and `Framework/Utilities/TickCacheDictionary.cs` (`RemoveWhere`).
- **Scenario:** A Content Patcher context update invalidates several or hundreds of assets after SMAPI has cached loader/editor discovery results for the current tick.
- **Root cause:** SMAPI called `RemoveWhere` separately for each invalidated asset name. Each call enumerated the entire operation cache, so clearing `N` names from `K` cached name/type entries cost `O(N × K)` cache-key visits.
- **Impact:** Main-thread transition CPU which grows multiplicatively with invalidation batch size and the number of asset operations discovered that tick. The measured farmhouse-to-farm warp invalidated six names; large startup/context batches can be much larger.
- **Expected benefit:** Every cached operation key is visited once, and invalidated-name membership is checked with the transaction's existing dictionary, reducing cleanup to `O(K)` expected time with no new collection.
- **Risk:** Low. The same ordinal-ignore-case `IAssetName` identity is used, every data-type variant for an invalidated name is removed, and unrelated names remain cached.
- **Status:** Fixed. Batched operation-cache cleanup now performs one stateful `RemoveWhere` pass over all cached keys.

### 80. Exact invalidation batches probe every name in every manager

- **Affected code:** `Framework/ContentCoordinator.cs` (`InvalidateExactCache`), `Framework/ContentManagers/IContentManager.cs` and `BaseContentManager.cs`, and `Framework/Content/ContentCache.cs`.
- **Scenario:** A mod submits a large exact-name invalidation batch while SMAPI has roughly one game content manager per code mod, many of which have empty or small private caches.
- **Root cause:** SMAPI nested every game content manager over every requested name and performed a dictionary lookup for each pair. A batch of `N` names across `M` managers therefore performed `M × N` probes even when most managers cached only a handful of assets.
- **Impact:** Main-thread transition CPU proportional to manager count times invalidation batch size.
- **Expected benefit:** Each manager now probes the smaller side of the intersection: direct name lookups when the request set is smaller, or one enumeration of its actual cached entries when its cache is smaller. Expected work becomes the sum of `min(requested names, cached entries)` across managers.
- **Risk:** Low to medium. The same normalized case-insensitive name set determines matches, texture instances remain retained for in-place propagation, non-textures retain disposal behavior, and the scalar exact-name path is unchanged. Dictionary removal during enumeration retains the runtime behavior already used by predicate invalidation.
- **Status:** Fixed. Content managers expose their internal cache count, and exact batches adapt per manager without constructing another index or collection.

### 81. Map patches alternate layers and hash tilesheets for every tile

- **Affected code:** `Framework/Content/AssetDataForMap.cs` (`PatchMap`).
- **Scenario:** Content Patcher applies an overlay or replacement to a large expansion map with multiple layers and tilesheets during initial load or a context-driven reload.
- **Root cause:** The tile-copy loop iterated coordinate first and layer second. Every coordinate re-entered layer enumeration, full replacement re-enumerated target-only layers, and adjacent tiles on one layer still performed a dictionary lookup for the same tilesheet because work alternated between layers.
- **Impact:** Transition CPU proportional to patch width × height × layer count, on top of the unavoidable tile cloning. The target pack contains 1,873 TMX/TBIN maps totaling about 129 MB.
- **Expected benefit:** Patches traverse one layer's contiguous tile matrix at a time, clear target-only layers in dedicated passes, and reuse the last source-to-target tilesheet mapping for neighboring tiles. Missing target layers also avoid an immediate ID lookup after creation.
- **Risk:** Low to medium. Overlay, replace-by-layer, and full-replace final states are unchanged; tile and layer properties still copy once; source tiles still clone into target-owned sheets; and zero-area behavior is retained. Only the private order in which independent layer cells are assigned changes.
- **Status:** Fixed. The patch loop is layer-first with a per-layer tilesheet fast cache and focused multi-layer coverage for all three patch modes.

### 82. Fallback map tilesheets are uploaded into two content-manager caches

- **Affected code:** `Framework/ContentManagers/ModContentManager.cs` (`TryGetTilesheetAssetName`).
- **Scenario:** Loading or reloading a custom map whose tilesheet falls back to a game-content asset instead of a file in the mod folder.
- **Root cause:** Map validation fully loaded and cached the fallback texture through the requesting mod's private game content manager. The map retained only its asset key; the map display device later resolved that key through `Game1.content`, decoded/uploaded another texture instance, and retained it in a different cache.
- **Impact:** Duplicate content-pipeline work, GPU upload, and texture memory for vanilla or shared tilesheets referenced by custom maps. Expansion packs multiply this across maps and private mod content managers.
- **Expected benefit:** Validation's first load populates the same cache the map renderer uses, so drawing reuses the already validated texture instead of creating a second GPU resource.
- **Risk:** Medium-low. Loader/editor selection, localization, error classification, and physical content-folder fallback checks are unchanged. The cache owner changes to the primary game manager, which is already the manager used by the map display device for these non-managed keys.
- **Status:** Fixed. Game-content fallback validation now loads through the coordinator's primary `Game1.content` manager; local mod tilesheets retain their existing managed-asset route.

### 83. Opaque image overlays read back and blend the target texture

- **Affected code:** `Framework/Content/AssetDataForImage.cs` (`PatchImageImpl`).
- **Scenario:** Content Patcher or another editor applies a fully opaque overlay to a texture, including large replacement-like patches expressed using overlay mode and solid rectangles surrounded by transparent margins.
- **Root cause:** Only explicit replace mode used the direct GPU upload path. Overlay mode always read the target region back from the GPU, rented a merge buffer, visited every pixel, and uploaded the result even when every pixel inside the effective non-transparent bounds necessarily replaced the destination exactly.
- **Impact:** Avoidable synchronous GPU readback, pooled-buffer traffic, and per-pixel alpha work on the content-reload/warp path.
- **Expected benefit:** Fully opaque overlays perform a linear alpha check followed by the same direct upload as replacement, eliminating target readback and blend work. Solid rectangles with transparent margins upload only their cropped rows. Sparse and translucent overlays exit the check at their first non-opaque pixel and retain the cropped merge path.
- **Risk:** Low. The fast path applies only when every source alpha is 255, for which the existing overlay branch always assigns the source color regardless of destination. Empty and partially transparent inputs retain existing behavior.
- **Status:** Fixed. Opaque overlays and opaque effective rectangles now take the direct upload path, with focused coverage for opaque, translucent, transparent, empty, offset, and strided spans.

### 84. Residual map and localized routing paths allocate avoidable helpers

- **Affected code:** `Framework/Content/AssetDataForMap.cs` (tilesheet ID disambiguation) and `Framework/ContentManagers/BaseContentManager.cs` (`LoadLocalized`).
- **Scenario:** Repeated map patches add genuinely different same-ID tilesheets, or a non-English player requests a cached base asset using casing different from the localized-name mapping.
- **Root cause:** Tilesheet disambiguation created an enumerable iterator and capturing predicate to find the next suffix. Localized loading used case-sensitive string inequality after a case-insensitive dictionary hit, so case-only differences reparsed an already equivalent base key.
- **Impact:** Small transition allocations and routing work multiplied by conflicting map patches or mixed-case localized loads.
- **Expected benefit:** Suffix discovery is a direct allocation-free loop, and equivalent localized base keys proceed directly to the cache load without another parsed-name lookup.
- **Risk:** Low. Generated suffix order remains `z_id`, `z_id_2`, and onward. Localized comparison now matches the dictionary and `IAssetName` ordinal-ignore-case identity already used by the surrounding cache.
- **Status:** Fixed. Map suffix probing no longer uses LINQ, and cached localized key routing uses ordinal-ignore-case equality.

### 85. Separate same-tick map invalidations repeatedly search the full world

- **Affected code:** `Metadata/CoreAssetPropagator.cs` (`Propagate`, `PropagateMap`, and `GetLocationsByMapName`).
- **Scenario:** Content packs invalidate many maps through separate single-name API calls while one update tick is blocked, such as a startup or context refresh involving expansion maps.
- **Root cause:** SMAPI indexed locations only when one invalidation transaction contained multiple maps. Its per-tick topology list was reused across transactions, but every isolated map still linearly compared its name against every live location and spouse-room path.
- **Measured evidence:** The captured target-pack load emitted 27 separate single-map invalidation transactions from 08:52:50 through 08:52:54 within the same blocked update sequence. Each took the direct full-world search path despite the stable tick-local topology.
- **Impact:** Main-thread startup/context-transition CPU proportional to isolated map invalidations multiplied by live expansion locations.
- **Expected benefit:** The first two isolated map invalidations retain allocation-free direct scans. On the third, SMAPI builds one tick-local map index; every later isolated map in that tick becomes a dictionary lookup. The measured 27-call sequence therefore replaces 27 full searches with two searches, one index traversal, and 24 lookups.
- **Risk:** Low to medium. The index uses the same existing per-tick topology and spouse-room semantics already used for multi-map batches, and expires on the same game-tick boundary. Focused coverage verifies both immediate multi-map indexing and the adaptive sequence across four separate calls.
- **Status:** Fixed. Map propagation now shares an adaptive per-tick index across invalidation transactions after the direct-scan crossover.

### 86. TMX conversion searches tileset metadata for every populated tile

- **Affected code:** `Framework/SCore.cs`, the bundled `Platonymous.TMXTile` format registration, and `Framework/Content/OptimizedTmxFormat.cs`.
- **Scenario:** Loading large expansion maps from unpacked TMX files during startup, a warp, or content invalidation.
- **Root cause:** The bundled TMX converter linearly selected a target tilesheet and then created LINQ iterators to search the parsed tileset and its explicit tile definitions for every populated map cell. Most cells only need a static tile, but still repeated both metadata searches.
- **Measured evidence:** The 255,000-cell Ridgeside Village map contained 48,568 populated cells. Its repeated metadata lookups took about 151 ms in isolation versus about 9 ms through a map-level index. Full conversion fell from about 259 ms to 131 ms. Across the 20 largest installed TMX maps, conversion fell from 1,843 ms to 750 ms (59.3%). Before the separate identity-transform cleanup in finding 87, serializing both results to xTile's binary format produced byte-identical output for every map.
- **Impact:** Main-thread map-load stalls and transition jank proportional to populated TMX tile count, plus one or more short-lived LINQ iterator objects per populated tile.
- **Expected benefit:** Tilesheet ranges and animated-tile definitions are indexed once per conversion. Static tiles take a short range scan with no LINQ allocation; animated definitions use one dictionary probe. XML parsing itself is unchanged and remains roughly half of a large map's total parse-and-convert time.
- **Risk:** Medium-low. SMAPI registers a derived TMX format which retains the bundled implementation for tileset, image-layer, compatibility, and storage behavior, while reproducing its layer/property/object conversion semantics. Focused tests cover interface dispatch, binary-equivalent map output, animations, every flip combination, sheet selection, empty cells, and invalid IDs; the 20-map installed-pack comparison adds representative expansion coverage.
- **Status:** Fixed. TMX registration now uses the indexed converter without a persistent cache or first-load write penalty.

### 87. TMX conversion stores two identity properties on every populated tile

- **Affected code:** `Framework/Content/OptimizedTmxFormat.cs` (`LoadTile`).
- **Scenario:** Every populated tile converted from an unpacked TMX expansion map, including the tens of thousands of tiles in a single large outdoor location.
- **Root cause:** TMXTile created a fresh tile, queried its empty property collection for `@Rotation` and `@Flip`, then wrote both properties even when their computed values were zero. Those identity properties persist for the lifetime of the loaded map despite being equivalent to an absent property in TMXTile's own accessors and renderer.
- **Measured evidence:** In an alternating A/B benchmark over the 20 largest installed TMX maps, retaining zero transforms took 1,594 ms and allocated 375.3 MiB across two passes. Omitting them took 980 ms and allocated 214.4 MiB: about 38.5% less indexed conversion time and 42.9% less allocation. Per pass, this removed roughly 307 ms and 80.5 MiB for those 20 maps.
- **Impact:** Main-thread map-load CPU, temporary garbage collection pressure, and retained per-map property dictionaries proportional to populated tile count.
- **Expected benefit:** Untransformed tiles retain an empty property collection. Only real rotations or flips write a transform property, while `GetRotationValue`, `GetFlip`, and rendering still return the same effective values.
- **Risk:** Medium-low. A mod directly testing for the presence of TMXTile's internal zero-valued `@Rotation` or `@Flip` key will now see it absent, although the public extension accessors and rendered output are unchanged. Focused tests cover absent identity keys, every non-identity flip/rotation combination, and binary equivalence after removing only the bundled converter's redundant identity entries.
- **Status:** Fixed. The indexed converter stores transform properties only when their effective value is nonzero.

### 88. TMX conversion writes null into every empty cell of a new layer

- **Affected code:** `Framework/Content/OptimizedTmxFormat.cs` (`LoadLayers`).
- **Scenario:** Sparse expansion maps whose serialized layer data includes every coordinate, including hundreds of thousands of empty cells.
- **Root cause:** For each zero GID, TMXTile returned `null` and assigned it through xTile's tile-array indexer even though a newly allocated layer already contains `null` in every slot. The target Ridgeside Village map had roughly 206,000 empty cells among 255,000 serialized coordinates.
- **Measured evidence:** In an alternating A/B benchmark over the 20 largest installed TMX maps, retaining empty writes took 1,188 ms across two passes versus 844 ms when skipping them, about 29% less conversion time. Allocation was effectively unchanged (214.4 MiB versus 213.7 MiB), confirming this removes indexer/assignment CPU rather than masking allocation differences.
- **Impact:** Main-thread map-load CPU proportional to total map area rather than populated tile count, especially noticeable for large sparse outdoor maps.
- **Expected benefit:** Zero GIDs only advance the source coordinate. Nonempty tiles retain the same indexed conversion and target coordinate, while the untouched fresh layer slot has the identical final `null` value.
- **Risk:** Low. Both normal and chunked layer paths operate on newly constructed layers, and the existing binary-equivalence fixture includes empty cells. No observable tile, property, layer, or serialization state changes.
- **Status:** Fixed. Normal and chunked TMX layers bypass conversion and xTile assignment for zero GIDs.

### 89. Unpacked maps use a small generic file buffer without a sequential-read hint

- **Affected code:** `Framework/ContentManagers/ModContentManager.cs` (`LoadMapFile`).
- **Scenario:** Reading large unpacked TMX or TBIN expansion maps from Linux storage during startup, warps, and content reloads.
- **Root cause:** xTile's generic path loader opened every map through the basic `FileStream(path, Open, Read)` overload, which uses a small general-purpose buffer and no sequential-access hint despite both map formats consuming the stream from start to finish.
- **Measured evidence:** Alternating end-to-end parse-and-convert passes over the 20 largest installed TMX maps took 3,609 ms with the default stream and 3,201 ms with a 64 KiB sequential stream, about 11.3% less time. Conversion logic and output were identical between cases.
- **Impact:** Extra read/syscall and buffering overhead on the main-thread map-load path, independent of the converter CPU improvements in findings 86–88.
- **Expected benefit:** TMX and TBIN readers receive a 64 KiB buffer plus the platform sequential-scan hint, while still streaming directly from the mod file with no whole-file copy or retained buffer.
- **Risk:** Low. The same registered `IMapFormat` is selected by extension, receives the same file bytes, and retains xTile's exact outer error message and inner exception behavior. File sharing is explicitly read-only.
- **Status:** Fixed. Unpacked maps are opened with `FileStreamOptions` tuned for sequential map parsing before dispatch to the registered format.

### 90. Map patches copy empty tile properties and repeatedly snapshot animation frames

- **Affected code:** `Framework/Content/AssetDataForMap.cs` (`PatchMap` and `CreateTile`).
- **Scenario:** Content Patcher applies a large expansion-map patch containing mostly ordinary static tiles, or clones animated tiles during a map load/context refresh.
- **Root cause:** SMAPI invoked `PropertyCollection.CopyFrom` for every populated tile even when the source collection was empty. For animated tiles it also read xTile's defensive-copying `TileFrames` property for the allocation length, every loop condition, and every indexed access, creating roughly `2N + 2` frame-array snapshots for an `N`-frame animation. Every cloned frame was incorrectly assigned the currently displayed frame's tilesheet instead of its own source sheet, and its frame-specific properties were discarded.
- **Measured evidence:** Only 18,034 of 215,195 populated tiles in the 10 largest installed TMX maps had properties. Alternating patch passes over those maps took 714 ms with unconditional copies versus 464 ms when empty collections were skipped, about 35% less time in the measured target-building/patch sequence with identical allocation volume. A focused 100,000-iteration eight-frame clone benchmark allocated 285.3 MiB through the repeated getter pattern versus 142.7 MiB when the source frame array was captured once.
- **Impact:** Main-thread map-load/reload CPU proportional to populated patch tiles, plus avoidable allocation/GC pressure and incorrect cross-tilesheet animations.
- **Expected benefit:** Property work is paid only by the minority of tiles that contain metadata. Each animated tile takes one source frame snapshot, and every frame maps through its own source-to-target tilesheet identity while retaining frame metadata.
- **Risk:** Low. Empty property collections have no values to copy; nonempty tile properties retain the existing copy path. Focused tests preserve static tile properties and patch modes, and verify frame count, interval, blend modes, and distinct target tilesheets.
- **Status:** Fixed. Empty per-tile property copies are skipped and animated frames are captured once with per-frame tilesheet/property copying.

### 91. Explicit XNB map sheets are treated as different identities

- **Affected code:** `Framework/Content/AssetDataForMap.cs` (`NormalizeTilesheetPathForComparison`).
- **Scenario:** A source map patch and target map reference the same tilesheet, but one uses an explicit legacy `.xnb` extension while the other uses the canonical extensionless game asset key.
- **Root cause:** Patch identity normalization removed `Maps/`, separator differences, casing, and `.png`, but retained `.xnb` even though SMAPI's content routing treats explicit `.xnb` and extensionless keys as the same asset.
- **Impact:** SMAPI can add and retain a redundant `z_` tilesheet/texture during a map patch instead of reusing the target sheet, with conditional Linux case/path differences.
- **Expected benefit:** Equivalent XNB-backed sheets reuse the existing target identity, avoiding unnecessary sheet disambiguation and texture retention.
- **Risk:** Low. Only the legacy image extension is removed for comparison; genuinely different paths still disambiguate. Focused fixtures cover lower/upper-case `.xnb`, prefix/separator differences, and a truly different source.
- **Status:** Fixed. Map tilesheet comparison now strips `.png` or `.xnb` ordinal-ignore-case.

### 92. Canonical file paths are split and rejoined for every probe

- **Affected code:** `SMAPI.Toolkit/Utilities/PathUtilities.cs` (`NormalizePath`), reached from `CaseInsensitiveFileLookup.GetFile`, `MinimalFileLookup`, content-pack APIs, and data helpers.
- **Scenario:** Loading PNGs, maps, JSON, fonts, tilesheets, and other files from large Linux content packs where callers already pass canonical `/`-separated paths.
- **Root cause:** Every `NormalizePath` call trimmed the string, used `string.Split` to allocate a segment array and substring for each component, then joined those segments into another string before the exact filesystem probe. This happened even when normalization could not change a character.
- **Measured evidence:** Alternating old/new passes over 20,000 real installed mod-file paths took 74 ms and allocated 61.2 MiB through split/join versus 5 ms and effectively zero bytes through the canonical fast path, about 93% less normalization time in the warmed microbenchmark.
- **Impact:** Startup and content-reload CPU plus short-lived GC pressure proportional to mod file probes; the target installation contains tens of thousands of content files.
- **Expected benefit:** Canonical paths return the original string after two vectorized separator checks. Noncanonical paths are scanned once and written directly into one result string without segment arrays or substring objects.
- **Risk:** Low. Existing root and trailing-separator behavior is retained; focused fixtures cover absolute/relative paths, Windows-style input, mixed and repeated separators, surrounding whitespace, root-only values, trailing separators, and canonical reference identity. Windows UNC roots remain preserved on Windows, while Unix now produces the platform-canonical single root expected by the existing fixture.
- **Status:** Fixed for the .NET 6 runtime path. The netstandard compatibility target retains its prior implementation.

### 93. Successful JSON reads retain a full UTF-16 file copy

- **Affected code:** `SMAPI.Toolkit/Serialization/JsonHelper.cs` (`ReadJsonFileIfExists`).
- **Scenario:** Loading large content-pack data, translation, event, and configuration JSON during startup or a context-driven reload.
- **Root cause:** SMAPI called `File.ReadAllText` before deserialization, materializing the entire file as a UTF-16 string in addition to Newtonsoft's reader buffers and the resulting object graph. Large files cross the large-object-heap threshold and amplify transition GC pressure.
- **Measured evidence:** The 20 largest installed JSON files total 15.2 MiB. Alternating two-pass deserialization to representative `JToken` graphs took 1,101 ms and allocated 263.4 MiB through `ReadAllText`, versus 1,137 ms and 144.1 MiB through a streaming reader: about 45% less allocation for roughly 3% more parsing time.
- **Impact:** Peak managed memory and garbage collection during startup/reloads, especially for multi-megabyte translation and event files. The target pack's largest JSON file is about 2.4 MiB.
- **Expected benefit:** Successful reads deserialize directly from the file stream, eliminating the full-file UTF-16 allocation. Invalid syntax alone falls back to the old text path so curly-quote repair and detailed diagnostics remain compatible.
- **Risk:** Low to medium. Serializer settings, converters, comments, BOM detection, null handling, and public error formatting must remain unchanged. Focused tests cover all of those paths, including repaired and unrecoverable curly quotes.
- **Status:** Fixed. Valid JSON uses `StreamReader` and `JsonTextReader`; missing files retain the false result, filesystem I/O errors remain unwrapped, and reader syntax failures retain the text compatibility fallback.

### 94. Case-insensitive lookup conflates case-distinct Linux roots

- **Affected code:** `SMAPI.Toolkit/Utilities/PathLookups/CaseInsensitiveFileLookup.cs` (`CachedRoots` and `GetCachedFor`).
- **Scenario:** Two Linux mod/content roots have absolute paths which differ only by casing, which ext4 and other common case-sensitive filesystems permit.
- **Root cause:** The global lookup-object cache used `StringComparer.OrdinalIgnoreCase` for root paths. The second root therefore reused the first root's lookup instance, whose immutable `RootPath` remained anchored to the wrong directory. The ordinary dictionary was also unsynchronized despite content helpers supporting concurrent access.
- **Impact:** Files can resolve from the wrong mod folder or appear missing, potentially causing failed content loads and repeated fallback/error work. This is conditional correctness rather than broad frame-time cost.
- **Expected benefit:** Unix root identity remains ordinal/case-sensitive while relative lookup within each root remains case-insensitive. Atomic cache creation also removes the global concurrent dictionary race.
- **Risk:** Low. Windows keeps ordinal-ignore-case root identity; Unix may create separate lookup objects only where the filesystem can represent distinct roots. A real temporary-filesystem fixture verifies two case-distinct roots return their own sentinel content through mismatched relative casing.
- **Status:** Fixed. The root cache is a platform-comparer `ConcurrentDictionary`; relative file matching semantics are unchanged.

### 95. TMX TileData properties are reconverted for every covered tile

- **Affected code:** `Framework/Content/OptimizedTmxFormat.cs` (`LoadObjects`).
- **Scenario:** Loading expansion maps which use Tiled `TileData` objects to apply actions, collision, or other metadata across rectangular regions of tiles.
- **Root cause:** The converter selected and boxed each property's typed value inside the innermost tile loop. One immutable xTile `PropertyValue` was therefore allocated again for every covered populated tile, and typed properties repeated their string type dispatch. The coordinate math also divided object width by tile height and object Y by tile width, which selected the wrong region when a TMX map used rectangular tiles.
- **Measured evidence:** The installed target pack contains 960 TMX maps with 38,577 `TileData` objects, covering 85,527 tile positions and causing about 92,961 property assignments. The 20 heaviest maps account for 32,368 assignments per pass. Across four independent five-pass end-to-end conversion runs, the old path took an aggregate 3,179 ms versus 2,619 ms after preconversion, about 17.6% less time; every individual run was faster (8% to 31%). One representative five-pass run allocated 214.9 MiB versus 214.0 MiB.
- **Impact:** Main-thread map-load and reload CPU proportional to `TileData` property count multiplied by covered populated tiles, plus short-lived property wrappers.
- **Expected benefit:** Each object property is converted once, then its immutable value is assigned to every target tile. Single-property objects avoid even a temporary value-array allocation. Rectangular tile maps apply metadata to their intended coordinates.
- **Risk:** Low. xTile `PropertyValue` exposes no mutation API and already copies only its immutable wrapped value. Property names, types, overwrite order, empty-cell behavior, and layer matching remain unchanged. Focused coverage verifies multiple typed properties across a non-square tile grid and exact affected/unaffected coordinates.
- **Status:** Fixed. TileData values are preconverted once per source object and width/Y calculations use their corresponding tile dimensions.

## Requested audit coverage

| Requested area | Detailed evidence |
| --- | --- |
| Per-tick world, location, building, object, NPC, terrain, furniture, and chest tracking | Findings 1, 2, 18, 23, 29, 40, 41, 42, 48, 54, and 72 |
| Duplicate `LocationsWatcher` update/reset | Finding 1 |
| Chest scanning and snapshot comparisons | Findings 2 and 48 |
| Asset loading, lookup, and invalidation | Findings 3, 4, 9, 10, 15, 31, 33, 37, 38, 39, 53, 56, 57, 58, 60, 64, 69, 70, 76, 78, 79, 80, 84, 89, 92, and 93 |
| Exact and batched invalidation APIs | Findings 3, 4, 79, and 80 |
| Map, NPC, texture, and content-manager propagation | Findings 5, 19, 28, 32, 34, 41, 43, 55, 63, 64, 66, 75, 81, 82, 85, 86, 87, 88, 90, and 91 |
| Content Patcher-scale invalidation bursts | Findings 4, 5, 19, 22, 27, 32, 34, 37, 41, 43, 55, 56, 62, 63, 68, 78, 79, and 85 |
| Synchronous logging and `AutoFlush` stalls | Findings 6, 46, and 52 |
| Per-tile rendering overhead | Findings 7, 30, and 35 |
| PNG decode, conversion, texture creation, and decoded caching | Findings 8, 21, 22, 28, 45, 51, 65, and 83 |
| Content-manager lookup scaling | Findings 9, 53, 57, 58, and 73 |
| Asset-name parsing and normalization | Findings 10 and 36 |
| Linux case-insensitive file lookup | Findings 11, 38, 39, 92, and 94 |
| Assembly loading and rewrite caching | Findings 12 and 44 |
| Dependency resolution | Finding 13 |
| Disposable and weak-reference retention | Findings 14, 21, and 28 |
| Event dispatch and asset-request routing | Findings 15, 16, 25, 26, 27, 31, 33, 35, 47, 59, 61, 67, 69, 71, and 74 |
| Multiplayer message delivery | Finding 49 |
| Reflection API overhead | Findings 35 and 50 |
| GC pressure, memory growth, and texture memory | Findings 8, 14, 21, 22, 23, 24, 25, 26, 27, 28, 31, 32, 33, 34, 35, 36, 37, 39, 40, 43, 44, 46, 48, 49, 50, 52, 55, 57, 65, 67, 74, 82, 83, 84, 86, 87, 90, 91, 92, 93, and 95 |
| TMX map parsing and conversion | Findings 86, 87, 88, 89, and 95 |
| .NET 10, Harmony, tiering, and dynamic PGO | Finding 20 |

## Remaining implementation priority

1. Capture representative Linux traces from the target 200-code-mod/400-content-pack installation, especially cursor-position consumption, live `AssetRequested` frequency, propagation side-effect repetition, and the before/after chest-tracking frame cost.
2. Add a provider-generation model only if traces justify extending asset-operation caching across ticks without stale dynamic conditions.
3. Coalesce propagation side effects only after their ordering and intermediate-state contracts are proven.
4. Measure live GPU textures and privately owned uncached assets before extending byte budgeting beyond decoded CPU pixels.
5. Replace the fallback Linux mis-cased-path tree index only if traces show meaningful use after exact-first lookup.
6. Migrate to .NET 10 only after Harmony patching, tiered compilation, mod binary compatibility, installer packaging, and all supported platforms pass end-to-end game validation.

This order may change when a finding is disproved, an upstream change supersedes it, or runtime evidence shows a different bottleneck. Such changes should be recorded in the relevant finding rather than silently removing it.
