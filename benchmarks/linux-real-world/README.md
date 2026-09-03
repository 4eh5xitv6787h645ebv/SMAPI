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

## Exact candidate-package trusted-workload smoke

`qualify_candidate.py` applies the same archive profiles, immutable tree manifests, benchmark probe
acceptance, startup/workload-identity checks, 180-second steady-state scenario, transitions, and
isolated Xvfb/bubblewrap runner to one already-built Linux workflow candidate. It does not discover
or download any fixture, accept a live game/save path, or prepare a build from source. Every private
input path is mandatory. Private files must be current-user-owned, single-link ordinary files with
no group/world permissions; the prepared root and the output parent must be current-user-owned
mode-0700 directories. The output must be a new child outside the repository and known live
Steam/save roots.

Run it as the normal user after the exact merge-commit workflow candidate has passed the ordinary
package gates:

```bash
python3 benchmarks/linux-real-world/qualify_candidate.py \
  --candidate-zip "$candidate_zip" \
  --release-version "$release_version" \
  --release-commit "$release_commit" \
  --prepared-root "$private_prepared_root" \
  --workload-baseline "$private_workload_baseline" \
  --modpack-archive "$private_modpack_archive" \
  --save-archive "$private_save_archive" \
  --output-root "$new_private_output" \
  --cpu-list "$isolated_cpu_list"
```

The prepared root is the unchanged Phase 1 benchmark root containing `metadata.json` and the four
gold trees. The separate baseline must be its accepted official-preflight workload identity. The
adapter re-audits both original archives, verifies the prepared harness hashes and four trees,
copies the candidate through a no-follow descriptor, runs the structural package gate, installs it
into a clone of the prepared game, and verifies both installed SMAPI assemblies byte-for-byte
against `install.dat`. It then runs one diagnostics-disabled candidate sample in new game, Mods,
save, home, XDG, runtime, and temporary roots. The original archives, prepared root, metadata, and
baseline are rechecked after the run.

Successful stdout is exactly one JSON object with an explicit aggregate allowlist: public release
identity and candidate digest; `installedSmapiAssembliesMatched` and workload-identity match
booleans; public game version; aggregate loaded/skipped counts; steady/transition duration and sample counts; invalid
state counters; overflow/normal-exit facts; and verified-source counts. It never includes input or
output paths, the private workload fingerprint, save/mod identities, raw logs, configurations, or
timestamps. Failure stderr is only `schema`, `result`, and one fixed path-free code. Detailed package,
archive, installer, Xvfb, driver, console, probe, and SMAPI evidence remains mode-private below the
caller-owned output root and must never be committed or uploaded.

Before any shell package checker runs, the adapter bounds the candidate's compressed file size,
entry count, individual and total expanded sizes, and aggregate compression ratio; rejects unsafe
paths, entry types, encryption, and unsupported compression; and requires the expected Linux
package profile. All child stdout/stderr, including clone failures, stays in mode-`0600` private
logs. Interrupting the adapter terminates and reaps active process groups and Xvfb, then emits only
the fixed path-free `interrupted` failure object.

This is one trusted smoke sample, not an A/B performance comparison, universal performance claim,
public-artifact attestation, GUI lifecycle test, or proof that the supplied `--release-commit` was
the candidate's source commit. Establish that association from the workflow run and its provenance
before invoking this adapter; repeat public checksum/provenance and graphical/manual install,
update, repair, backup, rollback, and uninstall qualification against the six published assets
separately.
