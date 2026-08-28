---
layout: default
title: Getting started
description: Choose the right build, evaluate the fork safely, and collect useful Linux evidence.
kicker: Linux desktop evaluation guide
---

## Before you install anything

This fork is a development preview with no tagged player release. For an ordinary Stardew Valley
setup, use the [official SMAPI installer](https://smapi.io/). Continue here only if you want to test
the fork's Linux performance or diagnostics and are comfortable compiling development code.

<div class="notice warning" markdown="1">
**Protect your normal setup.** Keep save backups and use a disposable copy of the game, `Mods`
directory, and save root for comparisons. Do not replace your only working SMAPI installation just
to collect a benchmark.
</div>

## Get the source

```bash
git clone https://github.com/4eh5xitv6787h645ebv/SMAPI.git
cd SMAPI
git switch develop
```

The project is based on SMAPI 4.5.2. Building requires the same developer toolchain and game
references as upstream. Read the [compile-from-source documentation](technical/smapi.md#compile-from-source-code)
before building: a Debug rebuild can copy files into the detected game directory.

There is no supported prebuilt fork package at this snapshot. Do not download binaries claiming to
be this fork from unrelated mirrors.

## Establish a fair baseline

For a meaningful comparison, keep these constant:

- the same native Linux Stardew Valley build and game version;
- the same mod folders and configurations;
- the same save, location, weather, time, and in-game action;
- the same display resolution, compositor/session, and launch wrapper;
- the same warm-up time and measurement duration; and
- no background package updates, shader compilation, or unrelated heavy processes.

Compare official SMAPI first, then the fork, and alternate the order for repeated runs. Record raw
values rather than only the percentage difference.

## Use the Mod Health Report

Open the SMAPI console while the game is running:

```text
health start
```

Reproduce the problem. Enter `health mark` near the moment it happens, then finish with:

```text
health stop
health view
```

The report remains local. It contains mod names, IDs, versions, dependency IDs, callback identities,
and statuses, so inspect it before sharing. See the [screenshot guide](mod-health-report.md) for the
viewer, retry flow, keyboard/controller controls, and privacy boundaries.

## Measure observed callback time

For a focused performance sample:

```text
performance start
```

Reproduce the slowdown, then run:

```text
performance stop
```

To report individual update ticks which miss a 60 FPS frame budget:

```text
performance start 16.667
```

Performance tracking adds measurement overhead and is disabled by default. A slow observed callback
shows where time was measured, not necessarily the ultimate cause. Harmony patches, direct calls,
GPU work, and several native/background paths cannot be attributed to a mod by these boundaries.

## Report a useful fork issue

Include:

- fork commit (`git rev-parse HEAD`) and official SMAPI comparison version;
- Linux distribution, kernel, desktop session, CPU, GPU, and RAM;
- native Linux game version and launch wrapper;
- mod count and whether the issue reproduces with official SMAPI;
- exact reproduction steps and frequency;
- the normal SMAPI log; and
- a reviewed Mod Health Report when relevant.

Remove private information before posting. Open fork-specific reports in the
[fork issue tracker](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues). Use the
[official community](https://smapi.io/community) for general SMAPI or mod support.
