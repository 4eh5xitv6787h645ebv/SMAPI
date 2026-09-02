# Linux GUI screenshot evidence bundle

This directory holds the machine-readable provenance and source PNGs for the Phase 4 graphical-installer evidence. The authoritative state and evidence-class requirements are defined in [`../../technical/linux-gui-screenshot-evidence.md`](../../technical/linux-gui-screenshot-evidence.md).

`manifest.schema.json` defines one entry for each of the exact 57 screenshot IDs. A completed `manifest.json` is intentionally not present until the exact reviewed public package has been qualified and every required source PNG exists; an empty or placeholder manifest must not be presented as completed evidence. The manifest records one top-level reviewed production identity, and every capture — including controlled fixtures — must repeat that exact source commit/tree, release tag and URL, package hash, and GUI/backend binary hashes.

The release-selection boundary is important: R1 may show bounded public prerelease choices and the local-package route, but it must not claim an installed/current, upgrade, or downgrade relationship. Those relationships are authenticated only after game-receipt inspection and belong to U1–U2.

## Validation

Create a temporary newline-delimited private-string file outside the repository. Include every private fixture name, username, local root, and other capture-specific token which must not appear. Do not commit that file or its contents. Each value must contain at least four characters.

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
- unsafe filenames, inconsistent provenance for a shared adjacent-state PNG, bad PNG structure or CRCs, interlacing, unsupported color encodings, unknown/needless chunks, animation, trailing bytes, invalid zlib data, decoded-size excess, or malformed scanlines;
- mismatched SHA-256 hashes or pixel dimensions;
- a top-level production identity mismatch in any of the 57 entries, including a controlled fixture;
- missing source, binary, environment, runtime, capture, durable-state, editing, privacy-review, qualification, alt-text, or caption provenance, including environment/capture/privacy provenance for retained originals;
- anything other than the exact A4 100/125/150/200 scale sources, A5 light/dark/high-contrast sources, A6 GNOME+KDE X11/x11 sources, or A7 GNOME+KDE Wayland/xwayland sources;
- a controlled fixture used for an ID which requires real qualification;
- unsafe release links, missing repository-relative qualification links, common path/credential/signed-URL patterns, or configured private strings in manifest text or PNG bytes.

The automated scan cannot determine what rendered pixels depict. Every image still requires the recorded original-resolution human privacy review mandated by the specification. If that review finds private data, discard and recapture the image; do not redact application pixels.

Run the validator's fixture-free self-tests with:

```sh
python3 build/scripts/test-linux-gui-screenshot-evidence.py
```

The self-tests create all data under private temporary directories and cover a valid 57-ID bundle, a safely shared adjacent-state PNG, production-identity mixing, exact inventory failures, environment-matrix gaps, invalid or bomb-like PNG streams, unknown metadata/critical chunks, privacy leaks, and other tampered or broken-provenance cases.
