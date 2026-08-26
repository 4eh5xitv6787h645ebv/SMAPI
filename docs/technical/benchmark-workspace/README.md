# Real-world large-modpack benchmark workspace

This folder contains a reproducible real-world workload for testing and benchmarking this fork's changes: the full mod list and the save game from the Linux player whose reports drove issues #135–#139, #146, and #156. It complements [`linux-large-mod-performance-audit.md`](../linux-large-mod-performance-audit.md), which tracks the fork's findings against workloads like this one.

The save owner shares this data for testing purposes; feel free to relocate or trim it (e.g. move the archive to a release asset) if that fits the repo better.

## Contents

| file | description |
| :--- | :--- |
| [`mod-list.md`](mod-list.md) | Human-readable mod list: 308 mods (129 C# mods, 179 content packs) with versions, unique IDs, and update keys. |
| [`mods.json`](mods.json) | The same list in machine-readable form, including folder paths. |
| `Blossom_389524656.tar.xz` | The save game (~1.3 MB compressed, ~81 MB extracted): main save file, `SaveGameInfo`, `JsonAssets/` ID cache, and SpaceCore serialization indexes. Backup/rotation copies are excluded. |

Mods are **not** included in the repo itself. For convenience, a one-shot snapshot of the full `Mods` folder (746 MB `tar.zst`, 1.0 GB extracted, matching the list exactly) is available as a release asset, shared by the save owner **for fork testing only** — mod authors retain all rights, so download mods from their official pages (via the update keys in the mod list) for any other use:

> https://github.com/adventurexplore/SMAPI/releases/download/benchmark-mods-2026-08-26/Mods-2026-08-26.tar.zst

Extract it into the game folder so you get `Stardew Valley/Mods/`:

```bash
tar --zstd -xf Mods-2026-08-26.tar.zst -C "path/to/Stardew Valley"
```

## Benchmark machine

- Framework Laptop 13: Intel i5-1340P, Intel Iris Xe (Mesa 26.1.6), 64 GB RAM
- Arch Linux (kernel 7.1.8), KDE Plasma 6 on Wayland
- Stardew Valley 1.6, **native Linux build**, launched through gamescope at 3440×1440

## Workload

The "Blossom" farm is a mid/late-game, heavily automated save: Automate with large machine groups, Stardew Valley Expanded, East Scarp, Ridgeside Village, Vanilla Plus Professions, and the rest of the mod list. It's the save on which stock SMAPI's per-tick overhead was the difference between smooth play and morning stutter.

Reference measurements on this machine and save (180 s steady-state samples, external instrumentation of `SGame.Update`/`Game1.Update`/mod handlers):

| metric | stock SMAPI 4.5.1 | this fork |
| :--- | ---: | ---: |
| SMAPI framework overhead per tick | 5.892 ms (p95 7.201 ms) | ~0.149 ms (p95 0.196 ms) |
| allocation per tick | 4787 KB | 959 KB |
| `SGame.Update` + `Draw` per frame | 14.696 ms | 6.785 ms |

## Using the save

1. Extract into the Linux save folder so you get `~/.config/StardewValley/Saves/Blossom_389524656/`:

   ```bash
   tar -xJf Blossom_389524656.tar.xz -C ~/.config/StardewValley/Saves/
   ```

2. Install the mods from [`mod-list.md`](mod-list.md). The save loads with SMAPI's missing-mod warnings if only a subset is installed, but timings are only representative with the full list.
3. For profiling, `performance start` / `performance stop` in the SMAPI console (see the audit doc's "Runtime mod diagnostics" section).
