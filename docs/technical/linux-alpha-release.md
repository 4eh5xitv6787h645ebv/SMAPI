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
and provenance establish identity and integrity, but byte-for-byte reproducible ZIP output is not
claimed.

## Requirements and safety boundary

- Native Linux x86_64 Stardew Valley 1.6.14 or later. Android/mobile is not supported.
- A normal desktop user account. **Never run the installer with `sudo` or as root.**
- The game must be closed.
- Back up saves, `Mods`, and any existing `smapi-internal/config.user.json` before changing loaders.
- Download only from this repository's
  [alpha 1 release page](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1).

The alpha has no graphical updater. The console installer changes the `StardewValley` launcher,
preserves the vanilla launcher as `StardewValley-original`, and installs two Linux runtime hosts.
Rollback is a deliberate uninstall-and-reinstall procedure, not an atomic snapshot.

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

Manual removal must follow the exact file manifest in the installer source. Do not recursively
delete the game folder, `Mods`, saves, `ErrorLogs`, or `HealthReports`.

## Diagnostics, privacy, and limitations

The alpha's `health` and `performance` tools remain local and bounded. Their reports can contain mod
names, IDs, versions, dependency IDs, callback identities, filesystem-derived details, and system
information. Inspect any report before sharing it. The installer and release workflow never upload
the private benchmark modpack or save.

The published performance comparison describes one controlled workstation and workload. It is not
a universal FPS, power, CPU-use, or latency claim. There is no GUI/updater yet, and the current
rollback flow is not atomic. See the [comparison](../upstream-comparison.md),
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
merged to `develop`. A candidate is built by dispatching `linux-alpha-release.yml` with the exact
40-character merge commit, embedded version, and reserved tag. Only after the full test suite,
isolated lifecycle, and trusted-workload qualifications pass for that exact commit is the annotated
tag created at the same commit. The tag-triggered workflow rebuilds and qualifies the package,
creates checksums and metadata, attests the checksum subjects, and publishes a prerelease. The
downloaded public asset—not a local package—is then used for the final clean-room verification.
