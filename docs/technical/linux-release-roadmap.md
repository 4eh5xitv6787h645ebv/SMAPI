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

### GUI screenshots and visual documentation

- [ ] Capture current screenshots from the exact reviewed GUI build, without private fixture names, paths, or other personal data.
- [ ] Document game-folder detection and manual selection with screenshots.
- [ ] Document install, update, repair, and uninstall states with screenshots.
- [ ] Document backup, recovery selection, and rollback states with screenshots.
- [ ] Document download progress, checksum verification, and successful completion with screenshots.
- [ ] Document understandable error, modified/unknown-file protection, and detailed-log views with screenshots.
- [ ] Document keyboard focus, practical screen scaling, X11, and Wayland behavior with screenshots.
- [ ] Add the current screenshot set to both repository user documentation and GitHub Pages.
- [ ] Verify every published screenshot and its containing documentation page returns HTTP 200.

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
- [x] Emit and fully verify the exact next-alpha quartet of installer ZIP, manifest, two-subject `SHA256SUMS`, and plural-artifact build metadata only on the annotated-tag workflow path ([workflow and authority review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179#issuecomment-5458606915)).
- [x] Keep pull-request and pre-tag dispatch artifacts explicitly non-authoritative, with actual-workflow candidate metadata and no manifest companion ([clean exact-head workflow review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179#issuecomment-5458606915)).
- [x] Inspect the exact prepared candidate ZIP through the shared bounded outer/nested layout, path, type, mode, allowlist, and canonicalization checks before a tag exists, without emitting authority artifacts (real 52 MB artifact: 107 outer support files, 252 payload files, 95,010,744 expanded bytes; [verification record](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179#issuecomment-5458606915)).
- [x] Reject hostile paths, collisions, special files and modes, corrupt or missing nested payloads, resource-bound violations, linked inputs, non-executable launchers, tampered quartet members, and mismatched release identity ([516-case Core and 21-case PackageTool results](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179#issuecomment-5458606915)).
- [x] Verify both checksummed subjects after workflow-artifact transfer, attest both subjects, and publish exactly the four named files on a tagged next-alpha run ([independent security/workflow review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179#issuecomment-5458606915)).
- [x] Document the non-cryptographic environment guard, two-attestation provenance boundary, unauthenticated informational build fields, external companion model, and immutable alpha.1 history ([clean documentation re-review](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/179#issuecomment-5458606915)).
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
