---
layout: default
title: Official SMAPI vs this fork
description: A direct, evidence-based comparison of support, features, performance, and tradeoffs.
kicker: Comparison snapshot · 28 August 2026
---

## The short answer

Use [official SMAPI](https://github.com/Pathoschild/SMAPI) if you want a supported installer,
cross-platform releases, and the community's standard troubleshooting path. Evaluate this fork if
you use Linux desktop, have a very large mod collection, and want its optimization work or local
diagnostics enough to accept development-build risk.

This fork is not “SMAPI but always faster.” Results depend on the mods, save, content, machine, and
bottleneck. Its measured wins target SMAPI-owned overhead; it cannot make an expensive mod callback,
Harmony patch, GPU workload, or operating-system stall disappear.

## Current project comparison

This snapshot compares upstream commit
[`79f9bbbe`](https://github.com/Pathoschild/SMAPI/commit/79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0)
with fork commit
[`fd62edca`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/fd62edca6bd28c5a368cb403af59603f655a8eee),
before this documentation refresh. Both identify as SMAPI 4.5.2; the fork snapshot was 217 commits
ahead and zero commits behind that upstream base.

| Area | Official SMAPI 4.5.2 | This Linux fork |
| --- | --- | --- |
| Project role | Official mod loader and API | Unofficial performance/diagnostics development fork |
| Supported audience | General players and mod authors | Linux desktop testers, especially large modpacks |
| Platforms | Windows, macOS, Linux | Linux desktop focus; no Android/mobile work |
| Release delivery | Tagged 4.5.2 player release | No tagged fork release at this snapshot |
| Existing mods | Canonical compatibility target | Derived from 4.5.2 and intended to retain compatibility, but not an upstream guarantee |
| Performance approach | General-purpose upstream behavior | 95-item Linux large-pack audit plus targeted fixes |
| Mod diagnosis | Console and normal SMAPI log | Same foundation plus private `health` report/viewer |
| Runtime profiling | Normal log diagnostics | Opt-in bounded `performance` capture and attribution |
| Support route | [SMAPI community](https://smapi.io/community) | [Fork issue tracker](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues) |

## Feature differences

### Incremental work instead of broad polling

The fork tracks normal chest inventories, player inventory, skills, and dirty locations through
change notifications where compatibility permits. Upstream-derived paths repeatedly walked more of
that state. The fallback paths remain for runtime types which cannot safely use notification-based
tracking.

### Content and map pipeline work

The fork adds batching, indexing, caching, and allocation reductions across asset parsing,
invalidation, propagation, TMX conversion, texture patching, JSON reads, file routing, and assembly
rewrites. These changes matter most during large content loads and transition bursts; they do not
all run every frame.

### Local health and performance reports

`health` builds a bounded, sanitized report and provides an in-game viewer. `performance` samples
work visible at SMAPI-managed boundaries. Raw log text, stack traces, absolute paths, save names,
configuration values, and update keys are deliberately excluded from health exports. Neither tool
uploads data automatically.

## Performance evidence

### Historical whole-workload comparison

The direct end-to-end comparison was contributed with
[PR #158](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/158). It used a Framework Laptop 13
(Intel i5-1340P, Iris Xe, 64 GB RAM), Arch Linux, native Stardew Valley under gamescope at 3440×1440,
308 mods (129 code mods and 179 content packs), and the mid/late-game Blossom save. Each steady-state
sample lasted 180 seconds, with external instrumentation around `SGame.Update`, `Game1.Update`, and
mod handlers.

| Metric | Stock SMAPI 4.5.1 | Tested fork build | Difference |
| --- | ---: | ---: | ---: |
| Mean SMAPI framework overhead per tick | 5.892 ms | ~0.149 ms | **−97.5%** |
| p95 SMAPI framework overhead per tick | 7.201 ms | 0.196 ms | **−97.3%** |
| Allocation per tick | 4,787 KB | 959 KB | **−80.0%** |
| `SGame.Update` + `Draw` per frame | 14.696 ms | 6.785 ms | **−53.8%** |

<div class="notice" markdown="1">
**How to read this table:** it is strong evidence for that exact historical setup, not a current
4.5.2-vs-4.5.2 release benchmark. The save, mod set, and sample duration were controlled, but the
result came from one machine and no confidence intervals were recorded. A fresh repeated comparison
against stock 4.5.2 is still needed before treating these percentages as a current release claim.
</div>

### Isolated A/B benchmarks

The audit also tested old and optimized implementations under the same process and workload. “Before”
means the upstream-derived path before that specific fork optimization; it is not a measurement of
an entire stock SMAPI frame. Results are rounded as originally recorded.

| Workload | Before | Fork optimization | Observed result |
| --- | ---: | ---: | ---: |
| Convert 20 largest installed TMX maps; metadata lookup | 1,843 ms | 750 ms | **59.3% less time** |
| Convert 20 largest TMX maps twice; omit identity transforms | 1,594 ms / 375.3 MiB | 980 ms / 214.4 MiB | **38.5% less time; 42.9% less allocation** |
| Convert 20 largest TMX maps twice; skip empty tile writes | 1,188 ms | 844 ms | **29.0% less time** |
| Parse and convert 20 largest TMX maps; sequential 64 KiB reads | 3,609 ms | 3,201 ms | **11.3% less time** |
| Patch 10 largest TMX maps; skip empty property copies | 714 ms | 464 ms | **35.0% less time** |
| Normalize 20,000 real mod-file paths | 74 ms / 61.2 MiB | 5 ms / ~0 B | **93.2% less time; allocation removed** |
| Deserialize 20 largest JSON files (15.2 MiB) twice | 1,101 ms / 263.4 MiB | 1,137 ms / 144.1 MiB | **45.3% less allocation; ~3% more time** |
| Convert `TileData` across four five-pass runs | 3,179 ms | 2,619 ms | **17.6% less time** |

Additional warmed checks recorded:

- two million repeated canonical asset parses were about **6.4× faster** through the bounded cache;
- a cached reflection field lookup fell from **368 bytes per call to zero** over 10,000 calls;
- a no-op asset request fell from **120 bytes to 56 bytes** of allocation; and
- a full 36-slot normal inventory track/diff/reset loop allocated **zero bytes** over 10,000 warmed
  unchanged iterations.

The complete scenarios, correctness checks, risks, and rejected ideas are preserved in the
[95-finding performance audit](technical/linux-large-mod-performance-audit.md).

## What the numbers do not prove

- They do not promise a particular FPS, startup time, or percentage improvement for another player.
- Microbenchmarks isolate code paths and can overstate their share of a real frame.
- The historical whole-workload baseline is stock 4.5.1, not the current 4.5.2 release.
- SMAPI-owned timing cannot fully attribute Harmony patches, direct calls, GPU work, native work,
  background threads, scheduling, or I/O outside its boundaries.
- Some optimizations trade memory for reuse through explicit bounded caches; the audit documents
  their caps and invalidation rules.

## Compatibility and risk

The fork retains the same 4.5.2 version identity and is designed around existing SMAPI mods, but it
changes many internal hot paths and does not yet publish a stable release. Keep backups, evaluate it
against a disposable game/save copy, and reproduce problems on official SMAPI before reporting an
upstream regression.

<div class="button-row">
  <a class="button primary" href="getting-started.html">Evaluate the fork safely</a>
  <a class="button" href="https://github.com/Pathoschild/SMAPI/releases/tag/4.5.2">Get official SMAPI</a>
</div>
