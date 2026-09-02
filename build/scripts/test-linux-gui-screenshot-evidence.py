#!/usr/bin/env python3
"""Self-tests for validate-linux-gui-screenshot-evidence.py."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
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


def make_png(red: int, *, text: str | None = None) -> bytes:
    signature = b"\x89PNG\r\n\x1a\n"
    ihdr = png_chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0))
    metadata = b"" if text is None else png_chunk(b"tEXt", b"Comment\x00" + text.encode("utf-8"))
    image = png_chunk(b"IDAT", zlib.compress(bytes((0, red, 20, 30, 255))))
    return signature + ihdr + metadata + image + png_chunk(b"IEND", b"")


def make_capture(evidence_id: str, filename: str, digest: str) -> dict[str, Any]:
    real = evidence_id in REAL_IDS
    tag = "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2"
    release = {
        "tag": tag if real else None,
        "package_sha256": "1" * 64 if real else None,
        "public_release_url": (
            f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/{tag}" if real else None
        ),
    }
    return {
        "id": evidence_id,
        "filename": filename,
        "alt_text": f"Installer evidence state {evidence_id} with the relevant action and status visible.",
        "caption": f"{evidence_id}: verified installer evidence captured for the Phase 4 matrix.",
        "evidence_class": "real_qualification" if real else "controlled_fixture",
        "source": {
            "commit": "2" * 40,
            "tree": "3" * 40,
        },
        "release": release,
        "binaries": {
            "gui_sha256": "4" * 64,
            "backend_sha256": "5" * 64,
        },
        "fixture_or_injection": "Disposable public-data qualification fixture; no private workload data.",
        "operation": "State capture",
        "durable_state": {
            "before": "Disposable state recorded before capture.",
            "after": "Disposable state recorded after capture.",
        },
        "environment": {
            "distribution": "Example Linux 1",
            "architecture": "x86_64",
            "desktop_environment": "Example Desktop",
            "session_type": "x11",
            "display_backend": "x11",
            "display_scale_percent": 100,
            "theme": "light",
            "resolution": "1920x1080",
        },
        "runtime": {
            "avalonia": "11.3.12",
            "dotnet_sdk": "10.0.108",
            "dotnet_runtime": "10.0.8",
        },
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
        "qualification_reference": "docs/technical/linux-gui-screenshot-evidence.md",
    }


def write_fixture(root: Path) -> tuple[Path, Path, Path, dict[str, Any]]:
    assets = root / "assets"
    assets.mkdir(parents=True)
    captures: list[dict[str, Any]] = []
    for index, evidence_id in enumerate(EXPECTED_IDS):
        filename = f"{evidence_id.lower()}.png"
        data = make_png(index + 1)
        (assets / filename).write_bytes(data)
        captures.append(make_capture(evidence_id, filename, hashlib.sha256(data).hexdigest()))
    manifest = {
        "schema_version": 1,
        "screenshot_spec": "docs/technical/linux-gui-screenshot-evidence.md",
        "captures": captures,
    }
    manifest_path = root / "manifest.json"
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
    expect_failure(
        "duplicate ID",
        lambda manifest, _assets: manifest["captures"][2].__setitem__("id", "D2"),
        "duplicate screenshot IDs",
    )

    def duplicate_filename(manifest: dict[str, Any], _assets: Path) -> None:
        first = manifest["captures"][0]
        second = manifest["captures"][1]
        second["filename"] = first["filename"]
        second["capture"]["sha256"] = first["capture"]["sha256"]

    expect_failure("duplicate filename", duplicate_filename, "duplicate screenshot filenames")
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
        data = make_png(2, text="incidental metadata")
        (assets / entry["filename"]).write_bytes(data)
        entry["capture"]["sha256"] = hashlib.sha256(data).hexdigest()

    expect_failure("PNG metadata", add_metadata, "incidental tEXt metadata")

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
            "qualification_reference", "docs/technical/missing-evidence.md"
        ),
        "missing repository file",
    )
    expect_failure(
        "edited pixels",
        lambda manifest, _assets: manifest["captures"][1]["editing"].__setitem__(
            "application_pixels_altered", True
        ),
        "application_pixels_altered must be false",
    )
    print("Linux GUI screenshot evidence validator self-tests passed (1 derived-image and 18 negative cases).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
