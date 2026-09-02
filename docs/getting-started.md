---
layout: default
title: Getting started
description: Choose the right build, evaluate the fork safely, and collect useful Linux evidence.
kicker: Linux desktop evaluation guide
---

## Before you install anything

This fork has an [unofficial experimental Linux x86_64 alpha](technical/linux-alpha-release.md).
For an ordinary Stardew Valley setup, use the [official SMAPI installer](https://smapi.io/).
Continue here only if you want to test the fork's Linux performance or diagnostics, accept
prerelease risk, and can keep verified backups.

<div class="notice warning" markdown="1">
**Protect your normal setup.** Keep save backups and use a disposable copy of the game, `Mods`
directory, and save root for comparisons. Do not replace your only working SMAPI installation just
to collect a benchmark.
</div>

## Get the alpha or source

Players should download only
[all six published alpha 2 assets—the installer and its checksum, metadata, and provenance companions](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2),
then follow the [verification, installation, upgrade, uninstall, and rollback guide](technical/linux-alpha-release.md).
Do not use unrelated mirrors.

After verifying all six files, use `install on Linux (graphical).sh` in an X11 or XWayland desktop
session. The same ZIP retains `install on Linux.sh` for terminal-only, headless, or troubleshooting
use. That console launcher is a narrower legacy install/uninstall path, not a text version of the
graphical transaction: it does not repeat release verification or provide reviewed plans, Repair,
Backup, authenticated Rollback, or interrupted-operation recovery. Headless use must invoke the
packaged apphost directly with the complete command in the release guide. Run either path as your
normal user, never with `sudo` or as root, and review unsanitized console output before sharing it.

Contributors who need a source build can clone `develop`:

```bash
git clone https://github.com/4eh5xitv6787h645ebv/SMAPI.git
cd SMAPI
git switch develop
```

The project is based on SMAPI 4.5.2. Building requires the same developer toolchain and game
references as upstream. Read the [compile-from-source documentation](technical/smapi.md#compile-from-source-code)
before building: a Debug rebuild can copy files into the detected game directory.

Source builds do not have the public package attestation and should not be presented as the tagged
alpha.

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
