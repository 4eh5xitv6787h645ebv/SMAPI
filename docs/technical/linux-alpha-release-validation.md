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

The unrelated .NET 10 menu-click issue is not a release gate and is not included in this record.

## Public documentation and asset checks

After the release and documentation updates are live, this page will record the successful GitHub
Pages build and HTTP 200 results for the documentation home, getting-started guide, comparison,
alpha guide, validation page, screenshots, release page, and each public release asset.
