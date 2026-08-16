# Linux large-mod performance and correctness audit

This document tracks SMAPI-side performance and correctness findings for Linux players with very large mod sets (for example, 200+ code mods and 400+ content packs). It focuses on work SMAPI can avoid or make incremental; it does not attribute time spent inside individual mods to SMAPI.

The rankings prioritize gameplay frame pacing, then transition stalls, memory pressure, and startup time. Expected benefits are qualitative until measured in-game on representative Linux systems; no percentage claims are implied.

Statuses used below are **confirmed**, **fixed**, **deferred**, **rejected**, and **needs runtime evidence**.

## Current priority ranking

This is the current jank-first order, combining likely frame-time impact, frequency, confidence, and compatibility risk. Finding numbers link the ranking to the detailed evidence below; fixed entries remain ranked so the expected benefit of this fork is visible.

1. Finding 2 — demand-driven world, chest, and inventory tracking — partially fixed.
2. Finding 1 — duplicate world-location processing — fixed.
3. Finding 23 — per-tick core update closures — fixed.
4. Finding 35 — render-stage reflection and mismatched sprite batches — fixed.
5. Finding 40 — repeated current-screen dictionary lookups — fixed.
6. Finding 24 — pressed-key polling allocation while walking — fixed.
7. Finding 26 — unused cursor snapshots while the camera scrolls — fixed.
8. Finding 25 — held-input event snapshot allocations — fixed.
9. Finding 7 — normal-tile rendering overhead — fixed.
10. Finding 16 — managed-event and live asset-request dispatch allocations — partially fixed.
11. Finding 37 — redundant invalidation-batch cloning — fixed.
12. Finding 36 — per-parse locale delegate allocation — fixed.
13. Finding 34 — layer work repeated for every patched map tile — fixed.
14. Finding 15 — one-tick asset-operation cache lifetime — needs runtime evidence.
15. Finding 31 — intercepted asset-operation dispatch churn — fixed.
16. Finding 33 — asset-loader adapter closures — fixed.
17. Finding 27 — tick-cache factory and world-helper allocations — fixed.
18. Finding 6 — synchronous game-thread log flushing — fixed.
19. Finding 5 — repeated global invalidation-propagation searches — partially fixed.
20. Finding 32 — per-map warp comparison sets — fixed.
21. Finding 19 — repeated propagation side effects — deferred.
22. Finding 4 — no first-class batched exact invalidation — fixed.
23. Finding 3 — exact invalidation performing cache scans — fixed.
24. Finding 22 — oversized sparse image-patch transfers — fixed.
25. Finding 8 — PNG decode and conversion churn — partially fixed.
26. Finding 28 — texture-propagation temporary allocations and lifetime — fixed.
27. Finding 21 — unbudgeted texture and decoded-content memory — needs runtime evidence.
28. Finding 9 — linear content-manager routing — fixed.
29. Finding 10 — repeated asset-name strings — partially fixed.
30. Finding 14 — retained dead disposable wrappers — fixed.
31. Finding 29 — world trackers lost across reordered transfers — fixed.
32. Finding 30 — rectangular transformed-tile origin — fixed.
33. Finding 18 — reversed location event changes — fixed.
34. Finding 17 — swapped managed-event identifiers — fixed.
35. Finding 38 — case-sensitive Linux paint-mask matching — fixed.
36. Finding 39 — culture-sensitive and ambiguous Linux content-path comparisons — fixed.
37. Finding 11 — eager Linux case-insensitive tree indexing — partially fixed.
38. Finding 13 — repeated dependency-list scans — fixed.
39. Finding 12 — repeated assembly parsing and compatibility rewriting — deferred.
40. Finding 20 — .NET 6 runtime and disabled tiered compilation — deferred.

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
- **Root cause:** Each handler invocation pushes and pops the current mod context and enters exception-handling logic. Lazy dispatch originally created a new callback delegate for every handler on every raise; after caching those callbacks, high-frequency callers still created a capturing outer dispatch closure for each live asset request or routed network message.
- **Impact:** Steady gameplay, transitions, and garbage collection.
- **Expected benefit:** Caching lazy callbacks removes repeat dispatch allocations; a correctly scoped context fast path could further reduce framework overhead, although mod handler time will usually dominate.
- **Risk:** High. Current-mod attribution and exception isolation are correctness features and must not be weakened.
- **Status:** Partially fixed. Each registered handler owns one cached lazy-dispatch callback, and stateful raises now pass stack-held per-raise state by reference to cached static invokers. Live asset requests and routed network messages therefore avoid both the per-handler callback allocations and their per-raise capturing dispatch closure. Context stack operations and exception boundaries remain unchanged pending runtime evidence.

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

## Requested audit coverage

| Requested area | Detailed evidence |
| --- | --- |
| Per-tick world, location, building, object, NPC, terrain, furniture, and chest tracking | Findings 1, 2, 18, 23, 29, and 40 |
| Duplicate `LocationsWatcher` update/reset | Finding 1 |
| Chest scanning and snapshot comparisons | Finding 2 |
| Asset loading, lookup, and invalidation | Findings 3, 4, 9, 10, 15, 31, 33, 37, 38, and 39 |
| Exact and batched invalidation APIs | Findings 3 and 4 |
| Map, NPC, texture, and content-manager propagation | Findings 5, 19, 28, 32, and 34 |
| Content Patcher-scale invalidation bursts | Findings 4, 5, 19, 22, 27, 32, 34, and 37 |
| Synchronous logging and `AutoFlush` stalls | Finding 6 |
| Per-tile rendering overhead | Findings 7, 30, and 35 |
| PNG decode, conversion, texture creation, and decoded caching | Findings 8, 21, 22, and 28 |
| Content-manager lookup scaling | Finding 9 |
| Asset-name parsing and normalization | Findings 10 and 36 |
| Linux case-insensitive file lookup | Findings 11, 38, and 39 |
| Assembly loading and rewrite caching | Finding 12 |
| Dependency resolution | Finding 13 |
| Disposable and weak-reference retention | Findings 14, 21, and 28 |
| Event dispatch and asset-request routing | Findings 15, 16, 25, 26, 27, 31, 33, and 35 |
| GC pressure, memory growth, and texture memory | Findings 8, 14, 21, 22, 23, 24, 25, 26, 27, 28, 31, 32, 33, 34, 35, 36, 37, 39, and 40 |
| .NET 10, Harmony, tiering, and dynamic PGO | Finding 20 |

## Remaining implementation priority

1. Capture representative Linux traces from the target 200-code-mod/400-content-pack installation, especially live `AssetRequested` frequency and propagation side-effect repetition.
2. Add a provider-generation model only if traces justify extending asset-operation caching across ticks without stale dynamic conditions.
3. Coalesce propagation side effects only after their ordering and intermediate-state contracts are proven.
4. Establish a measured CPU/GPU byte budget, reuse threshold, and file-change policy before adding decoded texture caching or preloading.
5. Replace the fallback Linux mis-cased-path tree index only if traces show meaningful use after exact-first lookup.
6. Add a content-addressed assembly-rewrite cache with complete SMAPI, game, platform, symbol, handler, and configuration keys.
7. Migrate to .NET 10 only after Harmony patching, tiered compilation, mod binary compatibility, installer packaging, and all supported platforms pass end-to-end game validation.

This order may change when a finding is disproved, an upstream change supersedes it, or runtime evidence shows a different bottleneck. Such changes should be recorded in the relevant finding rather than silently removing it.
