---
layout: default
title: Linux alpha release validation
description: Sanitized qualification and clean-room verification record for the experimental Linux alpha.
kicker: Publication evidence record
---

This page is the sanitized evidence record for the first Linux alpha. It deliberately excludes the
private modpack, save, mod identities, configuration, local paths, logs, and report contents.

## Candidate identity

The [experimental prerelease](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1)
was published on 28 August 2026 from this immutable identity:

| Field | Verified value |
| --- | --- |
| Embedded version | `4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1` |
| Annotated tag | `fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1` |
| Tag object | `8b9ec025c241da50695471d8f7c7f54ed003a8b4` |
| Source commit | `6e5d708a09e7d2b6d9b5434bd1fac52ddbdb5f08` |
| Source tree | `0cbabfd1f7934433f3ad0c0f1874c89ba6f75773` |
| Installer | `SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip` |
| Installer size | 52,183,224 bytes |
| Installer SHA-256 | `94e5dd4af8075946143ae79ba206d90b1433351c84dceb4f4506c74e638d69c8` |

The annotated tag peels to the source commit above. Its tree exactly matches the independently
reviewed pull-request head, so the merge introduced no unreviewed tree change. An active repository
ruleset blocks update and deletion of matching alpha tags with no bypass.

## Automated qualification

The [tag-triggered release workflow](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33177145353)
passed the fixture-free runtime-dispatcher and analyzer tests, all Release builds, formatting check,
package structure test, and isolated install/update/uninstall/failure lifecycle test for the exact
release commit. Hosted CI uses pinned public game reference assemblies for
compilation only; because those assemblies are intentionally non-executable, the complete
game-bound test suite is run separately against executable assemblies in the authorized disposable
environment. That exact-commit run discovered 1,871 tests: 1,868 passed, three existing platform
cases were skipped, and none failed. Hosted fixture-free execution ran 21 dispatcher and 13 analyzer
tests with zero-test discovery treated as an error.

The exact merge candidate also passed the required deterministic performance gate, package
checksum and metadata checks, lifecycle and failure-path tests, official-4.5.2 rollback, and
independent release, security/privacy, testing, and final-diff reviews. Every actionable finding was
fixed before merge and the final tag-readiness review passed. The
[sanitized evidence record](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770)
links the workflow, review, and aggregate results.

## Clean isolated verification

A fresh download was verified and exercised in a new disposable Linux environment. Only environment
metadata and aggregate pass/fail evidence were retained:

- tag and full commit equality;
- `sha256sum --check` and GitHub attestation verification;
- normal-user install, update, uninstall, and official-4.5.2 rollback;
- preservation of unrelated game files, custom mods, saves, user configuration backup, current
  logs, and local Health Reports;
- default net6 runtime launch with the complete trusted workload;
- Mod Health Report generation and exact in-game viewer opening; and
- immutable input-manifest equality before and after the run.

The public download passed `sha256sum --check`. GitHub attestation verification bound its digest to
this repository, the tagged workflow, the immutable tag ref, and the exact source commit;
`build-metadata.json` independently matched the same identity, Release configuration, and
`linux-x64` runtime identifier.

Normal-user installation preserved the base game payload. The default-net6 complete trusted
workload loaded the expected 132 code mods and 176 content packs and reached authorized-save
gameplay. A fresh schema-v1 JSON/text Health Report pair was generated, the exact in-game viewer
opened, and its private screenshot was visually checked. The reports passed JSON parsing,
local-only behavior, and host-path, disposable-root, and save-name exclusions.

A separate uninterrupted public-package run recorded 180.003 seconds of steady gameplay, 11,423
updates, 3,687 draws, zero invalid-world/location/position ticks, bounded buffers, both deterministic
warp transitions, and normal exit. Normal-user uninstall removed the fork runtime while preserving
the base game, complete Mods input, save, and local reports. The earlier exact-merge candidate run
independently recorded 180.019 seconds, 11,406 updates, 4,800 draws, the same state-validity results,
both transitions, and normal exit. The exact candidate also rolled back successfully to a
checksum-verified official SMAPI 4.5.2 installer.

The private fixtures, logs, reports, paths, configuration, and screenshots were neither committed
nor uploaded. The complete sanitized final record is on
[umbrella issue #168](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770).

The unrelated .NET 10 menu-click issue is not a release gate and is not included in this record.

## Public documentation and asset checks

The release page and all three public assets returned HTTP 200 during publication verification.
After the documentation follow-up merged as `af6e23bc`, the
[GitHub Pages deployment](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33179582509)
passed. Fresh HTTP checks returned 200 for the documentation home, getting-started guide,
comparison, release notes, alpha guide, validation record, and every committed screenshot. The
deployed pages contained the release tag link, public SHA-256, distinct prerelease identity, and
updated installation path; a repository-wide stale-wording check found no remaining inaccurate
no-release or pending-publication claim.
