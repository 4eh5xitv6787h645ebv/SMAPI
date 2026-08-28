---
layout: default
title: Faster, observable SMAPI for Linux
description: An evidence-led Linux desktop fork for large Stardew Valley mod collections.
kicker: Unofficial SMAPI development fork
---

<div class="notice warning" markdown="1">
**Development preview.** This fork has no tagged player release yet. If you want the supported,
stable mod loader, install [official SMAPI](https://smapi.io/). This site documents the fork for
Linux users and contributors evaluating its changes.
</div>

<div class="metric-grid">
  <div class="metric-card">
    <span class="metric-value">97.5%</span>
    <span class="metric-label">lower mean framework overhead</span>
    <span class="metric-note">one historical 308-mod A/B</span>
  </div>
  <div class="metric-card">
    <span class="metric-value">80.0%</span>
    <span class="metric-label">lower allocation per tick</span>
    <span class="metric-note">same machine, save, and sample length</span>
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

The headline comparison is a 180-second steady-state measurement on an Arch Linux Framework Laptop
13 with 308 mods and the Blossom save. Stock SMAPI 4.5.1 measured 5.892 ms mean framework overhead
per tick; the tested fork build measured approximately 0.149 ms. Allocation fell from 4,787 KB to
959 KB per tick.

Those values are a historical workload result, not a universal FPS claim or a fresh 4.5.2 release
comparison. The detailed page shows the complete numbers and clearly separates whole-game evidence
from isolated path benchmarks.

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
</div>

## Project boundaries

- Linux desktop is the fork's supported development focus; Android/mobile is excluded.
- Diagnostic collection is bounded, opt-in where it adds overhead, and local-only.
- A timing identifies where SMAPI observed elapsed time; it does not automatically prove root cause.
- Official SMAPI remains the recommended choice for general players and cross-platform support.
