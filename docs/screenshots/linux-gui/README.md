# Linux GUI screenshot evidence bundle

This directory holds the machine-readable provenance and source PNGs for the Phase 4 graphical-installer evidence. The authoritative state and evidence-class requirements are defined in [`../../technical/linux-gui-screenshot-evidence.md`](../../technical/linux-gui-screenshot-evidence.md).

`manifest.schema.json` defines one entry for each of the exact 57 screenshot IDs. A completed `manifest.json` is intentionally not present until the exact reviewed public package has been qualified and every required source PNG exists; an empty or placeholder manifest must not be presented as completed evidence. The manifest records one top-level reviewed production identity, and every capture — including controlled fixtures — must repeat that exact source commit/tree, release tag and URL, package hash, and GUI/backend binary hashes.

The release-selection boundary is important: R1 may show bounded public prerelease choices and the local-package route, but it must not claim an installed/current, upgrade, or downgrade relationship. Those relationships are authenticated only after game-receipt inspection and belong to U1–U2.

## Validation

Create a temporary newline-delimited private-string file outside the repository. Include every private fixture name, username, local root, and other capture-specific token which must not appear. Keep it current-user-owned at exact mode `0600`; do not commit that file or its contents. Each value must contain at least four characters.

Run:

```sh
python3 build/scripts/validate-linux-gui-screenshot-evidence.py \
  --manifest docs/screenshots/linux-gui/manifest.json \
  --assets-root docs/screenshots/linux-gui \
  --private-strings-file /path/to/private-strings.txt
```

The validator fails closed on:

- any missing, duplicate, or unknown matrix ID, or anything other than exactly 57 entries;
- an incomplete assets-root inventory: only `README.md`, `manifest.schema.json`, `manifest.json`, and every referenced final/original PNG are allowed; directories, nested assets, orphan files, symlinks, and multiply-linked files fail;
- unsafe filenames, any final PNG filename or pixel hash reused across evidence IDs, bad PNG structure or CRCs, interlacing, unsupported color encodings, unknown/needless chunks, animation, trailing bytes, invalid zlib data, decoded-size excess, or malformed scanlines;
- mismatched SHA-256 hashes or pixel dimensions;
- a top-level production identity mismatch in any of the 57 entries, including a controlled fixture;
- missing source, binary, environment, runtime, capture, durable-state, editing, privacy-review, qualification, alt-text, or caption provenance, including environment/capture/privacy provenance for retained originals;
- anything other than the exact A4 100/125/150/200 scale sources, A5 light/dark/high-contrast sources, A6 GNOME+KDE X11/x11 sources, or A7 GNOME+KDE Wayland/xwayland sources;
- a controlled fixture used for an ID which requires real qualification;
- unsafe release links; unanchored, missing, wrong-ID, generic, or untrusted qualification references; common path/credential/signed-URL patterns; or configured private strings in manifest text or PNG bytes. Real rows accept only an exact fork Actions run URL or an evidence-ID-specific anchor in a dedicated qualification/validation record; controlled rows may also use their anchored row in the screenshot specification, except A8 which requires separate AT-SPI/Orca qualification evidence.

The automated scan cannot determine what rendered pixels depict. Every image still requires the recorded original-resolution human privacy review mandated by the specification. If that review finds private data, discard and recapture the image; do not redact application pixels.

Hosted CI reruns the validator with a nonprivate sentinel so it can enforce the closed inventory,
generic path/credential patterns, hashes, PNG structure, identity, and matrix contract. CI cannot
know private capture-specific names without improperly uploading them. Its sentinel run never
substitutes for the required local run with the complete uncommitted private-string file above.

## Private capture staging

Use `build/scripts/stage-linux-gui-screenshot.py` to create a canonical PNG and a capture-provenance
sidecar in a private directory **outside the repository**. The staging directory must
already exist, be owned by the current user, and have mode `0700`. The tool creates both outputs at
mode `0600` and never overwrites either one.

