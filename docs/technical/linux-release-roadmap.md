# Linux desktop release and user-experience roadmap

This is the execution checklist for the fork's Linux desktop release and user-experience work. [Umbrella issue #168](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168) mirrors this checklist and links the resulting pull requests, releases, benchmark evidence, and independent reviews. Checkboxes are marked complete only after the linked evidence is verified. Items controlled solely by an upstream maintainer may instead be marked **externally pending** with a link.

Scope constraints apply throughout:

- [ ] Keep Android and mobile code out of scope.
- [ ] Do not include or reopen the unrelated .NET 10 menu-click issue.
- [ ] Preserve unrelated user changes.
- [ ] Never commit, mirror, republish, or include the private modpack or Blossom save in artifacts.
- [ ] Never modify the live game installation, live `Mods` directory, or live saves.
- [x] Treat the complete PR #158 modpack release and Blossom save as explicitly trusted and authorized for extraction and execution without per-mod trust, provenance, licensing, or code review, and without asking again.
- [ ] Use focused feature branches and separate pull requests where that improves safety or reviewability.
- [ ] Commit and push every meaningful completed change.
- [ ] Use independent agents for architecture, performance, security/privacy, UX/accessibility, installer, testing, documentation, and final-diff reviews.
- [ ] Address every actionable review finding before merging.
- [ ] Merge every completed phase into `develop` before starting dependent work.
- [ ] Continue until every item is complete or an unavoidable external blocker satisfies the goal-blocked policy.

## Tracking and governance

