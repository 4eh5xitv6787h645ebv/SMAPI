# Linux 4.5.2 A/B benchmark results

These are descriptive one-machine results for one pinned private workload, not universal FPS claims. Each value below is the median of five separate full-duration processes unless stated otherwise.

## Update and draw timing

| Metric | Official 4.5.2 | Fork, diagnostics disabled |
| --- | ---: | ---: |
| Mean update | 14.596 ms | 7.228 ms |
| p50 update | 11.799 ms | 4.632 ms |
| p95 update | 26.265 ms | 18.546 ms |
| p99 update | 35.659 ms | 26.681 ms |
| Maximum update | 548.410 ms | 100.893 ms |
| Mean framework envelope | 10.422 ms | 3.348 ms |
| Accumulated measured update+draw elapsed duration per draw interval, mean | 232.280 ms | 39.843 ms |

Fork mean update time was lower in 5 of 5 paired runs; paired differences ranged from -53.9% to -46.1% (mean -49.8%). The framework envelope includes identical probe overhead and observed mod callbacks dispatched outside the base-game window.

## Allocation, GC, and slow updates

| Metric | Official 4.5.2 | Fork, diagnostics disabled |
| --- | ---: | ---: |
| Main-thread allocation/update | 1384.6 KiB | 887.6 KiB |
| Process allocation/update | 1432.0 KiB | 905.7 KiB |
| Process GC0 collections/180 s | 3686 | 2382 |
| Process GC1 collections/180 s | 72 | 76 |
| Process GC2 collections/180 s | 2 | 2 |
| Updates over 16.667 ms | 22.27% | 14.93% |
| Updates over 33.333 ms | 1.61% | 0.20% |
| Updates over 50 ms | 0.183% | 0.009% |

GC counts are process-wide correlations, not attribution to SMAPI or a mod.

## Startup, save loading, and transitions

| Boundary | Official 4.5.2 | Fork, diagnostics disabled |
| --- | ---: | ---: |
| Probe entry to game launched | 7.109 s | 6.717 s |
| Game launched to save loaded | 165.570 s | 129.805 s |
| Town warp observed | 184.2 ms | 178.0 ms |
| Town warp settled | 5727.4 ms | 5083.8 ms |
| Farm warp observed | 153.0 ms | 178.8 ms |
| Farm warp settled | 5511.7 ms | 5028.1 ms |

The fork loaded the save faster in 5 of 5 fixed-order pairs, but A always preceded B, so the magnitude cannot be separated from order and cache warming. Individual observed warp boundaries were noisy; Farm-observed timing was slower for the fork in 4 of 5 pairs. Settled durations and full per-run ranges are retained in `summary.json`.

## Diagnostic overhead

| Metric | Disabled control | Enabled |
| --- | ---: | ---: |
| Mean update | 7.251 ms | 7.432 ms |
| p95 update | 18.507 ms | 18.769 ms |
| Main-thread allocation/update | 883.7 KiB | 886.3 KiB |

Paired mean-update overhead ranged from 1.3% to 8.3% (mean 4.0%).

## Host CPU and headless draw cadence

| Metric | Official 4.5.2 | Fork, diagnostics disabled |
| --- | ---: | ---: |
| Selected-core mean busy time | 37.2% | 47.0% |
| Headless steady draws/second | 4.19 | 22.20 |

Selected-core busy time was higher for the fork in 5 of 5 pairs and coincided with the much higher Xvfb/llvmpipe draw cadence. These captures do not support claims of lower CPU use, lower power, general efficiency, or desktop FPS.

## Variation and limitations

Median-run mean-update CV was 0.065 for official and 0.019 for the fork. Steady draw counts ranged from 473–1144 official and 3802–4072 fork.

- One shared Linux workstation and one private workload; results are not universal FPS claims.
- The framework envelope includes mod callbacks dispatched outside the base-game window and identical probe overhead.
- GC counts are process-wide correlations, not attribution; coincident counts cover only outer updates.
- SMAPI startup log boundaries have one-second timestamp resolution.
- Shared-host load and filesystem caches cannot be eliminated completely; per-run variation is retained.
- Probe startup timing begins at mod entry and cannot observe native launcher/runtime work before that boundary.
- Tiered compilation is disabled to avoid a reproducible .NET 6 JIT crash in this workload, so results do not represent the default tiered-runtime configuration.
- A null audio backend is used consistently because the isolated Xvfb session has no audio device.
- Draw cadence and update-and-draw distributions were measured under Xvfb with llvmpipe software rendering; they are renderer diagnostics, not desktop FPS. Official steady draw counts varied from 473 to 1,144 per run.
- At the 300-draw acceptance floor, draw p99 is supported by only roughly the worst three observations and is less stable than update p99.
- Chosen-core mean busy time was higher for the fork in 5 of 5 main pairs and headless draw cadence was much higher; these captures do not show lower CPU use, lower power, or general efficiency. Busy time includes llvmpipe and other host work.
- Every main pair ran official A before fork B. Product is therefore confounded with within-pair order and filesystem/cache warming, especially for save-load timing; the observed magnitude is evidence only for this fixed-order session.
- Fork process-wide Gen1 collections were 2–5 higher across the main pairs, while Farm warp-observed timing was slower in 4 of 5 pairs; GC pause duration was not measured and warp observations were noisy, so neither is classified as a confirmed regression.

See `summary.json` for every per-run distribution, cross-run variation, paired difference, allocation/GC count, slow-update count, transition, environment field, exact commit, and calculation method. The `raw/` files retain the sanitized numeric records.
