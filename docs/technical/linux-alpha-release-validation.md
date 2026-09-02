---
layout: default
title: Linux alpha release validation
description: Sanitized qualification and clean-isolated verification record for the experimental Linux alpha.
kicker: Publication evidence record
---

This page records the current alpha 2 evidence first and retains alpha 1 as release history. It
deliberately excludes the private modpack, save, mod identities, configuration, local paths, logs,
report contents, and authentication data.

## Alpha 2 published identity

The [experimental alpha 2 prerelease](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2)
was published on 2 September 2026 UTC from this immutable identity:

| Field | Verified value |
| --- | --- |
| Embedded version | `4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2` |
| Annotated tag | `fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2` |
| Tag object | `782ae58170f4399947e03455e968775cc090666a` |
| Source commit | `052699e8ccba0d13f9d4f02e0bb199aa04cec605` |
| Source tree | `95bfb5cf8744daf15d59f4799a593fd8be7bca8d` |
| Installer | `SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip` |
| Installer size | 108,110,771 bytes |
| Installer SHA-256 | `a1d8669881b8ba87c3511689b810211148430798f30bc7a42e3fd74bc5630dfd` |
| Install-manifest size | 56,879 bytes |
| Install-manifest SHA-256 | `eac8e97fbfdd437e9e165ab72ce55d782ea28449798414c8bf3e704c7a8de5a3` |
| Attestation-bundle size | 12,680 bytes |
| Attestation-bundle SHA-256 | `7b468ab561513c2c3042ec0c9725b1522090b4483049b4d8933fe4f8b5291a4b` |

The annotated tag peels to the source commit above. The public release exposes exactly six files:
the installer ZIP, canonical install manifest, `SHA256SUMS`, `build-metadata.json`, local
attestation bundle, and the bundle's checksum sidecar. The tag workflow, release page, and all six
asset URLs returned successful results during publication verification.

## Exact-commit qualification

The [tag-triggered release workflow](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33669816773)
passed exact-source validation, script and formatting checks, all Release builds, fixture-free
runtime-dispatcher and analyzer tests, the complete fixture-free installer Core and GUI suites,
release-asset tests, package construction and structural qualification, workflow-artifact transfer
verification, both subject attestations, local-bundle verification, and immutable-identity
prerelease publication. Its qualification, attestation, and publication jobs all completed
successfully for source commit `052699e8ccba0d13f9d4f02e0bb199aa04cec605`.

Before tagging, the exact reviewed merge commit passed the full game-bound SMAPI suite against
executable game assemblies, isolated install/update/uninstall/failure and rollback checks, the
required deterministic performance gate, and independent release, security/privacy, testing, and
final-diff reviews. Every actionable review finding was resolved before publication.

The same exact source commit's non-authoritative merge candidate passed the authorized complete
trusted-workload smoke in a disposable Linux environment. Its installed net6 SMAPI assembly matched
the candidate's `install.dat` entry byte-for-byte; the expected workload identity, save identity,
and game version matched; 132 code mods and 176 content packs loaded with one expected skip; and
180.004 seconds of steady gameplay plus both transitions completed with zero invalid-world,
location, or position ticks and a normal exit. All immutable fixture-source manifests were unchanged.
The [sanitized candidate record](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5514688251)
contains only aggregate evidence.

The tagged workflow rebuilds the ZIP, and byte-for-byte reproducibility is not claimed. The trusted
candidate result therefore qualifies the exact reviewed source and candidate package; it is not
presented as proof that the separately rebuilt public ZIP ran that private workload.

## Fresh public-download verification

A fresh post-publication qualification downloaded the six public files into a new private
same-user directory and passed all of these checks:

- the release ID, immutable tag, commit, tree, exact six-file inventory, asset IDs, canonical URLs,
  uploaded states, sizes, timestamps, and GitHub-supplied digests were pinned before download and
  matched a fresh inventory after verification;
- every downloaded byte count and available API digest matched, `SHA256SUMS --check --strict`
  accepted exactly the manifest and ZIP, and the bundle sidecar accepted the local bundle;
- build metadata, canonical install-manifest authority, release identity, package structure, file
  types, links, modes, and the graphical/backend sibling layout all agreed;
- both checksum subjects passed local-bundle GitHub attestation verification against this repository,
  the tagged `linux-alpha-release.yml` identity, the exact source commit and tag ref, and the hosted
  runner policy; and
- the verified public ZIP passed the package qualifier, packaged graphical-installer smoke, and a
  new disposable install/update/uninstall/failure lifecycle without using the private workload.

The first real qualifier attempt exposed an incorrect GitHub CLI hostname argument. Focused
[PR #252](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/252) changed only that API boundary to
the canonical `github.com`, added a regression test which rejects the broken hostname, passed both
required workflows and independent review, and was merged before the successful fresh rerun. The
[sanitized public qualification record](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5515036792)
links the release, workflow, exact identities, six assets, and aggregate lifecycle results.

This proves the publicly downloadable GUI package and its clean-isolated fixture-free lifecycle.
Authentic GNOME/KDE, X11/XWayland, AT-SPI, scaling, and complete production-workflow screenshot
qualification remain separate pending Phase 4 evidence. The public ZIP has not yet been claimed to
pass the private trusted workload; only the exact-source candidate result above makes that claim.

## Historical alpha 1

Alpha 1 remains available as historical evidence at
[`fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1`](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1).
It was published on 28 August 2026 with embedded version
`4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1`, tag object
`8b9ec025c241da50695471d8f7c7f54ed003a8b4`, source commit
`6e5d708a09e7d2b6d9b5434bd1fac52ddbdb5f08`, and source tree
`0cbabfd1f7934433f3ad0c0f1874c89ba6f75773`. Its 52,183,224-byte installer ZIP had SHA-256
`94e5dd4af8075946143ae79ba206d90b1433351c84dceb4f4506c74e638d69c8`.

The [alpha 1 tag workflow](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33177145353),
exact-candidate testing, trusted-workload smoke, fresh public-download checksum/attestation check,
normal-user lifecycle, official-4.5.2 rollback, Mod Health generation/viewer check, and immutable
fixture-manifest comparison passed. Its complete sanitized record remains on
[umbrella issue #168](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5453527770).

Alpha 1 has only three public assets and no graphical installer, canonical install-manifest
companion, or local attestation bundle. Keep it as historical rollback evidence; use alpha 2 for the
current documented graphical workflow.

The unrelated .NET 10 menu-click issue is not a release gate and is not included in either record.

## Documentation publication status

The historical alpha 1 documentation deployment
[`33179582509`](https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/33179582509) passed and its
important pages and committed screenshots returned HTTP 200. This alpha 2 documentation update must
receive its own successful Pages deployment and fresh HTTP checks after merge; those later checks
are not inferred from the historical deployment or from a local Markdown build.
