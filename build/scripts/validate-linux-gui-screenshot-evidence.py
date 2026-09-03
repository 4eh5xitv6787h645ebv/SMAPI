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

E2_FAULTS = ("permission", "read-only", "disk-full", "cross-device")

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
ACTIONS_RUN_RE = re.compile(
    r"^https://github\.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/[1-9][0-9]*"
    r"(?:/attempts/[1-9][0-9]*)?$"
)

FORBIDDEN_TEXT_PATTERNS = (
    (re.compile(r"(?:(?:^|[\s'\"=(])/(?!/)|:(?!//)/)[^\s'\"<>]+"), "absolute path"),
    (re.compile(r"\bfile://", re.IGNORECASE), "file URL"),
    (re.compile(r"\b(?:gh[opsu]_[A-Za-z0-9_]{12,}|github_pat_[A-Za-z0-9_]{12,})\b"), "GitHub token"),
    (re.compile(r"\bBearer\s+[A-Za-z0-9._~+/-]{8,}", re.IGNORECASE), "bearer credential"),
    (re.compile(r"(?:[?&](?:token|access_token|signature|sig|x-amz-signature)=)", re.IGNORECASE), "signed or credentialed URL"),
)

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
PNG_ALLOWED_CHUNKS = frozenset({b"IHDR", b"cHRM", b"gAMA", b"sRGB", b"PLTE", b"tRNS", b"IDAT", b"IEND"})
PNG_SINGLETON_CHUNKS = frozenset({b"IHDR", b"cHRM", b"gAMA", b"sRGB", b"PLTE", b"tRNS", b"IEND"})
MAX_PNG_FILE_BYTES = 64 * 1024 * 1024
MAX_PNG_DIMENSION = 32768
MAX_PNG_PIXELS = 64_000_000
MAX_PNG_DECODED_BYTES = 256 * 1024 * 1024
FIXED_ASSET_FILES = frozenset({"README.md", "manifest.schema.json", "manifest.json"})


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


def validate_package_url(value: Any, context: str, tag: str) -> str:
    text = validate_https_url(value, context)
    tag_match = re.fullmatch(
        r"fork-4eh5xitv6787h645ebv-linux-v([0-9]+\.[0-9]+\.[0-9]+)-alpha\.([1-9][0-9]*)",
        tag,
    )
    if tag_match is None:
        fail(f"{context} can't derive a package identity from the recorded tag")
    version_base, alpha_number = tag_match.groups()
    package_name = (
        f"SMAPI-{version_base}-unofficial.4eh5xitv6787h645ebv.linux.alpha.{alpha_number}"
        "-linux-x64-installer.zip"
    )
    expected = f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/{tag}/{package_name}"
    if text != expected:
        fail(f"{context} must be the exact installer ZIP URL derived from the recorded tag")
    return text


def markdown_anchor_exists(document: str, anchor: str) -> bool:
    escaped = re.escape(anchor)
    if re.search(rf"\bid\s*=\s*(['\"]){escaped}\1", document, flags=re.IGNORECASE):
        return True
    if re.search(rf"\{{:?#{escaped}\}}", document, flags=re.IGNORECASE):
        return True
    for heading in re.findall(r"^#{1,6}[ \t]+(.+?)\s*#*\s*$", document, flags=re.MULTILINE):
        heading = re.sub(r"!?\[([^]]*)\]\([^)]*\)", r"\1", heading)
        heading = re.sub(r"<[^>]+>", "", heading)
        heading = heading.replace("`", "").replace("*", "").replace("_", "")
        slug = re.sub(r"[^a-z0-9 -]", "", heading.casefold())
        slug = re.sub(r"[ \t]+", "-", slug.strip())
        if slug == anchor:
            return True
    return False


