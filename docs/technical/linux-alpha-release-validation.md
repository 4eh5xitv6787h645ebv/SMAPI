---
layout: default
title: Linux alpha release validation
description: Sanitized qualification and clean-room verification record for the experimental Linux alpha.
kicker: Publication evidence record
---

This page is the sanitized evidence record for the first Linux alpha. It deliberately excludes the
private modpack, save, mod identities, configuration, local paths, logs, and report contents.

## Candidate identity

Publication is pending. The final record will identify the exact embedded version, annotated tag,
source commit and tree, qualification workflow run, release URL, installer filename and size,
SHA-256, metadata asset, and GitHub attestation verification result.

## Automated qualification

The final record will link the fixture-free runtime-dispatcher and analyzer tests, Release build,
formatting check, package structure test, and isolated install/update/uninstall/failure lifecycle
test for the exact release commit. Hosted CI uses pinned public game reference assemblies for
compilation only; because those assemblies are intentionally non-executable, the complete
game-bound test suite is run separately against executable assemblies in the authorized disposable
environment. The record will publish sanitized discovered/passed/skipped/failed counts for that
exact-commit run. It will also record the required performance-gate result and the independent
release, security/privacy, testing, and final-diff reviews.

The pre-review branch candidate at `0085f7559c8c754086584c8f41f72e13599ca75a`
passed the [Linux alpha qualification workflow](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33170191391)
and [deterministic performance gates](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33170191358).
Its public-input suites ran 21 dispatcher and 13 analyzer tests with zero-test discovery treated as
an error. The full game-bound suite separately discovered 1,871 tests against executable assemblies:
1,868 passed, three existing platform cases were skipped, and none failed. This checkpoint is not
the release commit; all exact-commit qualifications will be repeated after review and merge.

## Clean isolated verification

After publication, a fresh download will be verified and exercised in a disposable Linux
environment. The published record will contain only environment metadata and aggregate pass/fail
evidence for:

- tag and full commit equality;
- `sha256sum --check` and GitHub attestation verification;
- normal-user install, update, uninstall, and official-4.5.2 rollback;
- preservation of unrelated game files, custom mods, saves, user configuration backup, current
  logs, and local Health Reports;
- default net6 runtime launch with the complete trusted workload;
- Mod Health Report generation and exact in-game viewer opening; and
- immutable input-manifest equality before and after the run.

Before independent review, the downloaded Actions candidate passed checksum/metadata/package
verification, install/update/uninstall/failure lifecycle checks, and rollback to the checksum-
verified official 4.5.2 installer. A disposable default-net6 full-workload session loaded the
expected 132 code mods and 176 content packs with one expected skip, reached authorized-save
gameplay, recorded 180.021 seconds of steady gameplay plus both transitions, and exited normally.
Its normal-shutdown Health Report matched schema v1 and the documented permission, size, local-only,
and targeted privacy-canary constraints. A separate stable-state run opened the in-game viewer; its
private screenshot was visually checked and was neither committed nor uploaded. The complete
sanitized checkpoint is recorded on [pull request #172](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/172#issuecomment-5452607444).

The unrelated .NET 10 menu-click issue is not a release gate and is not included in this record.

## Public documentation and asset checks

After the release and documentation updates are live, this page will record the successful GitHub
Pages build and HTTP 200 results for the documentation home, getting-started guide, comparison,
alpha guide, validation page, screenshots, release page, and each public release asset.