The direct mode accepts one visible X11 or XWayland client-window ID, checks that the window is
viewable, its title and process ID match, and the retained `/proc` executable bytes match the reviewed
GUI SHA-256 before and after ImageMagick capture. The executable may be at most 256 MiB. No executable
path is recorded. X window properties and `_NET_WM_PID` are advisory values which another client on
the same display can spoof. Capture on a controlled isolated display, keep unrelated clients out of
that session, and visually confirm the exact application, title, and state at original resolution.

The import mode accepts an already captured app-window PNG plus a path-free capture-tool and command
description. The imported PNG must be outside the repository, current-user-owned, single-link, and
exact mode `0600`. In either mode, the source must be a bounded, noninterlaced, 8-bit RGB or RGBA PNG.
The tool decodes the pixels, writes a canonical `IHDR`/`IDAT`/`IEND` PNG, decodes that result again,
and refuses it unless the source and result pixels are byte-identical. The sidecar records both the
PNG and decoded-pixel SHA-256 digests, removed chunk types, exact production identity, environment,
runtime, evidence context, durable-state claims, and qualification reference.

Write the exact eight-field `production_identity` JSON object defined by the manifest schema to a
JSON file outside the repository. Prepare the complete mode-`0600` private-string file there too,
then run `--help` for the required evidence, environment, and runtime arguments. Record the SDK
version actually installed in the capture environment; if none is installed because the package is
self-contained, record that exact not-installed/not-used state instead of inventing a version. For
direct G1 capture, the exact production window title and source-specific arguments are:

```sh
python3 build/scripts/stage-linux-gui-screenshot.py \
  --window-id "$window_id" \
  --expected-window-pid "$window_pid" \
  --expected-window-title "SMAPI Linux Installer — Local diagnostics" \
  --stage-directory "$private_capture_stage" \
  --filename "g1-real-diagnostics.png" \
  --evidence-id G1 \
  --evidence-class real_qualification \
  --production-identity "$production_identity_file" \
  --private-strings-file "$private_strings_file" \
  --fixture-or-injection "No injected fault; real successful lifecycle" \
  --operation "View diagnostics after committed install" \
  --durable-before "Clean isolated game copy" \
  --durable-after "Authenticated alpha installation committed" \
  --qualification-reference "docs/technical/linux-gui-alpha3-screenshot-qualification.md#evidence-g1" \
  --distribution "Ubuntu 24.04 LTS" \
  --architecture x86_64 \
  --desktop-environment GNOME \
  --session-type wayland \
  --display-backend xwayland \
  --display-scale-percent 100 \
  --theme light \
  --resolution 1920x1080 \
  --avalonia 12.1.1 \
  --dotnet-sdk "not installed; self-contained package" \
  --dotnet-runtime 10.0.11
```

When staging each of the four retained E2 source PNGs, also pass exactly one of `--fault permission`,
`--fault read-only`, `--fault disk-full`, or `--fault cross-device`. `--fault` is rejected for every
other evidence ID. Contact-sheet assembly remains a later reviewed step.

The sidecar deliberately sets privacy review to `pending`. It is not a manifest entry and must not
be copied into this directory. Inspect the canonical PNG at original resolution, complete the
independent privacy review, and then copy its bytes unchanged into the final bundle while checking
the recorded SHA-256. Contact sheets, lossless crops, final captions/alt text, and manifest assembly
remain separate reviewed steps. The tool does not OCR pixels, prove the visible workflow state,
qualify filesystem effects, fabricate fixtures, or support native-Wayland window capture.

Run the validator's fixture-free self-tests with:

```sh
python3 build/scripts/test-linux-gui-screenshot-evidence.py
python3 build/scripts/test-stage-linux-gui-screenshot.py
```

The self-tests create all data under private temporary directories and cover a valid 57-ID bundle, rejection of cross-ID filename or pixel reuse, production-identity mixing, exact inventory failures, environment-matrix gaps, invalid or bomb-like PNG streams, unknown metadata/critical chunks, privacy leaks, and other tampered or broken-provenance cases.
