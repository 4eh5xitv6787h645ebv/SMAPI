# Linux desktop release and user-experience roadmap

This is the execution checklist for the fork's Linux desktop release and user-experience work. [Umbrella issue #168](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168) mirrors this checklist and links the resulting pull requests, releases, benchmark evidence, and independent reviews. Checkboxes are marked complete only after the linked evidence is verified. Items controlled solely by an upstream maintainer may instead be marked **externally pending** with a link.

Scope constraints apply throughout:

- [ ] Keep Android and mobile code out of scope.
- [ ] Do not include or reopen the unrelated .NET 10 menu-click issue.
- [ ] Preserve unrelated user changes.
- [ ] Never commit, mirror, republish, or include the private modpack or Blossom save in artifacts.
- [ ] Never modify the live game installation, live `Mods` directory, or live saves.
- [ ] Use focused feature branches and separate pull requests where that improves safety or reviewability.
- [ ] Commit and push every meaningful completed change.
- [ ] Use independent agents for architecture, performance, security/privacy, UX/accessibility, installer, testing, documentation, and final-diff reviews.
- [ ] Address every actionable review finding before merging.
- [ ] Merge every completed phase into `develop` before starting dependent work.

## Tracking and governance

- [x] Create this repository roadmap with a checkbox for every phase, requirement, test, review, pull request, release, and definition-of-done item.
- [x] Create [umbrella GitHub issue #168](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168) with the mirrored checklist.
- [x] Link the umbrella issue from this roadmap.
- [x] Commit and push the initial roadmap immediately (`cb0830b1`).
- [ ] Keep both checklists synchronized as evidence is verified.

## Phase 1 — Current upstream comparison and reproducible benchmarks

### Inputs, isolation, and methodology

- [ ] Pin official SMAPI 4.5.2 commit `79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0` as the A build.
- [ ] Record the exact fork commit used as the B build.
- [ ] Retrieve the complete trusted PR #158 modpack release and Blossom save without committing, mirroring, republishing, or artifacting either fixture.
- [ ] Audit the disposable fixture extraction paths before extraction.
- [ ] Build a disposable isolated Linux test environment that cannot modify the live game installation, live `Mods` directory, or live saves.
- [ ] Verify the A and B builds use the same game build, mods, configurations, save state, resolution, session, launch wrapper, warm-up, and scenarios.
- [ ] Define repeatable startup, steady-state gameplay, and important load/warp transition scenarios.
- [ ] Automate alternating A/B sample order.
- [ ] Run at least five samples per build.
- [ ] Measure at least 180 seconds of steady-state gameplay in every sample.
- [ ] Record sanitized raw results, environment metadata, exact commits, scripts, and calculation methods in the repository.

### Measurements and analysis

- [ ] Measure startup phases.
- [ ] Measure mean, p50, p95, p99, and maximum update time.
- [ ] Measure SMAPI framework overhead.
- [ ] Measure update-and-draw time.
- [ ] Measure allocations per tick.
- [ ] Measure GC collection counts.
- [ ] Measure slow-tick counts.
- [ ] Measure important load and warp transitions.
- [ ] Measure diagnostics-disabled overhead.
- [ ] Measure diagnostics-enabled overhead.
- [ ] Report distributions and run-to-run variation.
- [ ] State clearly that one-machine results are not universal FPS claims.
- [ ] Identify and fix every confirmed fork regression.
- [ ] Rerun comparisons affected by fixes.
- [ ] Obtain an independent performance/methodology review.
- [ ] Address every actionable methodology or conclusion finding.

### Phase 1 publication and integration

- [ ] Update `README.md` with current 4.5.2-versus-fork evidence and limitations.
- [ ] Update the GitHub Pages comparison with current evidence and limitations.
- [ ] Update the performance audit with current evidence and limitations.
- [ ] Update release notes with current evidence and limitations.
- [ ] Open a focused Phase 1 fork pull request.
- [ ] Obtain independent final-diff and documentation reviews.
- [ ] Pass required CI and repository checks.
- [ ] Address every actionable Phase 1 review finding.
- [ ] Merge the Phase 1 pull request into `develop` and close it.
- [ ] Verify `develop` equals `origin/develop` after the merge.

## Phase 2 — Automated performance regression testing

### Deterministic coverage

- [ ] Convert stable map/TMX conversion hot paths into reproducible tests or benchmarks.
- [ ] Cover canonical path handling.
- [ ] Cover JSON streaming allocation.
- [ ] Cover asset-name parsing.
- [ ] Cover cached reflection.
- [ ] Cover event dispatch.
- [ ] Cover inventory/chest idle tracking.
- [ ] Cover content invalidation.
- [ ] Cover other suitable deterministic audited hot paths.
- [ ] Make deterministic correctness assertions blocking gates.
- [ ] Make deterministic allocation assertions blocking gates.
- [ ] Keep noisy wall-clock thresholds informational on shared CI unless a statistically defensible stable gate is demonstrated.
- [ ] Record machine-readable baselines.
- [ ] Produce readable comparison artifacts.

### Phase 2 validation and integration

- [ ] Add CI execution that neither embeds nor downloads the private modpack/save.
- [ ] Verify the suite detects intentional correctness regressions.
- [ ] Verify the suite detects intentional allocation regressions.
- [ ] Revert all intentional regression probes after verification.
- [ ] Repeatedly run required checks to verify they are stable and non-flaky.
- [ ] Open a focused Phase 2 fork pull request.
- [ ] Obtain independent architecture, performance, testing, and final-diff reviews.
- [ ] Address every actionable Phase 2 review finding.
- [ ] Pass required CI and repository checks.
- [ ] Merge the Phase 2 pull request into `develop` and close it.
- [ ] Verify `develop` equals `origin/develop` after the merge.

## Phase 3 — First Linux alpha release

### Versioning and release automation

- [ ] Define and document a fork-specific prerelease version/tag scheme that cannot be mistaken for official SMAPI or collide with upstream tags.
- [ ] Make GitHub Actions accept and explicitly build an exact reviewed release commit.
- [ ] Produce the Linux installer/package from that exact reviewed commit.
- [ ] Produce SHA-256 checksums.
- [ ] Produce build metadata that records the exact commit and build inputs.
- [ ] Produce GitHub provenance/attestation where supported.

### Release qualification

- [ ] Run focused tests.
- [ ] Run the full SMAPI test suite.
- [ ] Run Release builds.
- [ ] Run formatting checks.
- [ ] Run packaging tests.
- [ ] Run runtime-dispatcher tests.
- [ ] Run isolated installation tests.
- [ ] Run isolated update tests.
- [ ] Run isolated uninstall tests.
- [ ] Run isolated rollback tests.
- [ ] Run a final trusted-modpack smoke test without publishing the fixtures.
- [ ] Obtain independent release, security/privacy, testing, and final-diff reviews.
- [ ] Address every actionable release review finding.

### Publication and clean-room verification

- [ ] Open a focused Phase 3 fork pull request.
- [ ] Pass required CI and repository checks.
- [ ] Merge the Phase 3 pull request into `develop` and close it.
- [ ] Verify the release tag points to the exact reviewed commit.
- [ ] Publish a GitHub prerelease clearly labeled experimental.
- [ ] Document the supported platform and requirements in the prerelease.
- [ ] Document known limitations in the prerelease.
- [ ] Document installation, upgrade, and rollback steps in the prerelease.
- [ ] Publish checksums, provenance, comparison results, issue-tracker links, and documentation links in the prerelease.
- [ ] Download the published artifact into a clean isolated environment.
- [ ] Verify the downloaded artifact checksum.
- [ ] Verify published provenance/attestation where supported.
- [ ] Install the downloaded artifact in the clean environment.
- [ ] Launch the complete trusted workload.
- [ ] Generate and view a Mod Health Report.
- [ ] Uninstall or roll back successfully.
- [ ] Record clean-room verification results without private fixture data.
- [ ] Update the README with the downloadable prerelease.
- [ ] Update GitHub Pages with the downloadable prerelease.
- [ ] Remove inaccurate “no tagged release” wording.
- [ ] Verify `develop` equals `origin/develop` after the phase.

## Phase 4 — Linux graphical installer/updater

### Architecture and behavior

- [ ] Build a simple, maintainable Linux desktop GUI around existing installer behavior without duplicating installation rules.
- [ ] Support game-folder detection.
- [ ] Support user game-folder selection.
- [ ] Support install.
- [ ] Support update.
- [ ] Support repair.
- [ ] Support uninstall.
- [ ] Support backup.
- [ ] Support rollback.
- [ ] Support release selection.
- [ ] Show download progress.
- [ ] Verify package checksums before installation.
- [ ] Show understandable errors.
- [ ] Write a detailed local log.
- [ ] Never require root for a normal user installation.
- [ ] Protect unrelated game files.
- [ ] Detect modified or unknown existing SMAPI files before replacement.
- [ ] Make destructive actions explicit and recoverable.
- [ ] Keep the non-GUI/manual installation path documented.

### Desktop UX and accessibility

- [ ] Support complete keyboard-only operation.
- [ ] Provide readable focus indication.
- [ ] Support practical screen scaling.
- [ ] Verify practical X11 behavior.
- [ ] Verify practical Wayland behavior.

### Phase 4 tests, reviews, packaging, and integration

- [ ] Add GUI unit tests.
- [ ] Add GUI integration tests.
- [ ] Add failure-path tests.
- [ ] Add interrupted-download tests.
- [ ] Add corrupted-package tests.
- [ ] Add rollback tests.
- [ ] Add accessibility-focused tests.
- [ ] Obtain independent installer architecture review.
- [ ] Obtain independent security/privacy review.
- [ ] Obtain independent UX/accessibility review.
- [ ] Obtain independent testing and final-diff reviews.
- [ ] Address every actionable Phase 4 review finding.
- [ ] Package the GUI through the release workflow.
- [ ] Document GUI installation, update, repair, uninstall, backup, rollback, logs, errors, and troubleshooting.
- [ ] Open a focused Phase 4 fork pull request.
- [ ] Pass required CI and repository checks.
- [ ] Merge the Phase 4 pull request into `develop` and close it.
- [ ] Publish a publicly downloadable GUI package tied to an exact reviewed commit.
- [ ] Verify the public GUI package from a clean isolated environment.
- [ ] Verify `develop` equals `origin/develop` after the phase.

## Phase 5 — Improved Mod Health Report recommendations

### Evidence model and findings

- [ ] Base findings and suggested actions only on evidence SMAPI actually observed.
- [ ] Add clear severity.
- [ ] Add clear confidence.
- [ ] Add concrete evidence.
- [ ] Add limitations.
- [ ] Add safe next steps without falsely blaming mods.
- [ ] Cover load failures.
- [ ] Cover missing dependencies.
- [ ] Cover version incompatibilities.
- [ ] Cover repeated errors.
- [ ] Cover failed callbacks.
- [ ] Cover logging floods.
- [ ] Cover slow observed callbacks.
- [ ] Cover capture-quality problems.
- [ ] Cover relevant startup and transition evidence.

### Compatibility, privacy, and outputs

- [ ] Preserve deterministic output.
- [ ] Preserve bounded memory, files, and tasks.
- [ ] Preserve local-only behavior.
- [ ] Preserve privacy exclusions.
- [ ] Preserve exact-report viewer semantics.
- [ ] Preserve compatibility with existing reports or implement and document an explicit schema migration.
- [ ] Update the report viewer.
- [ ] Update text format.
- [ ] Update JSON format.
- [ ] Update schema.
- [ ] Update screenshots.
- [ ] Update documentation.
- [ ] Update localization text.

### Phase 5 validation and integration

- [ ] Add comprehensive recommendation unit and integration tests.
- [ ] Validate diagnostics-disabled overhead with the complete trusted workload.
- [ ] Validate privacy exclusions with the complete trusted workload.
- [ ] Validate bounded behavior with synthetic maximum-capacity fixtures.
- [ ] Obtain independent architecture, security/privacy, UX/accessibility, testing, documentation, and final-diff reviews.
- [ ] Address every actionable Phase 5 review finding.
- [ ] Open a focused Phase 5 fork pull request.
- [ ] Pass required CI and repository checks.
- [ ] Merge the Phase 5 pull request into `develop` and close it.
- [ ] Verify `develop` equals `origin/develop` after the merge.

## Phase 6 — Startup and loading progress diagnostics

### Diagnostics and attribution boundaries

- [ ] Add bounded Linux desktop startup/loading phase timing.
- [ ] Provide useful progress visibility.
- [ ] Measure SMAPI initialization.
- [ ] Measure mod discovery and resolution.
- [ ] Measure assembly loading and rewriting.
- [ ] Measure mod entry initialization.
- [ ] Measure content-pack registration.
- [ ] Measure save loading.
- [ ] Measure other boundaries SMAPI can safely observe.
- [ ] Identify long observed phases.
- [ ] Identify observed contributors without attributing unobserved game time to a mod.
- [ ] Do not attribute unobserved Harmony time to a mod.
- [ ] Do not attribute unobserved native, GPU, filesystem, or operating-system time to a mod.
- [ ] Integrate relevant evidence into the Mod Health Report.
- [ ] Integrate diagnostics evidence into performance documentation.
- [ ] Avoid noisy default logging.
- [ ] Avoid per-operation overhead when diagnostics are disabled.

### Phase 6 validation and integration

- [ ] Add correctness tests.
- [ ] Add allocation tests.
- [ ] Add concurrency tests.
- [ ] Add truncation and boundedness tests.
- [ ] Add privacy tests.
- [ ] Add real-workload tests with the private fixtures kept outside artifacts.
- [ ] Obtain independent architecture, performance, security/privacy, UX/accessibility, testing, documentation, and final-diff reviews.
- [ ] Address every actionable Phase 6 review finding.
- [ ] Open a focused Phase 6 fork pull request.
- [ ] Pass required CI and repository checks.
- [ ] Merge the Phase 6 pull request into `develop` and close it.
- [ ] Verify `develop` equals `origin/develop` after the merge.

## Phase 7 — Upstream-ready optimizations

### Selection and preparation

- [ ] Fetch and review the complete fork delta against current `Pathoschild/SMAPI` `develop`.
- [ ] Identify low-risk, platform-neutral correctness or performance improvements that separate cleanly from Linux-only features.
- [ ] Document Linux-only or high-risk experimental changes not submitted upstream and explain why.
- [ ] Rebase every selected change onto current upstream `develop`.
- [ ] Minimize every selected upstream diff.
- [ ] Add upstream-appropriate correctness tests.
- [ ] Add upstream-appropriate benchmarks or performance evidence.
- [ ] Preserve upstream style.
- [ ] Preserve upstream platform and API compatibility.
- [ ] Ensure no private fixture data appears in commits, test assets, PR bodies, or artifacts.

### Submission and follow-through

- [ ] Obtain independent upstream-readiness, architecture, performance, testing, documentation, and final-diff reviews.
- [ ] Address every actionable Phase 7 review finding.
- [ ] Submit a focused upstream GitHub pull request for each justified change.
- [ ] Verify every submitted upstream PR is valid and CI-clean.
- [ ] Respond to actionable upstream review or CI findings received during this goal.
- [ ] Mark maintainer-only approval or merge items **externally pending** when applicable; upstream merge is not required for completion.
- [ ] Open a focused Phase 7 documentation/integration pull request on the fork.
- [ ] Pass required fork CI and repository checks.
- [ ] Merge the Phase 7 fork pull request into `develop` and close it.
- [ ] Verify `develop` equals `origin/develop` after the merge.

## Final definition of done

- [ ] Every umbrella issue and roadmap checkbox is complete with linked evidence, or explicitly marked **externally pending** where only an upstream maintainer can act.
- [ ] All fork pull requests are reviewed, fixed, merged into `develop`, and closed.
- [ ] The Linux alpha release artifact is publicly downloadable.
- [ ] The graphical installer artifact is publicly downloadable.
- [ ] Both public artifacts are verified from a clean isolated environment.
- [ ] Current benchmark results and limitations are published in the README.
- [ ] Current benchmark results and limitations are published on GitHub Pages.
- [ ] Automated performance checks run successfully.
- [ ] Mod Health recommendations work with the complete trusted PR #158 workload.
- [ ] Startup diagnostics work with the complete trusted PR #158 workload.
- [ ] Documentation covers installation.
- [ ] Documentation covers update.
- [ ] Documentation covers rollback.
- [ ] Documentation covers benchmark methodology.
- [ ] Documentation covers performance comparison and limitations.
- [ ] Documentation covers diagnostics.
- [ ] Documentation covers privacy.
- [ ] Documentation covers screenshots.
- [ ] Documentation covers troubleshooting.
- [ ] GitHub Pages builds successfully.
- [ ] Every important GitHub Pages page and asset returns HTTP 200.
- [ ] Delete every local feature branch created for this work, leaving only `develop` locally.
- [ ] Delete every origin feature branch created for this work, leaving only `develop` on origin.
- [ ] Do not alter branches belonging to `upstream` or `cinderbox` remotes.
- [ ] Verify the repository worktree is clean.
- [ ] Verify local `develop` equals `origin/develop`.
- [ ] Verify `origin` contains only `develop`.
- [ ] Verify all work is committed and pushed.