- [x] Create this repository roadmap with a checkbox for every phase, requirement, test, review, pull request, release, and definition-of-done item ([independent review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/169#issuecomment-5448507702)).
- [x] Create [umbrella GitHub issue #168](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168) with the mirrored checklist.
- [x] Link the umbrella issue from this roadmap.
- [x] Commit and push the initial roadmap immediately ([`cb0830b1`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/cb0830b1)).
- [x] Open and link roadmap pull request [#169](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/169).
- [x] Obtain an [independent roadmap documentation/completeness review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/169#issuecomment-5448499270).
- [x] Obtain an [independent roadmap final-diff review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/169#issuecomment-5448507702).
- [x] Address every actionable roadmap review finding (`22baae38`).
- [x] Pass applicable roadmap PR checks (`git diff --check`; no repository checks configured for this documentation-only branch).
- [x] Merge roadmap PR [#169](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/169) into `develop` and close it (`44ba3bda`).
- [x] Verify `develop` equals `origin/develop` after the roadmap merge (`44ba3bda`).
- [x] Keep both checklists synchronized as evidence is verified ([mirrored umbrella issue #168](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168)).

## Phase 1 — Current upstream comparison and reproducible benchmarks

### Inputs, isolation, and methodology

- [x] Pin official SMAPI 4.5.2 commit `79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0` as the A build ([verified preflight](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5449436971)).
- [x] Record exact fork commit [`3c98eadd2bddc24d43c889afb11b155e92469882`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/3c98eadd2bddc24d43c889afb11b155e92469882) as the B build.
- [x] Retrieve the complete trusted PR #158 modpack release and Blossom save without committing, mirroring, republishing, or artifacting either fixture ([private-fixture preflight evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5449436971)).
- [x] Audit the disposable fixture extraction paths before extraction ([containment-audited preflight](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5449436971)).
- [x] Build a disposable isolated Linux test environment that cannot modify the live game installation, live `Mods` directory, or live saves ([isolation preflight](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5449436971)).
- [x] Verify the A and B builds use the same game build, mods, configured controls, save state, resolution, session, launch wrapper, warm-up, and scenarios ([reviewed evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5451115555)).
- [x] Define repeatable startup, steady-state gameplay, and important load/warp transition scenarios ([verified preflight](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5449436971)).
- [x] Automate alternating A/B sample order ([preregistered runner](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170)).
- [x] Run five A/B samples per build in the defined fixed A-before-B paired order ([20 accepted captures](https://github.com/4eh5xitv6787h645ebv/SMAPI/tree/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/raw)).
- [x] Measure at least 180 seconds of steady-state gameplay in every sample ([machine-readable summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.json)).
- [x] Record sanitized raw results, environment metadata, exact commits, scripts, and calculation methods in the repository ([reviewed evidence bundle](https://github.com/4eh5xitv6787h645ebv/SMAPI/tree/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world)).

### Measurements and analysis

- [x] Measure startup phases ([machine-readable summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.json)).
- [x] Measure mean, p50, p95, p99, and maximum update elapsed duration ([summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.md)).
- [x] Measure the SMAPI framework envelope with explicit elapsed-duration semantics and attribution limits ([machine-readable summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.json)).
- [x] Measure accumulated update-and-draw elapsed duration per draw interval with explicit non-frame semantics ([machine-readable summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.json)).
- [x] Measure allocations per update ([summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.md)).
- [x] Measure process and coincident GC collection counts ([machine-readable summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.json)).
- [x] Measure slow-update counts and normalized percentages ([summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.md)).
- [x] Measure important load and warp transitions ([summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.md)).
- [x] Measure diagnostics-disabled control overhead ([summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.md)).
- [x] Measure diagnostics-enabled overhead ([summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.md)).
- [x] Report every per-run distribution, 56-field cross-run variation group, and 63 paired metric families ([machine-readable summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/9480e39737d201a9dbb7a9737f41c4b848bee5f3/benchmarks/linux-real-world/results/summary.json)).
- [x] State clearly that one-machine results are not universal FPS claims ([benchmark methodology](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/3c98eadd2bddc24d43c889afb11b155e92469882/benchmarks/linux-real-world/README.md)).
- [x] Review all adverse signals and confirm that none establish a fork regression requiring a code fix; retain higher selected-core busy time, Gen1 counts, and noisy Farm timing as limitations ([independent review evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5451115555)).
- [x] Rerun every comparison after affected harness/probe fixes; the final uninterrupted suite accepted all 20 captures ([reviewed evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5451115555)).
- [x] Obtain independent performance-results and failure-analysis reviews ([PASS evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5451115555)).
- [x] Obtain independent methodology-and-conclusions reviews ([PASS evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5451115555)).
- [x] Address every actionable methodology, security/privacy, failure-analysis, and conclusion finding ([25 passing tests and review summary](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5451115555)).

### Phase 1 publication and integration

- [x] Update `README.md` with current 4.5.2-versus-fork evidence and limitations ([`bc5937c2`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/bc5937c2)).
- [x] Update the GitHub Pages home and comparison with current evidence and limitations ([`bc5937c2`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/bc5937c2); [successful deployment](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33162274803); verified HTTP 200).
- [x] Update the performance audit with current evidence and limitations ([`bc5937c2`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/bc5937c2)).
- [x] Update release notes with current evidence and limitations ([`bc5937c2`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/bc5937c2)).
- [x] Open a focused [Phase 1 fork pull request #170](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170).
- [x] Obtain independent Phase 1 final-diff, evidence, and security/privacy reviews ([PASS evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5451242326)).
- [x] Obtain independent Phase 1 documentation, performance-claims, and privacy reviews ([PASS evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5451183384)).
- [x] Pass applicable repository checks; no GitHub PR status checks were configured before merge ([25 tests, probe build, link validation, and diff-check evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5451242326); [post-merge Pages build](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33162274803)).
- [x] Address every actionable Phase 1 review finding and obtain re-review PASS ([final review evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170#issuecomment-5451242326)).
- [x] Merge Phase 1 pull request [#170](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/170) into `develop` and close it ([`1cd1b435`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/1cd1b4353d63eeb635bdf9c9a20171b14b83133b)).
- [x] Verify local `develop` equals `origin/develop` after the merge (`1cd1b435`).

## Phase 2 — Automated performance regression testing

### Deterministic coverage

- [x] Convert stable map/TMX conversion hot paths into reproducible tests or benchmarks ([merged implementation](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171); [final TMX review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451991695)).
- [x] Cover canonical path handling ([suite documentation and baseline](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171/files)).
- [x] Cover JSON streaming allocation ([suite documentation and baseline](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171/files)).
- [x] Cover asset-name parsing ([suite documentation and baseline](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171/files)).
- [x] Cover cached reflection ([suite documentation and baseline](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171/files)).
- [x] Cover event dispatch ([production-core review evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451947272)).
- [x] Cover inventory/chest idle tracking ([production-core review evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451947272)).
- [x] Cover content invalidation ([production-core review evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451947272)).
- [x] Cover other suitable deterministic audited hot paths ([11-scenario hosted artifact](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33166744493)).
- [x] Make deterministic correctness assertions blocking gates ([three-attempt hosted run](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33166744493)).
- [x] Make deterministic allocation assertions blocking gates ([three-attempt hosted run](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33166744493)).
- [x] Keep noisy wall-clock thresholds informational on shared CI unless a statistically defensible stable gate is demonstrated ([methodology and final evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451947272)).
- [x] Record machine-readable baselines ([versioned schema and baseline](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171/files)).
- [x] Produce readable comparison artifacts ([verified JSON and Markdown artifact](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33166744493)).

### Phase 2 validation and integration

- [x] Add CI execution that neither embeds nor downloads the private modpack/save ([pinned read-only workflow and privacy review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451991695)).
- [x] Verify the suite detects intentional correctness regressions ([probe evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451947272)).
- [x] Verify the suite detects intentional allocation regressions ([probe evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451947272)).
- [x] Revert all intentional regression probes after verification ([clean-tree evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451947272)).
- [x] Repeatedly run required checks to verify they are stable and non-flaky ([three hosted attempts and ten local repeats](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451947272)).
- [x] Open a focused [Phase 2 fork pull request #171](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171).
- [x] Obtain an independent Phase 2 architecture review ([PASS evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451947272)).
- [x] Obtain an independent Phase 2 performance review ([PASS evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451947272)).
- [x] Obtain an independent Phase 2 testing review ([PASS evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451947272)).
- [x] Obtain an independent Phase 2 final-diff review ([PASS evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451991695)).
- [x] Address every actionable Phase 2 review finding ([fix and re-review evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171#issuecomment-5451947272)).
- [x] Pass required CI and repository checks ([strict required check, three successful attempts](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33166744493)).
- [x] Merge Phase 2 pull request [#171](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/171) into `develop` and close it ([`298c77ae`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/298c77ae5b91f10ede15eb0c9a4ba34e36af4bb9)).
- [x] Verify local `develop` equals `origin/develop` after the merge (`298c77ae`).

## Phase 3 — First Linux alpha release

### Versioning and release automation

- [x] Define and document a fork-specific prerelease version/tag scheme that cannot be mistaken for official SMAPI or collide with upstream tags ([scheme and validation](linux-alpha-release.md#release-identity)).
- [x] Make GitHub Actions accept and explicitly build an exact reviewed release commit ([exact-tag workflow run](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33177145353)).
- [x] Produce the Linux installer/package from that exact reviewed commit ([public prerelease](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1)).
- [x] Produce SHA-256 checksums ([exact-head workflow and independent download verification](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172#issuecomment-5452607444)).
- [x] Produce build metadata that records the exact commit and build inputs ([metadata assertions](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172#issuecomment-5452607444)).
- [x] Produce GitHub provenance/attestation where supported ([tag workflow and independent public-download verification](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)).

### Release qualification

- [x] Run focused tests ([21 dispatcher and 13 analyzer tests with hard zero-discovery failure](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33170191391)).
- [x] Run the full SMAPI test suite ([1,871 discovered; 1,868 passed; 3 platform skips; 0 failed](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172#issuecomment-5452607444)).
- [x] Run Release builds ([exact-head hosted build](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33170191391)).
- [x] Run formatting checks ([exact-head hosted checks](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33170191391)).
- [x] Run packaging tests ([hosted and downloaded-artifact verification](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172#issuecomment-5452607444)).
- [x] Run runtime-dispatcher tests ([21/21 exact-head hosted tests](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33170191391)).
- [x] Run isolated installation tests ([hosted and independent downloaded-artifact lifecycle](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172#issuecomment-5452607444)).
- [x] Run isolated update tests ([hosted and independent downloaded-artifact lifecycle](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172#issuecomment-5452607444)).
- [x] Run isolated uninstall tests ([hosted and independent downloaded-artifact lifecycle](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172#issuecomment-5452607444)).
- [x] Run isolated rollback tests against the exact merged release candidate ([exact-merge rollback repeat](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)).
- [x] Run a final trusted-modpack smoke test without publishing the fixtures against the exact merged release candidate ([180.019-second exact-merge evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)).
- [x] Obtain an independent Phase 3 release review ([final review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172#issuecomment-5452946668); [tag-readiness PASS](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)).
- [x] Obtain an independent Phase 3 security/privacy review ([final review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172#issuecomment-5452946668)).
- [x] Obtain an independent Phase 3 testing review ([final review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172#issuecomment-5452946668)).
- [x] Obtain an independent Phase 3 final-diff review ([tree-equality and artifact review](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)).
- [x] Address every actionable release review finding ([final review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172#issuecomment-5452946668)).

### Publication and clean-room verification

- [x] Open a focused Phase 3 fork pull request ([#172](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172)).
- [x] Pass required CI and repository checks ([final reviewed-head Linux alpha qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33174137229), [final reviewed-head deterministic performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33174137200), and [exact tagged release qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33177145353)).
- [x] Merge the Phase 3 pull request into `develop` and close it ([merged PR #172](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172); [`6e5d708a`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/6e5d708a09e7d2b6d9b5434bd1fac52ddbdb5f08)).
- [x] Verify the release tag points to the exact reviewed commit ([annotated-tag evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)).
- [x] Publish a GitHub prerelease clearly labeled experimental ([alpha 1 prerelease](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1)).
- [x] Document the supported platform and requirements in the prerelease ([release notes](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1)).
- [x] Document known limitations in the prerelease ([release notes](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1)).
- [x] Document installation, upgrade, and rollback steps in the prerelease ([release notes and alpha guide](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1)).
- [x] Publish checksums, provenance, comparison results, issue-tracker links, and documentation links in the prerelease ([release page and assets](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1)).
- [x] Download the published artifact into a clean isolated environment ([clean-room record](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)).
- [x] Verify the downloaded artifact checksum ([public SHA-256 verification](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)).
- [x] Verify published provenance/attestation where supported ([public attestation verification](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)).
- [x] Install the downloaded artifact in the clean environment ([normal-user installation record](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)).
- [x] Launch the complete trusted workload ([public-package 180.003-second run](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)).
- [x] Generate and view a Mod Health Report ([schema, privacy, and visual verification](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)).
- [x] Uninstall or roll back successfully ([public-package uninstall and exact-candidate rollback evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)).
- [x] Record clean-room verification results without private fixture data ([sanitized validation record](linux-alpha-release-validation.md)).
- [x] Update the README with the downloadable prerelease ([merged publication update](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/af6e23bc856c8e35c054ad8c784514a7c70248de)).
- [x] Update GitHub Pages with the downloadable prerelease ([successful deployment](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33179582509); important pages and committed assets verified HTTP 200).
- [x] Remove inaccurate “no tagged release” wording ([independent stale-wording review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/173#issuecomment-5453648737)).
- [x] Open, review, fix, pass required checks for, merge, and close the focused Phase 3 publication follow-up [PR #173](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/173) ([independent review and CI evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/173#issuecomment-5453648737); [`af6e23bc`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/af6e23bc856c8e35c054ad8c784514a7c70248de)).
- [x] Verify `develop` equals `origin/develop` after the phase (`af6e23bc856c8e35c054ad8c784514a7c70248de`).

## Phase 4 — Linux graphical installer/updater

### Architecture and behavior

- [ ] Build a simple, maintainable Linux desktop GUI around existing installer behavior without duplicating installation rules.
- [x] Support bounded read-only game-folder detection through the shared installer core ([merged PR #202](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/202)).
- [x] Support user game-folder selection and backend-authoritative manual validation without duplicating installer rules ([exact-head review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/202#issuecomment-5491551755)).
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

### GUI screenshots and visual documentation

- [ ] Follow the complete [Linux GUI screenshot evidence matrix](linux-gui-screenshot-evidence.md), label every image as real qualification or controlled fixture, and never present a mockup, generated image, or safe-demo capture as production evidence.
- [ ] Capture every production screenshot from the exact reviewed release package and applicable production frontend (GUI or manual console) without private fixture names, paths, or personal data.
- [ ] Document one automatically detected valid game, no-game and multiple-game detection states, and manual invalid/valid folder selection (D1–D4).
- [ ] Document packaged effective-UID-0 refusal before discovery, download, logging, or mutation (D5).
- [ ] Document current, upgrade, prerelease, downgrade, and local-package release-selection labels (R1).
- [ ] Document real public-package download progress and controlled cancellation/interruption with retry and incomplete-file cleanup (R2–R3).
- [ ] Document real checksum/release-metadata verification in progress and successful checksum plus attestation/provenance without conflating integrity and provenance (R4–R5).
- [ ] Document corrupt checksum/metadata/package and attestation/provenance/release-identity failures blocking mutation with safe next steps (R6–R7).
- [ ] Document fresh-install plan, confirmation, meaningful progress, and real successful completion (I1–I4).
- [ ] Document update plan, update or explicit downgrade confirmation, and real successful completion (U1–U3).
- [ ] Document repair of missing and modified receipt-owned files, exact opt-in replacement confirmation, and real successful completion (P1–P4).
- [ ] Document unknown, legacy, hard-linked, special-file, and ambiguous-launcher collision protection (X1).
- [ ] Document uninstall plan, destructive confirmation with default focus on Cancel, and real successful completion preserving unrelated files (N1–N3).
- [ ] Document backup plan and real success, a full recovery store/prune-required state, and destructive prune confirmation with default focus on Cancel (B1–B4).
- [ ] Document authenticated rollback/recovery selection, destructive confirmation with default focus on Cancel, progress, and real successful durable result (L1–L3).
- [ ] Document an active Cancel action, Cancel requested/rollback/Finishing safely behavior, and a real cancelled-and-rolled-back durable result (C1–C3).
- [ ] Document network/timeout, permission/read-only, disk-full, cross-device, stale-plan, root-replacement, concurrent-installer, and backend/protocol/writer errors (E1–E4).
- [ ] Document a real interrupted mutation requiring restart recovery and successful automatic recovery requiring fresh inspection (E5–E6).
- [ ] Document a real detailed local log, expanded technical error details, Copy sanitized diagnostics, and hostile-string privacy redaction (G1–G3).
- [ ] Document readable keyboard focus on the primary action and default visible Cancel focus for destructive dialogs (A1–A2).
- [ ] Document the 420-DIP layout at 200% and 100%, 125%, 150%, and 200% scale variants without hidden actions or horizontal page scrolling (A3–A4).
- [ ] Document light, dark, and high-contrast focus/error states and link representative live-status imagery to separate AT-SPI/Orca evidence (A5 and A8).
- [ ] Document the packaged GUI on GNOME and KDE under X11 and through XWayland in Wayland sessions (A6–A7).
- [ ] Document GUI manual-installation help plus real manual console launch and install or rollback completion from the same verified public package (M1–M3).
- [ ] Require real clean isolated qualification for every success, mutation, cancellation/recovery, public verification, real-log, root-refusal, and manual-lifecycle image identified by the matrix.
- [ ] Publish a machine-readable screenshot manifest and readable provenance record with exact source/artifact/binary hashes, capture environment and method, evidence class, durable-state context, privacy review, dimensions, and final PNG hashes.
- [ ] Retain and hash every individual source PNG used in a crop or contact sheet, strip incidental metadata, and record that application pixels were not altered.
- [ ] Add a representative screenshot-led user guide and linked qualification gallery to both repository documentation and GitHub Pages with accurate captions and descriptive alt text.
- [ ] Pass automated screenshot manifest, coverage, hash, privacy, link, and documentation checks.
- [ ] Build and deploy GitHub Pages successfully, then verify every important screenshot, manifest/provenance asset, and containing documentation page returns HTTP 200.

### Shared installer core foundation

- [x] Implement the shared transactional Linux installer core and strict frontend protocol ([merged PR #175](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/175); [`93027308`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/93027308c41f3e06117fa6f43732a5b1a40bf336)).
- [x] Pass the complete core suite, Release warnings-as-errors build, and both full-project formatting gates (488 tests; [final review evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/175#issuecomment-5457874064)).
- [x] Pass required CI on the exact reviewed core head ([Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33211923106) and [deterministic performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33211923112)).
- [x] Address every actionable installer-core architecture, security/privacy, testing, and final-diff finding ([clean exact-head re-review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/175#issuecomment-5457874064)).
- [x] Merge and close the core-only PR before dependent console/package integration work ([merged PR #175](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/175)).
- [x] Delete the merged core feature branch locally and on `origin`, and verify local `develop` equals `origin/develop` (`93027308c41f3e06117fa6f43732a5b1a40bf336`).

### Read-only runtime dispatcher integration

- [x] Remove launch-time dependency-file mutation from the unreleased Linux runtime dispatcher and keep installer mutation ownership explicit ([merged PR #177](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/177); exact reviewed head [`a545e0ce`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/a545e0ce950bac1d0db22d90ad2b1eba641345d3)).
- [x] Reject missing, mismatched, mode-mismatched, linked, special, empty, or oversized dependency metadata and unsafe runtime hosts without writing or following external entries ([49-case exact-head suite and independent review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/177#issuecomment-5458180796)).
- [x] Bound dependency comparison, detect ordinary replacement observed during validation, and document the nonprivileged same-user race boundary without overstating guarantees ([security/architecture re-review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/177#issuecomment-5458180796)).
- [x] Probe and document GNU launcher prerequisites with path-free diagnostics, while keeping published alpha.1 behavior distinct from unreleased next-alpha/source behavior ([testing/UX/documentation re-review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/177#issuecomment-5458180796)).
- [x] Preserve runtime selection, GC policy, exact argument forwarding, and usable current recovery guidance ([independent exact-head review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/177#issuecomment-5458180796)).
- [x] Pass 49/49 focused tests, Release warnings-as-errors, shell syntax, formatting, diff checks, package qualification, and deterministic performance gates ([Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33214697603); [performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33214697600)).
- [x] Address every actionable runtime-dispatcher security, portability, testing, UX, and documentation finding and obtain clean exact-head re-reviews ([review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/177#issuecomment-5458180796)).
- [x] Merge and close focused [PR #177](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/177) into `develop` ([`42eb00ff`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/42eb00ffcaff75d4d583655677d10a2e80f54b7d)).
- [x] Delete the merged dispatcher feature branch locally and on `origin`, and verify local `develop` equals `origin/develop` ([branch/ref evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/178#issuecomment-5458223201); `42eb00ffcaff75d4d583655677d10a2e80f54b7d`).

### Release manifest and package-workflow authority

- [x] Generate the canonical external Linux install-manifest companion from the exact finalized installer ZIP without modifying that ZIP ([merged PR #179](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179); [`c289d998`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/c289d9989250d4e51a2c9890de13466b35af733d)).
- [x] Emit and fully verify the four primary next-alpha package/metadata assets—installer ZIP, manifest, two-subject `SHA256SUMS`, and plural-artifact build metadata—only on the annotated-tag workflow path ([workflow and authority review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179#issuecomment-5458606915)).
- [x] Keep pull-request and pre-tag dispatch artifacts explicitly non-authoritative, with actual-workflow candidate metadata and no manifest companion ([clean exact-head workflow review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179#issuecomment-5458606915)).
- [x] Inspect the exact prepared candidate ZIP through the shared bounded outer/nested layout, path, type, mode, allowlist, and canonicalization checks before a tag exists, without emitting authority artifacts (real 52 MB artifact: 107 outer support files, 252 payload files, 95,010,744 expanded bytes; [verification record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179#issuecomment-5458606915)).
- [x] Reject hostile paths, collisions, special files and modes, corrupt or missing nested payloads, resource-bound violations, linked inputs, non-executable launchers, tampered quartet members, and mismatched release identity ([516-case Core and 21-case PackageTool results](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179#issuecomment-5458606915)).
- [x] Verify both checksummed subjects after workflow-artifact transfer, attest both subjects, and prepare the four primary named files for tagged next-alpha publication; the later attestation work adds the required bundle and sidecar for six public assets total ([independent security/workflow review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179#issuecomment-5458606915)).
- [x] Document the non-cryptographic environment guard, two-subject attestation provenance boundary, unauthenticated informational build fields, external companion model, and immutable alpha.1 history ([clean documentation re-review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179#issuecomment-5458606915)).
- [x] Pass required Linux qualification and deterministic performance checks on the exact reviewed head ([Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33218511989); [performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33218511910)).
- [x] Address every actionable release-authority architecture, installer, security/privacy, testing, workflow, documentation, and final-diff finding and obtain clean exact-head reviews ([review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179#issuecomment-5458606915)).
- [x] Merge and close focused [PR #179](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179), delete its feature branch locally and on `origin`, and verify local `develop` equals `origin/develop` (`c289d9989250d4e51a2c9890de13466b35af733d`).

### Installer host protocol and retained package authority

- [x] Classify actionable Linux filesystem failures through nested and aggregate wrappers without leaking private paths or collapsing disk-full, quota, read-only, permission, cross-device, concurrency, and generic I/O outcomes ([merged PR #181](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/181)).
- [x] Read the exact caller-selected release quartet through bounded retained no-follow handles, reject hostile path components and special or linked files, and make the production protocol opener use that hardened path ([actual-opener review evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/181#issuecomment-5459189115)).
- [x] Retain verified Linux package bytes in an anonymous sealed `memfd`, verify all required kernel seals before the authoritative hash, and prove a writable alias acquired before sealing cannot mutate, shrink, or grow the archive before ZIP consumption ([security review evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/181#issuecomment-5459144938)).
- [x] Define the bounded deterministic Protocol V1 JSONL contract with unique command correlation, strict request/event direction, opaque authority IDs, pull pagination, and explicit confirmation and cancellation acknowledgements ([merged PR #181](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/181)).
- [x] Make terminal outcome, durable state, exact error, recovery disposition, bounded counts, and next action typed protocol authority independent of mutable display prose ([protocol final-diff review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/181#issuecomment-5459144938)).
- [x] Reject invalid terminal-state combinations, contradictory or excessive aggregate counts, stale command/plan bindings, invented pre-execution work, and unrequested cancellation reclassification ([protocol final-diff review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/181#issuecomment-5459144938)).
- [x] Address every actionable retained-file, descriptor-immutability, cleanup-authority, cancellation, terminal-state, aggregate-count, and production-opener integration finding and obtain clean exact-head re-reviews ([review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/181#issuecomment-5459189115)).
- [x] Pass 123/123 Protocol V1 tests, 605/605 installer-core tests, 142/142 package tests, Release warnings-as-errors, formatting, and diff checks on the exact reviewed source ([final-head review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/181#issuecomment-5459189115); [post-merge 142/142 verification](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/181#issuecomment-5459217470)).
- [x] Pass required CI on the exact reviewed head ([Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33223747914); [deterministic performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33223747770)).
- [x] Merge and close focused [PR #181](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/181), delete its feature branch locally and on `origin`, and verify local `develop` equals `origin/develop` ([merge commit `2283994a`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/2283994a39aeeee591b0da016ba24769b09fe662)).

### Exact tagged-release attestation authority

- [x] Retain the exact package, manifest, local attestation bundle, and pinned verifier bytes as immutable authorities without reopening caller-controlled paths ([merged PR #184](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/184); [`0bd90bc6`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/0bd90bc6268738e67f49685646a41b42b8fbd916)).
- [x] Accept exactly one bounded GitHub Actions attestation statement whose ordered subjects are the canonical install manifest and installer package, and reject malformed, duplicate, missing, extra, or reversed subjects ([implementation and validation record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/184)).
- [x] Pin the verifier to GitHub CLI 2.92.0 by official archive digest, extracted binary size, and binary digest; run it without a shell or ambient credentials and with private HOME, XDG runtime, and D-Bus endpoints ([security boundary and validation record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/184)).
- [x] Bind accepted evidence to the reviewed repository and owner identities, exact tag reference, source commit, tagged workflow, GitHub-hosted push execution, public source, SLSA build and invocation evidence, transparency-log timestamp, and exact retained subject digests and sizes; expose only curated verified trust ([merged implementation](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/184)).
- [x] Bridge the sealed bundle through a retained, extension-bearing `.jsonl` descriptor, validate its exact identity, size, and immutable seals immediately before process start, and retain its descriptor lifetime through verifier teardown ([merged implementation](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/184)).
- [x] Gate verifier execution until exact pidfd, executable, session-leader, and unique retained-gate authority is established; on pre-authority failure leave the gate locked and reap through the bounded helper timeout without numeric PID signaling, then use exact pidfds for residual-session teardown only after gate release ([resolved security finding](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/184#issuecomment-5460091795)).
- [x] Prepare the annotated-tag workflow to emit a separately checksummed local attestation-bundle pair and cryptographically verify the package attestation from that exact produced bundle with the pinned policy before publication; retain tag-only publication as unexecuted until the next exact tag ([exact-head Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33231854458)).
- [x] Verify the extension-bearing sealed-bundle bridge and isolated pinned GitHub CLI 2.92.0 against a real public prior-alpha package and one-subject bundle in six successful runs without committing fixture data; do not treat that prior alpha as evidence for the new two-subject policy ([fixture-free validation record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/184)).
- [x] Pass 820/820 Installer Core tests in Debug and Release, 41/41 focused process-runner tests across repeated runs, 131/131 focused attestation tests, Release warnings-as-errors, formatting, diff, and actionlint checks ([validation record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/184)).
- [x] Pass exact-head Linux qualification and deterministic performance checks; verify that tag-only attestation and publication jobs skip on the pull-request path ([Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33231854458); [performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33231854483)).
- [x] Address every actionable attestation, workflow, package, process-boundary, security/privacy, architecture, testing, and final-diff finding before merging ([finding and resolution record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/184#issuecomment-5460091795)).
- [x] Merge and close focused [PR #184](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/184), delete its feature branch locally and on `origin`, and verify local `develop` equals `origin/develop` ([merge commit `0bd90bc6`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/0bd90bc6268738e67f49685646a41b42b8fbd916)).

PR #184 was intentionally an internal foundation when it merged. The dependent subsections below now record the separately reviewed persistence, public Core facade, verifier delivery, and safe graphical-shell work. Production console/GUI wiring, real operations, GUI packaging, and an actual tagged next-alpha publication remain unchecked and must not be inferred from these completed foundation items.

### Persisted schema-4 release authority and public Core facade

- [x] Add deterministic schema-4 release-authority policy to the canonical install manifest and persist only curated verified trust in the receipt, excluding raw bundles, certificates, verifier output, tokens, and signed URLs ([merged PR #188](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/188)).
- [x] Preserve exact legacy manifest-2/receipt-3 and manifest-3/receipt-3 compatibility; require manifest-4/receipt-4 and reject manifest-4/receipt-3 or legacy-manifest/receipt-4 downgrade and evidence-laundering combinations ([independent clean review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/188#issuecomment-5460337085)).
- [x] Reconstruct the exact unresolved attested manifest template without circularly embedding its own digest, and preserve policy/trust through generated-file evolution ([merged implementation](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/188)).
- [x] Expose one public Core opener that performs quartet, manifest, local bundle/sidecar, pinned-verifier attestation, trust binding, and extraction in order, with bounded cleanup and ownership transfer ([independent architecture/security review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/188#issuecomment-5460337085)).
- [x] Require bundle, bundle sidecar, and pinned verifier inputs on the protocol package-open path so schema-4 extraction cannot fall back to checksum-only authority ([merged PR #188](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/188)).
- [x] Prove a distinct schema-4 install, update, authenticated recovery selection, and rollback restores the exact prior canonical manifest/receipt bytes and prior release trust ([review-finding resolution](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/188#issuecomment-5460320347)).
- [x] Keep pre-attestation quartet qualification structural and deterministic without minting fake trust or weakening the schema-4 post-attestation extraction guard; pass PackageTool 21/21 after the CI-discovered integration fix ([failure and resolution record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/188#issuecomment-5460296375)).
- [x] Pass Installer Core 834/834 in Debug and Release, Release warnings-as-errors, formatting, exact-head Linux qualification, and deterministic performance checks ([Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33233772413); [performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33233772381)).
- [x] Address every actionable schema, persistence, recovery, protocol, PackageTool, architecture, security/privacy, testing, and final-diff finding and obtain a clean exact-head re-review ([clean review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/188#issuecomment-5460337085)).
- [x] Merge and close focused [PR #188](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/188), delete its feature branch/worktree locally and on `origin`, and synchronize `develop` ([merge commit `7e2010fc`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/7e2010fc1cae4b1ef7202994a86082d7d07f10dd)).

### Pinned verifier delivery in every Linux package

- [x] Stage the official GitHub CLI 2.92.0 binary and MIT license through one fail-closed helper that validates the archive, exact members, sizes, hashes, types, links, and final `0555`/`0444` modes ([merged PR #186](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/186)).
- [x] Bundle the verifier and license only under outer `internal/linux`, prove neither enters `install.dat` or the game, and preserve the exact six-public-asset release contract ([independent final review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/186#issuecomment-5460358178)).
- [x] Prevent output-path substitution with private same-filesystem staging, atomic no-replace publication, exact identity validation, and cleanup limited to captured private staging; add repeated symlink/directory substitution regressions ([resolved review finding](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/186)).
- [x] Require GNU coreutils 8.30 or later and directly probe `renameat2(RENAME_NOREPLACE)` support on the actual destination filesystem so unsupported environments fail before requested output creation ([resolved portability/security finding](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/186)).
- [x] Use the same pinned acquisition/staging policy for release qualification, tagged verification, standard develop builds, and documented Linux/WSL manual builds; inventory every repository Linux-only packaging call site ([merged implementation](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/186)).
- [x] Pass repeated hostile staging tests, a production-shaped Linux package build, package and lifecycle gates, Core 834/834 on the combined head, PackageTool 21/21, actionlint, formatting, and diff checks ([independent final review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/186#issuecomment-5460358178)).
- [x] Pass exact combined-head Linux qualification and deterministic performance checks while tag-only attestation/publication skip on the pull-request path ([Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33233979222); [performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33233979202)).
- [x] Address every actionable supply-chain, path-race, portability, workflow-caller, mode, documentation, privacy, testing, and final-diff finding and obtain a clean exact-head review ([clean review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/186#issuecomment-5460358178)).
- [x] Merge and close focused [PR #186](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/186), delete its feature branch/worktree locally and on `origin`, and synchronize `develop` ([merge commit `a1fe07f8`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/a1fe07f8e19126fe61638de762139e28f62ba278)).

### Reviewed safe graphical-shell slice

- [x] Build and launch a real Avalonia 12.1.1 Linux desktop shell using Core operation/durable-state types, with an exact internal sealed synthetic session, fixed bounded data, production-like arguments rejected, and execution unconditionally disabled ([merged PR #187](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/187)).
- [x] Add the GUI and GUI-test projects to the solution and required build/release workflows, scope NUnit 4.5.1 to the GUI tests, and run the explicit fixture-free GUI test/safety-smoke gates in CI ([clean security review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/187#issuecomment-5460457254)).
- [x] Provide unique working access keys, deterministic initial and forward/reverse focus, visible focus, automation names/help/roles/states, semantic headings, one concise polite result announcement, and primary-action contrast of at least 4.5:1 ([clean UX/accessibility review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/187#issuecomment-5460457254)).
- [x] Verify responsive arranged/rendered layouts at 420 DIPs and 100%, 125%, 150%, and 200% scale without horizontal page scrolling or title clipping; scope support wording to X11 and XWayland while native Wayland remains deferred ([merged tests and documentation](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/187)).
- [x] Run a repeatable Xvfb process smoke with disposable HOME/XDG state which proves five-second GUI health and no game-shaped path creation; independently verify the sealed demo composition initiates no installer-controlled discovery, mutation, or remote download, while documenting normal desktop runtime access ([reviewed safety wording](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/187)).
- [x] Capture a real privacy-clean app-only screenshot from exact source commit `73bea44c`, publish its source tree, environment, capture method, SHA-256, dimensions, timestamp, and privacy review, and link the qualified safe-demo page from the documentation index ([screenshot](../screenshots/linux-installer-safe-demo.png); [provenance](../screenshots/linux-installer-safe-demo.provenance.md)).
- [x] Pass 25/25 GUI tests, 834/834 Core tests, repeated GUI tests, GUI Release warnings-as-errors, scoped formatting, actionlint, package qualification, Xvfb safety smoke, and diff checks ([clean security/testing review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/187#issuecomment-5460457254)).
- [x] Pass exact-head Linux qualification and deterministic performance checks while tag-only publication jobs skip on the pull-request path ([Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33234930537); [performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33234930528)).
- [x] Address every actionable GUI CI, construction-boundary, accessibility, keyboard, scaling, focus, contrast, live-region, screenshot, privacy, documentation, dependency, security, testing, and final-diff finding and obtain independent clean exact-head reviews ([review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/187#issuecomment-5460457254)).
- [x] Merge and close focused [PR #187](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/187), delete its feature branch/worktree locally and on `origin`, relaunch the demo from merged `develop`, and verify local `develop` equals `origin/develop` ([merge commit `3abbb93a`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/3abbb93adfc87f61743b7d209799fd888e981b02); [post-merge verification](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/187#issuecomment-5460467774)).

The safe shell is not the production graphical installer. Real discovery, download, verification, install/update/repair/uninstall/backup/rollback, confirmations, progress/cancellation, failure recovery, packaging, public-artifact qualification, and screenshots of those production states remain unchecked below.

### Production screenshot evidence contract

- [x] Define a 57-state production screenshot matrix covering detection, release verification, every lifecycle operation, recovery, errors, logs, accessibility, desktop environments, and manual fallback, with all 57 identifiers unique ([merged PR #190](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/190); [evidence matrix](linux-gui-screenshot-evidence.md); exact reviewed head [`3b6a5eb4`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/3b6a5eb42eba79f561ce0ef42701142c7f4fdd7f)).
- [x] Separate real clean-isolated qualification from controlled production fixtures, forbid mockups, generated images, and the sealed safe demo from counting as production evidence, and require privacy-clean source-image hashes and provenance ([reviewed specification](linux-gui-screenshot-evidence.md)).
- [x] Link the screenshot contract from repository and GitHub Pages documentation while leaving every production capture, manifest, gallery, deployment, and HTTP-verification requirement unchecked until real evidence exists ([merged PR #190](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/190)).
- [x] Resolve the manual-console evidence wording finding, obtain a clean documentation/final-diff review, and pass required checks on the exact reviewed head ([PR #190 verification record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/190); [Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33236227822); [performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33236227816)).
- [x] Merge and close PR #190, delete its feature branch locally and on `origin`, and synchronize `develop` ([merge commit `903d94eb`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/903d94eb606094a6830f9bf63e8c316a376ff4cd)).

No production screenshot item is complete merely because the evidence contract exists. Every production image and its capture, privacy, provenance, publication, accessibility, desktop, and HTTP evidence remains unchecked above.

### Reviewed public release-acquisition authority

- [x] Parse a bounded public release catalog into deterministic candidates only when the fork tag and exact six public assets are present; enforce credential-free GitHub API/asset URI policy, per-asset and aggregate download bounds, unique names, and safe deterministic local labels ([merged PR #191](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/191); exact reviewed head [`30443a5d`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/30443a5d87a52e7bafa4159506884e86cec6c502)).
- [x] Resolve only an annotated tag reference through its tag object to its commit, require a fresh unchanged reference before exposing `SourceCommit`, and keep catalog, reference, and resolved-commit authorities immutable and nonconstructible outside Core ([merged implementation and review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/191)).
- [x] Reject malformed, duplicate, oversized, off-repository, lightweight, moved, cross-candidate, metadata-derived, and noncanonical inputs; pass 87/87 focused acquisition tests, 912/912 full Core Release tests, and a warnings-as-errors build with zero warnings and errors ([PR #191 verification record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/191)).
- [x] Cross-check the strict parser against the live read-only GitHub API shape and record that published alpha.1 has only the older three assets, so it is correctly excluded and cannot support a real production-GUI verified-success state ([live API evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/191#issuecomment-5460664535)).
- [x] Address the actionable authority-sequencing review finding, obtain a clean combined review, and pass exact-head required checks ([PR #191 review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/191); [Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33237021108); [performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33237021115)).
- [x] Merge and close PR #191, delete its feature branch locally and on `origin`, and synchronize `develop` ([merge commit `9294c88d`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/9294c88d8a9ce14e78a6eaf9108a120272647a34)).

This Core policy performs no networking, download, game discovery, package opening, or filesystem mutation. Production GUI release selection, download/progress, verification, and successful public-package evidence remain unchecked above.

### Bounded Linux protocol host

- [x] Add the sole Linux backend flag `--linux-protocol-v1-jsonl`, keep effective-UID-0 refusal first, route protocol mode before ambient package handling, reject mixed flags, and preserve the no-flag manual console path ([merged PR #192](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/192); exact reviewed head [`b4058f8e`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/b4058f8e9eb676de8e2e3426cc5590151d7f9ebd)).
- [x] Implement strict bounded incremental UTF-8 JSONL framing with one JSON-only standard-output owner, generic bounded diagnostics, fail-stop output, bounded admission, explicit cancellation, clean-EOF settlement, SIGTERM exit 130, and bounded teardown even for cancellation-resistant input or output ([security/privacy-reviewed implementation](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/192)).
- [x] Include Installer Core in the Linux package and prove the trimmed packaged handshake, root/mixed-flag ordering, clean EOF, FIFO handshake followed by SIGTERM, exact diagnostics, zero child processes, and empty disposable state without relying on ambient `install.dat` ([merged package/lifecycle integration](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/192)).
- [x] Pass 138/138 focused protocol/service/serializer/state tests, 13/13 host regressions, 927/927 full Core Release tests, repeated cancellation-resistant writer coverage, and a warnings-as-errors build with zero warnings and errors ([exact-head Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33237870897)).
- [x] Address every actionable shutdown, cancellation, writer, SIGTERM, watchdog, diagnostic-exactness, architecture, and documentation finding and obtain clean independent re-reviews ([architecture/compatibility review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/192#issuecomment-5460729556); [final-diff finding and clean re-review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/192#issuecomment-5460747826)).
- [x] Pass exact-head required checks, merge and close PR #192, delete its feature branch/worktree locally and on `origin`, and synchronize `develop` ([Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33237870897); [performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33237870899); [merge commit `56e56671`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/56e56671d3c3baf1139fd6422ea7d8588645e1d0)).

The protocol host is not the production GUI and does not migrate the retained manual console adapter onto Core operations. Production discovery, networking, GUI orchestration, operation screens, mutation, packaging, and real-success evidence remain unchecked above and below.

### Disconnected GUI backend bridge foundation

- [x] Make no-argument launch select production while keeping exact `--demo` as the sealed synthetic demo, refuse effective UID 0 before Avalonia or any process/network/staging/logging side effect, and fail the uncomposed production path closed instead of presenting the demo as a live installer ([merged PR #194](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/194); exact reviewed head [`080b9531`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/080b9531c40b40d274fcad62792b46ade30bbbde)).
- [x] Open only the exact `SMAPI.Installer` sibling through anchored no-follow executable authority; require a bounded same-UID, owner-executable, regular single-link inode and launch its retained parent-process `/proc/<pid>/fd/<fd>` identity without `PATH`, a shell, or a later executable-path lookup ([architecture/security review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/194#issuecomment-5461044164)).
- [x] Add a bounded asynchronous live-session client exposing only handshake and verified package-open operations; enforce strict UTF-8 JSONL framing, one correlated request at a time, exact release/package binding, authority revocation on duplicate, partial, delayed, malformed, or mismatched output, sanitized rejection/error surfaces, cancellation deadlines, shared disposal, and truthful bounded reap/quarantine behavior ([clean exact-head review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/194#issuecomment-5461044345)).
- [x] Exercise the actual `SMAPI.Installer` apphost through its retained descriptor across a handshake and two normal package rejections in one live session; pass 37/37 focused protocol-client tests, 74/74 GUI tests in Debug and Release, 940/940 Core Release tests, repeated race/frame/quarantine tests, Release warnings-as-errors, formatting, and diff checks ([security test record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/194#issuecomment-5461044164); [testing review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/194#issuecomment-5461044345); [Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33240747549)).
- [x] Address every actionable executable-authority, framing/deadline, response-binding, cancellation, disposal, reap/quarantine, diagnostics/privacy, architecture, testing, and final-diff finding and obtain clean exact-head re-reviews ([security review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/194#issuecomment-5461044164); [final review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/194#issuecomment-5461044345)).
- [x] Pass exact-head required checks, merge and close PR #194, delete its feature branch/worktree locally and on `origin`, and synchronize `develop` ([Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33240747549); [performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33240747559); [merge commit `4bdd2cee`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/4bdd2cee71ff216f16505f26ee85b66322d8c272)).

No production composition invokes this bridge, and this slice adds no GitHub networking, release download, game discovery, planning, game-file mutation, backup, rollback, or GUI operation orchestration; no production screenshot is justified by it. Its multi-file .NET apphost companion files remain directory-resolved; procfs is required; same-UID hostile mutation is outside the supported process boundary. Production release selection/verification UI, operation screens, packaging, public-artifact qualification, and every production screenshot remain unchecked above and below.

### Retained reviewed-release acquisition authority

- [x] Add one high-level Core acquisition entrypoint which accepts only an unforgeable reviewed catalog candidate, downloads its exact six assets sequentially through the credential-free reviewed GitHub policy, and exposes no public caller-selected root, pathname, transport, downloader, HTTP, redirect, or policy authority ([merged PR #196](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/196); exact reviewed head [`816fe03b`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/816fe03bb9cacb8d8724dd8fccb446cf6421bb27)).
- [x] Create a fresh exclusive same-effective-user mode-0700 retained Linux workspace before network I/O; publish only the six Core-derived mode-0600, single-link, no-special-bit files through anchored no-follow/no-replace authority, with exact catalog-advertised, per-asset, aggregate, timeout, and redirect bounds ([security/privacy review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/196#issuecomment-5461199611)).
- [x] Retain an opaque lease and internal process-descriptor directory projection, fence cancellation before and after every publication including the sixth, and perform bounded idempotent cleanup of only exact owned identities without recursive deletion, globbing, or a pathname fallback; leave renamed, replaced, modified, or unknown entries untouched ([architecture/test review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/196#issuecomment-5461199692)).
- [x] Reject short, oversized, declared- and unknown-length mismatches, symlinks, hard links, FIFOs, directories, wrong owners, unsafe modes, special bits, extra entries, leaf/workspace replacement, wrong release/tag authority, and disposed leases; pass 17/17 focused acquisition tests, 118/118 security-related tests, 109/109 architecture-relevant tests, 20 repeated acquisition-suite runs, and 956/956 full Core Release tests ([review evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/196#issuecomment-5461199611); [test evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/196#issuecomment-5461199692)).
- [x] Address every actionable directory-creation, cleanup-identity, post-rename publication, exact-length, progress-ordering, mode-compatibility, directory-enumeration, API-authority, cancellation, disposal, privacy, and temporal-wording finding and obtain clean exact-head security and testing re-reviews ([security review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/196#issuecomment-5461199611); [testing review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/196#issuecomment-5461199692)).
- [x] Pass exact-head required checks, merge and close PR #196, delete its feature branch/worktree locally and on `origin`, and synchronize `develop` ([Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33242246807); [performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33242246815); [merge commit `cae15f2a`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/cae15f2acbc16689f2283b053aa9734e765caaca)).

The acquisition slice by itself is not a completed production download or verification screen. The dependent retained acquisition-to-backend bridge below now accepts its strictly bound projection. The future serialized controller must still acquire the fresh tag reference after all six downloads, resolve it, bind the live lease, and wait for authoritative package-open before presenting success. No game discovery, planning, game-file mutation, GUI success, public GUI artifact, or production screenshot is justified by these backend slices, so those requirements remain unchecked above and below.

### Retained acquisition-to-backend bridge

- [x] Expose only one sealed immutable projection issued by a live same-candidate acquisition lease, with no public constructor or broad Core-internals grant to the GUI ([merged PR #198](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/198); exact reviewed head [`688edad0`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/688edad01165f8d835402ee956c1d71f80205d78)).
- [x] Capture the direct controller's proc descriptor directory once at backend-session construction; require stable parent PID, same effective UID, procfs, a mode-0500 directory with no special bits and the expected link count, reject root before opening it, and never re-anchor ([security/privacy review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/198#issuecomment-5461387489)).
- [x] Accept only six canonical `/proc/<direct-parent>/fd/<one-descriptor>/<exact-leaf>` paths with positive canonical parent PID, nonnegative canonical descriptor, exact field-specific leaves, and no mixed ordinary/proc authority ([final exact-head review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/198#issuecomment-5461403275)).
- [x] Bind the workspace's kernel-observed device, inode, and ctime through the strict unshipped Protocol V1 request; reject stale descriptor reuse, reparenting, workspace mutation, noncanonical or unknown nested fields, and missing or unexpected proc bindings ([architecture review and resolved findings](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/198#issuecomment-5461388456)).
- [x] Open one same-UID mode-0700 workspace and synchronously retain the exact six same-UID mode-0600, single-link, no-special-bit regular files before the first verification await; repeat the workspace, name, parent, and leaf-identity fences after capture ([security/privacy review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/198#issuecomment-5461387489)).
- [x] Route package, checksum, metadata, install-manifest, attestation-bundle, and bundle-checksum verification through transferred retained handles; require each read/copy operation's initial snapshot to equal the full captured identity and its final snapshot to equal the initial snapshot ([architecture review and resolved findings](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/198#issuecomment-5461388456)).
- [x] Preserve ordinary anchored no-follow verification when proc authority is unavailable, store only a sanitized one-shot unavailable state, reject proc requests without retry or re-anchoring, propagate privilege refusal, and dispose the authority only after active service work settles ([final exact-head review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/198#issuecomment-5461403275)).
- [x] Exercise the actual built installer apphost with six private parent-proc assets; prove a correlated sanitized package rejection, malformed-proc rejection followed by a healthy retry in the same session, confirmed child reap, closed descriptors, exact workspace cleanup, and 20/20 repeated runs without network or private fixtures ([PR verification record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/198)).
- [x] Pass 1000/1000 Core Release tests, 76/76 GUI Release tests, 76/76 GUI Debug tests, independent focused 161/161 Core and 2/2 actual-host/mapper tests, warning-as-error rebuilds, formatting, and diff checks on exact head `688edad0` ([final exact-head review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/198#issuecomment-5461403275)).
- [x] Address every actionable descriptor-reuse, parent identity, capability-surface, protocol-schema, settled-byte mutation, ordinary-path fallback, lifecycle, testing, architecture, security/privacy, and final-diff finding; obtain clean exact-head re-reviews ([security/privacy review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/198#issuecomment-5461387489); [architecture review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/198#issuecomment-5461388456); [final review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/198#issuecomment-5461403275)).
- [x] Pass exact-head required checks, merge and close PR #198, delete its feature branch/worktree locally and on `origin`, and synchronize `develop` ([Linux qualification](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33244023515); [performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33244023510); [merge commit `cba76355`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/cba76355294bb648ed3bf376faebfeea990c0f3a)).

This bridge still adds no production release-selection controller, GitHub networking orchestration, game discovery, planning, game-file mutation, or GUI success state. Production screenshots remain unjustified until the next real UI slice is implemented, packaged, and qualified.

### Reviewed production release-verification screen slice

- [x] Compose the real no-argument Avalonia window around the reviewed release service and direct-child protocol client while preserving the sealed synthetic experience only behind exact `--demo`; keep effective-UID-0 refusal ahead of Avalonia, network, process, staging, and logging side effects ([merged PR #200](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/200); exact reviewed head [`ce919020`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/ce9190205955df0a6bcc787e47f2982f6fc5d6e9)).
- [x] Load only complete compatible fork prereleases; observe the selected annotated tag, acquire the exact six assets with bounded aggregate progress, refresh the tag, bind the retained lease, and publish verified identity only after authoritative backend package-open success, with accepted cancellation and later session faults excluding or revoking success ([architecture re-review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/200#issuecomment-5461670487); [security/privacy/lifecycle re-review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/200#issuecomment-5461670474)).
- [x] Present truthful loading, no-compatible-release, selection, download, verification, cancellation, cleanup, failure, and verified-not-installed states; keep retry bounded, retain the release lease until package-open settles, hide the unwired next-screen action, expose only typed safe next steps, and display no private path, URI, credential, backend text, or workspace detail ([final UX/accessibility/testing review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/200#issuecomment-5461670480)).
- [x] Verify real Alt+E/Alt+D/Alt+C actions, forward and reverse dynamic focus traversal, readable focus, semantic headings, one exact live error announcement, accessible contrast, and rendered 420-DIP plus 100%, 125%, 150%, and 200% scale layouts without horizontal page scrolling ([final UX/accessibility/testing review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/200#issuecomment-5461670480)).
- [x] Pass 134/134 GUI tests in Debug and Release, 1001/1001 Core Release tests, repeated controller-race and access-key coverage, warnings-as-errors builds, formatting, diff checks, and exact-head required CI ([Linux qualification `33246585574`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33246585574); [performance gates `33246585412`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33246585412)).
- [x] Address every actionable architecture, networking, lifetime, cancellation, cleanup, privacy, UX, accessibility, testing, and final-diff finding; merge and close focused PR #200 into `develop` ([merge commit `3fafbbcf`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/3fafbbcf7a6c8691430e5fa2e7bf17f84ae5d0a4)).

Published alpha.1 still has only the older three assets, so the production catalog correctly shows no compatible graphical-installer release and cannot provide real verified-success evidence. This slice performs no game discovery, planning, installation, update, repair, uninstall, backup, rollback, or game-file mutation. It is not yet packaged as a public GUI artifact: every production packaged screenshot, clean-isolated public-artifact qualification, screenshot manifest/gallery, X11/XWayland capture, and HTTP publication check therefore remains unchecked above.

### Reviewed production game-discovery screen slice

- [x] Extend Protocol V1 with strict bounded automatic discovery and manual game-folder validation backed only by the shared read-only Linux discovery service; reject malformed, duplicate, excessive, noncanonical, or mismatched results ([merged PR #202](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/202); exact reviewed head [`8a1f6694`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/8a1f6694cf4b5e15e201cd19102c7d5e26e4785b)).
- [x] Transfer the exact verified package/backend session from release verification into discovery exactly once, require the package, discovery, and validation capabilities, preserve command/session correlation and backend-canonicalized path authority, and fail closed on handoff or session failure ([architecture and security review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/202#issuecomment-5491551755)).
- [x] Present truthful zero-, one-, and multiple-candidate states, automatically select only one valid candidate, support native manual folder selection with typed invalid reasons, and expose retry, cancel, and terminal session-fault behavior without planning or modifying game files ([merged production workflow](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/202)).
- [x] Provide keyboard access, readable focus, semantic live regions, distinct bounded screen-reader row names, bidi/surrogate-safe path display, and rendered 420-DIP plus 100%, 125%, 150%, and 200% scale coverage with awaited window/session teardown ([clean UX/accessibility/test review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/202#issuecomment-5491551755)).
- [x] Make disposal, terminal session faults, explicit cancellation, and linked caller-token cancellation authoritative across success commit, failure outcome, and finalization boundaries for automatic and manual operations; retain at most 64 candidates and sanitize picker/backend failures ([clean architecture/security review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/202#issuecomment-5491551755)).
- [x] Pass 184/184 GUI tests in Debug and Release, 159/159 focused Core protocol/discovery tests, five consecutive 44/44 focused discovery/workflow runs, Release warnings-as-errors, formatting, diff checks, and exact-head required CI ([Linux qualification `33489707978`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33489707978); [performance gates `33489707974`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33489707974)).
- [x] Address every actionable architecture, concurrency, lifecycle, security/privacy, UX/accessibility, testing, path-display, and final-diff finding; merge and close PR #202 into `develop`, delete its feature branch/worktree locally and on `origin`, and synchronize `develop` ([merge commit `7018b271`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/7018b271ab6092b83011911552c149d430ceb81d)).

This slice is read-only after release verification. It does not plan, install, update, repair, uninstall, back up, roll back, or otherwise mutate game files. It is not packaged in a public GUI artifact, and published alpha.1 cannot reach it because that release lacks the required six-asset authority set. Production screenshot claims remain unchecked until an exact reviewed packaged build can exercise these states in clean isolated qualification.

### Terminal game-discovery session semantics correction

- [x] Match the real process client by treating accepted discovery or manual-validation cancellation as a terminal backend-session closure; remove the fake-session-only retry promise and reject retry, browse, selection, validation, discovery, or candidate reuse after cancellation, backend failure, or session fault ([merged corrective PR #204](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/204); exact reviewed head [`75edac68`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/75edac6893bc9c2805f46991fd139d31143be673)).
- [x] Clear both discovered and manual candidate authority before every closed-state publication; make operation-side `SessionFaulted` self-sufficient while the independent watcher is paused, preserve published fault/failure precedence during late cancellation, and keep every terminal action gate fail-closed ([exact-head architecture and security review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/204#issuecomment-5492180949)).
- [x] Join cancellation, failure, watcher, operation-finalization, window-close, and controller-disposal paths through one exactly-once verified-session cleanup task; expose a focused `Alt+E` and Escape terminal Exit action with polite cancellation and assertive failure/fault announcements ([exact-head UX/accessibility review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/204#issuecomment-5492180949)).
- [x] Remove the unwired discovery Continue button, command, event, and candidate-forwarding seam until the real downstream screen can atomically receive both candidate and verified-session ownership; retain only inert internal readiness and strictly read-only endpoint copy ([review resolution and boundary](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/204)).
- [x] Pass 190/190 GUI tests in Debug and Release, five consecutive 50/50 focused discovery/workflow runs, the deterministic watcher-paused session-fault race, Release warnings-as-errors, formatting, diff checks, and exact-head required CI ([Linux qualification `33494204886`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33494204886); [performance gates `33494204875`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33494204875)).
- [x] Address every actionable architecture, lifecycle, authority, security/privacy, UX/accessibility, testing, and copy finding; obtain clean exact-head re-reviews, merge and close PR #204, delete its feature branch/worktree locally and on `origin`, and synchronize `develop` ([review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/204#issuecomment-5492180949); [merge commit `a4af3482`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/a4af348269c7fb0894a2e6325eb0cd73aa46fbd0)).

This correction adds no planning, execution, download, package, screenshot, or game-file mutation behavior. The discovery endpoint remains deliberately read-only; its future next action must be introduced only with the downstream screen and one atomic ownership handoff. Production screenshot and packaged-GUI requirements therefore remain unchecked.

### Capability-reduced read-only plan-inspection foundation

- [x] Atomically transfer one exact backend-issued valid game candidate together with the retained verified package session, bind it once to a restricted plan-inspection child, and make the parent inert after transfer ([merged PR #206](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/206); exact reviewed head [`855fe7a3`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/855fe7a35fcb3263b2d35b4edc6861decb8edb13)).
- [x] Require the plan lifecycle capability during handshake and expose only read-only Install, Update, Repair, Uninstall, and Backup inspection; reject Rollback and unknown operations locally before protocol admission ([exact-head review evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/206#issuecomment-5492940970)).
- [x] Fetch every advertised nonempty plan page sequentially under one aggregate deadline and strict 512-page/16-MiB bounds; verify exact session, candidate, package, operation, release, page order, duplicate, count, risk, default, confirmation, executability, and complete-digest semantics before presentation ([merged implementation](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/206)).
- [x] Project only bounded aggregate counts and truthful risk/confirmation properties, with no session, plan, package, candidate, path, digest, evidence, raw warning, approval, confirmation, or execution authority reaching presentation code ([security/privacy review evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/206#issuecomment-5492940970)).
- [x] Revoke and dispose the bound child on malformed transport, cancellation, timeout, terminal rejection, or session fault; preserve exactly-once cleanup across concurrent disposal and bound stale manual/discovery candidate authority ([exact-head lifecycle review evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/206#issuecomment-5492940970)).
- [x] Keep the exact canonical game path private to the bound child while presenting bounded control-, bidi-, separator-, and surrogate-safe display text; announce the transferred state through a focused polite status region which states both read-only scope and that nothing has changed ([resolved accessibility finding](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/855fe7a35fcb3263b2d35b4edc6861decb8edb13)).
- [x] Align receiptless Backup current/target-release semantics and enforce the exact globally valid pre-plan rejection code/action/terminal/log matrix while narrowing the process client to InspectPlan-reachable codes ([merged protocol correction](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/206)).
- [x] Pass 281/281 GUI Release tests, 1007/1007 Core Release tests, 177/177 focused discovery/session/process/accessibility tests, five consecutive transferred-state repetitions, formatting and diff checks, and exact-head required CI ([Linux qualification `33500074259`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33500074259); [performance gates `33500074227`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33500074227)).
- [x] Address the actionable lifecycle, operation-admission, capability, display-safety, stale-authority, Backup-semantics, rejection-matrix, projection-copy, transfer-test, and live-announcement findings; obtain clean exact-head final-diff, security/privacy/concurrency, and UX/accessibility/testing reviews ([final evidence record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/206#issuecomment-5492940970)).
- [x] Merge and close PR #206, delete its feature branch/worktree locally and on `origin`, and synchronize `develop` ([merge commit `ca12ddd5`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/ca12ddd56334ecdad79b011900bf7934a1f99a42)).

This foundation deliberately adds no visible plan screen, approval, confirmation, execution, progress stream, game-file mutation, recovery selection, rollback, download, packaging, or production screenshot. Those Phase 4 requirements remain unchecked until their own exact reviewed, packaged, clean-isolated evidence exists.

### Production-visible read-only plan review

- [x] Add a production-visible plan-review screen after verified release selection and exact game discovery, with one atomic ownership handoff into the downstream window and deterministic closure of any partially activated discovery or plan window ([merged PR #208](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/208); exact reviewed head [`4f2af884`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/4f2af884dfff905f7ec7a5da1838724455c1185a)).
- [x] Present exactly five explicit, no-default read-only choices—Install, Update, Repair, Uninstall, and Backup—and reject Rollback or undefined operations before contacting the backend ([exact-head evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/208#issuecomment-5493542843)).
- [x] Deep-copy only bounded aggregate plan facts into presentation state, validate exact fork release and typed operation/state/risk/rejection semantics, and expose no private path, backend object, identifier, digest, raw message, approval, confirmation, or execution authority through the view model or accessibility tree ([security/privacy review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/208#issuecomment-5493542843)).
- [x] Show truthful current/target release, observed-state, risk, operation/conflict/candidate aggregate, additional-notice, safe-default, and confirmation facts while explicitly labelling backend-provisional inclusion as neither user approval nor readiness ([merged implementation](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/208)).
- [x] Wait for exactly-once backend cleanup before publishing terminal completion, contain hostile cancellation callbacks and faulted session tasks, preserve cancellation/fault/disposal precedence, and cover the prior completion-versus-notification scheduling race with repeated tests ([exact-head lifecycle evidence](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/208#issuecomment-5493542843)).
- [x] Support keyboard-only operation, no-default initial focus, Alt-key access, Escape behavior, focused status/result/error/exit regions, readable focus, a 420-DIP responsive layout, and tested 1x–2x scaling without horizontal scrolling ([UX/accessibility review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/208#issuecomment-5493542843)).
- [x] Pass 343/343 GUI Release tests, five consecutive 58/58 focused plan-review repetitions, five consecutive 35/35 presentation/workflow repetitions, lifecycle/workflow tests, warnings-as-errors build, formatting/diff checks, and exact-head required CI ([Linux qualification `33504483527`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33504483527); [performance gates `33504483469`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33504483469)).
- [x] Address every actionable architecture, lifecycle, concurrency, validation, security/privacy, partial-activation, raw-automation, UX/accessibility, copy, and scheduling finding; obtain clean exact-head final-diff, security/privacy/concurrency, and UX/accessibility/testing reviews ([final evidence record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/208#issuecomment-5493542843)).
- [x] Merge and close PR #208, delete its feature branch/worktree locally and on `origin`, and synchronize `develop` ([merge commit `b825d553`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/b825d553c5dd99fab13406477c3a44495eeb68ab)).

This screen remains deliberately preview-only. Approval, confirmation, execution, mutation, progress, recovery, rollback, packaging, public-artifact qualification, and production screenshots remain unchecked until their own exact reviewed and clean-isolated evidence exists.

### Retained protocol execution-binding digest correction

- [x] Keep the public plan/prune presentation digest distinct from the retained Core confirmation digest, continue authenticating every public confirm, execute, cancel, progress, and terminal message against the public digest, and pass only the exact retained plan object's `ConfirmationDigest` into Core execution ([merged PR #210](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/210); exact reviewed head [`d702a532`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/d702a532dce3e08e6ce7d541ac21995955a99667)).
- [x] Cover ordinary execution, candidate-reissued plans, authenticated rollback plans, and recovery-prune plans; prove the inner execution-binding digest is rejected at every public confirmation, execution, cancellation, progress, and terminal boundary while the exact retained plan supplied to Core carries that binding ([focused protocol and state-machine tests](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/210)).
- [x] Execute a real Backup through the production protocol service and real Linux installer engine with deliberately distinct public and execution-binding digests; verify a committed typed terminal outcome and the new authenticated Backup recovery generation ([real-engine regression test](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/d702a532dce3e08e6ce7d541ac21995955a99667/src/SMAPI.Installer.Core.Tests/Protocol/V1/LinuxInstallerProtocolServiceRealEngineTests.cs)).
- [x] Pass 1011/1011 Core Release tests and 115/115 focused protocol/state-machine/real-engine tests, with clean formatting and diff checks and required CI runs [`33506935328`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33506935328) and [`33506935336`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33506935336).
- [x] Address the actionable testing/platform finding by combining NUnit's Linux execution gate with analyzer platform metadata; obtain clean testing/final-gate and exact-head finding-resolution reviews without changing GUI, documentation-claim, release, or screenshot behavior ([review and finding-resolution record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/210)).
- [x] Merge and close PR #210 at exact merge commit [`fffc7b25`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/fffc7b251ae73e8119023fc7a62a3859d2d0c2ab).

This correction proves only the retained Core execution binding and one real protocol-to-engine Backup regression. The production GUI still has no approval, confirmation, execution, progress, cancellation, interrupted-recovery, rollback, or recovery-prune surface. A publicly downloadable GUI package, clean-isolated GUI lifecycle qualification, and every production screenshot remain unchecked; no broader Phase 4 checkbox is completed by this subsection.

### Durable recovery-catalog equality and real prune regression

- [x] Compare reopened recovery release identities by immutable value at the exact authenticated catalog boundary, accepting distinct value-equal instances while continuing to reject distinct non-null unequal releases and null/non-null mismatches ([merged PR #212](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/212); exact reviewed feature head [`646506fe`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/646506fe1818c6be13b78b91c0b5264638eff993)).
- [x] Execute a real Linux install, Backup, persisted-history reload, reopened recovery catalog, and authenticated retain-one prune through the production protocol service and real installer engine; verify committed Backup durability, two catalog generations, value-equal reopened release identity, typed prune success, one logical removal, one physical cleanup, and one durable retained generation ([real-engine regression test](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/646506fe1818c6be13b78b91c0b5264638eff993/src/SMAPI.Installer.Core.Tests/Protocol/V1/LinuxInstallerProtocolServiceRealEngineTests.cs)).
- [x] Preserve deterministic Linux-only execution gating, isolated GUID-scoped fixture directories, bounded fixture cleanup, and explicit authority, inspection, recovery-handle, and protocol-service disposal ([testing/platform review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/212)).
- [x] Pass 158/158 Protocol V1 tests, 28/28 focused state-machine/real-engine tests, and 1014/1014 Core Release tests; pass fresh required CI runs [`33508802614`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33508802614) and [`33508802774`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33508802774).
- [x] Address the actionable assertion-completeness finding by adding a valid non-null unequal release-identity rejection case, retaining a separate null mismatch case, and asserting the accepted value-equal release projection; obtain clean exact-head testing/platform/final-gate review and clean post-base-update re-review ([finding-resolution and review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/212); updated reviewed merge head [`617d1f7f`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/617d1f7f6868f3ced6f0983dc82e427696a08a4a)).
- [x] Merge and close PR #212 at exact merge commit [`065af992`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/065af99290707f920fa707899e8d42e5e3ae6f95).

This subsection proves only Core/protocol recovery-catalog equality and real install-to-Backup-to-catalog-to-prune durability. The production GUI recovery, rollback, and destructive-prune surfaces remain unchecked, as do GUI packaging, public-artifact qualification, and every required production screenshot; no broader Phase 4 checkbox is completed here.

### Bounded candidate-approval and refreshed-plan foundation

- [x] Require the existing `candidate-approval` protocol capability and keep the exact plan ID/digest, complete game-root generation, opaque candidate IDs, observed file identities, and raw backend evidence private inside the process client; expose only normalized and escaped relative display paths, typed reason/disposition, and exact-reference scoped approval capabilities ([merged PR #214](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/214); exact reviewed head [`86d9638c`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/86d9638c79ff7cb21512d93972bb2e66a8bad92f)).
- [x] Reject empty, overbound, duplicate, foreign, stale, mixed, resurrected, reassigned, and cross-swapped candidate capabilities or IDs before they can acquire new authority; preserve a valid current binding after pre-wire validation failures and revoke it before an admitted request writes ([process-client and bound-session tests](https://github.com/4eh5xitv6787h645ebv/SMAPI/tree/86d9638c79ff7cb21512d93972bb2e66a8bad92f/src/SMAPI.Installer.Gui.Tests)).
- [x] Keep candidate ID/reference tombstones for the authenticated session, bound them at 65,536 entries so the complete 256-candidate one-at-a-time approval chain remains supported, fail closed at capacity, and clear them only during final fault/cleanup/disposal ([process-client ID tombstones](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/86d9638c79ff7cb21512d93972bb2e66a8bad92f/src/SMAPI.Installer.Gui/Backend/ProcessInstallerProtocolClient.cs); [bound-session reference tombstones](https://github.com/4eh5xitv6787h645ebv/SMAPI/blob/86d9638c79ff7cb21512d93972bb2e66a8bad92f/src/SMAPI.Installer.Gui/Backend/VerifiedInstallerSession.cs)).
- [x] Accept only the exact nonterminal `CandidateApprovalFailed`/`InspectAgain` rejection and fully validate every replacement plan: fresh retained protocol plan ID/digest, identical package ID, verified target-release identity, and full game-root generation, selected-candidate disappearance, globally fresh remaining IDs, unchanged remaining candidate semantics/observed identities, complete paging, and a recomputed presentation digest ([replacement-validation implementation and tests](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/214)).
- [x] Snapshot caller selections and backend result candidates with one bounded indexed read, outside lifecycle locks; discard hostile exception detail, return a stable read-only outward copy containing the exact retained references, and cover null, negative, oversized, lying, changing, throwing, and reentrant-disposal collections for initial inspection and replacement results ([final privacy/lifecycle fix](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/86d9638c79ff7cb21512d93972bb2e66a8bad92f)).
- [x] Preserve cancellation, session-fault, disposal, queued-command, precommit, and late-result precedence with exactly-once cleanup; pass 188/188 focused backend/API/host tests, 409/409 full GUI Release tests, and 250/250 repeated hostile/lifecycle/race cases with a zero-warning Release build and clean formatting/diff checks ([exact-head review record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/214#issuecomment-5494994220)).
- [x] Pass fresh exact-head required CI runs [`33515242040`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33515242040) and [`33515242027`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33515242027); verify the actual built protocol host advertises the required capability without adding a package-verification bypass, committed release fixture, private workload, download, or CI dependency.
- [x] Address every actionable stale-generation, boundedness, lifecycle-race, privacy-contract, and hostile-result-collection finding; obtain clean final exact-head architecture, security/privacy, and testing/lifecycle re-reviews; merge and close PR #214 and delete its feature branch/worktree locally and on `origin` ([merge commit `dcddbba7`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/dcddbba725b106f38d28005516053be621f89984)).

This foundation authorizes only additive candidate selection followed by a newly validated read-only preview. The production GUI still has no visible candidate controls, confirmation, execution, mutation, progress, recovery, rollback, or prune surface. The self-contained actual-host inspect-to-approval lifecycle still requires a cryptographically valid reviewed six-asset package and remains for exact release-package qualification; GUI packaging, public-artifact qualification, and every required production screenshot remain unchecked.

### Phase 4 tests, reviews, packaging, and integration

- [ ] Add GUI unit tests.
- [ ] Add GUI integration tests.
- [ ] Add failure-path tests.
- [ ] Add interrupted-download tests.
- [ ] Add corrupted-package tests.
- [ ] Add rollback tests.
- [ ] Add accessibility-focused tests.
- [x] Obtain [independent installer architecture review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/175#issuecomment-5453931128).
- [x] Obtain [independent security/privacy review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/175#issuecomment-5453890576).
- [x] Obtain [independent UX/accessibility review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/175#issuecomment-5453890879).
- [x] Obtain [independent Phase 4 testing review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/175#issuecomment-5453890879).
- [ ] Obtain independent Phase 4 final-diff review.
- [ ] Address every actionable Phase 4 review finding.
- [ ] Package and document the GUI through the release workflow only after independent security/privacy and UX/accessibility findings are addressed.
- [ ] Document GUI installation, update, repair, uninstall, backup, rollback, logs, errors, and troubleshooting.
- [x] Open focused [Phase 4 fork pull request #175](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/175).
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
- [ ] Validate diagnostics-disabled overhead with synthetic maximum-capacity fixtures.
- [ ] Validate privacy exclusions with synthetic maximum-capacity fixtures.
- [ ] Validate bounded behavior with synthetic maximum-capacity fixtures.
- [ ] Obtain an independent Phase 5 architecture review.
- [ ] Obtain an independent Phase 5 security/privacy review.
- [ ] Obtain an independent Phase 5 UX/accessibility review.
- [ ] Obtain an independent Phase 5 testing review.
- [ ] Obtain an independent Phase 5 documentation review.
- [ ] Obtain an independent Phase 5 final-diff review.
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
- [ ] Obtain an independent Phase 6 architecture review.
- [ ] Obtain an independent Phase 6 performance review.
- [ ] Obtain an independent Phase 6 security/privacy review.
- [ ] Obtain an independent Phase 6 UX/accessibility review.
- [ ] Obtain an independent Phase 6 testing review.
- [ ] Obtain an independent Phase 6 documentation review.
- [ ] Obtain an independent Phase 6 final-diff review.
- [ ] Address every actionable Phase 6 review finding.
- [ ] Open a focused Phase 6 fork pull request.
- [ ] Pass required CI and repository checks.
- [ ] Merge the Phase 6 pull request into `develop` and close it.
- [ ] Verify `develop` equals `origin/develop` after the merge.

## Phase 7 — Upstream-ready optimizations (deferred by user)

The user explicitly deferred all Phase 7 work on 2026-08-28. The scope decision is checked below, while
the original requirements remain unchecked and visibly deferred; no upstream review, branch change,
or pull request was performed.

- [x] Record the user's 2026-08-28 decision to defer Phase 7 for now.

### Selection and preparation

- [ ] Deferred/out of current scope: fetch and review the complete fork delta against current `Pathoschild/SMAPI` `develop`.
- [ ] Deferred/out of current scope: identify low-risk, platform-neutral correctness or performance improvements that separate cleanly from Linux-only features.
- [ ] Deferred/out of current scope: keep Linux-only or high-risk experimental changes in this fork.
- [ ] Deferred/out of current scope: document Linux-only or high-risk experimental changes not submitted upstream and explain why.
- [ ] Deferred/out of current scope: rebase every selected change onto current upstream `develop`.
- [ ] Deferred/out of current scope: minimize every selected upstream diff.
- [ ] Deferred/out of current scope: add upstream-appropriate correctness tests.
- [ ] Deferred/out of current scope: add upstream-appropriate benchmarks or performance evidence.
- [ ] Deferred/out of current scope: preserve upstream style.
- [ ] Deferred/out of current scope: preserve upstream platform and API compatibility.
- [ ] Deferred/out of current scope: ensure no private fixture data appears in commits, test assets, PR bodies, or artifacts.

### Submission and follow-through

- [ ] Deferred/out of current scope: obtain an independent Phase 7 upstream-readiness review.
- [ ] Deferred/out of current scope: obtain an independent Phase 7 architecture review.
- [ ] Deferred/out of current scope: obtain an independent Phase 7 performance review.
- [ ] Deferred/out of current scope: obtain an independent Phase 7 testing review.
- [ ] Deferred/out of current scope: obtain an independent Phase 7 documentation review.
- [ ] Deferred/out of current scope: obtain an independent Phase 7 final-diff review.
- [ ] Deferred/out of current scope: address every actionable Phase 7 review finding.
- [ ] Deferred/out of current scope: submit a focused upstream GitHub pull request for each justified change.
- [ ] Deferred/out of current scope: verify every submitted upstream PR is valid and CI-clean.
- [ ] Deferred/out of current scope: respond to actionable upstream review or CI findings received during this goal.
- [ ] Deferred/out of current scope: mark maintainer-only approval or merge items **externally pending** when applicable.
- [ ] Deferred/out of current scope: open a focused Phase 7 documentation/integration pull request on the fork.
- [ ] Deferred/out of current scope: pass required fork CI and repository checks.
- [ ] Deferred/out of current scope: merge the Phase 7 fork pull request into `develop` and close it.
- [ ] Deferred/out of current scope: verify `develop` equals `origin/develop` after the merge.

## Final definition of done

- [ ] Every umbrella issue and roadmap checkbox is complete with linked evidence, explicitly deferred by the user, or marked **externally pending** where only an upstream maintainer can act.
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
- [ ] Documentation screenshots cover every major GUI workflow and state listed in Phase 4.
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
