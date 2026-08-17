# Linux large-mod performance and correctness audit

This document tracks SMAPI-side performance and correctness findings for Linux players with very large mod sets (for example, 200+ code mods and 400+ content packs). It focuses on work SMAPI can avoid or make incremental; it does not attribute time spent inside individual mods to SMAPI.

The rankings prioritize gameplay frame pacing, then transition stalls, memory pressure, and startup time. Expected benefits are qualitative until measured in-game on representative Linux systems; no percentage claims are implied.

Statuses used below are **confirmed**, **fixed**, **deferred**, **rejected**, and **needs runtime evidence**.

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
12. Finding 53 — duplicate cached-asset probes — fixed.
13. Finding 16 — managed-event and live asset-request dispatch allocations — partially fixed.
14. Finding 37 — redundant invalidation-batch cloning — fixed.
15. Finding 36 — per-parse locale delegate allocation — fixed.
16. Finding 34 — layer work repeated for every patched map tile — fixed.
17. Finding 15 — one-tick asset-operation cache lifetime — rejected without a provider contract.
18. Finding 31 — intercepted asset-operation dispatch churn — fixed.
19. Finding 33 — asset-loader adapter closures — fixed.
20. Finding 27 — tick-cache factory and world-helper allocations — fixed.
21. Finding 6 — synchronous game-thread log flushing — fixed.
22. Finding 46 — synchronous log formatting on the game thread — fixed.
23. Finding 52 — eager asset-operation trace formatting — fixed.
24. Finding 5 — repeated global invalidation-propagation searches — partially fixed.
25. Finding 41 — incomplete and duplicate world-location topology — fixed.
26. Finding 43 — per-asset propagation key normalization allocations — fixed.
27. Finding 32 — per-map warp comparison sets — fixed.
28. Finding 19 — repeated propagation side effects — partially fixed.
29. Finding 4 — no first-class batched exact invalidation — fixed.
30. Finding 3 — exact invalidation performing cache scans — fixed.
31. Finding 22 — oversized sparse image-patch transfers — fixed.
32. Finding 51 — decoded-texture cache-miss metadata syscalls — fixed.
33. Finding 8 — PNG decode and conversion churn — fixed with a bounded repeat-decode cache.
34. Finding 28 — texture-propagation temporary allocations and lifetime — fixed.
35. Finding 21 — unbudgeted texture and decoded-content memory — partially fixed.
36. Finding 9 — linear content-manager routing — fixed.
37. Finding 10 — repeated asset-name strings — fixed.
38. Finding 14 — retained dead disposable wrappers — fixed.
39. Finding 29 — world trackers lost across reordered transfers — fixed.
40. Finding 42 — location trackers lack source ownership — fixed.
41. Finding 30 — rectangular transformed-tile origin — fixed.
42. Finding 18 — reversed location event changes — fixed.
43. Finding 17 — swapped managed-event identifiers — fixed.
44. Finding 38 — case-sensitive Linux paint-mask matching — fixed.
45. Finding 39 — culture-sensitive and ambiguous Linux content-path comparisons — fixed.
46. Finding 11 — eager Linux case-insensitive tree indexing — partially fixed.
47. Finding 13 — repeated dependency-list scans — fixed.
48. Finding 44 — repeated loaded-assembly scans and dependency parsing — fixed.
49. Finding 12 — repeated assembly parsing and compatibility rewriting — fixed.
50. Finding 45 — incorrect overlay alpha composition — queued.
51. Finding 49 — mod messages serialize even when no remote peer will receive them — fixed.
52. Finding 50 — public reflection cache hits allocate lookup machinery — fixed.
53. Finding 20 — .NET 6 runtime and disabled tiered compilation — deferred.

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
- **Status:** Fixed. The exact-name helper now uses direct cache-key lookup across only game content managers, with the same localization cleanup, temporary-map handling, propagation, events, and reporting as predicate invalidation.

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
- **Status:** Partially fixed. Non-caching namespaced managers are excluded from invalidation and loaded-value scans. Texture propagation reuses the exact manager targets already discovered during invalidation instead of rescanning every code mod's game content manager, with a full-scan fallback for the base-name half of localized invalidations. Multi-asset NPC dialogue/schedule bursts build one exact-name index instead of scanning every NPC for each asset. Multi-map bursts similarly index locations and spouse-room targets in one world pass instead of scanning every location for each map. Resuming an invalidated NPC schedule now finds the latest applicable entry in one linear, allocation-free pass instead of filtering and sorting every key. Single-asset world invalidations retain the cheaper direct scans. Side-effect batching remains deferred.

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

### 24. Keyboard polling allocates an array while walking

- **Affected code:** `Framework/Input/KeyboardStateBuilder.cs` (`Reset`).
- **Scenario:** Every focused Linux update while one or more keyboard keys are held, including ordinary WASD walking.
- **Root cause:** SMAPI calls MonoGame's parameterless `KeyboardState.GetPressedKeys`, which constructs a new exact-sized array whenever at least one key is pressed, then immediately copies those keys into SMAPI's reusable set.
- **Impact:** Steady gameplay and garbage collection.
- **Expected benefit:** A caller-owned key buffer removes the continuous pressed-key array allocation during keyboard movement and other held input.
- **Risk:** Low. MonoGame exposes the key count and a buffer-filling overload; SMAPI must process only the populated prefix because a reused buffer can retain older values past that count.
- **Status:** Fixed. Keyboard polling now grows one per-player buffer only when the simultaneous key-count high-water mark exceeds its capacity, fills it through MonoGame's nonallocating overload, and reads exactly the reported number of keys.

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

