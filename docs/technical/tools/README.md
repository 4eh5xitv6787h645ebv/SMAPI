# PR #158 fixture integrity tool

`fixture_archive_audit.py` verifies the exact compressed size and SHA-256 of the two pinned Mod Health Report runtime fixtures, inventories their tar entries under fixed containment limits, and optionally extracts them into a new disposable directory. The default `audit` operation does not extract or execute archive content.

From the repository root:

```bash
python3 docs/technical/tools/fixture_archive_audit.py audit save /path/to/Blossom_389524656.tar.xz
python3 docs/technical/tools/fixture_archive_audit.py audit modpack /path/to/Mods-2026-08-26.tar.zst
```

Extraction is explicit and the destination must not exist:

```bash
python3 docs/technical/tools/fixture_archive_audit.py audit modpack /path/to/Mods-2026-08-26.tar.zst --extract /new/disposable/Mods-fixture
```

The modpack reader invokes `zstd` directly without a shell and streams its output into Python's tar reader. Override the executable with `--zstd-command /absolute/path/to/zstd` if needed. Only regular files and directories are accepted. Stored ownership and modes are ignored; extracted directories and files use `0700` and `0600` respectively.

Project PR #158's metadata through the narrow support allowlist, excluding `folder`, author, update-key, and unknown fields:

```bash
python3 docs/technical/tools/fixture_archive_audit.py project-mods /path/to/mods.json --output /new/disposable/mods.projected.json
```

Run the deterministic synthetic containment tests without either external fixture:

```bash
python3 -m unittest discover -s docs/technical/tools -p 'test_*.py' -v
```

This tool establishes fixture integrity and extraction containment only. It does not inspect mods for trust, provenance, licensing, or behavior, and it never runs archive content.
