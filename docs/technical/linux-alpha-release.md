---
layout: default
title: Linux alpha release guide
description: Verify, install, upgrade, remove, or roll back the experimental SMAPI Linux fork.
kicker: Experimental Linux x86_64 prerelease
---

This guide is for the first **unofficial experimental Linux desktop alpha**. It is not an official
SMAPI release and is not the default recommendation for most players. Use official SMAPI if you
want the broadly supported cross-platform release.

## Release identity

The fork uses identifiers which cannot collide with inherited official SMAPI tags or look like an
official stable release:

| Item | First alpha |
| --- | --- |
| Embedded version | `4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1` |
| Git tag | `fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1` |
| Release title | `Experimental SMAPI Linux Fork 4.5.3 alpha 1` |
| Installer | `SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip` |

Later alphas increment the final number. A published tag is never reused or moved. The public
release assets are built from the exact tag commit by GitHub Actions; the build records its source
commit, tree, pinned game-reference commit, runner, SDKs, package size, and SHA-256. Recorded inputs
describe how the artifact was built, but only the package/manifest hashes and independently verified
GitHub attestations establish published identity, integrity, and provenance. The workflow-run URL,
runner, pinned reference-build commit, timestamp, and .NET details are bounded informational fields;
clean-machine verification of the four primary package/metadata assets does not authenticate them.
Byte-for-byte reproducible ZIP
output is not claimed.

## Requirements and safety boundary

- Native Linux x86_64 Stardew Valley 1.6.14 or later. Android/mobile is not supported.
- A normal desktop user account. **Never run the installer with `sudo` or as root.**
- GNU Bash; the published alpha.1 launcher and runtime dispatcher are Bash scripts.
- The game must be closed.
- Back up saves, `Mods`, and any existing `smapi-internal/config.user.json` before changing loaders.
- Download only from this repository's
  [alpha 1 release page](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1).

The alpha has no graphical updater. The console installer changes the `StardewValley` launcher,
preserves the vanilla launcher as `StardewValley-original`, and installs two Linux runtime hosts.
Rollback is a deliberate uninstall-and-reinstall procedure, not an atomic snapshot.

**Unreleased next-alpha/source-build note:** PR #177 changes the runtime dispatcher to validation-only
behavior which has not shipped in alpha.1. Builds containing that change additionally require GNU
coreutils (`stat` and `timeout`) and GNU diffutils (`cmp`). The published alpha.1 dispatcher does not
perform those capability checks and still creates or refreshes its net6 dependency metadata at
launch when needed.

## Verify before extracting or running

Download the installer ZIP, `SHA256SUMS`, and `build-metadata.json` from the same prerelease into an
empty directory. Do not use a mirror and never pipe a download into a shell.

```bash
sha256sum --check SHA256SUMS
gh attestation verify \
  SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1-linux-x64-installer.zip \
  -R 4eh5xitv6787h645ebv/SMAPI
```

The checksum command must report `OK`. The attestation must identify this repository and the
`linux-alpha-release.yml` workflow. Inspect `build-metadata.json` and confirm that its package name,
SHA-256, release tag, and full source commit match the release page.

### Unreleased next-alpha six-asset set

The published alpha.1 above has three assets and no install-manifest companion. A future tagged
next-alpha build from source containing the Phase 4 package-authority work will instead publish an
exact six-file set. Its four primary package/metadata assets are:

- the finalized installer ZIP;
- `SMAPI-<embedded-version>-linux-x64-install-manifest.json`, a canonical external companion which
  records the exact installer-owned files, hashes, sizes, Unix modes, and release identity;
- `SHA256SUMS`, with exactly two sorted subjects: the manifest followed by the installer ZIP; and
- `build-metadata.json`, whose plural `artifacts` array records those same two subjects.

The other two public assets are the GitHub attestation bundle
`SMAPI-<embedded-version>-linux-x64-attestation-bundle.jsonl` and its `.sha256` sidecar. The sidecar
detects transport corruption of the local bundle; only successful verification of the signed bundle
under the pinned policy establishes provenance.

