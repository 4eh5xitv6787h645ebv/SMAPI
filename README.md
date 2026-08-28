# SMAPI — Linux Performance Fork

[![Documentation](https://img.shields.io/badge/docs-live-5b8def?style=for-the-badge)](https://4eh5xitv6787h645ebv.github.io/SMAPI/)
[![Upstream](https://img.shields.io/badge/upstream-SMAPI_4.5.2-43a047?style=for-the-badge)](https://github.com/Pathoschild/SMAPI)
![Platform](https://img.shields.io/badge/focus-Linux_desktop-f0a202?style=for-the-badge)
![Status](https://img.shields.io/badge/status-development_preview-c44536?style=for-the-badge)

An unofficial Linux desktop fork of [SMAPI](https://github.com/Pathoschild/SMAPI), tuned for large
Stardew Valley mod collections. It keeps SMAPI's familiar mod-loading and API surface while reducing
avoidable framework work and adding private, local tools for finding slow or unhealthy mods.

> [!IMPORTANT]
> This is a development fork, not an official SMAPI release. It currently has no tagged player
> download. Most players should install [official SMAPI](https://smapi.io/) unless they specifically
> want to evaluate this fork on Linux and are comfortable building and testing development code.

## At a glance

| | Official SMAPI | This fork |
| --- | --- | --- |
| Best for | Most players and mod authors | Linux players testing very large mod sets |
| Platforms | Official Windows, macOS, and Linux support | Linux desktop is the tested project focus |
| Current base | SMAPI 4.5.2 | SMAPI 4.5.2 plus Linux-focused changes |
| Player diagnostics | Console and standard log | Standard tools plus `health` and `performance` reports |
| Releases | Published tagged releases | No tagged release yet |
| Support | Official SMAPI community | This repository's issue tracker |

The fork audit currently tracks **95 performance and correctness findings**. Its main themes are
incremental world/inventory tracking, lower-allocation input and event paths, faster asset routing,
less repeated map work, bounded performance diagnostics, and a private in-game Mod Health Report.

## Measured comparison

A current same-session Linux comparison used official SMAPI 4.5.2 at
[`79f9bbbe`](https://github.com/Pathoschild/SMAPI/commit/79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0)
and this fork at [`3c98eadd`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/3c98eadd2bddc24d43c889afb11b155e92469882).
It ran five fixed-order A/B pairs with the same 132 loaded code mods, 176 loaded content packs,
private save, game build, resolution, configuration, wrapper, warm-up, and scenario. Every process
contributed at least 180 seconds of steady-state gameplay.

| Median-of-run metric | Official 4.5.2 | Fork, diagnostics disabled | Observed difference |
| --- | ---: | ---: | ---: |
| Mean update elapsed duration | 14.596 ms | 7.228 ms | **50.5% lower** |
| p95 update elapsed duration | 26.265 ms | 18.546 ms | **29.4% lower** |
| p99 update elapsed duration | 35.659 ms | 26.681 ms | **25.2% lower** |
| Mean framework envelope | 10.422 ms | 3.348 ms | **67.9% lower** |
| Main-thread allocation/update | 1,384.6 KiB | 887.6 KiB | **35.9% lower** |

The framework envelope is outer-update elapsed duration minus the measured base-game update window.
It includes descheduling, identical probe overhead, and observed mod callbacks dispatched outside
that window; it is not pure SMAPI CPU time or causal attribution to the fork changes.

Mean update time was lower in all five pairs; paired differences ranged from −53.9% to −46.1%.
These are descriptive results from one workstation and one authorized private workload—not a
universal FPS, CPU-use, power, or latency promise. A always preceded B, tiered compilation was
disabled, audio used a null backend, and Xvfb used llvmpipe software rendering. Fork selected-core
busy time was higher in every pair, so the captures do not establish general efficiency. See the
[full comparison and methodology](https://4eh5xitv6787h645ebv.github.io/SMAPI/upstream-comparison.html)
or [benchmark summary](benchmarks/linux-real-world/results/summary.md) for distributions, run-to-run
variation, adverse signals, diagnostic overhead, and calculation details. The commit-pinned
[sanitized result bundle](https://github.com/4eh5xitv6787h645ebv/SMAPI/tree/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results)
contains the numeric raw JSONL and machine-readable summary.

## What this fork adds

### Lower overhead for large mod sets

- Event-driven chest, inventory, skill, and location tracking replaces repeated full-world polling.
- Content invalidation and propagation batch or index repeated work.
- TMX parsing, tile conversion, image patches, JSON loading, and path routing avoid known duplicate
  CPU work and temporary allocation.
- Assembly parsing and compatibility-rewrite results are reused when safe.

### Mod Health Report

Use `health start`, reproduce a problem, then use `health stop` and `health view`. The local report
organizes load failures, repeated errors, timing evidence, and data limitations without uploading
anything.

[![The private in-game Mod Health Report viewer.](docs/screenshots/mod-health-report-overview.png)](https://4eh5xitv6787h645ebv.github.io/SMAPI/mod-health-report.html)

[Open the illustrated Mod Health Report guide →](https://4eh5xitv6787h645ebv.github.io/SMAPI/mod-health-report.html)

### Opt-in performance diagnostics

Run `performance start`, reproduce a slowdown, and then run `performance stop`. The bounded report
ranks work observed at SMAPI-managed callbacks and separates game update time, observed callbacks,
SMAPI-owned update dispatch, and unattributed residual time. Profiling is disabled by default.

## Start here

- [Read the new documentation site](https://4eh5xitv6787h645ebv.github.io/SMAPI/)
- [Compare official SMAPI and this fork](https://4eh5xitv6787h645ebv.github.io/SMAPI/upstream-comparison.html)
- [Evaluate the fork safely](https://4eh5xitv6787h645ebv.github.io/SMAPI/getting-started.html)
- [Review the full performance audit](docs/technical/linux-large-mod-performance-audit.md)
- [Report a fork issue](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues)

For normal installation help, modding documentation, compatibility information, and community
support, use the [official SMAPI website](https://smapi.io/) and
[player guide](https://stardewvalleywiki.com/Modding:Player_Guide).

## Project scope

This repository focuses on native Linux desktop SMAPI. Android/mobile is out of scope. The fork is
derived from upstream SMAPI and remains licensed under the terms in [LICENSE.txt](LICENSE.txt).
SMAPI and Stardew Valley belong to their respective authors; this repository is not affiliated with
ConcernedApe or the official SMAPI maintainers.
