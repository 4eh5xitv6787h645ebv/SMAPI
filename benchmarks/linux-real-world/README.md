# Linux real-world A/B benchmark

This harness compares exact SMAPI commits with the same native Linux game build, trusted external workload, configuration, probe, session, resolution, launch wrapper, warm-up, steady-state duration, and transition scenario. The private PR #158 modpack and save are never stored in this repository or emitted by the probe.

The common probe patches the same runtime boundaries in both products without modifying their assemblies. It captures outer SMAPI-runner updates, base-game update windows, outer draws, main-thread allocation deltas, process-wide allocation/GC phase totals, GC collections coincident with outer updates, and fixed scenario markers. Its bounded arrays are allocated before the save loads, and telemetry is written only during normal shutdown. “Framework envelope” means the outer update minus the base-game update window; it includes observed mod callbacks dispatched outside the base-game window and is not pure SMAPI CPU attribution. Harmony timers and probes add identical observer overhead to both builds, so absolute timings include that shared cost. Every accepted run contains at least 300 steady draw observations spanning the 180-second window and at least 10 transition draw observations spanning the scripted transition window, in addition to the stronger update-count gates.

Each accepted sample uses a fresh copy (using filesystem reflinks when supported) of immutable game, mod, save, HOME, and XDG inputs. The wrapper runs with an empty environment in a bubblewrap PID/network/IPC/UTS namespace. It exposes read-only `/usr` runtime files and font configuration, a private `/proc`, basic devices, the dedicated Xvfb socket, and the writable sample root; host identity data in `/etc` and `/sys`, host homes, the repository, live game data, user runtime sockets, and networking are unavailable. A null OpenAL backend avoids headless audio-device errors. The workload auto-loads its configured save, which the probe checks privately without emitting its identifier. After a 60-second warm-up, the probe records at least 180 wall-clock seconds of stationary, unpaused, menu-free gameplay and rejects state/location/position changes, then performs verified local-player warps to vanilla `Town` and `Farm` locations and exits without saving. The benchmark copy raises the workload's idle-pause timeout to one hour identically for all products so the capture cannot pause. Pre-launch hashes prove the exact probe config/manifest inputs; post-run validation allows SMAPI's numeric JSON formatting normalization but requires the exact config key/value semantics and unchanged manifest.

The minimum main sequence is five uninterrupted strict alternating samples per product (`A1, B1, …, A5, B5`; the starting side is preregistered). Diagnostics overhead then uses five separate fork-disabled controls paired with five fork-enabled samples; individual tick logging stays disabled and disabled/enabled order alternates between pairs. The pinned fixture contains exactly one content pack which SMAPI skips because its dependency is absent. Official preflight A privately establishes a SHA-256 signature over normalized loaded-code, loaded-content-pack, and skipped-item log entries; preflight B and every final sample must match that exact identity and count. Names, reasons, and the signature remain private, while sanitized results report only counts and `identityMatched: true`. A run also fails on a wrong tree/file hash, incomplete workload identity, wrong save/state, missing or reordered marker, invalid timing partition, buffer overflow, insufficient measured duration, wrong resolution, or abnormal exit. Gold input manifests are verified before and after the suite.

Each suite must complete in one runner invocation; resuming would create a new Xvfb/runtime session and invalidate the common-session claim. Every accepted sample pins its suite environment/session metadata digest. If a final run fails, archive the incomplete private `runs` and `diagnostic-runs` directories together with `environment.json`, then restart the entire final sequence. Preflight uses separate `preflight-runs`, `preflight-environment.json`, `preflight-plan.json`, and `preflight-workload-identity.json` files; archive every one which exists before restarting an interrupted disposable pair. Analysis revalidates preflight as a prerequisite, but never includes its measurements or private workload signature in published results.

Private preparation example:

```bash
python3 benchmarks/linux-real-world/prepare.py \
  --repo "$PWD" \
  --private-root /outside/repository/private-benchmark \
  --game-source /outside/repository/clean-native-game \
  --modpack-archive /outside/repository/Mods-fixture.tar.zst \
  --save-archive /outside/repository/Save-fixture.tar.xz \
  --fork-commit COMMIT
```

Run the preregistered sequence:

```bash
python3 benchmarks/linux-real-world/run_ab.py \
  --private-root /outside/repository/private-benchmark \
  --samples 5 \
  --start a \
  --cpu-list 7,8,9,19,20,21
```

Before the final sequence, run one full-duration sample per product in a separate namespace which analysis never consumes:

```bash
python3 benchmarks/linux-real-world/run_ab.py \
  --private-root /outside/repository/private-benchmark \
  --preflight \
  --cpu-list 7,8,9,19,20,21
```

The scripts reject private sources or outputs which overlap the repository, detected Steam game trees, or the live Stardew save/config directory. Preparation verifies both fixture archives and extracts them itself through the containment auditor; caller-supplied extracted trees are never accepted. Deterministic gold tree manifests are then verified before and after the suite. Raw consoles, SMAPI logs, configs, fixture trees, and other private runtime outputs remain in the private root. Only `analyze.py`'s numeric, allowlisted output is eligible for `results/`; generation fails if private path/save canaries appear. After analysis, run `verify_runtime.py --private-root <private-root> --output benchmarks/linux-real-world/results/runtime-provenance.json`, then `publish_results.py --results benchmarks/linux-real-world/results`. The verifier hashes the actual post-suite A/B gold runtimes; the publisher strictly validates every raw record, adds complete cross-run and paired variation, removes private fixture fingerprints, and renders the human-readable summary using only validated public data.

Both product assemblies run on the native game's .NET 6 runtime through the same byte-identical official 4.5.2 apphost and game-derived runtime-deps file, copied and hashed during preparation. This also completes official SMAPI's required first-launch deps update before any measured process. Tiered compilation is disabled identically because the complete workload triggers a reproducible native .NET 6 JIT crash during mod entry when it is enabled; that wrapper setting is recorded as a limitation and is not a claim about default-launch performance. The outer Python/taskset/bubblewrap wrapper and arguments are otherwise identical, so the fork dispatcher isn't part of this code-path comparison. SMAPI log startup boundaries have one-second resolution, while probe-entry-to-game-launch and game-launch-to-save-load boundaries use the monotonic high-resolution clock. The probe cannot observe native launcher/runtime work before mod entry. The selected config keys common to both commits are canonicalized to official values and hashed; fork-only diagnostics keys are disabled except in explicitly labeled enabled samples.

One-machine results are descriptive evidence for this hardware and workload. They are not universal FPS claims. Official preflight calibration on the headless llvmpipe renderer produced 643 steady and 32 transition draw records while completing 10,589 steady and 605 transition updates; this established the 300/10 draw acceptance floor with headroom. Published draw cadence is a renderer diagnostic, not desktop FPS. At the 300-observation floor, draw p99 is supported by only roughly the worst three observations and is less stable than update p99. Filesystem caches cannot be globally reset safely, shared-host noise cannot be eliminated completely, and five pairs are too few for broad population claims; publish per-run distributions and variation rather than treating pooled ticks as independent machines.
