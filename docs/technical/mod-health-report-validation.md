# Linux Mod Health Report validation record

This record captures reproducible evidence for the Linux desktop Mod Health Report. It complements the contract in `mod-health-report-plan.md`; it does not extend support to Android/mobile or address the unrelated .NET 10 menu-click issue.

## Reviewed prerequisites

- Baseline: `origin/develop` at `7cb06cfd6` (bounded diagnostics from issue #154 / PR #155).
- Issue #156 and PR #157 were still open when the original Mod Health Report was reviewed. The pinned PR #157 head `66b806b6ab702ba0008ddf72ea01c9b1d3adcd5a` was independently reviewed; its safe base-game/observed-mod split and GC collection signals were incorporated and hardened without adding another collector. At that historical point, proposed SMAPI/other attribution was deliberately left as unavailable/residual because the report did not yet own a separate measurement boundary.
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

The post-review source under test was `bb954ce97` plus documentation-only evidence updates. A package preflight compared the built assembly with both host-specific package assemblies; stale host-specific copies found during a preliminary run were replaced before either final run. The final net6 and net10 assemblies all had SHA-256 `e9a53fead33101038f7b60e8bd44df156d9212bb58a5d355dd1a04cc4bf65b98`.

The repository gates passed:

- the full Release test run: 1,625 passed, three existing skips, zero failures;
- the Release solution build: zero errors and nine existing warnings;
- explicit publishes for both supported Linux desktop hosts;
- `LinuxRuntimeDispatcherTests`: four passed, zero failed;
- `dotnet format --verify-no-changes` restricted to modified files;
- `git diff --check` and the Android/mobile path exclusion check;
- the fixture tool's synthetic suite: 13 passed, zero failed.

Both final full-corpus runs used Xvfb with PTY command input, `de_DE.UTF-8`, fresh mode-`0700` home and XDG runtime roots, the pinned complete Mods release, the pinned Blossom save, the external probe, and a top-level probe content pack. Each host-specific assembly hash was checked immediately before launch. The final packaged console reported 132 code mods and 177 content packs, including the probe and its content pack. The report inventory therefore contained 309 loaded entries, plus 127 problem-first discovered identities, one skipped identity, and no ignored, invalid, or failed identities. This distinction is expected because the ledger records discovery observations which aren't part of the console's loaded-mod totals.

| Host / flow | Final reason | Final updates | Slow updates | JSON bytes | Text bytes | On-behalf-of callback rows |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| net6, manual `health start` / `health stop` | `user-stop` | 2,292 | 19 | 1,290,641 | 108,161 | 94 |
| net10, `EnableModHealthReportOnLaunch` / normal exit | `normal-shutdown` | 2,561 | 22 | 1,331,802 | 123,417 | 155 |

The net6 interim snapshot contained 1,168 updates, and the net10 interim snapshot contained 1,284. All four reports used the fixed 33.333 ms threshold, valid timing partitions, per-update/sample generation collection counts, reproduction marks, structured probe callback failures, and retained on-behalf-of content operations. Each retained the deliberate roughly 1.2-second unobserved stall as more than one second of base-game-exclusive time instead of attributing it to the probe. The net6 final sample exceeded the 600-update recent ring while preserving its earlier worst update.

Every JSON payload validated against the checked-in schema v1. Each completion marker named exactly its matching text and JSON pair; report IDs matched the marker stem and both payloads. The report directories were `0700`, all payloads/markers were `0600`, filenames used `report-` plus 16 hexadecimal characters, and every payload was below five MiB. Manual and automated inspection found none of the injected player/farm/save names, home/game/archive paths, IP address, token, private manifest fields, configuration values, update keys, or private repository identity canaries in either format. The generated findings consistently used observed/attribution-limited wording and directed detailed exception review to the normal SMAPI log.

A separate probe-only net6 PTY/Xvfb run exercised the real retry workflow. After a successful interim export, the disposable `HealthReports` directory was moved aside and replaced temporarily with a regular file. The next `health report` failed with an `IOException` without crashing or stopping capture. After restoring the original private directory, `health retry` exported the exact frozen 1,907-update snapshot even though status had advanced to 3,261 updates. Its report set `writeRetry` to `true`, passed the schema and privacy scans, and normal `health stop` / probe-driven game exit then completed successfully. A simple mode change could not induce this failure because the publisher correctly repaired the private directory mode before writing.

The disposable launch-time setting was restored to `false` after the net10 run. The source fixture, source game, and live user state remained unchanged. GitHub PR, review, merge, issue-closure, synchronization, and final branch-state evidence is added to the feature PR and verified after merge.

## Definitive post-final-review verification

The independent final-diff reviews found and corrected additional report-accuracy, text-safety, lifecycle, queue, API-timing, bounded-dominance, and omission-overflow issues after the earlier runs above. The definitive source build was commit `4237f0c907a12f0ea3af9f008659f6c6ba527d48`; its main assembly and both installed host-specific package copies had SHA-256 `d40a57c09260e9114065539fd3ff755458ad126d8f115abba1ee8f8d7bbaeb9f`. Documentation-only evidence commits after that source commit do not change the tested assembly.

The definitive repository gates passed:

- the full Release test run: 1,667 passed, three existing skips, zero failures;
- the Release solution build: zero errors and nine existing warnings;
- explicit publishes for both supported Linux desktop hosts;
- `LinuxRuntimeDispatcherTests`: four passed, zero failed;
- formatting verification restricted to all feature-modified C# files;
- `git diff --check`, the Android/mobile path exclusion check, and the fixture tool's 14-test synthetic suite.

Fresh private home/XDG roots were created for each full-corpus host. The pinned save was re-audited and independently extracted into each new root. Both runs used the unchanged complete trusted Mods fixture and probe from the disposable game, Xvfb/PTTY input, `de_DE.UTF-8`, and the exact package assembly above. The console again loaded 132 code mods and 177 content packs; each final report recorded 309 loaded entries, 127 problem-first discovered identities, one skipped identity, and no ignored, invalid, or failed identities. The Blossom save auto-loaded and reached normal gameplay in both hosts.

| Host / flow | Runtime | Final reason | Final updates | Slow updates | JSON bytes | Text bytes |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| net6, manual start/report/stop | .NET 6.0.32 x64 | `user-stop` | 1,973 | 21 | 1,356,084 | 110,303 |
| net10, launch-time capture/report/normal exit | .NET 10.0.11 x64 | `normal-shutdown` | 2,235 | 20 | 1,363,282 | 122,817 |

The net6 interim report froze at 568 updates; the net10 interim report froze at 1,036. Each final report retained 600 recent updates, 100 worst updates, valid per-update partitions, valid capture/update GC data, one reproduction mark, structured probe failures, and the deliberate unattributed stall. The reserved SMAPI/other bucket now explicitly reported unavailable at the capture and update levels because no separately owned measurement boundary was active; it was never presented as an observed zero.

All four full-corpus JSON payloads validated against the checked-in schema v1. Their completion markers exactly named the matching text/JSON files, report IDs matched their stems, directories were `0700`, all payloads and markers were `0600`, and every payload remained below five MiB. The final reports contained no injected player, farm, save, home/game/archive path, IP, token, configuration, update-key, private author/description, or repository-identity canary. The disposable launch-time setting was restored to `false` after net10 exited.

A fresh probe-only net6 run then revalidated the exact frozen retry workflow on the same final assembly. An initial interim export succeeded at 647 updates. The private report directory was moved aside and replaced with an intentional regular-file collision; a second export failed with `IOException`, published no incomplete pair, and did not stop capture. By the next status, live capture had advanced to 2,255 updates. After the private directory was restored, `health retry` published the exact frozen 1,750-update model with `writeRetry: true`; the subsequent user-stop report contained 3,614 updates. All three completed pairs passed schema, privacy, pairing, and `0700`/`0600` permission checks, and no temporary file remained.

The isolated fixture roots were the only runtime state changed. The source archives, disposable game's trusted Mods corpus, source game installation, live Mods/saves, and repository source state remained unchanged by the runs.

## Final GitHub and branch state

PR #160 merged into `develop` as `9c90f7402775aaedf1091aefe4809637a8d47e69` on 2026-08-26 after independent final reviews of its exact head found no remaining blockers. The merge closed feature issue #159. Issue #156 remains open because the report deliberately identifies the SMAPI/other timing boundary as unavailable until a separately owned measurement boundary exists. External PR #157 was closed as superseded by the reviewed and hardened implementation in PR #160; fixture-only PR #158 was closed after its trusted release and Blossom save had completed their isolated validation role. No pull request remains open in the repository.

GitHub confirmed `develop` as the default branch before cleanup. The 94 other branches owned by `origin` and the two other local branch refs were then deleted at the user's explicit request. A prune and direct remote query confirmed that both the local repository and `origin` retain only `develop`, with local `develop`, `origin/develop`, and the remote head all resolving to the merge commit above before this evidence-only follow-up. Detached fixture/review worktrees and the external `upstream` and `cinderbox` remotes were not changed; they do not create branches owned by this repository.

## Issue #156 follow-up contract

The runtime artifacts and hashes above predate the #156 follow-up and correctly retain `smapiOtherTimingAvailable: false`; they are historical evidence and are not rewritten as though they exercised the new boundary. The follow-up preserves schema version 1 and the existing `smapiOtherMilliseconds` / `smapiOtherTimingAvailable` field names while defining the category more narrowly as **SMAPI update dispatch observed outside the base-game update**. It is exclusive of observed callbacks within that boundary. It must never be described as total SMAPI CPU or as proof that SMAPI caused a delay, because elapsed time in the boundary can include waiting, scheduling, and unobserved nested work.

For a valid update whose owned dispatch boundary is available, base-game-exclusive time, observed callback time, exclusive SMAPI update-dispatch time, and residual time reconcile to the total. A valid measured zero remains distinguishable from unavailable through the boolean. If dispatch timing is unavailable for an update or for any part of an aggregate sample, the schema's SMAPI category is zero with availability false and the otherwise-separated dispatch time is folded back into residual so the four schema-v1 buckets still reconcile without presenting an unobserved zero. Invalid timing suppresses all partition totals and percentage findings.

Focused contract validation covers available nonzero timing, available zero timing, mixed/unavailable fallback, invalid partitions, cautious text and console labels, schema/example equivalence, and finding semantics for base-game-boundary, SMAPI-dispatch-boundary, and truly residual dominance. The earlier full-corpus runs do not establish packaged behavior for this follow-up; fresh supported-host runtime evidence must be recorded before treating #156 as fully validated.

## Issue #156 fresh packaged runtime verification

The exact post-fix source under test was `01c0e4953b504268c7b54e18f5575e8f0b0f0d73`. Its main Release assembly and both staged host-specific package copies had SHA-256 `0152b7f4ac782882e75858f81589a1088f343cf5f3e6d8e58a649b510f6894bd`. A preliminary net6 run at the prior head exposed a projection-only floating-point edge case: a collector-valid raw tick could fail a second strict comparison after conversion to milliseconds. The preliminary artifact was rejected as final evidence. The post-fix builder accepts only tightly bounded conversion noise, deterministically reconciles the exported buckets, and retains suppression for materially invalid partitions; focused builder and health suites passed 9/9 and 139/139 respectively, and the main full Release suite passed 1,698 tests with three existing skips and zero failures. The Release SMAPI build and both host publishes completed with zero errors and 13 existing warnings.

Both fresh post-fix runs used Xvfb with PTY command input, `de_DE.UTF-8`, new isolated home roots and mode-`0700` XDG roots, the complete trusted fixture, the Blossom save, and the external probe plus its top-level content pack. Each packaged console loaded 132 code mods and 177 content packs, and the save reached normal gameplay. The net6 flow manually started capture, created an interim report, added a reproduction mark, exercised slow/nested/failing/log-heavy/content/background callbacks and an unobserved stall, stopped capture, and exited normally through the probe. The net10 flow exercised the same probe, created an interim report, then exited with capture active so the final report used the `normal-shutdown` path.

| Host / flow | Runtime | Final reason | Final updates | Slow updates | JSON bytes | Text bytes |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| net6, interim then manual stop | .NET 6.0.32 x64 | `user-stop` | 2,248 | 24 | 1,377,380 | 112,999 |
| net10, interim then normal exit | .NET 10.0.11 x64 | `normal-shutdown` | 3,716 | 22 | 1,365,413 | 113,060 |

The net6 interim report froze at 94 updates during the deliberately heavy load transition; the net10 interim report froze at 2,586. All four post-fix reports validated against the checked-in schema v1. Every aggregate and every retained valid update reported `smapiOtherTimingAvailable: true`, contained nonzero observed SMAPI dispatch time at the aggregate level, and reconciled total time to base-game-exclusive + observed callbacks + exclusive SMAPI update dispatch + residual within `0.000001` milliseconds. Both final reports retained 600 recent and 100 worst updates with zero invalid retained rows and zero `invalidTimingPartitionUpdates`. The probe content pack remained represented through on-behalf-of callback rows, while the deliberate greater-than-one-second unobserved stall remained in base-game-exclusive time rather than being attributed to the probe.

Every completion marker named exactly its matching text/JSON pair. Report directories were `0700`, payloads and markers were `0600`, and all payloads remained below five MiB. Neither output format contained the injected player, farm, save, absolute path, IP-address, or token canaries. Both processes exited normally, and only the fresh isolated fixture roots were changed. This fresh dual-host evidence satisfies the packaged-runtime gate for issue #156.

## Issue #166 in-game viewer verification

The viewer source under packaged test was commit `83a6e582f` and did not change the schema-v1 document, enum values, sanitized examples, collector, or analyzer. The main Release assembly and both officially staged host assemblies had SHA-256 `e5af6f87207049efc8d070d7a9ad06ea33dc5f7da66b3d651d24633966da8a0c`. The official `CopyToGameFolder` staging target was used because the raw host projects are dispatcher placeholders whose entry points intentionally return without launching SMAPI.

Repository gates at that source passed:

- 185 focused health/viewer tests, zero failures;
- the full Release suite: 1,863 passed, three existing skips, zero failures;
- the Release solution build: zero errors and nine existing warnings;
- explicit net6 and net10 Linux desktop publishes plus four runtime-dispatcher tests;
- formatting verification restricted to changed files, `git diff --check`, and the Android/mobile exclusion check;
- independent architecture/lifecycle, privacy/test, and game UX reviews of the exact source head, with no unresolved findings.

The complete trusted PR #158 release and Blossom save were re-audited with the pinned sizes, hashes, containment limits, and inventory shown above, then extracted into a new private disposable root. The same fresh 309-entry fixture (132 code mods and 177 content packs, including the external probe and its content pack) reached normal Blossom gameplay on both supported hosts. The source archives, trusted source Mods tree, disposable source game, live Mods/saves, and repository were not modified by the runs. Screenshots were used only for transient visual inspection and were not added to the repository.

| Host | Runtime / locale | Packaged viewer flow | Completed artifacts |
| --- | --- | --- | ---: |
| net6 | .NET 6.0.32 x64 / `de-DE` | active capture, interim, mark, full eight-section navigation, exact write failure/retry, user stop, final view, normal exit | 3 pairs |
| net10 | .NET 10.0.11 x64 / `de-DE` | ledger-only preparation/view, full inventory, mouse/keyboard/controller input, timed interim followed by pending final, stopped final view, normal exit | 5 retained pairs |

The net6 retry test temporarily replaced only the isolated report directory with a regular-file collision. The viewer kept the exact built model in memory, prominently labeled it not saved, exposed retry guidance, and then changed that same request ID to saved after the private directory was restored and `health retry` completed. The final net6 report retained 25,980 completed updates and 50 slow updates. The net10 timed interim and final were queued together; the bounded writer reported that the final waited behind the current write, completed both exact request IDs, and the viewer settled on the newer stopped final. The final net10 timed model retained 803 completed updates and four slow updates. Ledger-only preparation, saved, write-failed, retrying/saved, active interim, pending-final succession, and stopped-final behavior were therefore exercised in the packaged game; exact absent, rejected, superseded, canceled, disposed, reset, shutdown, and concurrent-read transitions also passed their deterministic coordinator/session/menu tests.

At 1280×720 the menu rendered the persistent privacy notice, eight sections, bounded footer actions, long rows, details, expanded limitations, stable relative artifact paths, and the 309-entry inventory without persistent per-row components. Mouse clicks and wheel, keyboard arrows/Page Up/Page Down/Home/End/Enter/Tab/Escape/P/I, window recomputation, and direct versus layered close behavior were exercised. A virtual Xbox-style device was visible as both Linux event and joystick input; packaged SDL mapped and released all requested A/B/X/Y, D-pad, shoulder, back, and start events, while the live viewer exercised controller status/privacy, section, focus, activation, layered back, and close behavior. The German game locale used translation-ready viewer chrome with the default English fallback, and no schema-v1 finding text was reinterpreted or translated.

All three net6 and all five retained net10 JSON payloads validated against the checked-in schema. Every report had a matching text payload and completion marker; report payloads and markers were mode `0600`, retention stopped at five complete pairs, and no incomplete pair remained. Automated and manual scans found none of the injected player, farm, save, absolute home/game/archive path, IP address, token, private author/description, configuration, update-key, or repository-identity canaries. The viewer used only the same frozen sanitized model and stable relative paths, performed no upload/network/clipboard/browser/file-manager action, and did not inspect live logs, saves, manifests, configuration, or metadata.