def validate_reference(
    value: Any,
    context: str,
    repository_root: Path,
    evidence_id: str,
    evidence_class: str,
) -> str:
    text = require_nonempty(value, context)
    if text.startswith("https://"):
        if ACTIONS_RUN_RE.fullmatch(text) is None:
            fail(f"{context} must be an exact fork GitHub Actions run URL")
        return text
    if text.count("#") != 1:
        fail(f"{context} local reference must include one non-empty evidence anchor")
    path_text, anchor = text.split("#", 1)
    if not anchor:
        fail(f"{context} local reference must include one non-empty evidence anchor")
    expected_anchor = f"evidence-{evidence_id.casefold()}"
    if anchor != expected_anchor:
        fail(f"{context} local anchor must identify {expected_anchor}")
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
    screenshot_spec = Path("docs/technical/linux-gui-screenshot-evidence.md")
    is_dedicated_record = (
        path.parent == Path("docs/technical")
        and path.suffix == ".md"
        and ("qualification" in path.stem or "validation" in path.stem)
    )
    if evidence_class == "real_qualification" and not is_dedicated_record:
        fail(f"{context} real evidence requires a dedicated qualification/validation record or Actions run")
    if evidence_class == "controlled_fixture" and path != screenshot_spec and not is_dedicated_record:
        fail(f"{context} controlled evidence requires the anchored screenshot spec or a dedicated record")
    if evidence_id == "A8" and path == screenshot_spec:
        fail(f"{context} A8 requires separate AT-SPI/Orca qualification evidence")
    document = read_single_link_text(candidate, f"qualification reference {path_text}")
    if not markdown_anchor_exists(document, anchor):
        fail(f"{context} anchor does not exist in {path_text}: {anchor}")
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


def load_private_strings(path: Path, repository_root: Path) -> tuple[str, ...]:
    try:
        absolute = path.absolute()
        metadata = absolute.lstat()
        resolved = absolute.resolve(strict=True)
        if (
            absolute.is_symlink()
            or not stat.S_ISREG(metadata.st_mode)
            or metadata.st_nlink != 1
            or metadata.st_uid != os.geteuid()
            or stat.S_IMODE(metadata.st_mode) != 0o600
            or resolved != absolute
        ):
            fail("private-string file must be one normalized current-user single-link file at exact mode 0600")
        if resolved == repository_root or repository_root in resolved.parents:
            fail("private-string file must be outside the repository")
        lines = read_single_link_text(resolved, "private-string file").splitlines()
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


