# Linux large-mod performance and correctness audit

This document tracks SMAPI-side performance and correctness findings for Linux players with very large mod sets (for example, 200+ code mods and 400+ content packs). It focuses on work SMAPI can avoid or make incremental; it does not attribute time spent inside individual mods to SMAPI.

The rankings prioritize gameplay frame pacing, then transition stalls, memory pressure, and startup time. Expected benefits are qualitative until measured in-game on representative Linux systems; no percentage claims are implied.

Statuses used below are **confirmed**, **fixed**, **deferred**, **rejected**, and **needs runtime evidence**.

## Ranked findings

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
- **Status:** Partially fixed. The chest-inventory and player-inventory stages disable stack baselines, comparisons, and snapshot construction when their events have no listeners, with a fresh baseline on activation; unobserved chest watchers now also unsubscribe from inventory notifications and skip both their update and reset traversals. Verbose diagnostic logging keeps player inventory tracking active when requested. Location collection watchers push one aggregate dirty notification, and unobserved-chest ticks process and snapshot only the locations in that dirty set instead of traversing the whole world. World snapshots copy only event families which have listeners and retain only locations with relevant changes. Building indoor references use their net-field notifications and a dirty set, replacing the every-tick scan of all buildings with changed-only processing. The underlying collection watchers remain active because SMAPI needs them to discover location topology independently of public event subscriptions.

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
- **Status:** Partially fixed. Normal PNG decoding converts Skia's native premultiplied RGBA/BGRA span directly into the final XNA array, eliminating both full-image intermediate managed arrays. On Linux's normal RGBA path, matching packed rows are now copied in bulk instead of constructing every pixel individually; padded rows and BGRA data retain channel-correct handling, and unexpected decode formats retain the general fallback. A mutation-safe decoded cache remains deferred until representative runtime data can establish a byte budget, reuse threshold, and file-change policy without increasing large-pack paging risk.

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
- **Status:** Partially fixed. `AssetName` no longer allocates and retains a lowercase copy for every parsed name; ordinal case-insensitive equality and hashing operate directly on the canonical name. Runtime asset normalization now returns already-canonical strings unchanged and writes noncanonical paths directly into one result string instead of splitting into an array and per-segment strings. Reusable parsed-name instances remain deferred.

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
- **Status:** Confirmed; deferred behind gameplay fixes.

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
- **Status:** Needs runtime evidence.

### 16. Event dispatch performs per-handler context operations and lazy callback allocations

- **Affected code:** `Framework/Events/ManagedEvent.cs` (`Raise`) and `Framework/Events/ManagedEventHandler.cs`.
- **Scenario:** High-frequency update/input events with many subscribers, and repeated live asset requests while walking or transitioning.
- **Root cause:** Each handler invocation pushes and pops the current mod context and enters exception-handling logic. The lazy dispatch overload also creates a new callback delegate for every handler on every raise.
- **Impact:** Steady gameplay, transitions, and garbage collection.
- **Expected benefit:** Caching lazy callbacks removes repeat dispatch allocations; a correctly scoped context fast path could further reduce framework overhead, although mod handler time will usually dominate.
- **Risk:** High. Current-mod attribution and exception isolation are correctness features and must not be weakened.
- **Status:** Partially fixed. Each registered handler now owns one cached lazy-dispatch callback instead of allocating one on every asset request or filtered message dispatch. Context stack operations and exception boundaries remain unchanged pending runtime evidence.

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
- **Status:** Confirmed; deferred as part of batched propagation.

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
- **Status:** Needs runtime evidence and a concrete ownership model.

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

## Implementation order

1. Correct duplicate location processing and validate temporary-location events.
2. Correct the swapped event identifiers.
3. Add indexed content-manager lookup and prune dead disposable references.
4. Add exact-key invalidation, followed by a compatible batch API.
5. Add the normal-tile rendering fast path.
6. Make world/chest tracking listener-aware without losing internal baselines.
7. Batch propagation and coalesce global side effects.
8. Move logging to a bounded, crash-safe background writer.
9. Reduce asset-name allocations and share Linux path indexes.
10. Add bounded decode and assembly-rewrite caches.
11. Consider .NET 10 after Harmony and packaging compatibility are proven.

This order may change when a finding is disproved, an upstream change supersedes it, or runtime evidence shows a different bottleneck. Such changes should be recorded in the relevant finding rather than silently removing it.
