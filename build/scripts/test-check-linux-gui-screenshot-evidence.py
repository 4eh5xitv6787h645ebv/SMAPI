#!/usr/bin/env python3
"""Adversarial self-tests for the Linux GUI screenshot repository-state gate."""

from __future__ import annotations

from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from typing import Callable


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
GATE = REPOSITORY_ROOT / "build/scripts/check-linux-gui-screenshot-evidence.py"
SOURCE_ASSETS = REPOSITORY_ROOT / "docs/screenshots/linux-gui"


def make_bootstrap(root: Path) -> tuple[Path, Path]:
    assets = root / "assets"
    assets.mkdir()
    for name in ("README.md", "manifest.schema.json"):
        shutil.copy2(SOURCE_ASSETS / name, assets / name)
    private_strings = root / "private-strings.txt"
    private_strings.write_text("fixture-private-sentinel\n", encoding="utf-8")
    return assets, private_strings


def run_gate(assets: Path, private_strings: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            sys.executable,
            str(GATE),
            "--assets-root", str(assets),
            "--private-strings-file", str(private_strings),
        ],
        check=False,
        capture_output=True,
        text=True,
    )


def expect_failure(
    name: str,
    mutation: Callable[[Path, Path], None],
    expected: str,
) -> None:
    with tempfile.TemporaryDirectory(prefix="smapi-gui-repository-state-test.") as temporary:
        assets, private_strings = make_bootstrap(Path(temporary))
        mutation(assets, private_strings)
        result = run_gate(assets, private_strings)
        combined = result.stdout + result.stderr
        if result.returncode == 0 or expected not in combined:
            raise AssertionError(
                f"{name}: expected failure containing {expected!r}, got exit {result.returncode}:\n{combined}"
            )


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="smapi-gui-repository-state-valid.") as temporary:
        assets, private_strings = make_bootstrap(Path(temporary))
        result = run_gate(assets, private_strings)
        if result.returncode != 0 or "bootstrap files only" not in result.stdout:
            raise AssertionError(f"valid bootstrap state was rejected:\n{result.stdout}{result.stderr}")

    expect_failure(
        "missing README",
        lambda assets, _private: (assets / "README.md").unlink(),
        "missing: README.md",
    )
    expect_failure(
        "missing schema",
        lambda assets, _private: (assets / "manifest.schema.json").unlink(),
        "missing: manifest.schema.json",
    )
    expect_failure(
        "PNG without manifest",
        lambda assets, _private: (assets / "a1.png").write_bytes(b"not evidence"),
        "unexpected without manifest.json: a1.png",
    )
    expect_failure(
        "extra file without manifest",
        lambda assets, _private: (assets / "notes.txt").write_text("unexpected", encoding="utf-8"),
        "unexpected without manifest.json: notes.txt",
    )
    expect_failure(
        "directory without manifest",
        lambda assets, _private: (assets / "nested").mkdir(),
        "unexpected without manifest.json: nested",
    )

    def replace_readme_with_symlink(assets: Path, _private: Path) -> None:
        readme = assets / "README.md"
        target = assets / "manifest.schema.json"
        readme.unlink()
        readme.symlink_to(target.name)

    expect_failure(
        "bootstrap symlink",
        replace_readme_with_symlink,
        "bootstrap asset README.md must be a non-symlink",
    )

    def replace_readme_with_hard_link(assets: Path, _private: Path) -> None:
        readme = assets / "README.md"
        target = assets / "manifest.schema.json"
        readme.unlink()
        readme.hardlink_to(target)

    expect_failure(
        "bootstrap hard link",
        replace_readme_with_hard_link,
        "bootstrap asset README.md must be a non-symlink, single-link regular file",
    )

    expect_failure(
        "invalid manifest delegates to full validator",
        lambda assets, _private: (assets / "manifest.json").write_text("{}\n", encoding="utf-8"),
        "Linux GUI screenshot evidence validation failed: manifest is missing required fields",
    )
    expect_failure(
        "manifest symlink delegates fail closed",
        lambda assets, _private: (assets / "manifest.json").symlink_to("README.md"),
        "can't open manifest without following links",
    )
    expect_failure(
        "missing private-string file",
        lambda _assets, private: private.unlink(),
        "can't inspect private-string file",
    )

    print("Linux GUI screenshot repository-state tests passed (1 success and 10 negative cases).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