def read_single_link_text(path: Path, context: str, maximum_bytes: int = 16 * 1024 * 1024) -> str:
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
    except OSError as exc:
        fail(f"can't open {context} without following links: {exc}")
    try:
        initial = os.fstat(descriptor)
        if not stat.S_ISREG(initial.st_mode) or initial.st_nlink != 1:
            fail(f"{context} must be a single-link regular file")
        if initial.st_size <= 0 or initial.st_size > maximum_bytes:
            fail(f"{context} violates the {maximum_bytes}-byte size bound")
        chunks: list[bytes] = []
        remaining = initial.st_size
        while remaining:
            chunk = os.read(descriptor, min(remaining, 1024 * 1024))
            if not chunk:
                fail(f"{context} changed or ended while being read")
            chunks.append(chunk)
            remaining -= len(chunk)
        if os.read(descriptor, 1):
            fail(f"{context} grew while being read")
        final = os.fstat(descriptor)
        identity_fields = ("st_dev", "st_ino", "st_mode", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
        if any(getattr(initial, field) != getattr(final, field) for field in identity_fields):
            fail(f"{context} changed while being read")
    except OSError as exc:
        fail(f"can't read {context}: {exc}")
    finally:
        os.close(descriptor)
    try:
        return b"".join(chunks).decode("utf-8")
    except UnicodeError as exc:
        fail(f"{context} is not valid UTF-8: {exc}")


def parse_png(path: Path, private_strings: tuple[str, ...]) -> tuple[int, int, str]:
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
    except OSError as exc:
        fail(f"can't open PNG {path.name} without following links: {exc}")
    try:
        initial = os.fstat(descriptor)
        if not stat.S_ISREG(initial.st_mode) or initial.st_nlink != 1:
            fail(f"PNG {path.name} must be a single-link regular file")
        if initial.st_size <= len(PNG_SIGNATURE) or initial.st_size > MAX_PNG_FILE_BYTES:
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
    ended_idat = False
    seen_chunks: set[bytes] = set()
    idat_parts: list[bytes] = []
    channels: int | None = None
    color_type: int | None = None
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
            if (
                width == 0 or height == 0
                or width > MAX_PNG_DIMENSION or height > MAX_PNG_DIMENSION
                or width * height > MAX_PNG_PIXELS
            ):
                fail(f"PNG {path.name} has invalid dimensions")
            bit_depth, color_type, compression, filter_method, interlace = chunk_data[8:13]
            if bit_depth != 8 or color_type not in (2, 6) or compression != 0 or filter_method != 0 or interlace != 0:
                fail(f"PNG {path.name} has an invalid IHDR encoding")
            channels = 3 if color_type == 2 else 4
        elif chunk_type == b"IHDR":
            fail(f"PNG {path.name} has duplicate IHDR metadata")
        if chunk_type not in PNG_ALLOWED_CHUNKS:
            kind = "critical" if chunk_type[0] & 0x20 == 0 else "ancillary"
            fail(f"PNG {path.name} contains disallowed {kind} chunk {chunk_type.decode('ascii')}")
        if chunk_type in PNG_SINGLETON_CHUNKS and chunk_type in seen_chunks:
            fail(f"PNG {path.name} contains duplicate {chunk_type.decode('ascii')} chunk")
        if chunk_type in {b"cHRM", b"gAMA", b"sRGB", b"PLTE", b"tRNS"} and saw_idat:
            fail(f"PNG {path.name} has {chunk_type.decode('ascii')} after image data")
        expected_chunk_lengths = {b"cHRM": 32, b"gAMA": 4, b"sRGB": 1}
        if chunk_type in expected_chunk_lengths and length != expected_chunk_lengths[chunk_type]:
            fail(f"PNG {path.name} has malformed {chunk_type.decode('ascii')} metadata")
        if chunk_type == b"PLTE" and (length == 0 or length % 3 != 0 or length > 768 or color_type == 6):
            fail(f"PNG {path.name} has an unnecessary or malformed PLTE chunk")
        if chunk_type == b"tRNS" and (color_type != 2 or length != 6):
            fail(f"PNG {path.name} has an unnecessary or malformed tRNS chunk")
        if saw_idat and chunk_type != b"IDAT":
            ended_idat = True
        if chunk_type == b"IDAT":
            if ended_idat:
                fail(f"PNG {path.name} has non-consecutive IDAT chunks")
            saw_idat = True
            idat_parts.append(chunk_data)
        if chunk_type == b"IEND":
            if length != 0 or chunk_end != len(data) or not saw_idat:
                fail(f"PNG {path.name} has malformed or trailing data after IEND")
            saw_iend = True
        seen_chunks.add(chunk_type)
        offset = chunk_end
        chunk_index += 1
    if not saw_iend or width is None or height is None or channels is None:
        fail(f"PNG {path.name} is incomplete")
    expected_decoded = height * (1 + width * channels)
    if expected_decoded > MAX_PNG_DECODED_BYTES:
        fail(f"PNG {path.name} violates the decoded-byte bound")
    decompressor = zlib.decompressobj()
    decoded = bytearray()
    try:
        for compressed in idat_parts:
            remaining_output = expected_decoded + 1 - len(decoded)
            decoded.extend(decompressor.decompress(compressed, max(remaining_output, 1)))
            if decompressor.unconsumed_tail or len(decoded) > expected_decoded:
                fail(f"PNG {path.name} exceeds its exact decoded scanline size")
        decoded.extend(decompressor.flush(expected_decoded + 1 - len(decoded)))
    except zlib.error as exc:
        fail(f"PNG {path.name} has invalid zlib image data: {exc}")
    if not decompressor.eof or decompressor.unused_data or decompressor.unconsumed_tail:
        fail(f"PNG {path.name} has incomplete or trailing zlib image data")
    if len(decoded) != expected_decoded:
        fail(f"PNG {path.name} decoded scanline size is {len(decoded)}, expected {expected_decoded}")
    row_bytes = 1 + width * channels
    if any(decoded[offset] > 4 for offset in range(0, len(decoded), row_bytes)):
        fail(f"PNG {path.name} has an invalid scanline filter")
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
        root_required = root["required"]
        identity_reference = root["properties"]["production_identity"]["$ref"]
        e2_faults = root["$defs"]["e2Fault"]["enum"]
        original_source = root["$defs"]["capture"]["properties"]["editing"]["properties"]["original_sources"]["items"]
        e2_contract = next(
            contract for contract in root["$defs"]["capture"]["allOf"]
            if contract.get("if", {}).get("properties", {}).get("id", {}).get("const") == "E2"
        )
        e2_editing = e2_contract["then"]["properties"]["editing"]["properties"]
        e2_sources = e2_editing["original_sources"]
        e2_contains = [item["contains"]["properties"]["fault"]["const"] for item in e2_sources["allOf"]]
    except (KeyError, TypeError) as exc:
        fail(f"schema does not expose the required capture-ID contract: {exc}")
    except StopIteration:
        fail("schema does not expose the required E2 fault-gallery contract")
    if ids != list(EXPECTED_IDS) or minimum != len(EXPECTED_IDS) or maximum != len(EXPECTED_IDS):
        fail("schema capture IDs or count do not exactly match the 57-ID contract")
    if "production_identity" not in root_required or identity_reference != "#/$defs/productionIdentity":
        fail("schema does not require the reviewed production identity")
    source_required = {"filename", "sha256", "width", "height", "environment", "capture", "privacy_review"}
    e2_source_required = {"fault", "fixture_or_injection", "operation", "durable_state"}
    if (
        e2_faults != list(E2_FAULTS)
        or not source_required.issubset(original_source["required"])
        or original_source["properties"].get("fault", {}).get("$ref") != "#/$defs/e2Fault"
        or e2_editing.get("contact_sheet", {}).get("const") is not True
        or e2_sources.get("minItems") != len(E2_FAULTS)
        or e2_sources.get("maxItems") != len(E2_FAULTS)
        or not e2_source_required.issubset(e2_sources.get("items", {}).get("required", []))
        or e2_contains != list(E2_FAULTS)
    ):
        fail("schema does not require the exact four-source E2 fault-gallery provenance contract")


def validate_spec_contract(spec_path: Path) -> None:
    try:
        text = spec_path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as exc:
        fail(f"can't read screenshot evidence specification: {exc}")
    ids = tuple(re.findall(r"^\| ([DRIUPXNBLCGEAM]\d+) \|", text, flags=re.MULTILINE))
    if ids != EXPECTED_IDS:
        fail("screenshot evidence specification does not exactly match the ordered 57-ID contract")
    anchors = tuple(re.findall(r'<a id="evidence-([a-z][0-9]+)"></a>', text))
    expected_anchors = tuple(evidence_id.casefold() for evidence_id in EXPECTED_IDS)
    if anchors != expected_anchors:
        fail("screenshot evidence specification does not expose one ordered anchor for every evidence ID")
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
    e2_match = re.search(r"^\| E2 \| (.+?) \| (.+?) \|", text, flags=re.MULTILINE)
    if e2_match is None:
        fail("screenshot evidence specification is missing E2 semantics")
    e2_text = " ".join(e2_match.groups()).casefold()
    required_e2_phrases = (
        "exactly four visible failure states",
        "permission, read-only, disk-full, and cross-device",
        "four-source controlled real-filesystem fault contact sheet",
        "retaining all four original pngs",
    )
    if any(phrase not in e2_text for phrase in required_e2_phrases):
        fail("E2 must remain an exact four-source permission/read-only/disk-full/cross-device contact sheet")


def validate_environment(value: Any, context: str) -> dict[str, Any]:
    fields = (
        "distribution", "architecture", "desktop_environment", "session_type", "display_backend",
        "display_scale_percent", "theme", "resolution",
    )
    environment = require_object(value, context, fields, fields)
    for field in ("distribution", "architecture", "desktop_environment"):
        require_nonempty(environment[field], f"{context}.{field}", 160)
    if environment["session_type"] not in ("x11", "wayland"):
        fail(f"{context}.session_type is invalid")
    if environment["display_backend"] not in ("x11", "xwayland"):
        fail(f"{context}.display_backend is invalid")
    if environment["display_backend"] == "xwayland" and environment["session_type"] != "wayland":
        fail(f"{context} xwayland requires a wayland session")
    require_int(environment["display_scale_percent"], 50, 400, f"{context}.display_scale_percent")
    if environment["theme"] not in ("light", "dark", "high_contrast"):
        fail(f"{context}.theme is invalid")
    require_pattern(environment["resolution"], RESOLUTION_RE, f"{context}.resolution")
    return environment


def validate_privacy_review(value: Any, context: str) -> dict[str, Any]:
    fields = ("status", "reviewer", "inspected_original_resolution", "notes")
    privacy = require_object(value, context, fields, fields)
    if privacy["status"] != "pass":
        fail(f"{context}.status must be pass")
    require_nonempty(privacy["reviewer"], f"{context}.reviewer", 160)
    require_bool(privacy["inspected_original_resolution"], True, f"{context}.inspected_original_resolution")
    require_nonempty(privacy["notes"], f"{context}.notes", 1200)
    return privacy


def validate_production_identity(value: Any) -> dict[str, str]:
    fields = (
        "source_commit", "source_tree", "release_tag", "package_url", "package_sha256", "public_release_url",
        "gui_binary_sha256", "backend_binary_sha256",
    )
    identity = require_object(value, "manifest.production_identity", fields, fields)
    require_pattern(identity["source_commit"], GIT_OBJECT_RE, "manifest.production_identity.source_commit")
    require_pattern(identity["source_tree"], GIT_OBJECT_RE, "manifest.production_identity.source_tree")
    tag = require_pattern(identity["release_tag"], TAG_RE, "manifest.production_identity.release_tag")
    validate_package_url(identity["package_url"], "manifest.production_identity.package_url", tag)
    require_sha256(identity["package_sha256"], "manifest.production_identity.package_sha256")
    validate_https_url(identity["public_release_url"], "manifest.production_identity.public_release_url", github_release_tag=tag)
    require_sha256(identity["gui_binary_sha256"], "manifest.production_identity.gui_binary_sha256")
    require_sha256(identity["backend_binary_sha256"], "manifest.production_identity.backend_binary_sha256")
    return identity


def validate_capture(
    item: Any,
    index: int,
    assets_root: Path,
    repository_root: Path,
    private_strings: tuple[str, ...],
    production_identity: dict[str, str],
) -> tuple[str, str, str, tuple[tuple[str, tuple[Any, ...]], ...], tuple[Any, ...]]:
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
    commit = require_pattern(source["commit"], GIT_OBJECT_RE, f"{context}.source.commit")
    tree = require_pattern(source["tree"], GIT_OBJECT_RE, f"{context}.source.tree")
    if commit != production_identity["source_commit"] or tree != production_identity["source_tree"]:
        fail(f"{context}.source does not match manifest.production_identity")

    release = require_object(
        capture_item["release"],
        f"{context}.release",
        ("tag", "package_url", "package_sha256", "public_release_url"),
        ("tag", "package_url", "package_sha256", "public_release_url"),
    )
    tag = require_pattern(release["tag"], TAG_RE, f"{context}.release.tag")
    package_url = validate_package_url(release["package_url"], f"{context}.release.package_url", tag)
    package_sha256 = require_sha256(release["package_sha256"], f"{context}.release.package_sha256")
    public_release_url = validate_https_url(
        release["public_release_url"], f"{context}.release.public_release_url", github_release_tag=tag,
    )
    if (
        tag != production_identity["release_tag"]
        or package_url != production_identity["package_url"]
        or package_sha256 != production_identity["package_sha256"]
        or public_release_url != production_identity["public_release_url"]
    ):
        fail(f"{context}.release does not match manifest.production_identity")

    binaries = require_object(
        capture_item["binaries"],
        f"{context}.binaries",
        ("gui_sha256", "backend_sha256"),
        ("gui_sha256", "backend_sha256"),
    )
    gui_sha256 = require_sha256(binaries["gui_sha256"], f"{context}.binaries.gui_sha256")
    backend_sha256 = require_sha256(binaries["backend_sha256"], f"{context}.binaries.backend_sha256")
    if (
        gui_sha256 != production_identity["gui_binary_sha256"]
        or backend_sha256 != production_identity["backend_binary_sha256"]
    ):
        fail(f"{context}.binaries does not match manifest.production_identity")
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

    environment = validate_environment(capture_item["environment"], f"{context}.environment")

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
    if evidence_id == "E2" and not editing["contact_sheet"]:
        fail("E2 must be a contact sheet retaining every matrix source PNG")
    if evidence_id == "E2" and len(original_sources) != len(E2_FAULTS):
        fail("E2 original sources must provide exactly permission/read-only/disk-full/cross-device faults")
    minimum_sources = 2 if editing["contact_sheet"] else (1 if editing["lossless_crop"] else 0)
    if len(original_sources) < minimum_sources or (minimum_sources == 0 and original_sources):
        fail(f"{context}.editing.original_sources does not match the crop/contact-sheet declaration")
    original_names: list[str] = []
    original_provenance: list[tuple[str, tuple[Any, ...]]] = []
    original_base_fields = (
        "filename", "sha256", "width", "height", "environment", "capture", "privacy_review",
    )
    original_e2_fields = original_base_fields + (
        "fault", "fixture_or_injection", "operation", "durable_state",
    )
    original_environments: list[dict[str, Any]] = []
    original_faults: list[str] = []
    for source_index, source_item in enumerate(original_sources):
        source_context = f"{context}.editing.original_sources[{source_index}]"
        required_source_fields = original_e2_fields if evidence_id == "E2" else original_base_fields
        source = require_object(source_item, source_context, required_source_fields, required_source_fields)
        source_filename = require_pattern(source["filename"], FILENAME_RE, f"{source_context}.filename")
        if source_filename == filename:
            fail(f"{source_context}.filename must identify a separately retained source PNG")
        expected_source_sha256 = require_sha256(source["sha256"], f"{source_context}.sha256")
        expected_source_width = require_int(source["width"], 1, 32768, f"{source_context}.width")
        expected_source_height = require_int(source["height"], 1, 32768, f"{source_context}.height")
        source_fault = source.get("fault")
        source_fixture = source.get("fixture_or_injection")
        source_operation = source.get("operation")
        source_durable: dict[str, Any] | None = None
        if evidence_id == "E2":
            if source_fault not in E2_FAULTS:
                fail(f"{source_context}.fault must identify an exact E2 fault class")
            original_faults.append(source_fault)
            source_fixture = require_nonempty(
                source_fixture, f"{source_context}.fixture_or_injection", 1200
            )
            source_operation = require_nonempty(source_operation, f"{source_context}.operation", 240)
            source_durable = require_object(
                source["durable_state"],
                f"{source_context}.durable_state",
                ("before", "after"),
                ("before", "after"),
            )
            require_nonempty(source_durable["before"], f"{source_context}.durable_state.before", 1200)
            require_nonempty(source_durable["after"], f"{source_context}.durable_state.after", 1200)
        source_environment = validate_environment(source["environment"], f"{source_context}.environment")
        source_capture_fields = ("timestamp", "tool", "command")
        source_capture = require_object(
            source["capture"], f"{source_context}.capture", source_capture_fields, source_capture_fields
        )
        require_pattern(source_capture["timestamp"], TIMESTAMP_RE, f"{source_context}.capture.timestamp")
        require_nonempty(source_capture["tool"], f"{source_context}.capture.tool", 160)
        require_nonempty(source_capture["command"], f"{source_context}.capture.command", 1200)
        source_privacy = validate_privacy_review(source["privacy_review"], f"{source_context}.privacy_review")
        source_width, source_height, actual_source_sha256 = parse_png(
            assets_root / source_filename, private_strings
        )
        if (source_width, source_height) != (expected_source_width, expected_source_height):
            fail(f"{source_context} dimensions do not match retained PNG {source_filename}")
        if actual_source_sha256 != expected_source_sha256:
            fail(f"{source_context}.sha256 does not match retained PNG {source_filename}")
        original_names.append(source_filename)
        original_environments.append(source_environment)
        original_provenance.append((source_filename, (
            expected_source_sha256, expected_source_width, expected_source_height,
            source_fault, source_fixture, source_operation,
            json.dumps(source_durable, sort_keys=True) if source_durable is not None else None,
            json.dumps(source_environment, sort_keys=True), json.dumps(source_capture, sort_keys=True),
            json.dumps(source_privacy, sort_keys=True),
        )))
    if len(set(original_names)) != len(original_names):
        fail(f"{context}.editing.original_sources contains duplicate filenames")

    if evidence_id in {"E2", "A4", "A5", "A6", "A7"} and not editing["contact_sheet"]:
        fail(f"{evidence_id} must be a contact sheet retaining every matrix source PNG")
    if evidence_id == "E2" and sorted(original_faults) != sorted(E2_FAULTS):
        fail("E2 original sources must provide exactly permission/read-only/disk-full/cross-device faults")
    if evidence_id == "A4":
        scales = [item["display_scale_percent"] for item in original_environments]
        if sorted(scales) != [100, 125, 150, 200]:
            fail("A4 original sources must provide exactly the 100/125/150/200 scale set")
    if evidence_id == "A5":
        themes = [item["theme"] for item in original_environments]
        if sorted(themes) != ["dark", "high_contrast", "light"]:
            fail("A5 original sources must provide exactly light/dark/high_contrast themes")
    if evidence_id in {"A6", "A7"}:
        expected_session = "x11" if evidence_id == "A6" else "wayland"
        expected_backend = "x11" if evidence_id == "A6" else "xwayland"
        matrix = {
            (item["desktop_environment"], item["session_type"], item["display_backend"])
            for item in original_environments
        }
        expected_matrix = {
            ("GNOME", expected_session, expected_backend),
            ("KDE", expected_session, expected_backend),
        }
        if len(original_environments) != 2 or matrix != expected_matrix:
            fail(
                f"{evidence_id} original sources must provide exactly GNOME+KDE "
                f"{expected_session}/{expected_backend}"
            )

    privacy = validate_privacy_review(capture_item["privacy_review"], f"{context}.privacy_review")
    validate_reference(
        capture_item["qualification_reference"],
        f"{context}.qualification_reference",
        repository_root,
        evidence_id,
        evidence_class,
    )

    png_path = assets_root / filename
    width, height, actual_sha256 = parse_png(png_path, private_strings)
    if (width, height) != (expected_width, expected_height):
        fail(
            f"{context}.capture dimensions {expected_width}x{expected_height} do not match "
            f"PNG {filename} dimensions {width}x{height}"
        )
    if actual_sha256 != expected_sha256:
        fail(f"{context}.capture.sha256 does not match PNG {filename}")
    shared_final_provenance = (
        expected_width, expected_height, expected_sha256,
        json.dumps(environment, sort_keys=True), json.dumps(runtime, sort_keys=True),
        json.dumps(capture, sort_keys=True), json.dumps(editing, sort_keys=True),
        json.dumps(privacy, sort_keys=True),
    )
    return evidence_id, filename, expected_sha256, tuple(original_provenance), shared_final_provenance


def validate_manifest(
    manifest_path: Path,
    schema_path: Path,
    spec_path: Path,
    assets_root: Path,
    repository_root: Path,
    private_strings_path: Path,
) -> None:
    try:
        manifest = json.loads(read_single_link_text(manifest_path, "manifest"))
    except json.JSONDecodeError as exc:
        fail(f"can't read manifest: {exc}")
    try:
        schema = json.loads(read_single_link_text(schema_path, "schema"))
    except json.JSONDecodeError as exc:
        fail(f"can't read schema: {exc}")

    validate_schema_contract(schema)
    validate_spec_contract(spec_path)
    private_strings = load_private_strings(private_strings_path, repository_root)
    scan_private_text(iter_strings(manifest), private_strings)

    root_fields = ("schema_version", "screenshot_spec", "production_identity", "captures")
    root = require_object(manifest, "manifest", root_fields, root_fields)
    if root["schema_version"] != 1:
        fail("manifest.schema_version must be 1")
    if root["screenshot_spec"] != "docs/technical/linux-gui-screenshot-evidence.md":
        fail("manifest.screenshot_spec must name the authoritative repository specification")
    production_identity = validate_production_identity(root["production_identity"])
    captures = root["captures"]
    if not isinstance(captures, list):
        fail("manifest.captures must be an array")
    if len(captures) != len(EXPECTED_IDS):
        fail(f"manifest must contain exactly {len(EXPECTED_IDS)} captures")

    seen_ids: list[str] = []
    final_provenance: dict[str, tuple[Any, ...]] = {}
    final_hashes: dict[str, str] = {}
    original_provenance: dict[str, tuple[Any, ...]] = {}
    for index, item in enumerate(captures):
        evidence_id, filename, final_sha256, originals, provenance = validate_capture(
            item, index, assets_root, repository_root, private_strings, production_identity
        )
        seen_ids.append(evidence_id)
        if filename in final_provenance:
            fail(f"each evidence ID must use a distinct final screenshot filename; repeated: {filename}")
        if final_sha256 in final_hashes:
            fail(
                "each evidence ID must use distinct final screenshot pixels; "
                f"{filename} duplicates {final_hashes[final_sha256]}"
            )
        final_provenance[filename] = provenance
        final_hashes[final_sha256] = filename
        for original_filename, original_identity in originals:
            if (
                original_filename in original_provenance
                and original_provenance[original_filename] != original_identity
            ):
                fail(f"retained original filename {original_filename} has inconsistent provenance")
            original_provenance[original_filename] = original_identity

    duplicate_ids = sorted({value for value in seen_ids if seen_ids.count(value) > 1})
    if duplicate_ids:
        fail(f"manifest contains duplicate screenshot IDs: {', '.join(duplicate_ids)}")
    if set(seen_ids) != set(EXPECTED_IDS):
        missing = sorted(set(EXPECTED_IDS) - set(seen_ids))
        unknown = sorted(set(seen_ids) - set(EXPECTED_IDS))
        fail(
            "manifest screenshot-ID coverage is incomplete"
            + (f"; missing: {', '.join(missing)}" if missing else "")
            + (f"; unknown: {', '.join(unknown)}" if unknown else "")
        )
    original_filenames = set(original_provenance)
    conflicting_roles = sorted(set(final_provenance) & original_filenames)
    if conflicting_roles:
        fail(f"PNG filenames cannot be both final and retained original: {', '.join(conflicting_roles)}")

    if manifest_path.parent != assets_root or manifest_path.name != "manifest.json":
        fail("manifest must be the fixed manifest.json file directly inside assets root")
    if schema_path.parent != assets_root or schema_path.name != "manifest.schema.json":
        fail("schema must be the fixed manifest.schema.json file directly inside assets root")
    expected_assets = FIXED_ASSET_FILES | set(final_provenance) | original_filenames
    try:
        entries = list(os.scandir(assets_root))
    except OSError as exc:
        fail(f"can't inventory screenshot assets root: {exc}")
    actual_assets = {entry.name for entry in entries}
    missing_assets = sorted(expected_assets - actual_assets)
    unexpected_assets = sorted(actual_assets - expected_assets)
    if missing_assets or unexpected_assets:
        fail(
            "screenshot assets inventory is not exact"
            + (f"; missing: {', '.join(missing_assets)}" if missing_assets else "")
            + (f"; unexpected: {', '.join(unexpected_assets)}" if unexpected_assets else "")
        )
    for entry in entries:
        entry_stat = entry.stat(follow_symlinks=False)
        if not stat.S_ISREG(entry_stat.st_mode) or entry.is_symlink() or entry_stat.st_nlink != 1:
            fail(f"screenshot asset {entry.name} must be a single-link regular file, never a directory or symlink")
        if entry.name not in FIXED_ASSET_FILES and not entry.name.endswith(".png"):
            fail(f"screenshot asset {entry.name} must be a referenced PNG")


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
            args.manifest.absolute(),
            args.schema.absolute(),
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
