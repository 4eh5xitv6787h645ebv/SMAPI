#!/usr/bin/env python3
"""Validate the complete Phase 4 Linux GUI screenshot evidence bundle."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import struct
import sys
from typing import Any, Iterable
from urllib.parse import urlparse
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

REAL_QUALIFICATION_IDS = frozenset({
    "D1", "D5",
    "R2", "R4", "R5",
    "I1", "I2", "I3", "I4",
    "U1", "U2", "U3",
    "P4",
    "N1", "N2", "N3",
    "B1", "B2",
    "L1", "L2", "L3",
    "C1", "C2", "C3",
    "E5", "E6",
    "G1",
    "A6", "A7",
    "M2", "M3",
})

SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
GIT_OBJECT_RE = re.compile(r"^[0-9a-f]{40}$")
FILENAME_RE = re.compile(r"^[a-z0-9][a-z0-9._-]*\.png$")
RESOLUTION_RE = re.compile(r"^[1-9][0-9]{1,4}x[1-9][0-9]{1,4}$")
TIMESTAMP_RE = re.compile(
    r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$"
)
TAG_RE = re.compile(r"^fork-4eh5xitv6787h645ebv-linux-v\d+\.\d+\.\d+-alpha\.[1-9]\d*$")

FORBIDDEN_TEXT_PATTERNS = (
    (re.compile(r"(?:^|[\s'\"=(])/(?:home|Users)/[^\s'\"<>]+", re.IGNORECASE), "personal absolute path"),
    (re.compile(r"\bfile://", re.IGNORECASE), "file URL"),
    (re.compile(r"\b(?:gh[opsu]_[A-Za-z0-9_]{12,}|github_pat_[A-Za-z0-9_]{12,})\b"), "GitHub token"),
    (re.compile(r"\bBearer\s+[A-Za-z0-9._~+/-]{8,}", re.IGNORECASE), "bearer credential"),
    (re.compile(r"(?:[?&](?:token|access_token|signature|sig|x-amz-signature)=)", re.IGNORECASE), "signed or credentialed URL"),
)

REJECTED_PNG_CHUNKS = frozenset({b"tEXt", b"zTXt", b"iTXt", b"eXIf", b"tIME"})
APNG_CHUNKS = frozenset({b"acTL", b"fcTL", b"fdAT"})
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


class EvidenceError(Exception):
    pass


def fail(message: str) -> None:
    raise EvidenceError(message)


def require_object(value: Any, context: str, required: Iterable[str], allowed: Iterable[str]) -> dict[str, Any]:
    if not isinstance(value, dict):
        fail(f"{context} must be an object")
    required_set = set(required)
    allowed_set = set(allowed)
    missing = sorted(required_set - value.keys())
    unknown = sorted(value.keys() - allowed_set)
    if missing:
        fail(f"{context} is missing required fields: {', '.join(missing)}")
    if unknown:
        fail(f"{context} has unknown fields: {', '.join(unknown)}")
    return value


def require_nonempty(value: Any, context: str, maximum: int = 4096) -> str:
    if not isinstance(value, str) or not value.strip():
        fail(f"{context} must be a non-empty string")
    if len(value) > maximum:
        fail(f"{context} exceeds {maximum} characters")
    return value


def require_pattern(value: Any, pattern: re.Pattern[str], context: str) -> str:
    text = require_nonempty(value, context)
    if pattern.fullmatch(text) is None:
        fail(f"{context} has an invalid format")
    return text


def require_sha256(value: Any, context: str) -> str:
    return require_pattern(value, SHA256_RE, context)


def require_bool(value: Any, expected: bool, context: str) -> None:
    if value is not expected:
        fail(f"{context} must be {str(expected).lower()}")


def require_int(value: Any, minimum: int, maximum: int, context: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or not minimum <= value <= maximum:
        fail(f"{context} must be an integer from {minimum} through {maximum}")
    return value


def validate_https_url(value: Any, context: str, *, github_release_tag: str | None = None) -> str:
    text = require_nonempty(value, context)
    parsed = urlparse(text)
    if parsed.scheme != "https" or not parsed.netloc or parsed.username or parsed.password or parsed.query or parsed.fragment:
        fail(f"{context} must be an HTTPS URL without credentials, query, or fragment")
    if github_release_tag is not None:
        expected = f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/{github_release_tag}"
        if text != expected:
            fail(f"{context} must be the exact fork release URL for the recorded tag")
    return text


def validate_reference(value: Any, context: str, repository_root: Path) -> str:
    text = require_nonempty(value, context)
    if text.startswith("https://"):
        return validate_https_url(text, context)
    if "#" in text:
        path_text, _anchor = text.split("#", 1)
    else:
        path_text = text
    path = Path(path_text)
    if path.is_absolute() or not path.parts or ".." in path.parts or "." in path.parts:
        fail(f"{context} must be a normalized repository-relative path or HTTPS URL")
    candidate = repository_root.joinpath(path)
    try:
        resolved = candidate.resolve(strict=True)
        resolved.relative_to(repository_root)
        candidate_stat = candidate.lstat()
    except (OSError, ValueError):
        fail(f"{context} points to a missing repository file: {path_text}")
    if not stat.S_ISREG(candidate_stat.st_mode) or resolved != candidate:
        fail(f"{context} must point to a non-symlink repository file: {path_text}")
    return text


def iter_strings(value: Any) -> Iterable[str]:
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for key, nested in value.items():
            yield str(key)
            yield from iter_strings(nested)
    elif isinstance(value, list):
        for nested in value:
            yield from iter_strings(nested)


def load_private_strings(path: Path) -> tuple[str, ...]:
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeError) as exc:
        fail(f"can't read private-string file: {exc}")
    values: list[str] = []
    for line_number, raw in enumerate(lines, start=1):
        value = raw.strip()
        if not value or value.startswith("#"):
            continue
        if len(value) < 4:
            fail(f"private-string file line {line_number} is shorter than four characters")
        values.append(value)
    if not values:
        fail("private-string file must contain at least one non-comment value")
    return tuple(values)


def scan_private_text(strings: Iterable[str], private_strings: tuple[str, ...]) -> None:
    private_folded = tuple((value, value.casefold()) for value in private_strings)
    for value in strings:
        for pattern, description in FORBIDDEN_TEXT_PATTERNS:
            if pattern.search(value):
                fail(f"text privacy scan found a {description}")
        folded = value.casefold()
        for original, forbidden in private_folded:
            if forbidden in folded:
                fail(f"text privacy scan found a configured private string ({len(original)} characters)")


def scan_private_bytes(data: bytes, private_strings: tuple[str, ...], context: str) -> None:
    lowered = data.lower()
    for value in private_strings:
        utf8 = value.encode("utf-8")
        utf16 = value.encode("utf-16-le")
        if utf8.lower() in lowered or utf16.lower() in lowered:
            fail(f"{context} contains a configured private string ({len(value)} characters)")


def parse_png(path: Path, private_strings: tuple[str, ...]) -> tuple[int, int, str]:
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
    except OSError as exc:
        fail(f"can't open PNG {path.name} without following links: {exc}")
    try:
        initial = os.fstat(descriptor)
        if not stat.S_ISREG(initial.st_mode) or initial.st_nlink != 1:
            fail(f"PNG {path.name} must be a single-link regular file")
        if initial.st_size <= len(PNG_SIGNATURE) or initial.st_size > 64 * 1024 * 1024:
            fail(f"PNG {path.name} violates the 64 MiB file-size bound")
        chunks: list[bytes] = []
        remaining = initial.st_size
        while remaining:
            chunk = os.read(descriptor, min(remaining, 1024 * 1024))
            if not chunk:
                fail(f"PNG {path.name} changed or ended while being read")
            chunks.append(chunk)
            remaining -= len(chunk)
        if os.read(descriptor, 1):
            fail(f"PNG {path.name} grew while being read")
        final = os.fstat(descriptor)
        identity_fields = ("st_dev", "st_ino", "st_mode", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
        if any(getattr(initial, field) != getattr(final, field) for field in identity_fields):
            fail(f"PNG {path.name} changed while being read")
        data = b"".join(chunks)
    except OSError as exc:
        fail(f"can't read PNG {path.name}: {exc}")
    finally:
        os.close(descriptor)
    scan_private_bytes(data, private_strings, f"PNG {path.name}")
    if not data.startswith(PNG_SIGNATURE):
        fail(f"{path.name} is not a PNG")

    offset = len(PNG_SIGNATURE)
    width: int | None = None
    height: int | None = None
    chunk_index = 0
    saw_idat = False
    saw_iend = False
    while offset < len(data):
        if len(data) - offset < 12:
            fail(f"PNG {path.name} has a truncated chunk")
        length = struct.unpack(">I", data[offset:offset + 4])[0]
        chunk_type = data[offset + 4:offset + 8]
        chunk_end = offset + 12 + length
        if chunk_end > len(data):
            fail(f"PNG {path.name} has a truncated {chunk_type!r} chunk")
        chunk_data = data[offset + 8:offset + 8 + length]
        if re.fullmatch(rb"[A-Za-z]{4}", chunk_type) is None:
            fail(f"PNG {path.name} has an invalid chunk type")
        recorded_crc = struct.unpack(">I", data[offset + 8 + length:chunk_end])[0]
        actual_crc = zlib.crc32(chunk_type)
        actual_crc = zlib.crc32(chunk_data, actual_crc) & 0xFFFFFFFF
        if recorded_crc != actual_crc:
            fail(f"PNG {path.name} has an invalid chunk checksum")
        if chunk_index == 0:
            if chunk_type != b"IHDR" or length != 13:
                fail(f"PNG {path.name} does not start with a canonical IHDR")
            width, height = struct.unpack(">II", chunk_data[:8])
            if width == 0 or height == 0 or width > 32768 or height > 32768:
                fail(f"PNG {path.name} has invalid dimensions")
            bit_depth, color_type, compression, filter_method, interlace = chunk_data[8:13]
            valid_bit_depths = {
                0: {1, 2, 4, 8, 16},
                2: {8, 16},
                3: {1, 2, 4, 8},
                4: {8, 16},
                6: {8, 16},
            }
            if (
                color_type not in valid_bit_depths
                or bit_depth not in valid_bit_depths[color_type]
                or compression != 0
                or filter_method != 0
                or interlace not in (0, 1)
            ):
                fail(f"PNG {path.name} has an invalid IHDR encoding")
        elif chunk_type == b"IHDR":
            fail(f"PNG {path.name} has duplicate IHDR metadata")
        if chunk_type in REJECTED_PNG_CHUNKS:
            fail(f"PNG {path.name} contains incidental {chunk_type.decode('ascii')} metadata")
        if chunk_type in APNG_CHUNKS:
            fail(f"PNG {path.name} must be a static image")
        if chunk_type == b"IDAT":
            saw_idat = True
        if chunk_type == b"IEND":
            if length != 0 or chunk_end != len(data) or not saw_idat:
                fail(f"PNG {path.name} has malformed or trailing data after IEND")
            saw_iend = True
        offset = chunk_end
        chunk_index += 1
    if not saw_iend or width is None or height is None:
        fail(f"PNG {path.name} is incomplete")
    return width, height, hashlib.sha256(data).hexdigest()


def validate_schema_contract(schema: Any) -> None:
    root = require_object(
        schema,
        "schema",
        ("$schema", "$id", "title", "description", "type", "additionalProperties", "required", "properties", "$defs"),
        ("$schema", "$id", "title", "description", "type", "additionalProperties", "required", "properties", "$defs"),
    )
    try:
        ids = root["$defs"]["capture"]["properties"]["id"]["enum"]
        minimum = root["properties"]["captures"]["minItems"]
        maximum = root["properties"]["captures"]["maxItems"]
    except (KeyError, TypeError) as exc:
        fail(f"schema does not expose the required capture-ID contract: {exc}")
    if ids != list(EXPECTED_IDS) or minimum != len(EXPECTED_IDS) or maximum != len(EXPECTED_IDS):
        fail("schema capture IDs or count do not exactly match the 57-ID contract")


def validate_spec_contract(spec_path: Path) -> None:
    try:
        text = spec_path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as exc:
        fail(f"can't read screenshot evidence specification: {exc}")
    ids = tuple(re.findall(r"^\| ([DRIUPXNBLCGEAM]\d+) \|", text, flags=re.MULTILINE))
    if ids != EXPECTED_IDS:
        fail("screenshot evidence specification does not exactly match the ordered 57-ID contract")
    r1_match = re.search(r"^\| R1 \| (.+?) \|", text, flags=re.MULTILINE)
    if r1_match is None:
        fail("screenshot evidence specification is missing R1 semantics")
    r1_text = r1_match.group(1).casefold()
    required_r1_phrases = (
        "bounded public prerelease choices",
        "local-package route",
        "without claiming an installed/current, upgrade, or downgrade relationship",
        "authenticated game-receipt inspection",
    )
    if any(phrase not in r1_text for phrase in required_r1_phrases):
        fail("R1 must remain pre-receipt catalog/local-route evidence; authenticated relationships belong to U1-U2")


def validate_capture(
    item: Any,
    index: int,
    assets_root: Path,
    repository_root: Path,
    private_strings: tuple[str, ...],
) -> tuple[str, str]:
    context = f"captures[{index}]"
    required = (
        "id", "filename", "alt_text", "caption", "evidence_class", "source", "release", "binaries",
        "fixture_or_injection", "operation", "durable_state", "environment", "runtime", "capture", "editing",
        "privacy_review", "qualification_reference",
    )
    capture_item = require_object(item, context, required, required)
    evidence_id = require_nonempty(capture_item["id"], f"{context}.id", 3)
    if evidence_id not in EXPECTED_IDS:
        fail(f"{context}.id is unknown: {evidence_id}")
    filename = require_pattern(capture_item["filename"], FILENAME_RE, f"{context}.filename")
    require_nonempty(capture_item["alt_text"], f"{context}.alt_text", 600)
    require_nonempty(capture_item["caption"], f"{context}.caption", 1200)
    evidence_class = capture_item["evidence_class"]
    if evidence_class not in ("real_qualification", "controlled_fixture"):
        fail(f"{context}.evidence_class is invalid")
    if evidence_id in REAL_QUALIFICATION_IDS and evidence_class != "real_qualification":
        fail(f"{evidence_id} requires real_qualification evidence")

    source = require_object(capture_item["source"], f"{context}.source", ("commit", "tree"), ("commit", "tree"))
    require_pattern(source["commit"], GIT_OBJECT_RE, f"{context}.source.commit")
    require_pattern(source["tree"], GIT_OBJECT_RE, f"{context}.source.tree")

    release = require_object(
        capture_item["release"],
        f"{context}.release",
        ("tag", "package_sha256", "public_release_url"),
        ("tag", "package_sha256", "public_release_url"),
    )
    if evidence_class == "real_qualification":
        tag = require_pattern(release["tag"], TAG_RE, f"{context}.release.tag")
        require_sha256(release["package_sha256"], f"{context}.release.package_sha256")
        validate_https_url(
            release["public_release_url"],
            f"{context}.release.public_release_url",
            github_release_tag=tag,
        )
    else:
        optional_release_values = (release["tag"], release["package_sha256"], release["public_release_url"])
        if any(value is not None for value in optional_release_values):
            if not all(value is not None for value in optional_release_values):
                fail(f"{context}.release fields must be either all recorded or all null")
            tag = require_pattern(release["tag"], TAG_RE, f"{context}.release.tag")
            require_sha256(release["package_sha256"], f"{context}.release.package_sha256")
            validate_https_url(
                release["public_release_url"],
                f"{context}.release.public_release_url",
                github_release_tag=tag,
            )

    binaries = require_object(
        capture_item["binaries"],
        f"{context}.binaries",
        ("gui_sha256", "backend_sha256"),
        ("gui_sha256", "backend_sha256"),
    )
    require_sha256(binaries["gui_sha256"], f"{context}.binaries.gui_sha256")
    require_sha256(binaries["backend_sha256"], f"{context}.binaries.backend_sha256")
    require_nonempty(capture_item["fixture_or_injection"], f"{context}.fixture_or_injection", 1200)
    require_nonempty(capture_item["operation"], f"{context}.operation", 240)

    durable = require_object(
        capture_item["durable_state"],
        f"{context}.durable_state",
        ("before", "after"),
        ("before", "after"),
    )
    require_nonempty(durable["before"], f"{context}.durable_state.before", 1200)
    require_nonempty(durable["after"], f"{context}.durable_state.after", 1200)

    environment_fields = (
        "distribution", "architecture", "desktop_environment", "session_type", "display_backend",
        "display_scale_percent", "theme", "resolution",
    )
    environment = require_object(
        capture_item["environment"], f"{context}.environment", environment_fields, environment_fields
    )
    for field in ("distribution", "architecture", "desktop_environment"):
        require_nonempty(environment[field], f"{context}.environment.{field}", 160)
    if environment["session_type"] not in ("x11", "wayland"):
        fail(f"{context}.environment.session_type is invalid")
    if environment["display_backend"] not in ("x11", "xwayland"):
        fail(f"{context}.environment.display_backend is invalid")
    if environment["display_backend"] == "xwayland" and environment["session_type"] != "wayland":
        fail(f"{context}.environment xwayland requires a wayland session")
    require_int(environment["display_scale_percent"], 50, 400, f"{context}.environment.display_scale_percent")
    if environment["theme"] not in ("light", "dark", "high_contrast"):
        fail(f"{context}.environment.theme is invalid")
    require_pattern(environment["resolution"], RESOLUTION_RE, f"{context}.environment.resolution")

    runtime_fields = ("avalonia", "dotnet_sdk", "dotnet_runtime")
    runtime = require_object(capture_item["runtime"], f"{context}.runtime", runtime_fields, runtime_fields)
    for field in runtime_fields:
        require_nonempty(runtime[field], f"{context}.runtime.{field}", 160)

    capture_fields = ("timestamp", "tool", "command", "width", "height", "sha256")
    capture = require_object(capture_item["capture"], f"{context}.capture", capture_fields, capture_fields)
    require_pattern(capture["timestamp"], TIMESTAMP_RE, f"{context}.capture.timestamp")
    require_nonempty(capture["tool"], f"{context}.capture.tool", 160)
    require_nonempty(capture["command"], f"{context}.capture.command", 1200)
    expected_width = require_int(capture["width"], 1, 32768, f"{context}.capture.width")
    expected_height = require_int(capture["height"], 1, 32768, f"{context}.capture.height")
    expected_sha256 = require_sha256(capture["sha256"], f"{context}.capture.sha256")

    editing_fields = (
        "lossless_crop", "contact_sheet", "application_pixels_altered", "statement",
        "original_sources",
    )
    editing = require_object(capture_item["editing"], f"{context}.editing", editing_fields, editing_fields)
    if not isinstance(editing["lossless_crop"], bool) or not isinstance(editing["contact_sheet"], bool):
        fail(f"{context}.editing crop and contact-sheet fields must be booleans")
    require_bool(editing["application_pixels_altered"], False, f"{context}.editing.application_pixels_altered")
    require_nonempty(editing["statement"], f"{context}.editing.statement", 1200)
    original_sources = editing["original_sources"]
    if not isinstance(original_sources, list) or len(original_sources) > 16:
        fail(f"{context}.editing.original_sources must be an array of no more than 16 entries")
    minimum_sources = 2 if editing["contact_sheet"] else (1 if editing["lossless_crop"] else 0)
    if len(original_sources) < minimum_sources or (minimum_sources == 0 and original_sources):
        fail(f"{context}.editing.original_sources does not match the crop/contact-sheet declaration")
    original_names: list[str] = []
    original_fields = ("filename", "sha256", "width", "height")
    for source_index, source_item in enumerate(original_sources):
        source_context = f"{context}.editing.original_sources[{source_index}]"
        source = require_object(source_item, source_context, original_fields, original_fields)
        source_filename = require_pattern(source["filename"], FILENAME_RE, f"{source_context}.filename")
        if source_filename == filename:
            fail(f"{source_context}.filename must identify a separately retained source PNG")
        expected_source_sha256 = require_sha256(source["sha256"], f"{source_context}.sha256")
        expected_source_width = require_int(source["width"], 1, 32768, f"{source_context}.width")
        expected_source_height = require_int(source["height"], 1, 32768, f"{source_context}.height")
        source_width, source_height, actual_source_sha256 = parse_png(
            assets_root / source_filename, private_strings
        )
        if (source_width, source_height) != (expected_source_width, expected_source_height):
            fail(f"{source_context} dimensions do not match retained PNG {source_filename}")
        if actual_source_sha256 != expected_source_sha256:
            fail(f"{source_context}.sha256 does not match retained PNG {source_filename}")
        original_names.append(source_filename)
    if len(set(original_names)) != len(original_names):
        fail(f"{context}.editing.original_sources contains duplicate filenames")

    privacy_fields = ("status", "reviewer", "inspected_original_resolution", "notes")
    privacy = require_object(capture_item["privacy_review"], f"{context}.privacy_review", privacy_fields, privacy_fields)
    if privacy["status"] != "pass":
        fail(f"{context}.privacy_review.status must be pass")
    require_nonempty(privacy["reviewer"], f"{context}.privacy_review.reviewer", 160)
    require_bool(
        privacy["inspected_original_resolution"], True, f"{context}.privacy_review.inspected_original_resolution"
    )
    require_nonempty(privacy["notes"], f"{context}.privacy_review.notes", 1200)
    validate_reference(capture_item["qualification_reference"], f"{context}.qualification_reference", repository_root)

    png_path = assets_root / filename
    width, height, actual_sha256 = parse_png(png_path, private_strings)
    if (width, height) != (expected_width, expected_height):
        fail(
            f"{context}.capture dimensions {expected_width}x{expected_height} do not match "
            f"PNG {filename} dimensions {width}x{height}"
        )
    if actual_sha256 != expected_sha256:
        fail(f"{context}.capture.sha256 does not match PNG {filename}")
    return evidence_id, filename


def validate_manifest(
    manifest_path: Path,
    schema_path: Path,
    spec_path: Path,
    assets_root: Path,
    repository_root: Path,
    private_strings_path: Path,
) -> None:
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        fail(f"can't read manifest: {exc}")
    try:
        schema = json.loads(schema_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        fail(f"can't read schema: {exc}")

    validate_schema_contract(schema)
    validate_spec_contract(spec_path)
    private_strings = load_private_strings(private_strings_path)
    scan_private_text(iter_strings(manifest), private_strings)

    root_fields = ("schema_version", "screenshot_spec", "captures")
    root = require_object(manifest, "manifest", root_fields, root_fields)
    if root["schema_version"] != 1:
        fail("manifest.schema_version must be 1")
    if root["screenshot_spec"] != "docs/technical/linux-gui-screenshot-evidence.md":
        fail("manifest.screenshot_spec must name the authoritative repository specification")
    captures = root["captures"]
    if not isinstance(captures, list):
        fail("manifest.captures must be an array")
    if len(captures) != len(EXPECTED_IDS):
        fail(f"manifest must contain exactly {len(EXPECTED_IDS)} captures")

    seen_ids: list[str] = []
    seen_filenames: list[str] = []
    for index, item in enumerate(captures):
        evidence_id, filename = validate_capture(
            item, index, assets_root, repository_root, private_strings
        )
        seen_ids.append(evidence_id)
        seen_filenames.append(filename)

    duplicate_ids = sorted({value for value in seen_ids if seen_ids.count(value) > 1})
    duplicate_filenames = sorted({value for value in seen_filenames if seen_filenames.count(value) > 1})
    if duplicate_ids:
        fail(f"manifest contains duplicate screenshot IDs: {', '.join(duplicate_ids)}")
    if duplicate_filenames:
        fail(f"manifest contains duplicate screenshot filenames: {', '.join(duplicate_filenames)}")
    if set(seen_ids) != set(EXPECTED_IDS):
        missing = sorted(set(EXPECTED_IDS) - set(seen_ids))
        unknown = sorted(set(seen_ids) - set(EXPECTED_IDS))
        fail(
            "manifest screenshot-ID coverage is incomplete"
            + (f"; missing: {', '.join(missing)}" if missing else "")
            + (f"; unknown: {', '.join(unknown)}" if unknown else "")
        )


def parse_args() -> argparse.Namespace:
    script_path = Path(__file__).resolve()
    repository_root = script_path.parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--assets-root", type=Path, required=True)
    parser.add_argument("--private-strings-file", type=Path, required=True)
    parser.add_argument("--repository-root", type=Path, default=repository_root)
    parser.add_argument(
        "--schema",
        type=Path,
        default=repository_root / "docs/screenshots/linux-gui/manifest.schema.json",
    )
    parser.add_argument(
        "--spec",
        type=Path,
        default=repository_root / "docs/technical/linux-gui-screenshot-evidence.md",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        repository_root = args.repository_root.resolve(strict=True)
        validate_manifest(
            args.manifest.resolve(strict=True),
            args.schema.resolve(strict=True),
            args.spec.resolve(strict=True),
            args.assets_root.resolve(strict=True),
            repository_root,
            args.private_strings_file.resolve(strict=True),
        )
    except (EvidenceError, OSError) as exc:
        print(f"Linux GUI screenshot evidence validation failed: {exc}", file=sys.stderr)
        return 1
    print(f"Linux GUI screenshot evidence is valid: {len(EXPECTED_IDS)} IDs and source PNGs verified.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
