# Linux Mod Health Report validation record

This record captures reproducible evidence for the Linux desktop Mod Health Report. It complements the contract in `mod-health-report-plan.md`; it does not extend support to Android/mobile or address the unrelated .NET 10 menu-click issue.

## Reviewed prerequisites

- Baseline: `origin/develop` at `7cb06cfd6` (bounded diagnostics from issue #154 / PR #155).
- Issue #156 and PR #157 were still open when reviewed. The pinned PR #157 head `66b806b6ab702ba0008ddf72ea01c9b1d3adcd5a` was independently reviewed; its safe base-game/observed-mod/SMAPI-other update split and GC collection signals were incorporated and hardened in this branch without adding another collector.
- Fixture PR #158 was still open at pinned head `599c8b786215c7cfa5bf395fa4b726c0d1c61805` and was used as an external test workspace only.
- Feature issue: #159.

## Fixture integrity and isolation

The user-authorized fixture was kept outside the repository and live game/save trees. The checked-in `tools/fixture_archive_audit.py` utility independently revalidated the actual files with these results:

| Fixture | Compressed bytes | SHA-256 | Entries | Files | Directories | Expanded bytes | Largest file | Max depth |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `Blossom_389524656.tar.xz` | 1,291,524 | `6f707c73d02a05eed42adee0d9d9b434e92dff84a841e2f00047456921ae4bca` | 6 | 4 | 2 | 84,028,043 | 82,522,715 | 2 |
| `Mods-2026-08-26.tar.zst` | 746,198,040 | `337d157bb2cf7283eaf6796c259a21d6e2e71baf134cb67a1f2a655e31cb312c` | 25,226 | 21,984 | 3,242 | 1,018,793,776 | 20,696,901 | 10 |

The audit accepts only regular files and directories, rejects path traversal, links, devices, FIFOs, sparse/unsupported entries and realized escapes, ignores archive ownership/modes, and extracts with private modes only. Its synthetic suite covers missing, hash/size-mismatched, malformed, oversized, unsafe and inventory-mismatched inputs. The actual `mods.json` projection retained only its support allowlist and contained no `folder`, author, update-key, Blossom, or absolute-home values.

Disposable roots used for runtime work (local absolute paths are intentionally not published):

- packaged game: `$DISPOSABLE_GAME`;
- isolated home/XDG/save state: `$ISOLATED_HOME` and `$ISOLATED_XDG_RUNTIME`;
- PR workspace: `/tmp/smapi-pr158-worktree`;
- archive staging: `/tmp/smapi-pr158-modpack.N54ljI`.

The source game, source archives, live Mods directory, and live saves were hash/tree checked and not modified. The fixture and purpose-built probe are not committed, mirrored, re-uploaded, or included in build artifacts.

## Parent-versus-feature overhead evidence

An uncommitted evidence-only NUnit harness raised the same warmed `ManagedEvent<EventArgs>` with 0, 1, 10 and 100 listeners against clean `origin/develop` and this feature. Both used Release/net10, `DOTNET_TieredCompilation=0`, identical callback bodies, three alternating runs, 100,000 measured raises (20,000 for 100 listeners), and `GC.GetAllocatedBytesForCurrentThread`. The table contains medians; stopwatch ticks are intentionally kept raw because shared-machine wall time is informational.

| Tracking | Listeners | Parent ticks | Feature ticks | Parent bytes | Feature bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| disabled | 0 | 244,286 | 247,922 | 0 | 0 |
| disabled | 1 | 2,685,000 | 2,754,271 | 0 | 0 |
| disabled | 10 | 20,001,594 | 21,157,627 | 0 | 0 |
| disabled | 100 | 36,700,089 | 36,172,465 | 0 | 0 |
| enabled | 0 | 239,887 | 247,552 | 0 | 0 |
| enabled | 1 | 580,905,363 | 673,275,519 | 122,322,824 | 122,324,120 |
| enabled | 10 | 5,635,472,779 | 6,475,705,376 | 1,202,314,296 | 1,202,320,216 |
| enabled | 100 | 11,977,882,888 | 13,510,607,789 | 2,420,357,256 | 2,420,360,272 |

The disabled dispatch path remained allocation-free at every listener count. Enabled allocations were effectively unchanged; the richer opt-in collector added roughly 13–16% wall time in listener-bearing cases in this microbenchmark. This is opt-in diagnostic overhead, not a whole-game or frame-time claim. Permanent allocation gates additionally cover disabled handler/tick boundaries, ordinary log counters, concurrent Trace/Info counting, and bounded maximum-capacity report generation.

## Preliminary packaged runtime evidence

The first packaged net6 corpus run used Xvfb/PTTY input, `de_DE.UTF-8`, the complete pinned Mods archive, the Blossom save, and an external probe covering slow/nested/failing/log-heavy/command/content/background callbacks plus an unobserved Harmony stall. Stardew loaded the `Apple Blossom` save normally and reported 132 code mods plus 176 content packs (308 loaded entries). Event Limiter also generated synthetic metadata observations, so the session ledger correctly recorded 436 total discovered identities rather than treating the console's loaded count as the discovery total.

The interim report froze at 2,233 updates and the final report at 5,746 updates. An early 91 ms update remained in the worst-update list beyond the 600-update recent ring. Both JSON reports validated against schema v1 and each text/JSON payload remained below five MiB. The report directory was mode `0700`; payloads and completion markers were `0600`.

Privacy inspection found none of the injected save/farm/player, path, IP, token, configuration, repository, or private-mod identity canaries in either format. The report retained the probe's structured callback failures, log-flood evidence, process-wide GC correlation, and unattributed stall wording without attributing the unobserved stall to the probe.

This first run ended through a window close and produced an X11 `BadDrawable`, so it is preliminary evidence only for normal shutdown. Final validation repeats the workflow with the post-review build, explicit normal game exit, on-behalf-of content-pack placement, and both Linux desktop hosts.

## Final verification

Final test/build/package/runtime commands and GitHub/merge evidence are recorded here after the post-review runtime matrix completes.