The ZIP and manifest are both GitHub-attested. After a future release actually publishes that set,
copy the exact names, tag, and commit from its release page and verify them with GitHub CLI 2.92.0:

```bash
package_name='COPY THE EXACT INSTALLER ZIP NAME HERE'
manifest_name='COPY THE EXACT INSTALL MANIFEST NAME HERE'
bundle_name='COPY THE EXACT ATTESTATION BUNDLE NAME HERE'
release_tag='COPY THE EXACT RELEASE TAG HERE'
release_commit='COPY THE EXACT 40-CHARACTER RELEASE COMMIT HERE'
sha256sum --check --strict SHA256SUMS
sha256sum --check --strict "$bundle_name.sha256"
gh attestation verify "$package_name" \
  --bundle "$bundle_name" \
  --hostname github.com \
  --repo 4eh5xitv6787h645ebv/SMAPI \
  --predicate-type https://slsa.dev/provenance/v1 \
  --cert-oidc-issuer https://token.actions.githubusercontent.com \
  --cert-identity "https://github.com/4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/$release_tag" \
  --signer-digest "$release_commit" \
  --source-ref "refs/tags/$release_tag" \
  --source-digest "$release_commit" \
  --deny-self-hosted-runners \
  --limit 2 \
  --format json
```

Both package/manifest checksum subjects and the bundle checksum must report `OK`. The successful
attestation statement must identify this repository, the tagged `linux-alpha-release.yml` workflow,
and the selected release commit, and contain exactly the package and manifest subjects. A pull-request
build, `develop` build, manual source build, or pre-tag workflow-dispatch candidate is non-authoritative:
the production workflow records its actual identity and does not invoke the release-manifest
creation path. The tool's tag-context check prevents accidental candidate minting, but it is not a
cryptographic provenance boundary and its environment can be reproduced by a local caller. Only
successful verification of the GitHub attestation statement, whose subjects are exactly the package
and manifest, against this repository, tagged workflow, and selected commit establishes published
release authority. Do not substitute an unattested local or candidate four-primary-asset set for the
published tagged six-asset set.

## Install

Extract the verified ZIP. In a desktop session, run `install on Linux.sh`. If the file manager does
not open a terminal, open a terminal in the extracted folder and run:

```bash
bash "install on Linux.sh"
```

Konsole, Alacritty, GNOME Terminal, and xterm are detected by the launcher. A headless or scripted
installation can call the existing installer behavior directly:

```bash
./internal/linux/SMAPI.Installer \
  --no-prompt --install --game-path "/absolute/path/to/Stardew Valley"
```

A successful headless operation exits 0. Invalid arguments exit 2; unexpected filesystem or
runtime failures exit 1. The installer never needs root for a user-owned game installation.

## Upgrade or repair this alpha

Close the game, verify the newer package independently, back up the current user configuration,
then run its installer against the same game folder. Installation first removes known SMAPI files,
restores and re-backs up the vanilla launcher, and installs the new payload. Custom mods, saves,
current SMAPI logs, and local Mod Health Reports are not uninstall targets.

The two bundled mods, Console Commands and Save Backup, are updated in place. An uninstall
intentionally leaves them in `Mods`, just as the upstream installer does.

## Uninstall or roll back

Keep the verified alpha installer used for the current installation. To return to vanilla:

```bash
./internal/linux/SMAPI.Installer \
  --no-prompt --uninstall --game-path "/absolute/path/to/Stardew Valley"
```

This restores the exact vanilla launcher backup and removes the fork-only net6/net10 hosts and
SMAPI internal files. It leaves the bundled and custom mods in `Mods`; vanilla ignores them.

To roll back to official SMAPI 4.5.2 or an earlier verified fork package:

1. Copy `smapi-internal/config.user.json` and the current `ErrorLogs` folder (including
   `HealthReports`) somewhere outside the game and app-data folders. Official SMAPI 4.5.2 still
   removes the current `ErrorLogs` folder during its install pass; restore the reviewed backup
   afterwards.
2. Run this alpha's uninstaller first. The official 4.5.2 uninstaller does not know the fork-only
   net6/net10 host filenames and must not be used directly over the alpha.
