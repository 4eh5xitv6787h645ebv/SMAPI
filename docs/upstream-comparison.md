---
layout: default
title: Official SMAPI vs this fork
description: A direct, evidence-based comparison of support, features, performance, and tradeoffs.
kicker: Comparison snapshot · 3 September 2026
---

## The short answer

Use [official SMAPI](https://github.com/Pathoschild/SMAPI) if you want a supported installer,
cross-platform releases, and the community's standard troubleshooting path. Evaluate this fork if
you use Linux desktop, have a very large mod collection, and want its optimization work or local
diagnostics enough to accept experimental-prerelease risk.

This fork is not “SMAPI but always faster.” Results depend on the mods, save, content, machine, and
bottleneck. Its measured wins target SMAPI-owned overhead; it cannot make an expensive mod callback,
Harmony patch, GPU workload, or operating-system stall disappear.

## Current project comparison

This snapshot compares upstream commit
[`79f9bbbe`](https://github.com/Pathoschild/SMAPI/commit/79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0)
with fork commit
[`3c98eadd`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/3c98eadd2bddc24d43c889afb11b155e92469882),
the exact B build used for the current benchmark. Both identify as SMAPI 4.5.2.

| Area | Official SMAPI 4.5.2 | This Linux fork |
| --- | --- | --- |
| Project role | Official mod loader and API | Unofficial performance/diagnostics development fork |
| Supported audience | General players and mod authors | Linux desktop testers, especially large modpacks |
| Platforms | Windows, macOS, Linux | Linux desktop focus; no Android/mobile work |
| Release delivery | Tagged 4.5.2 player release | [Unofficial experimental Linux alpha 2 with graphical and terminal installers](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2) |
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

### Current 4.5.2 whole-workload comparison

The current controlled comparison ran on one Linux workstation (AMD Ryzen Threadripper 2920X,
62.8 GiB reported memory, kernel 6.18.33-1-MANJARO) with 132 loaded code mods, 176 loaded content
packs, one expected skip, and an authorized private save. Official and fork processes shared the
same vanilla game assembly, .NET 6.0.32 runtime, mod/save trees, configured controls, 1280×720 Xvfb
session, llvmpipe renderer, null audio backend, launch wrapper, six selected logical CPUs, 60-second
warm-up, and scripted save/warp scenario. Tiered compilation was disabled identically to avoid a
reproducible native .NET 6 JIT crash in this workload.

Five fixed-order A/B pairs ran official A then fork B, with at least 180 seconds of measured steady
gameplay in every separate process. Values below are the median across the five per-run statistics.

| Metric | Official SMAPI 4.5.2 | Fork, diagnostics disabled |
| --- | ---: | ---: |
| Mean update elapsed duration | 14.596 ms | 7.228 ms |
| p50 update | 11.799 ms | 4.632 ms |
| p95 update | 26.265 ms | 18.546 ms |
| p99 update | 35.659 ms | 26.681 ms |
| Maximum update | 548.410 ms | 100.893 ms |
| Mean framework envelope | 10.422 ms | 3.348 ms |
| Main-thread allocation/update | 1,384.6 KiB | 887.6 KiB |
| Process allocation/update | 1,432.0 KiB | 905.7 KiB |
| Updates over 16.667 ms | 22.27% | 14.93% |
| Updates over 33.333 ms | 1.61% | 0.20% |
| Updates over 50 ms | 0.183% | 0.009% |
| Game launched to save loaded | 165.570 s | 129.805 s |

The framework envelope is outer-update elapsed duration minus the measured base-game update window.
It includes descheduling, identical probe overhead, and observed mod callbacks dispatched outside
that window; it is not pure SMAPI CPU time or causal attribution to the fork changes.

Mean update time was lower in all five pairs; paired differences ranged from −53.9% to −46.1%
(mean −49.8%). In the separate diagnostics comparison, enabled-vs-disabled paired mean-update
overhead ranged from 1.3% to 8.3% (mean 4.0%), with negligible allocation change in these captures.

<div class="notice warning" markdown="1">
**Limits:** these are descriptive one-machine results, not universal FPS, CPU-use, power, or latency
claims. A always preceded B, so product and within-pair cache/order effects are confounded,
especially for save loading. Xvfb/llvmpipe draw cadence is not desktop FPS, and accumulated
update-plus-draw values are elapsed duration spent in measured update/draw calls per draw interval
rather than frame latency. Selected-core busy time was higher for the fork in all five pairs;
process Gen1 collections were 2–5 higher, and Farm-observed warp timing was slower in four pairs.
Those last two signals lack pause-duration or stable transition evidence and are not classified as
confirmed regressions.
</div>

The repository retains the [human-readable summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.md),
[machine-readable summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.json),
and [complete sanitized result bundle](https://github.com/4eh5xitv6787h645ebv/SMAPI/tree/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results)
with raw numeric JSONL, environment/runtime provenance, exact commits, calculation method, and
run-to-run variation. The commit-pinned [benchmark harness directory](https://github.com/4eh5xitv6787h645ebv/SMAPI/tree/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world)
contains the scripts. The private modpack and save are not included.

### Historical whole-workload comparison

The direct end-to-end comparison was contributed with
[PR #158](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/158). It used a Framework Laptop 13
(Intel i5-1340P, Iris Xe, 64 GB RAM), Arch Linux, native Stardew Valley under gamescope at 3440×1440,
308 mods (129 code mods and 179 content packs), and an authorized mid/late-game private save. Each
steady-state sample lasted 180 seconds, with external instrumentation around `SGame.Update`,
`Game1.Update`, and mod handlers.

| Metric | Stock SMAPI 4.5.1 | Tested fork build | Difference |
| --- | ---: | ---: | ---: |
| Mean SMAPI framework overhead per tick | 5.892 ms | ~0.149 ms | **−97.5%** |
| p95 SMAPI framework overhead per tick | 7.201 ms | 0.196 ms | **−97.3%** |
| Allocation per tick | 4,787 KB | 959 KB | **−80.0%** |
| `SGame.Update` + `Draw` per frame | 14.696 ms | 6.785 ms | **−53.8%** |

<div class="notice" markdown="1">
**How to read this table:** it is evidence for that exact historical setup, not the current
4.5.2-vs-4.5.2 benchmark above. The save, mod set, and sample duration were controlled, but the
result came from one machine and no confidence intervals were recorded.
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
- The historical table uses stock 4.5.1; only the current table compares official 4.5.2 with the
  exact fork benchmark commit.
- SMAPI-owned timing cannot fully attribute Harmony patches, direct calls, GPU work, native work,
  background threads, scheduling, or I/O outside its boundaries.
- Some optimizations trade memory for reuse through explicit bounded caches; the audit documents
  their caps and invalidation rules.

## Compatibility and risk

The historical benchmark build identified as 4.5.2 so it could be compared directly with official
4.5.2. The public alpha instead uses the distinct prerelease identity
`4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2`. The fork is designed around existing SMAPI
mods, but it changes many internal hot paths and does not publish a stable release. Keep backups,
evaluate it against a disposable game/save copy, and reproduce problems on official SMAPI before
reporting an upstream regression.

<div class="button-row">
  <a class="button primary" href="getting-started.html">Evaluate the fork safely</a>
  <a class="button" href="https://github.com/Pathoschild/SMAPI/releases/tag/4.5.2">Get official SMAPI</a>
</div>
