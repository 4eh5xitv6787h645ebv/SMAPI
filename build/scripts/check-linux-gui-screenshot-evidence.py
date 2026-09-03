#!/usr/bin/env python3
"""Fail closed on the repository's Linux GUI screenshot evidence state."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import stat
import subprocess
import sys


BOOTSTRAP_FILES = frozenset({"README.md", "manifest.schema.json"})


def fail(message: str) -> None:
    print(f"Linux GUI screenshot repository-state check failed: {message}", file=sys.stderr)
    raise SystemExit(1)


def require_regular_single_link(path: Path, description: str) -> None:
    try:
        item_stat = path.lstat()
    except OSError as exc:
        fail(f"can't inspect {description}: {exc}")
    if path.is_symlink() or not stat.S_ISREG(item_stat.st_mode) or item_stat.st_nlink != 1:
        fail(f"{description} must be a non-symlink, single-link regular file")


def parse_args() -> argparse.Namespace:
    script_path = Path(__file__).resolve()
    repository_root = script_path.parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--private-strings-file", type=Path, required=True)
    parser.add_argument(
        "--assets-root",
        type=Path,
        default=repository_root / "docs/screenshots/linux-gui",
        help=argparse.SUPPRESS,
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    script_path = Path(__file__).resolve()
    repository_root = script_path.parents[2]
    assets_root = args.assets_root.absolute()
    private_strings_file = args.private_strings_file.absolute()

    try:
        assets_stat = assets_root.lstat()
        resolved_assets = assets_root.resolve(strict=True)
    except OSError as exc:
        fail(f"can't inspect the screenshot assets root: {exc}")
    if (
        assets_root.is_symlink()
        or not stat.S_ISDIR(assets_stat.st_mode)
        or resolved_assets != assets_root
    ):
        fail("the screenshot assets root must be one real normalized directory")
    require_regular_single_link(private_strings_file, "private-string file")
    private_metadata = private_strings_file.lstat()
    try:
        resolved_private_strings = private_strings_file.resolve(strict=True)
    except OSError as exc:
        fail(f"can't inspect private-string file: {exc}")
    if (
        private_metadata.st_uid != os.geteuid()
        or stat.S_IMODE(private_metadata.st_mode) != 0o600
        or resolved_private_strings != private_strings_file
    ):
        fail("private-string file must be normalized, current-user-owned, and exact mode 0600")
    if resolved_private_strings == repository_root or repository_root in resolved_private_strings.parents:
        fail("private-string file must be outside the repository")

    try:
        entries = list(os.scandir(assets_root))
    except OSError as exc:
        fail(f"can't inventory the screenshot assets root: {exc}")

    manifest_path = assets_root / "manifest.json"
    if not any(entry.name == "manifest.json" for entry in entries):
        actual_names = {entry.name for entry in entries}
        if actual_names != BOOTSTRAP_FILES:
            missing = sorted(BOOTSTRAP_FILES - actual_names)
            unexpected = sorted(actual_names - BOOTSTRAP_FILES)
            details = []
            if missing:
                details.append(f"missing: {', '.join(missing)}")
            if unexpected:
                details.append(f"unexpected without manifest.json: {', '.join(unexpected)}")
            fail("the pre-capture assets inventory is not exact; " + "; ".join(details))
        for name in sorted(BOOTSTRAP_FILES):
            require_regular_single_link(assets_root / name, f"bootstrap asset {name}")
        print("Linux GUI screenshot repository state is valid: bootstrap files only; no evidence is claimed.")
        return 0

    validator = repository_root / "build/scripts/validate-linux-gui-screenshot-evidence.py"
    command = [
        sys.executable,
        str(validator),
        "--manifest", str(manifest_path),
        "--assets-root", str(assets_root),
        "--schema", str(assets_root / "manifest.schema.json"),
        "--private-strings-file", str(private_strings_file),
    ]
    return subprocess.run(command, check=False).returncode


if __name__ == "__main__":
    sys.exit(main())
