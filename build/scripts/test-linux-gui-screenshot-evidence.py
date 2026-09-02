#!/usr/bin/env python3
"""Self-tests for validate-linux-gui-screenshot-evidence.py."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
from typing import Any, Callable
import zlib


EXPECTED_IDS = (
    "D1", "D2", "D3", "D4", "D5",
    "R1", "R2", "R3", "R4", "R5", "R6", "R7",
    "I1", "I2", "I3", "I4",
    "U1", "U2", "U3",
    "P1", "P2", "P3", "P4",
    "X1",
    "N1", "N2", "N3",
    "B1", "B2", "B3", "B4",
    "L1", "L2", "L3",
    "C1", "C2", "C3",
    "E1", "E2", "E3", "E4", "E5", "E6",
    "G1", "G2", "G3",
    "A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8",
    "M1", "M2", "M3",
)

REAL_IDS = frozenset({
    "D1", "D5", "R2", "R4", "R5", "I1", "I2", "I3", "I4", "U1", "U2", "U3", "P4",
    "N1", "N2", "N3", "B1", "B2", "L1", "L2", "L3", "C1", "C2", "C3", "E5", "E6",
    "G1", "A6", "A7", "M2", "M3",
})

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
VALIDATOR = REPOSITORY_ROOT / "build/scripts/validate-linux-gui-screenshot-evidence.py"
PRIVATE_TOKEN = "fixture-secret-needle"


def png_chunk(chunk_type: bytes, data: bytes) -> bytes:
    crc = zlib.crc32(chunk_type)
    crc = zlib.crc32(data, crc) & 0xFFFFFFFF
    return struct.pack(">I", len(data)) + chunk_type + data + struct.pack(">I", crc)


def make_png(
    red: int,
    *,
    before_idat: tuple[tuple[bytes, bytes], ...] = (),
    compressed: bytes | None = None,
    width: int = 1,
    height: int = 1,
) -> bytes:
    signature = b"\x89PNG\r\n\x1a\n"
    ihdr = png_chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    metadata = b"".join(png_chunk(kind, payload) for kind, payload in before_idat)
    raw = b"".join(bytes((0, red, 20, 30, 255)) * width for _ in range(height))
    image = png_chunk(b"IDAT", zlib.compress(raw) if compressed is None else compressed)
    return signature + ihdr + metadata + image + png_chunk(b"IEND", b"")


def make_split_idat_png(red: int) -> bytes:
    signature = b"\x89PNG\r\n\x1a\n"
    ihdr = png_chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0))
    compressed = zlib.compress(bytes((0, red, 20, 30, 255)))
    midpoint = len(compressed) // 2
    return (
        signature + ihdr + png_chunk(b"IDAT", compressed[:midpoint])
        + png_chunk(b"IDAT", compressed[midpoint:]) + png_chunk(b"IEND", b"")
    )


TAG = "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2"
PRODUCTION_IDENTITY = {
    "source_commit": "2" * 40,
    "source_tree": "3" * 40,
    "release_tag": TAG,
    "package_url": f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/{TAG}/SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip",
    "package_sha256": "1" * 64,
    "public_release_url": f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/{TAG}",
    "gui_binary_sha256": "4" * 64,
    "backend_binary_sha256": "5" * 64,
}


def make_capture(evidence_id: str, filename: str, digest: str) -> dict[str, Any]:
    real = evidence_id in REAL_IDS
    release = {
        "tag": TAG,
        "package_url": PRODUCTION_IDENTITY["package_url"],
        "package_sha256": "1" * 64,
        "public_release_url": PRODUCTION_IDENTITY["public_release_url"],
    }
    return {
        "id": evidence_id,
        "filename": filename,
        "alt_text": f"Installer evidence state {evidence_id} with the relevant action and status visible.",
        "caption": f"{evidence_id}: verified installer evidence captured for the Phase 4 matrix.",
        "evidence_class": "real_qualification" if real else "controlled_fixture",
        "source": {"commit": "2" * 40, "tree": "3" * 40},
        "release": release,
        "binaries": {"gui_sha256": "4" * 64, "backend_sha256": "5" * 64},
        "fixture_or_injection": "Disposable public-data qualification fixture; no private workload data.",
        "operation": "State capture",
        "durable_state": {
            "before": "Disposable state recorded before capture.",
            "after": "Disposable state recorded after capture.",
        },
        "environment": environment(),
        "runtime": {"avalonia": "11.3.12", "dotnet_sdk": "10.0.108", "dotnet_runtime": "10.0.8"},
        "capture": {
            "timestamp": "2026-09-03T00:00:00+08:00",
            "tool": "fixture PNG writer",
            "command": "capture application-window",
            "width": 1,
            "height": 1,
            "sha256": digest,
        },
        "editing": {
            "lossless_crop": False,
            "contact_sheet": False,
            "application_pixels_altered": False,
            "statement": "No crop or pixel alteration; incidental metadata absent.",
            "original_sources": [],
        },
        "privacy_review": {
            "status": "pass",
            "reviewer": "independent-reviewer",
            "inspected_original_resolution": True,
            "notes": "Original-resolution fixture inspected; no private data present.",
        },
        "qualification_reference": (
            "https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/123456789"
            if real
            else f"docs/technical/linux-gui-screenshot-evidence.md#evidence-{evidence_id.casefold()}"
        ),
    }


def make_original_source(filename: str, digest: str, environment: dict[str, Any]) -> dict[str, Any]:
    return {
        "filename": filename,
        "sha256": digest,
        "width": 1,
        "height": 1,
        "environment": environment,
        "capture": {
            "timestamp": "2026-09-03T00:00:00+08:00",
            "tool": "fixture PNG writer",
            "command": "capture application-window",
        },
        "privacy_review": {
            "status": "pass",
            "reviewer": "independent-reviewer",
            "inspected_original_resolution": True,
            "notes": "Original-resolution fixture inspected; no private data present.",
        },
    }


def environment(*, scale: int = 100, theme: str = "light", desktop: str = "Example Desktop", session: str = "x11", backend: str = "x11") -> dict[str, Any]:
    return {
        "distribution": "Example Linux 1",
        "architecture": "x86_64",
        "desktop_environment": desktop,
        "session_type": session,
        "display_backend": backend,
        "display_scale_percent": scale,
        "theme": theme,
        "resolution": "1920x1080",
    }


def add_matrix_sources(captures: list[dict[str, Any]], assets: Path) -> None:
    matrices = {
        "A4": [environment(scale=value) for value in (100, 125, 150, 200)],
        "A5": [environment(theme=value) for value in ("light", "dark", "high_contrast")],
        "A6": [environment(desktop=value, session="x11", backend="x11") for value in ("GNOME", "KDE")],
        "A7": [environment(desktop=value, session="wayland", backend="xwayland") for value in ("GNOME", "KDE")],
    }
    by_id = {item["id"]: item for item in captures}
    for evidence_id, environments in matrices.items():
        entry = by_id[evidence_id]
        entry["editing"]["contact_sheet"] = True
        entry["editing"]["statement"] = "Contact sheet assembled without altering application pixels; all sources retained."
        for index, source_environment in enumerate(environments, start=1):
            filename = f"{evidence_id.lower()}-source-{index}.png"
            data = make_png(100 + index)
            (assets / filename).write_bytes(data)
            entry["editing"]["original_sources"].append(
                make_original_source(filename, hashlib.sha256(data).hexdigest(), source_environment)
            )


def write_fixture(root: Path) -> tuple[Path, Path, Path, dict[str, Any]]:
    assets = root / "assets"
    assets.mkdir(parents=True)
    captures: list[dict[str, Any]] = []
    for index, evidence_id in enumerate(EXPECTED_IDS):
        filename = f"{evidence_id.lower()}.png"
        data = make_png(index + 1)
        (assets / filename).write_bytes(data)
        captures.append(make_capture(evidence_id, filename, hashlib.sha256(data).hexdigest()))
    add_matrix_sources(captures, assets)
    manifest = {
        "schema_version": 1,
        "screenshot_spec": "docs/technical/linux-gui-screenshot-evidence.md",
        "production_identity": dict(PRODUCTION_IDENTITY),
        "captures": captures,
    }
    manifest_path = assets / "manifest.json"
    shutil.copy2(REPOSITORY_ROOT / "docs/screenshots/linux-gui/manifest.schema.json", assets / "manifest.schema.json")
    shutil.copy2(REPOSITORY_ROOT / "docs/screenshots/linux-gui/README.md", assets / "README.md")
    private_path = root / "private-strings.txt"
    private_path.write_text(PRIVATE_TOKEN + "\n", encoding="utf-8")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest_path, assets, private_path, manifest


def run_validator(manifest: Path, assets: Path, private_path: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            sys.executable,
            str(VALIDATOR),
            "--manifest", str(manifest),
            "--assets-root", str(assets),
            "--private-strings-file", str(private_path),
            "--repository-root", str(REPOSITORY_ROOT),
            "--schema", str(assets / "manifest.schema.json"),
        ],
        check=False,
        capture_output=True,
        text=True,
    )


def expect_failure(
    name: str,
    mutation: Callable[[dict[str, Any], Path], None],
    expected: str,
) -> None:
    with tempfile.TemporaryDirectory(prefix="smapi-gui-evidence-test.") as temporary:
        root = Path(temporary)
        manifest_path, assets, private_path, manifest = write_fixture(root)
        mutation(manifest, assets)
        manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        result = run_validator(manifest_path, assets, private_path)
        combined = result.stdout + result.stderr
        if result.returncode == 0 or expected not in combined:
            raise AssertionError(
                f"{name}: expected failure containing {expected!r}, got exit {result.returncode}:\n{combined}"
            )


def expect_success(name: str, mutation: Callable[[dict[str, Any], Path], None]) -> None:
    with tempfile.TemporaryDirectory(prefix="smapi-gui-evidence-test.") as temporary:
        root = Path(temporary)
        manifest_path, assets, private_path, manifest = write_fixture(root)
        mutation(manifest, assets)
        manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        result = run_validator(manifest_path, assets, private_path)
        if result.returncode != 0:
            raise AssertionError(f"{name}: expected success:\n{result.stdout}{result.stderr}")


def add_lossless_crop_source(manifest: dict[str, Any], assets: Path) -> None:
    entry = manifest["captures"][1]
    source_filename = "d2-original.png"
    data = make_png(200)
    (assets / source_filename).write_bytes(data)
    entry["editing"]["lossless_crop"] = True
    entry["editing"]["statement"] = "Lossless application-window crop; application pixels were not altered."
    entry["editing"]["original_sources"] = [{
        "filename": source_filename,
        "sha256": hashlib.sha256(data).hexdigest(),
        "width": 1,
        "height": 1,
        "environment": environment(),
        "capture": {
            "timestamp": "2026-09-03T00:00:00+08:00",
            "tool": "fixture PNG writer",
            "command": "capture application-window",
        },
        "privacy_review": {
            "status": "pass",
            "reviewer": "independent-reviewer",
            "inspected_original_resolution": True,
            "notes": "Original-resolution fixture inspected; no private data present.",
        },
    }]


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="smapi-gui-evidence-valid.") as temporary:
        manifest_path, assets, private_path, _manifest = write_fixture(Path(temporary))
        result = run_validator(manifest_path, assets, private_path)
        if result.returncode != 0 or "57 IDs" not in result.stdout:
            raise AssertionError(f"valid evidence was rejected:\n{result.stdout}{result.stderr}")

    expect_success("retained lossless-crop source", add_lossless_crop_source)

    expect_failure(
        "missing ID",
        lambda manifest, _assets: manifest["captures"].pop(),
        "exactly 57 captures",
    )
    expect_failure(
        "unknown ID",
        lambda manifest, _assets: manifest["captures"][-1].__setitem__("id", "Z9"),
        "id is unknown",
    )
    def duplicate_id(manifest: dict[str, Any], _assets: Path) -> None:
        manifest["captures"][2]["id"] = "D2"
        manifest["captures"][2]["qualification_reference"] = (
            "https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/123456789"
        )

    expect_failure("duplicate ID", duplicate_id, "duplicate screenshot IDs")

    def duplicate_filename(manifest: dict[str, Any], _assets: Path) -> None:
        first = manifest["captures"][0]
        second = manifest["captures"][1]
        (_assets / second["filename"]).unlink()
        second["filename"] = first["filename"]
        for field in ("environment", "runtime", "capture", "editing", "privacy_review"):
            second[field] = json.loads(json.dumps(first[field]))

    expect_success("shared adjacent-state final PNG", duplicate_filename)
    expect_failure(
        "path traversal",
        lambda manifest, _assets: manifest["captures"][1].__setitem__("filename", "../d2.png"),
        "filename has an invalid format",
    )
    expect_failure(
        "hash mismatch",
        lambda manifest, _assets: manifest["captures"][1]["capture"].__setitem__("sha256", "f" * 64),
        "sha256 does not match",
    )
    expect_failure(
        "dimension mismatch",
        lambda manifest, _assets: manifest["captures"][1]["capture"].__setitem__("width", 2),
        "do not match PNG",
    )

    def add_metadata(manifest: dict[str, Any], assets: Path) -> None:
        entry = manifest["captures"][1]
        data = make_png(2, before_idat=((b"tEXt", b"Comment\x00incidental metadata"),))
        (assets / entry["filename"]).write_bytes(data)
        entry["capture"]["sha256"] = hashlib.sha256(data).hexdigest()

    expect_failure("PNG metadata", add_metadata, "disallowed ancillary chunk tEXt")

    def tamper_original_source_hash(manifest: dict[str, Any], assets: Path) -> None:
        add_lossless_crop_source(manifest, assets)
        manifest["captures"][1]["editing"]["original_sources"][0]["sha256"] = "f" * 64

    expect_failure("original source hash", tamper_original_source_hash, "does not match retained PNG")
    expect_failure(
        "missing alt text",
        lambda manifest, _assets: manifest["captures"][1].__setitem__("alt_text", ""),
        "alt_text must be a non-empty string",
    )
    expect_failure(
        "missing caption",
        lambda manifest, _assets: manifest["captures"][1].__setitem__("caption", ""),
        "caption must be a non-empty string",
    )
    expect_failure(
        "real evidence class",
        lambda manifest, _assets: manifest["captures"][0].__setitem__("evidence_class", "controlled_fixture"),
        "D1 requires real_qualification evidence",
    )
    expect_failure(
        "missing provenance",
        lambda manifest, _assets: manifest["captures"][1].pop("qualification_reference"),
        "missing required fields: qualification_reference",
    )
    expect_failure(
        "private denylist text",
        lambda manifest, _assets: manifest["captures"][1].__setitem__(
            "caption", f"Unsafe {PRIVATE_TOKEN} disclosure"
        ),
        "configured private string",
    )
    expect_failure(
        "personal path",
        lambda manifest, _assets: manifest["captures"][1]["capture"].__setitem__(
            "command", "capture /home/example/private.png"
        ),
        "personal absolute path",
    )
    expect_failure(
        "bad release link",
        lambda manifest, _assets: manifest["captures"][0]["release"].__setitem__(
            "public_release_url", "https://example.invalid/release"
        ),
        "exact fork release URL",
    )
    expect_failure(
        "broken qualification link",
        lambda manifest, _assets: manifest["captures"][1].__setitem__(
            "qualification_reference", "docs/technical/missing-evidence.md#evidence-d2"
        ),
        "missing repository file",
    )
    expect_failure(
        "generic screenshot plan for real evidence",
        lambda manifest, _assets: manifest["captures"][0].__setitem__(
            "qualification_reference", "docs/technical/linux-gui-screenshot-evidence.md#evidence-d1"
        ),
        "real evidence requires a dedicated qualification/validation record or Actions run",
    )
    expect_failure(
        "missing qualification anchor",
        lambda manifest, _assets: manifest["captures"][1].__setitem__(
            "qualification_reference", "docs/technical/linux-gui-screenshot-evidence.md"
        ),
        "must include one non-empty evidence anchor",
    )
    expect_failure(
        "nonexistent local qualification anchor",
        lambda manifest, _assets: manifest["captures"][1].__setitem__(
            "qualification_reference", "docs/technical/linux-alpha-release-validation.md#evidence-d2"
        ),
        "anchor does not exist",
    )
    expect_failure(
        "arbitrary qualification HTTPS host",
        lambda manifest, _assets: manifest["captures"][1].__setitem__(
            "qualification_reference", "https://example.invalid/actions/runs/123456789"
        ),
        "must be an exact fork GitHub Actions run URL",
    )
    expect_failure(
        "wrong qualification evidence ID",
        lambda manifest, _assets: manifest["captures"][1].__setitem__(
            "qualification_reference", "docs/technical/linux-gui-screenshot-evidence.md#evidence-d3"
        ),
        "local anchor must identify evidence-d2",
    )
    expect_failure(
        "edited pixels",
        lambda manifest, _assets: manifest["captures"][1]["editing"].__setitem__(
            "application_pixels_altered", True
        ),
        "application_pixels_altered must be false",
    )

    expect_failure(
        "controlled capture mixed commit",
        lambda manifest, _assets: manifest["captures"][1]["source"].__setitem__("commit", "9" * 40),
        "source does not match manifest.production_identity",
    )
    expect_failure(
        "mixed release package",
        lambda manifest, _assets: manifest["captures"][1]["release"].__setitem__("package_sha256", "9" * 64),
        "release does not match manifest.production_identity",
    )
    expect_failure(
        "top-level non-package asset URL",
        lambda manifest, _assets: manifest["production_identity"].__setitem__(
            "package_url",
            f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/{TAG}/SHA256SUMS",
        ),
        "exact installer ZIP URL",
    )
    expect_failure(
        "mixed package URL",
        lambda manifest, _assets: manifest["captures"][1]["release"].__setitem__(
            "package_url",
            f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/{TAG}/different.tar.gz",
        ),
        "exact installer ZIP URL",
    )
    expect_failure(
        "mixed GUI binary",
        lambda manifest, _assets: manifest["captures"][1]["binaries"].__setitem__("gui_sha256", "9" * 64),
        "binaries does not match manifest.production_identity",
    )
    expect_failure(
        "orphan PNG",
        lambda _manifest, assets: (assets / "orphan.png").write_bytes(make_png(1)),
        "unexpected: orphan.png",
    )
    expect_failure(
        "nested asset directory",
        lambda _manifest, assets: (assets / "nested").mkdir(),
        "unexpected: nested",
    )
    expect_failure(
        "unlisted non-PNG",
        lambda _manifest, assets: (assets / "notes.txt").write_text("unexpected", encoding="utf-8"),
        "unexpected: notes.txt",
    )

    def replace_d2_png(manifest: dict[str, Any], assets: Path, data: bytes) -> None:
        entry = manifest["captures"][1]
        (assets / entry["filename"]).write_bytes(data)
        entry["capture"]["sha256"] = hashlib.sha256(data).hexdigest()

    expect_success(
        "concatenated IDAT stream",
        lambda manifest, assets: replace_d2_png(manifest, assets, make_split_idat_png(2)),
    )

    expect_failure(
        "iCCP metadata",
        lambda manifest, assets: replace_d2_png(
            manifest, assets, make_png(2, before_idat=((b"iCCP", b"profile\x00\x00" + zlib.compress(b"profile")),))
        ),
        "disallowed ancillary chunk iCCP",
    )
    expect_failure(
        "custom ancillary secret",
        lambda manifest, assets: replace_d2_png(
            manifest, assets, make_png(2, before_idat=((b"ruSt", PRIVATE_TOKEN.encode("utf-8")),))
        ),
        "configured private string",
    )
    expect_failure(
        "invalid zlib",
        lambda manifest, assets: replace_d2_png(manifest, assets, make_png(2, compressed=b"not-zlib")),
        "invalid zlib image data",
    )
    expect_failure(
        "unknown critical chunk",
        lambda manifest, assets: replace_d2_png(
            manifest, assets, make_png(2, before_idat=((b"ABCD", b"unknown"),))
        ),
        "disallowed critical chunk ABCD",
    )
    expect_failure(
        "decompression bomb",
        lambda manifest, assets: replace_d2_png(
            manifest, assets, make_png(2, compressed=zlib.compress(b"\x00" + b"x" * 100_000))
        ),
        "exceeds its exact decoded scanline size",
    )
    expect_failure(
        "missing original environment provenance",
        lambda manifest, _assets: manifest["captures"][49]["editing"]["original_sources"][0].pop("environment"),
        "missing required fields: environment",
    )
    expect_failure(
        "A4 incomplete scale matrix",
        lambda manifest, _assets: manifest["captures"][49]["editing"]["original_sources"][3]["environment"].__setitem__("display_scale_percent", 150),
        "100/125/150/200 scale set",
    )
    expect_failure(
        "A5 incomplete theme matrix",
        lambda manifest, _assets: manifest["captures"][50]["editing"]["original_sources"][2]["environment"].__setitem__("theme", "dark"),
        "light/dark/high_contrast themes",
    )
    expect_failure(
        "A6 wrong desktop matrix",
        lambda manifest, _assets: manifest["captures"][51]["editing"]["original_sources"][1]["environment"].__setitem__("desktop_environment", "GNOME"),
        "GNOME+KDE x11/x11",
    )
    expect_failure(
        "A7 wrong backend matrix",
        lambda manifest, _assets: manifest["captures"][52]["editing"]["original_sources"][1]["environment"].__setitem__("display_backend", "x11"),
        "GNOME+KDE wayland/xwayland",
    )
    print("Linux GUI screenshot evidence validator self-tests passed (3 success and 40 negative cases).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