3. Verify and install the selected older package.
4. Restore only compatible user configuration values if needed, and restore the reviewed log/report
   backup if you need to retain it.
5. Launch once and confirm the displayed SMAPI version.

## Manual installation path

The supported console installer is strongly preferred because it owns the file list and launcher
backup rules. If terminal automation cannot run it, `internal/linux/install.dat` is a normal ZIP:

1. Extract `install.dat` into a staging directory.
2. Back up `StardewValley` as `StardewValley-original` without overwriting an existing backup.
3. Copy the staged payload into the game directory without deleting unrelated files.
4. Copy `Stardew Valley.deps.json` to `StardewModdingAPI-net6.deps.json`.
5. Rename staged `unix-launcher.sh` to `StardewValley`.
6. Mark `StardewValley`, `StardewModdingAPI`, both `StardewModdingAPI-net*` hosts, and every
   private-runtime `createdump` executable as mode 755. The private app-relative runtime contains
   `host/fxr` and `shared/Microsoft.NETCore.App`; it intentionally does not bundle the `dotnet` CLI.

For an unreleased next-alpha/source build containing PR #177 only, step 4 must preserve both exact
bytes and Unix mode:

```bash
cp --preserve=mode -- "Stardew Valley.deps.json" "StardewModdingAPI-net6.deps.json"
```

That stricter validation-only behavior is not part of the published alpha.1 artifact.

Manual removal must follow the exact file manifest in the installer source. Do not recursively
delete the game folder, `Mods`, saves, `ErrorLogs`, or `HealthReports`.

## Diagnostics, privacy, and limitations

The alpha's `health` and `performance` tools remain local and bounded. Their reports can contain mod
names, IDs, versions, dependency IDs, callback identities, filesystem-derived details, and system
information. Inspect any report before sharing it. The installer and release workflow never upload
the private benchmark modpack or save.

The published performance comparison describes one controlled workstation and workload. It is not
a universal FPS, power, CPU-use, or latency claim. There is no GUI/updater yet, and the current
rollback flow is not atomic. The published alpha.1 dispatcher is not validation-only: its net6 path
may create or refresh dependency metadata before launch. The unreleased PR #177 source-build
dispatcher removes that mutation and rechecks path identities to catch ordinary concurrent changes
during validation, but same-user adversarial path replacement between validation and process
execution remains outside that nonprivileged launcher's threat boundary. Those statements describe
unreleased next-alpha/source-build behavior, not alpha.1. See the [comparison](../upstream-comparison.md),
[validation record](linux-alpha-release-validation.md), and
[issue tracker](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues).

## Maintainer release process

The Phase 3 pull request must pass `Linux alpha release qualification` and the repository's required
performance gate. Hosted CI compiles the game-bound test assembly against pinned public reference
assemblies, then executes the fixture-free runtime-dispatcher and analyzer suites with zero-test
discovery treated as an error. Reference assemblies are deliberately non-executable, so they must
not be presented as a full game-bound test run. The complete `SMAPI.Tests` suite is run separately
against executable game assemblies in the authorized disposable environment, with only sanitized
counts and pass/fail evidence published.

After independent release, security/privacy, testing, and final-diff reviews, the pull request is
merged to `develop`. A non-authoritative candidate is built by dispatching
`linux-alpha-release.yml` with the exact 40-character merge commit, embedded version, and reserved
tag. Its metadata records the actual pre-tag workflow ref; it does not mint a companion manifest
which the installer can accept as release authority. Only after the full test suite, isolated
lifecycle, and trusted-workload qualifications pass for that exact commit is the annotated tag
created at the same commit.

For a source revision containing the Phase 4 release-authority work, the tag-triggered workflow then
rebuilds and qualifies the finalized ZIP, creates its canonical external install manifest, emits
`SHA256SUMS` with exactly those two sorted subjects and plural-artifact build metadata, runs the
complete package/manifest authority verification before and after workflow-artifact transfer,
attests both subjects, exports the local attestation bundle and its checksum sidecar, and publishes
the exact six named release files. The downloaded public six-asset set—not a local package or pre-tag
candidate—is then used for final clean-room verification.
These next-alpha steps do not retroactively describe the published alpha.1 assets.
