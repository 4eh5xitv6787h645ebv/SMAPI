---
layout: default
title: Faster, observable SMAPI for Linux
description: An evidence-led Linux desktop fork for large Stardew Valley mod collections.
kicker: Unofficial SMAPI development fork
---

<div class="notice warning" markdown="1">
**Experimental prerelease.** This fork now publishes an
[unofficial Linux x86_64 alpha](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1).
If you want the supported stable mod loader, install [official SMAPI](https://smapi.io/). Use the
alpha only if you accept prerelease risk and can follow the documented backup and rollback steps.
</div>

<div class="metric-grid">
  <div class="metric-card">
    <span class="metric-value">49.8%</span>
    <span class="metric-label">lower paired mean update time</span>
    <span class="metric-note">mean difference across five 4.5.2 pairs</span>
  </div>
  <div class="metric-card">
    <span class="metric-value">35.9%</span>
    <span class="metric-label">lower allocation per update</span>
    <span class="metric-note">median of five runs on one machine</span>
  </div>
  <div class="metric-card">
    <span class="metric-value">95</span>
    <span class="metric-label">audited findings</span>
    <span class="metric-note">performance and correctness</span>
  </div>
</div>

The fork starts from SMAPI 4.5.2 and concentrates on framework work that becomes visible with
hundreds of mods: repeated world scans, content routing, map conversion, logging, event allocation,
and troubleshooting blind spots. It aims to preserve normal SMAPI mod compatibility while making
Linux performance easier to measure and explain.

<div class="button-row">
  <a class="button primary" href="technical/linux-alpha-release.html">Get the Linux alpha</a>
  <a class="button primary" href="upstream-comparison.html">Compare with official SMAPI</a>
  <a class="button" href="getting-started.html">Evaluate the fork</a>
</div>

## Choose the right SMAPI

| If you need… | Choose… | Why |
| --- | --- | --- |
| A supported installer and normal updates | [Official SMAPI](https://smapi.io/) | It is the stable, cross-platform release maintained for the community. |
| Linux testing with a very large mod list | This fork | It contains the optimization audit and diagnostic additions documented here. |
| Help using or creating ordinary mods | [Official documentation](https://smapi.io/docs) | The core API and community guidance live upstream. |
| Evidence about a specific slowdown | This fork's `health` and `performance` tools | They collect bounded local evidence at SMAPI-owned boundaries. |

## Measured performance

The current comparison ran official SMAPI 4.5.2 and the fork in five fixed-order A/B pairs on one
Linux workstation with the same 132 loaded code mods, 176 loaded content packs, and authorized
private save. Each separate process captured at least 180 seconds of steady gameplay. Median-of-run
mean update elapsed duration was 14.596 ms official and 7.228 ms fork; main-thread allocation per
update was 1,384.6 KiB and 887.6 KiB respectively.

Mean update time was lower in all five pairs, but this is descriptive evidence for that machine and
workload—not a universal FPS, CPU-use, power, or latency claim. A always ran before B, tiered
compilation was disabled, audio used a null backend, and rendering used Xvfb/llvmpipe. The fork's
selected-core busy time was higher in every pair. The detailed page retains full distributions,
run variation, diagnostic overhead, adverse signals, and the older historical comparison.

[Read the performance evidence and limitations →](upstream-comparison.html#performance-evidence)

## See what your mods are doing

The Mod Health Report is generated locally and opens inside Stardew Valley. It summarizes failed or
skipped mods, repeated errors, slow observed callbacks, capture quality, and the limits of what SMAPI
could attribute.

[![A Mod Health Report showing its privacy notice, eight report sections, status, and actions.](screenshots/mod-health-report-overview.png)](mod-health-report.html)

```text
health start
health mark
health stop
health view
```

[Open the illustrated guide →](mod-health-report.html)

## Documentation

<div class="doc-grid">
  <a class="doc-card" href="upstream-comparison.html">
    <strong>Upstream comparison</strong>
    <span>Features, benchmark tables, methodology, and limitations.</span>
  </a>
  <a class="doc-card" href="getting-started.html">
    <strong>Getting started</strong>
    <span>Choose a build, test safely, and collect useful evidence.</span>
  </a>
  <a class="doc-card" href="mod-health-report.html">
    <strong>Mod Health Report</strong>
    <span>Screenshot-led instructions for the private in-game report.</span>
  </a>
  <a class="doc-card" href="technical/linux-large-mod-performance-audit.html">
    <strong>Performance audit</strong>
    <span>All 95 findings, their risk, evidence, and implementation status.</span>
  </a>
  <a class="doc-card" href="technical/linux-gui-shell.html">
    <strong>Linux graphical installer</strong>
    <span>Review the unreleased production candidate, private diagnostics, troubleshooting, and retained terminal fallback.</span>
  </a>
  <a class="doc-card" href="technical/linux-gui-screenshot-evidence.html">
    <strong>Installer screenshot evidence plan</strong>
    <span>Review the pending capture matrix; production workflow screenshots are not available yet.</span>
  </a>
</div>

## Project boundaries

- Linux desktop is the fork's supported development focus; Android/mobile is excluded.
- Diagnostic collection is bounded, opt-in where it adds overhead, and local-only.
- A timing identifies where SMAPI observed elapsed time; it does not automatically prove root cause.
- Official SMAPI remains the recommended choice for general players and cross-platform support.