- **Affected code:** `Framework/ContentManagers/GameContentManager.cs` (`LoadExact`, `ApplyLoader`, `ApplyEditors`, and `AssertMaxOneRequiredLoader`), `Framework/SCore.cs` (`RequestAssetOperations`), and `Framework/Utilities/ContextHash.cs` (`Track`).
- **Scenario:** Linux gameplay transitions and context changes which reload many assets intercepted by Content Patcher-scale handler sets.
- **Root cause:** Every uncached intercepted load created a capturing recursive-load closure. Loader validation built a filtered array even in the normal zero-or-one-exclusive-loader case, selecting the winning loader used a LINQ maximum pass, and every application of a cached editor group rebuilt a stable LINQ ordering pipeline.
- **Impact:** Transitions and garbage collection, with cost repeated per reloaded asset and content manager.
- **Expected benefit:** Removes routine helper objects and redundant ordering work around mod loaders/editors, leaving more of the transition frame budget for the actual content edits and texture/map work.
- **Risk:** Low. The highest-priority loader still wins with registration order breaking ties, editor ordering remains stable for equal priorities, recursive-load cleanup still runs through `finally`, and the conflict diagnostics are unchanged.
- **Status:** Fixed. Recursive-load tracking now accepts explicit state and a cached static callback, the normal exclusive-loader check and winning-loader selection use direct list scans, and editor operations are stably ordered once when their tick-cached operation group is created and enumerated directly thereafter.

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
- **Status:** Queued as a correctness fix, separate from performance patches.

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

## Requested audit coverage

| Requested area | Detailed evidence |
| --- | --- |
| Per-tick world, location, building, object, NPC, terrain, furniture, and chest tracking | Findings 1, 2, 18, 23, 29, 40, 41, 42, and 48 |
| Duplicate `LocationsWatcher` update/reset | Finding 1 |
| Chest scanning and snapshot comparisons | Findings 2 and 48 |
| Asset loading, lookup, and invalidation | Findings 3, 4, 9, 10, 15, 31, 33, 37, 38, 39, and 53 |
| Exact and batched invalidation APIs | Findings 3 and 4 |
| Map, NPC, texture, and content-manager propagation | Findings 5, 19, 28, 32, 34, 41, and 43 |
| Content Patcher-scale invalidation bursts | Findings 4, 5, 19, 22, 27, 32, 34, 37, 41, and 43 |
| Synchronous logging and `AutoFlush` stalls | Findings 6, 46, and 52 |
| Per-tile rendering overhead | Findings 7, 30, and 35 |
| PNG decode, conversion, texture creation, and decoded caching | Findings 8, 21, 22, 28, 45, and 51 |
| Content-manager lookup scaling | Findings 9 and 53 |
| Asset-name parsing and normalization | Findings 10 and 36 |
| Linux case-insensitive file lookup | Findings 11, 38, and 39 |
| Assembly loading and rewrite caching | Findings 12 and 44 |
| Dependency resolution | Finding 13 |
| Disposable and weak-reference retention | Findings 14, 21, and 28 |
| Event dispatch and asset-request routing | Findings 15, 16, 25, 26, 27, 31, 33, 35, and 47 |
| Multiplayer message delivery | Finding 49 |
| Reflection API overhead | Findings 35 and 50 |
| GC pressure, memory growth, and texture memory | Findings 8, 14, 21, 22, 23, 24, 25, 26, 27, 28, 31, 32, 33, 34, 35, 36, 37, 39, 40, 43, 44, 46, 48, 49, 50, and 52 |
| .NET 10, Harmony, tiering, and dynamic PGO | Finding 20 |

## Remaining implementation priority

1. Capture representative Linux traces from the target 200-code-mod/400-content-pack installation, especially cursor-position consumption, live `AssetRequested` frequency, propagation side-effect repetition, and the before/after chest-tracking frame cost.
2. Add a provider-generation model only if traces justify extending asset-operation caching across ticks without stale dynamic conditions.
3. Coalesce propagation side effects only after their ordering and intermediate-state contracts are proven.
4. Measure live GPU textures and privately owned uncached assets before extending byte budgeting beyond decoded CPU pixels.
5. Replace the fallback Linux mis-cased-path tree index only if traces show meaningful use after exact-first lookup.
6. Correct source-over alpha composition after representative content-pack visual comparisons.
7. Migrate to .NET 10 only after Harmony patching, tiered compilation, mod binary compatibility, installer packaging, and all supported platforms pass end-to-end game validation.

This order may change when a finding is disproved, an upstream change supersedes it, or runtime evidence shows a different bottleneck. Such changes should be recorded in the relevant finding rather than silently removing it.
