#!/usr/bin/env python3
"""Capture or import one authentic Linux GUI screenshot into a private evidence staging directory."""

from __future__ import annotations

import argparse
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import selectors
import shutil
import signal
import stat
import struct
import subprocess
import sys
import tempfile
import time
from typing import Any, Iterable, NamedTuple
from urllib.parse import urlparse
import zlib


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
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
    "D1", "D5", "R2", "R4", "R5",
    "I1", "I2", "I3", "I4", "U1", "U2", "U3", "P4",
    "N1", "N2", "N3", "B1", "B2", "L1", "L2", "L3",
    "C1", "C2", "C3", "E5", "E6", "G1", "A6", "A7", "M2", "M3",
})
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
MAX_FILE_BYTES = 64 * 1024 * 1024
MAX_EXECUTABLE_BYTES = 256 * 1024 * 1024
MAX_COMMAND_OUTPUT_BYTES = 256 * 1024
MAX_X11_CLIENTS = 512
MAX_DIMENSION = 32768
MAX_PIXELS = 64_000_000
MAX_DECODED_BYTES = 256 * 1024 * 1024
FILENAME_RE = re.compile(r"^[a-z0-9][a-z0-9._-]*\.png$")
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
GIT_OBJECT_RE = re.compile(r"^[0-9a-f]{40}$")
TAG_RE = re.compile(r"^fork-4eh5xitv6787h645ebv-linux-v\d+\.\d+\.\d+-alpha\.[1-9]\d*$")
RESOLUTION_RE = re.compile(r"^[1-9]\d{1,4}x[1-9]\d{1,4}$")
WINDOW_ID_RE = re.compile(r"^(?:0x[0-9a-fA-F]+|[1-9]\d*)$")
DISPLAY_RE = re.compile(r"^(?:[A-Za-z0-9_.-]+)?:\d+(?:\.\d+)?$")
FORBIDDEN_TEXT_PATTERNS = (
    (
        re.compile(r"(?:(?:^|[\s'\"=(])/(?!/)|:(?!//)/)[^\s'\"<>]+"),
        "absolute path",
    ),
    (re.compile(r"\bfile://", re.IGNORECASE), "file URL"),
    (re.compile(r"\b(?:gh[opsu]_[A-Za-z0-9_]{12,}|github_pat_[A-Za-z0-9_]{12,})\b"), "GitHub token"),
    (re.compile(r"\bBearer\s+[A-Za-z0-9._~+/-]{8,}", re.IGNORECASE), "bearer credential"),
    (
        re.compile(r"(?:[?&](?:token|access_token|signature|sig|x-amz-signature)=)", re.IGNORECASE),
        "signed or credentialed URL",
    ),
)


class StagingError(Exception):
    """A safe, user-facing staging refusal."""


def fail(message: str) -> None:
    raise StagingError(message)


class ProcessIdentity(NamedTuple):
    process_id: int
    start_time: int
    executable_device: int
    executable_inode: int
    executable_size: int
    executable_sha256: str
    display: str


class WindowIdentity(NamedTuple):
    window_id: str
    process_id: int
    title: str
    geometry: tuple[int, int, int, int]


def run_bounded(
    command: list[str],
    *,
    timeout: float,
    text: bool = True,
    environment: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[Any]:
    """Run one fixed command with a shared stdout/stderr byte ceiling and hard deadline."""
    try:
        process = subprocess.Popen(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=environment,
            close_fds=True,
            start_new_session=True,
        )
    except OSError:
        fail("an X11 capture command could not be started safely")
    assert process.stdout is not None and process.stderr is not None
    selector = selectors.DefaultSelector()
    outputs: dict[int, bytearray] = {
        process.stdout.fileno(): bytearray(),
        process.stderr.fileno(): bytearray(),
    }
    selector.register(process.stdout, selectors.EVENT_READ)
    selector.register(process.stderr, selectors.EVENT_READ)
    deadline = time.monotonic() + timeout
    failure: str | None = None
    try:
        while selector.get_map():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                failure = "an X11 capture command exceeded its time bound"
                break
            events = selector.select(remaining)
            if not events:
                failure = "an X11 capture command exceeded its time bound"
                break
            for key, _mask in events:
                try:
                    chunk = os.read(key.fd, 65536)
                except OSError:
                    failure = "an X11 capture command output could not be read safely"
                    break
                if not chunk:
                    selector.unregister(key.fileobj)
                    continue
                outputs[key.fd].extend(chunk)
                if sum(len(value) for value in outputs.values()) > MAX_COMMAND_OUTPUT_BYTES:
                    failure = "an X11 capture command exceeded its output bound"
                    break
            if failure:
                break
        if failure is None:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                failure = "an X11 capture command exceeded its time bound"
            else:
                try:
                    return_code = process.wait(timeout=remaining)
                except subprocess.TimeoutExpired:
                    failure = "an X11 capture command exceeded its time bound"
        if failure is not None:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()
            fail(failure)
        stdout_bytes = bytes(outputs[process.stdout.fileno()])
        stderr_bytes = bytes(outputs[process.stderr.fileno()])
        if text:
            try:
                stdout: Any = stdout_bytes.decode("utf-8")
                stderr: Any = stderr_bytes.decode("utf-8")
            except UnicodeDecodeError:
                fail("an X11 capture command returned invalid UTF-8 text")
        else:
            stdout, stderr = stdout_bytes, stderr_bytes
        return subprocess.CompletedProcess(command, return_code, stdout, stderr)
    finally:
        selector.close()
        process.stdout.close()
        process.stderr.close()


def read_regular_file(
    path: Path,
    description: str,
    maximum: int = MAX_FILE_BYTES,
    *,
    private: bool = False,
) -> bytes:
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
    except OSError as exc:
        fail(f"can't open {description} safely: {exc.strerror or exc}")
    try:
        initial = os.fstat(descriptor)
        if not stat.S_ISREG(initial.st_mode) or initial.st_nlink != 1:
            fail(f"{description} must be a single-link regular file")
        if private and (
            initial.st_uid != os.geteuid() or stat.S_IMODE(initial.st_mode) != 0o600
        ):
            fail(f"{description} must be current-user-owned with exact mode 0600")
        if initial.st_size <= 0 or initial.st_size > maximum:
            fail(f"{description} violates its {maximum}-byte bound")
        chunks: list[bytes] = []
        remaining = initial.st_size
        while remaining:
            chunk = os.read(descriptor, min(remaining, 1024 * 1024))
            if not chunk:
                fail(f"{description} changed or ended while being read")
            chunks.append(chunk)
            remaining -= len(chunk)
        if os.read(descriptor, 1):
            fail(f"{description} grew while being read")
        final = os.fstat(descriptor)
        fields = ("st_dev", "st_ino", "st_mode", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
        if any(getattr(initial, field) != getattr(final, field) for field in fields):
            fail(f"{description} changed while being read")
        return b"".join(chunks)
    finally:
        os.close(descriptor)


def paeth(left: int, above: int, upper_left: int) -> int:
    estimate = left + above - upper_left
    distances = (abs(estimate - left), abs(estimate - above), abs(estimate - upper_left))
    return (left, above, upper_left)[distances.index(min(distances))]


def decode_png(data: bytes, description: str) -> tuple[int, int, int, bytes, tuple[str, ...]]:
    if not data.startswith(PNG_SIGNATURE):
        fail(f"{description} is not a PNG")
    offset = len(PNG_SIGNATURE)
    width = height = channels = color_type = None
    saw_idat = saw_iend = ended_idat = False
    idat_parts: list[bytes] = []
    chunk_names: list[str] = []
    while offset < len(data):
        if len(chunk_names) >= 4096:
            fail(f"{description} exceeds the PNG chunk-count bound")
        if len(data) - offset < 12:
            fail(f"{description} has a truncated PNG chunk")
        length = struct.unpack(">I", data[offset:offset + 4])[0]
        chunk_type = data[offset + 4:offset + 8]
        end = offset + 12 + length
        if end > len(data) or re.fullmatch(rb"[A-Za-z]{4}", chunk_type) is None:
            fail(f"{description} has a malformed PNG chunk")
        payload = data[offset + 8:offset + 8 + length]
        expected_crc = struct.unpack(">I", data[offset + 8 + length:end])[0]
        actual_crc = zlib.crc32(payload, zlib.crc32(chunk_type)) & 0xffffffff
        if expected_crc != actual_crc:
            fail(f"{description} has an invalid {chunk_type.decode('ascii')} checksum")
        name = chunk_type.decode("ascii")
        chunk_names.append(name)
        if len(chunk_names) == 1:
            if chunk_type != b"IHDR" or length != 13:
                fail(f"{description} does not start with one canonical IHDR")
            width, height = struct.unpack(">II", payload[:8])
            bit_depth, color_type, compression, filtering, interlace = payload[8:13]
            if (
                width == 0 or height == 0 or width > MAX_DIMENSION or height > MAX_DIMENSION
                or width * height > MAX_PIXELS
            ):
                fail(f"{description} has invalid dimensions")
            if bit_depth != 8 or color_type not in (2, 6) or compression or filtering or interlace:
                fail(f"{description} must be noninterlaced 8-bit RGB or RGBA")
            channels = 3 if color_type == 2 else 4
        elif chunk_type == b"IHDR":
            fail(f"{description} has duplicate IHDR metadata")
        if chunk_type[0] & 0x20 == 0 and chunk_type not in {b"IHDR", b"IDAT", b"IEND"}:
            fail(f"{description} contains unsupported critical chunk {name}")
        if chunk_type == b"acTL":
            fail(f"{description} must be a static PNG")
        if saw_idat and chunk_type != b"IDAT":
            ended_idat = True
        if chunk_type == b"IDAT":
            if ended_idat:
                fail(f"{description} has non-consecutive IDAT chunks")
            saw_idat = True
            idat_parts.append(payload)
        if chunk_type == b"IEND":
            if length != 0 or end != len(data) or not saw_idat:
                fail(f"{description} has malformed or trailing data after IEND")
            saw_iend = True
        offset = end
    if not saw_iend or width is None or height is None or channels is None or color_type is None:
        fail(f"{description} is incomplete")
    row_width = width * channels
    expected_size = height * (row_width + 1)
    if expected_size > MAX_DECODED_BYTES:
        fail(f"{description} violates the decoded-byte bound")
    decompressor = zlib.decompressobj()
    try:
        filtered = decompressor.decompress(b"".join(idat_parts), expected_size + 1)
        if len(filtered) > expected_size:
            fail(f"{description} has an invalid decoded scanline size or trailing compressed data")
        remaining_capacity = expected_size - len(filtered) + 1
        filtered += decompressor.flush(remaining_capacity)
    except (ValueError, zlib.error) as exc:
        fail(f"{description} has invalid compressed image data: {exc}")
    if (
        len(filtered) != expected_size or not decompressor.eof or decompressor.unused_data
        or decompressor.unconsumed_tail
    ):
        fail(f"{description} has an invalid decoded scanline size or trailing compressed data")
    pixels = bytearray(height * row_width)
    prior = bytearray(row_width)
    for row in range(height):
        start = row * (row_width + 1)
        filter_type = filtered[start]
        if filter_type > 4:
            fail(f"{description} uses an invalid PNG scanline filter")
        encoded = filtered[start + 1:start + 1 + row_width]
        decoded = bytearray(row_width)
        for index, value in enumerate(encoded):
            left = decoded[index - channels] if index >= channels else 0
            above = prior[index]
            upper_left = prior[index - channels] if index >= channels else 0
            predictor = (0, left, above, (left + above) // 2, paeth(left, above, upper_left))[filter_type]
            decoded[index] = (value + predictor) & 0xff
        pixels[row * row_width:(row + 1) * row_width] = decoded
        prior = decoded
    return width, height, color_type, bytes(pixels), tuple(chunk_names)


def png_chunk(kind: bytes, payload: bytes) -> bytes:
    checksum = zlib.crc32(payload, zlib.crc32(kind)) & 0xffffffff
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", checksum)


def encode_canonical_png(width: int, height: int, color_type: int, pixels: bytes) -> bytes:
    channels = 3 if color_type == 2 else 4
    row_width = width * channels
    scanlines = b"".join(b"\0" + pixels[row * row_width:(row + 1) * row_width] for row in range(height))
    header = struct.pack(">IIBBBBB", width, height, 8, color_type, 0, 0, 0)
    result = (
        PNG_SIGNATURE
        + png_chunk(b"IHDR", header)
        + png_chunk(b"IDAT", zlib.compress(scanlines, 9))
        + png_chunk(b"IEND", b"")
    )
    if len(result) > MAX_FILE_BYTES:
        fail("normalized PNG violates the final 64 MiB file-size bound")
    return result


def load_private_strings(path: Path) -> tuple[str, ...]:
    raw = read_regular_file(path, "private-string file", 1024 * 1024, private=True)
    try:
        lines = raw.decode("utf-8").splitlines()
    except UnicodeError:
        fail("private-string file must be valid UTF-8")
    values = tuple(line.strip() for line in lines if line.strip() and not line.lstrip().startswith("#"))
    if not values or any(len(value) < 4 for value in values):
        fail("private-string file must contain non-comment values of at least four characters")
    return values


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


def scan_safe_text(value: Any, private_strings: tuple[str, ...]) -> None:
    for text in iter_strings(value):
        for pattern, description in FORBIDDEN_TEXT_PATTERNS:
            if pattern.search(text):
                fail(f"staged provenance contains a {description}")
        folded = text.casefold()
        for private in private_strings:
            if private.casefold() in folded:
                fail(f"staged provenance contains a configured private string ({len(private)} characters)")


def validate_https(value: Any, description: str) -> str:
    if not isinstance(value, str) or not value:
        fail(f"production identity {description} is required")
    parsed = urlparse(value)
    if (
        parsed.scheme != "https" or not parsed.netloc or parsed.username or parsed.password
        or parsed.query or parsed.fragment
    ):
        fail(f"production identity {description} must be a credential-free HTTPS URL without query or fragment")
    return value


def require_text(value: Any, description: str, maximum: int) -> str:
    if not isinstance(value, str) or not value.strip():
        fail(f"{description} must be non-empty text")
    if len(value) > maximum:
        fail(f"{description} exceeds its {maximum}-character bound")
    return value


def load_identity(path: Path) -> dict[str, str]:
    try:
        value = json.loads(read_regular_file(path, "production-identity file", 64 * 1024).decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        fail(f"production-identity file is invalid: {exc}")
    fields = {
        "source_commit", "source_tree", "release_tag", "package_url", "package_sha256",
        "public_release_url", "gui_binary_sha256", "backend_binary_sha256",
    }
    if not isinstance(value, dict) or set(value) != fields:
        fail("production-identity file must contain exactly the manifest production_identity fields")
    if (
        not isinstance(value["source_commit"], str)
        or not isinstance(value["source_tree"], str)
        or not GIT_OBJECT_RE.fullmatch(value["source_commit"])
        or not GIT_OBJECT_RE.fullmatch(value["source_tree"])
    ):
        fail("production identity commit and tree must be lowercase full Git object IDs")
    tag = value["release_tag"]
    if not isinstance(tag, str) or not TAG_RE.fullmatch(tag):
        fail("production identity release tag is not a canonical fork alpha tag")
    for field in ("package_sha256", "gui_binary_sha256", "backend_binary_sha256"):
        if not isinstance(value[field], str) or not SHA256_RE.fullmatch(value[field]):
            fail(f"production identity {field} must be a lowercase SHA-256 digest")
    version, alpha = re.fullmatch(r"fork-4eh5xitv6787h645ebv-linux-v(.+)-alpha\.([1-9]\d*)", tag).groups()
    package_name = f"SMAPI-{version}-unofficial.4eh5xitv6787h645ebv.linux.alpha.{alpha}-linux-x64-installer.zip"
    expected_package = f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/{tag}/{package_name}"
    if validate_https(value["package_url"], "package_url") != expected_package:
        fail("production identity package_url does not match its release tag")
    expected_release = f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/{tag}"
    if validate_https(value["public_release_url"], "public_release_url") != expected_release:
        fail("production identity public_release_url does not match its release tag")
    return value


def require_private_directory(path: Path) -> Path:
    absolute = path.absolute()
    try:
        info = absolute.lstat()
        resolved = absolute.resolve(strict=True)
    except OSError as exc:
        fail(f"can't inspect staging directory: {exc.strerror or exc}")
    if absolute.is_symlink() or not stat.S_ISDIR(info.st_mode) or resolved != absolute:
        fail("staging directory must be one existing normalized non-symlink directory")
    repository_root = REPOSITORY_ROOT.resolve(strict=True)
    if resolved == repository_root or repository_root in resolved.parents:
        fail("private capture staging must be outside the repository")
    if info.st_uid != os.geteuid() or stat.S_IMODE(info.st_mode) & 0o077:
        fail("staging directory must be current-user-owned with no group or other permissions")
    return absolute


def require_file_outside_repository(path: Path, description: str) -> Path:
    absolute = path.absolute()
    try:
        resolved = absolute.resolve(strict=True)
    except OSError as exc:
        fail(f"can't inspect {description}: {exc.strerror or exc}")
    if resolved != absolute:
        fail(f"{description} must use one normalized non-symlink path")
    repository_root = REPOSITORY_ROOT.resolve(strict=True)
    if resolved == repository_root or repository_root in resolved.parents:
        fail(f"{description} must be outside the repository")
    return absolute


def hash_executable_descriptor(descriptor: int) -> str:
    initial = os.fstat(descriptor)
    if (
        not stat.S_ISREG(initial.st_mode)
        or initial.st_size <= 0
        or initial.st_size > MAX_EXECUTABLE_BYTES
    ):
        fail("the selected X11 client process executable is not a bounded regular file")
    digest = hashlib.sha256()
    remaining = initial.st_size
    while remaining:
        block = os.read(descriptor, min(remaining, 1024 * 1024))
        if not block:
            fail("the selected X11 client process executable ended while hashing")
        digest.update(block)
        remaining -= len(block)
    if os.read(descriptor, 1):
        fail("the selected X11 client process executable grew while hashing")
    final = os.fstat(descriptor)
    fields = ("st_dev", "st_ino", "st_mode", "st_size", "st_mtime_ns", "st_ctime_ns")
    if any(getattr(initial, field) != getattr(final, field) for field in fields):
        fail("the selected X11 client process executable changed while hashing")
    return digest.hexdigest()


def hash_process_executable(process_id: int) -> str:
    try:
        process = os.stat(f"/proc/{process_id}", follow_symlinks=False)
    except OSError:
        fail("the selected X11 client process could not be inspected")
    if process.st_uid != os.geteuid():
        fail("the selected X11 client process is not owned by the current user")
    try:
        descriptor = os.open(f"/proc/{process_id}/exe", os.O_RDONLY | os.O_CLOEXEC)
    except OSError:
        fail("the selected X11 client process executable could not be retained")
    try:
        return hash_executable_descriptor(descriptor)
    finally:
        os.close(descriptor)


def read_proc_file(process_id: int, name: str, maximum: int) -> bytes:
    try:
        descriptor = os.open(f"/proc/{process_id}/{name}", os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
    except OSError:
        fail("the selected X11 client process identity could not be inspected")
    try:
        chunks: list[bytes] = []
        total = 0
        while True:
            chunk = os.read(descriptor, min(65536, maximum + 1 - total))
            if not chunk:
                return b"".join(chunks)
            chunks.append(chunk)
            total += len(chunk)
            if total > maximum:
                fail("the selected X11 client process identity exceeds its byte bound")
    finally:
        os.close(descriptor)


def read_process_start_time(process_id: int) -> int:
    try:
        value = read_proc_file(process_id, "stat", 16384).decode("ascii")
    except UnicodeDecodeError:
        fail("the selected X11 client process stat identity is malformed")
    close = value.rfind(") ")
    if close < 2:
        fail("the selected X11 client process stat identity is malformed")
    fields = value[close + 2:].split()
    if len(fields) <= 19 or not fields[19].isdigit():
        fail("the selected X11 client process start time is malformed")
    start_time = int(fields[19])
    if start_time <= 0:
        fail("the selected X11 client process start time is malformed")
    return start_time


def inspect_process_identity(process_id: int) -> ProcessIdentity:
    if process_id <= 0:
        fail("the expected GUI process ID must be positive")
    try:
        process = os.stat(f"/proc/{process_id}", follow_symlinks=False)
    except OSError:
        fail("the selected X11 client process could not be inspected")
    if process.st_uid != os.geteuid():
        fail("the selected X11 client process is not owned by the current user")
    before_start = read_process_start_time(process_id)
    environment = read_proc_file(process_id, "environ", 1024 * 1024)
    displays = [
        item[len(b"DISPLAY="):]
        for item in environment.split(b"\0")
        if item.startswith(b"DISPLAY=")
    ]
    if len(displays) != 1:
        fail("the selected X11 client process has no unique DISPLAY identity")
    try:
        display = displays[0].decode("utf-8")
    except UnicodeDecodeError:
        fail("the selected X11 client process DISPLAY identity is malformed")
    try:
        descriptor = os.open(f"/proc/{process_id}/exe", os.O_RDONLY | os.O_CLOEXEC)
    except OSError:
        fail("the selected X11 client process executable could not be retained")
    try:
        executable = os.fstat(descriptor)
        executable_hash = hash_executable_descriptor(descriptor)
        retained_after_hash = os.fstat(descriptor)
    finally:
        os.close(descriptor)
    executable_fields = ("st_dev", "st_ino", "st_mode", "st_size", "st_mtime_ns", "st_ctime_ns")
    if any(getattr(executable, field) != getattr(retained_after_hash, field) for field in executable_fields):
        fail("the selected X11 client process executable identity changed while it was inspected")
    try:
        live_executable = os.stat(f"/proc/{process_id}/exe")
        final_process = os.stat(f"/proc/{process_id}", follow_symlinks=False)
    except OSError:
        fail("the selected X11 client process identity changed while it was inspected")
    if (
        live_executable.st_dev != executable.st_dev
        or live_executable.st_ino != executable.st_ino
        or live_executable.st_size != executable.st_size
        or final_process.st_uid != os.geteuid()
    ):
        fail("the selected X11 client process executable identity changed while it was inspected")
    after_start = read_process_start_time(process_id)
    if before_start != after_start:
        fail("the selected X11 client process identity changed while it was inspected")
    return ProcessIdentity(
        process_id=process_id,
        start_time=before_start,
        executable_device=executable.st_dev,
        executable_inode=executable.st_ino,
        executable_size=executable.st_size,
        executable_sha256=executable_hash,
        display=display,
    )


def require_process_identity(expected: ProcessIdentity) -> ProcessIdentity:
    actual = inspect_process_identity(expected.process_id)
    if actual != expected:
        fail("the selected X11 client process identity does not match the admitted GUI process")
    return actual


def parse_window_titles(output: str) -> frozenset[str]:
    titles: set[str] = set()
    for line in output.splitlines():
        match = re.fullmatch(r'(?:_NET_WM_NAME\([^)]*\)|WM_NAME\([^)]*\))\s*=\s*"(.*)"', line)
        if match is not None:
            titles.add(match.group(1))
    return frozenset(titles)


def parse_client_windows(output: str) -> tuple[str, ...]:
    windows: list[str] = []
    saw_list = False
    for line in output.splitlines():
        match = re.fullmatch(
            r"(_NET_CLIENT_LIST_STACKING|_NET_CLIENT_LIST)\([^)]*\)\s*"
            r"(?::\s*(?:window id #\s*)?|=\s*)(.*)",
            line,
        )
        if match is None:
            continue
        saw_list = True
        value = match.group(2).strip()
        if not value:
            continue
        for item in (part.strip() for part in value.split(",")):
            if not re.fullmatch(r"0x[0-9a-fA-F]+", item):
                fail("the X11 root client list is malformed")
            normalized = f"0x{int(item, 16):x}"
            if normalized == "0x0":
                fail("the X11 root client list contains an invalid window ID")
            if normalized not in windows:
                windows.append(normalized)
                if len(windows) > MAX_X11_CLIENTS:
                    fail("the X11 root client list exceeds its window-count bound")
    if not saw_list:
        fail("the X11 root has no bounded EWMH client list")
    return tuple(windows)


def parse_window_pid(output: str) -> int | None:
    values = re.findall(r"^_NET_WM_PID\(CARDINAL\)\s*=\s*([1-9]\d*)\s*$", output, re.MULTILINE)
    if not values:
        return None
    if len(values) != 1:
        fail("the selected X11 client has conflicting process properties")
    return int(values[0])


def parse_window_geometry(output: str) -> tuple[int, int, int, int]:
    values: list[int] = []
    for label in ("Absolute upper-left X", "Absolute upper-left Y", "Width", "Height"):
        matches = re.findall(rf"^\s*{re.escape(label)}:\s*(-?\d+)\s*$", output, re.MULTILINE)
        if len(matches) != 1:
            fail("the selected X11 client geometry is incomplete or ambiguous")
        values.append(int(matches[0]))
    if values[2] <= 0 or values[3] <= 0 or values[2] > MAX_DIMENSION or values[3] > MAX_DIMENSION:
        fail("the selected X11 client geometry is outside the capture bounds")
    return values[0], values[1], values[2], values[3]


def x_environment(expected_display: str) -> dict[str, str]:
    if not DISPLAY_RE.fullmatch(expected_display):
        fail("the expected DISPLAY identity is malformed")
    if os.environ.get("DISPLAY") != expected_display:
        fail("the capture process DISPLAY does not match the admitted GUI DISPLAY identity")
    environment = dict(os.environ)
    environment["DISPLAY"] = expected_display
    return environment


def inspect_window(
    window_id: str,
    expected_title: str,
    *,
    xwininfo: str,
    xprop: str,
    environment: dict[str, str],
) -> WindowIdentity | None:
    info = run_bounded(
        [xwininfo, "-id", window_id], timeout=10, environment=environment
    )
    properties = run_bounded(
        [xprop, "-id", window_id, "_NET_WM_NAME", "WM_NAME", "_NET_WM_PID", "_NET_WM_STATE"],
        timeout=10,
        environment=environment,
    )
    if info.returncode or properties.returncode:
        fail("an enumerated X11 client could not be inspected safely")
    titles = parse_window_titles(properties.stdout)
    if expected_title not in titles:
        return None
    if titles != {expected_title}:
        fail("the selected X11 client has conflicting WM_NAME and _NET_WM_NAME titles")
    if "Map State: IsViewable" not in info.stdout or "_NET_WM_STATE_HIDDEN" in properties.stdout:
        fail("the selected X11 client is not mapped and visible")
    process_id = parse_window_pid(properties.stdout)
    if process_id is None:
        fail("the selected X11 client has no exact _NET_WM_PID")
    return WindowIdentity(window_id, process_id, expected_title, parse_window_geometry(info.stdout))


def list_client_windows(xprop: str, environment: dict[str, str]) -> tuple[str, ...]:
    result = run_bounded(
        [xprop, "-root", "_NET_CLIENT_LIST_STACKING", "_NET_CLIENT_LIST"],
        timeout=10,
        environment=environment,
    )
    if result.returncode:
        fail("the X11 root client list could not be inspected safely")
    return parse_client_windows(result.stdout)


def discover_exact_window(
    expected_title: str,
    expected_process: ProcessIdentity,
    *,
    xwininfo: str,
    xprop: str,
    environment: dict[str, str],
) -> WindowIdentity:
    require_process_identity(expected_process)
    candidates: list[WindowIdentity] = []
    for window_id in list_client_windows(xprop, environment):
        window = inspect_window(
            window_id,
            expected_title,
            xwininfo=xwininfo,
            xprop=xprop,
            environment=environment,
        )
        if window is not None:
            if window.process_id != expected_process.process_id:
                fail("the exact-title X11 client does not belong to the admitted GUI process")
            candidates.append(window)
    if len(candidates) != 1:
        fail("expected exactly one mapped visible exact-title X11 client")
    return candidates[0]


def revalidate_exact_window(
    expected: WindowIdentity,
    expected_process: ProcessIdentity,
    *,
    xwininfo: str,
    xprop: str,
    environment: dict[str, str],
) -> None:
    if expected.window_id not in list_client_windows(xprop, environment):
        fail("the selected X11 client left the root client list during capture")
    actual = inspect_window(
        expected.window_id,
        expected.title,
        xwininfo=xwininfo,
        xprop=xprop,
        environment=environment,
    )
    if actual != expected:
        fail("the selected X11 client identity or geometry changed during capture")
    require_process_identity(expected_process)


def capture_discovered_window(
    expected_title: str,
    expected_process: ProcessIdentity,
) -> tuple[bytes, str, str, WindowIdentity]:
    xwininfo = shutil.which("xwininfo")
    xprop = shutil.which("xprop")
    importer = shutil.which("import")
    if not xwininfo or not xprop or not importer:
        fail("window capture requires xwininfo, xprop, and ImageMagick import")
    environment = x_environment(expected_process.display)
    selected = discover_exact_window(
        expected_title,
        expected_process,
        xwininfo=xwininfo,
        xprop=xprop,
        environment=environment,
    )
    revalidate_exact_window(
        selected,
        expected_process,
        xwininfo=xwininfo,
        xprop=xprop,
        environment=environment,
    )
    descriptor, temporary_name = tempfile.mkstemp(prefix="smapi-screenshot-capture.", suffix=".png")
    os.close(descriptor)
    try:
        result = run_bounded(
            [importer, "-window", selected.window_id, f"png32:{temporary_name}"],
            timeout=30,
            text=False,
            environment=environment,
        )
        if result.returncode:
            fail("ImageMagick could not capture the exact selected X11 client window")
        revalidate_exact_window(
            selected,
            expected_process,
            xwininfo=xwininfo,
            xprop=xprop,
            environment=environment,
        )
        data = read_regular_file(Path(temporary_name), "captured PNG")
        version = run_bounded([importer, "-version"], timeout=10, environment=environment)
        tool = (
            version.stdout.splitlines()[0].strip()
            if version.returncode == 0 and version.stdout
            else "ImageMagick import"
        )
        command = (
            f"import -window {selected.window_id} png32:<private-temporary-file>; "
            "canonical metadata normalization"
        )
        return data, tool[:160], command, selected
    finally:
        try:
            os.unlink(temporary_name)
        except FileNotFoundError:
            pass


def capture_window(
    window_id: str,
    expected_title: str,
    expected_process_id: int,
    expected_gui_sha256: str,
) -> tuple[bytes, str, str]:
    if not WINDOW_ID_RE.fullmatch(window_id):
        fail("window ID must be a positive decimal or hexadecimal X11 window ID")
    xwininfo = shutil.which("xwininfo")
    xprop = shutil.which("xprop")
    importer = shutil.which("import")
    if not xwininfo or not xprop or not importer:
        fail("window capture requires xwininfo, xprop, and ImageMagick import")
    info = run_bounded([xwininfo, "-id", window_id], timeout=10)
    title = run_bounded(
        [xprop, "-id", window_id, "_NET_WM_NAME", "WM_NAME", "_NET_WM_PID"], timeout=10
    )
    if info.returncode or "Map State: IsViewable" not in info.stdout:
        fail("the requested X11 window is not an exact visible client window")
    if title.returncode or expected_title not in parse_window_titles(title.stdout):
        fail("the requested X11 window title does not match the expected application title")
    pid_match = re.search(r"_NET_WM_PID\(CARDINAL\)\s*=\s*([1-9]\d*)", title.stdout)
    if pid_match is None or int(pid_match.group(1)) != expected_process_id:
        fail("the requested X11 window does not belong to the expected process ID")
    if hash_process_executable(expected_process_id) != expected_gui_sha256:
        fail("the requested X11 window process does not match the reviewed GUI binary SHA-256")
    descriptor, temporary_name = tempfile.mkstemp(prefix="smapi-screenshot-capture.", suffix=".png")
    os.close(descriptor)
    try:
        result = run_bounded(
            [importer, "-window", window_id, f"png32:{temporary_name}"],
            timeout=30,
            text=False,
        )
        if result.returncode:
            fail("ImageMagick could not capture the exact selected X11 client window")
        repeated = run_bounded(
            [xprop, "-id", window_id, "_NET_WM_NAME", "WM_NAME", "_NET_WM_PID"],
            timeout=10,
        )
        repeated_match = re.search(r"_NET_WM_PID\(CARDINAL\)\s*=\s*([1-9]\d*)", repeated.stdout)
        if (
            repeated.returncode
            or repeated_match is None
            or int(repeated_match.group(1)) != expected_process_id
            or expected_title not in parse_window_titles(repeated.stdout)
        ):
            fail("the selected X11 client identity changed during capture")
        if hash_process_executable(expected_process_id) != expected_gui_sha256:
            fail("the reviewed GUI process identity changed during capture")
        data = read_regular_file(Path(temporary_name), "captured PNG")
        version = run_bounded([importer, "-version"], timeout=10)
        tool = (
            version.stdout.splitlines()[0].strip()
            if version.returncode == 0 and version.stdout
            else "ImageMagick import"
        )
        command = f"import -window {window_id} png32:<private-temporary-file>; canonical metadata normalization"
        return data, tool[:160], command
    finally:
        try:
            os.unlink(temporary_name)
        except FileNotFoundError:
            pass


def write_new_file(path: Path, data: bytes) -> None:
    created = False
    try:
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW, 0o600)
        created = True
    except OSError as exc:
        fail(f"can't create staged file {path.name}: {exc.strerror or exc}")
    try:
        view = memoryview(data)
        while view:
            written = os.write(descriptor, view)
            if written <= 0:
                fail(f"staged file {path.name} could not be written completely")
            view = view[written:]
        os.fsync(descriptor)
    except Exception:
        if created:
            try:
                os.unlink(path)
            except OSError:
                pass
        raise
    finally:
        os.close(descriptor)


class PrivateArgumentParser(argparse.ArgumentParser):
    """Reject malformed invocations without echoing caller-supplied private values."""

    def error(self, message: str) -> None:
        fail("invalid command-line arguments")


def parse_args() -> argparse.Namespace:
    parser = PrivateArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--input", type=Path, help="Import an existing app-window PNG; its path is never recorded.")
    source.add_argument("--window-id", help="Capture one exact visible X11/XWayland client window.")
    source.add_argument(
        "--discover-window", "--qualification-window",
        action="store_true",
        help="Discover the unique admitted visible X11/XWayland client without accepting an XID.",
    )
    parser.add_argument(
        "--expected-window-title", "--expected-gui-title",
        dest="expected_window_title",
        help="Required exact GUI title for window capture.",
    )
    parser.add_argument(
        "--expected-window-pid", "--expected-gui-pid",
        dest="expected_window_pid", type=int,
        help="Required reviewed GUI process ID for window capture.",
    )
    parser.add_argument(
        "--expected-gui-process-start-time", "--expected-gui-start-time",
        "--expected-window-process-start-time",
        dest="expected_gui_process_start_time", type=int,
    )
    parser.add_argument(
        "--expected-gui-exe-device", "--expected-gui-executable-device",
        "--expected-window-exe-device",
        dest="expected_gui_exe_device", type=int,
    )
    parser.add_argument(
        "--expected-gui-exe-inode", "--expected-gui-executable-inode",
        "--expected-window-exe-inode",
        dest="expected_gui_exe_inode", type=int,
    )
    parser.add_argument(
        "--expected-gui-exe-size", "--expected-gui-executable-size",
        "--expected-window-exe-size",
        dest="expected_gui_exe_size", type=int,
    )
    parser.add_argument(
        "--expected-gui-exe-sha256", "--expected-gui-executable-sha256",
        "--expected-window-exe-sha256",
        dest="expected_gui_exe_sha256",
    )
    parser.add_argument(
        "--expected-display", "--expected-display-identity",
        dest="expected_display",
        help="Exact DISPLAY identity for qualification discovery.",
    )
    parser.add_argument("--capture-tool", help="Sanitized capture-tool description required with --input.")
    parser.add_argument("--capture-command", help="Sanitized path-free capture command required with --input.")
    parser.add_argument("--stage-directory", type=Path, required=True)
    parser.add_argument("--filename", required=True)
    parser.add_argument("--evidence-id", choices=EXPECTED_IDS, required=True)
    parser.add_argument("--evidence-class", choices=("real_qualification", "controlled_fixture"), required=True)
    parser.add_argument("--fault", choices=("permission", "read-only", "disk-full", "cross-device"))
    parser.add_argument("--production-identity", type=Path, required=True)
    parser.add_argument("--private-strings-file", type=Path, required=True)
    parser.add_argument("--fixture-or-injection", required=True)
    parser.add_argument("--operation", required=True)
    parser.add_argument("--durable-before", required=True)
    parser.add_argument("--durable-after", required=True)
    parser.add_argument("--qualification-reference", required=True)
    parser.add_argument("--distribution", required=True)
    parser.add_argument("--architecture", required=True)
    parser.add_argument("--desktop-environment", required=True)
    parser.add_argument("--session-type", choices=("x11", "wayland"), required=True)
    parser.add_argument("--display-backend", choices=("x11", "xwayland"), required=True)
    parser.add_argument("--display-scale-percent", type=int, required=True)
    parser.add_argument("--theme", choices=("light", "dark", "high_contrast"), required=True)
    parser.add_argument("--resolution", required=True)
    parser.add_argument("--avalonia", required=True)
    parser.add_argument("--dotnet-sdk", required=True)
    parser.add_argument("--dotnet-runtime", required=True)
    return parser.parse_args()


def main() -> int:
    try:
        args = parse_args()
        if not FILENAME_RE.fullmatch(args.filename):
            fail("filename must be a safe lowercase PNG basename")
        if args.evidence_id in REAL_QUALIFICATION_IDS and args.evidence_class != "real_qualification":
            fail(f"{args.evidence_id} requires real_qualification evidence")
        if args.evidence_id == "E2" and args.fault is None:
            fail("E2 staging requires one exact --fault value")
        if args.evidence_id != "E2" and args.fault is not None:
            fail("--fault is permitted only for E2 source staging")
        if args.display_backend == "xwayland" and args.session_type != "wayland":
            fail("XWayland display backend requires a Wayland session")
        if not 50 <= args.display_scale_percent <= 400:
            fail("display scale must be from 50 through 400 percent")
        if not RESOLUTION_RE.fullmatch(args.resolution):
            fail("resolution must use WIDTHxHEIGHT decimal syntax")
        stage = require_private_directory(args.stage_directory)
        private_strings_path = require_file_outside_repository(
            args.private_strings_file,
            "private-string file",
        )
        private_strings = load_private_strings(private_strings_path)
        identity = load_identity(args.production_identity)
        context_text = {
            "expected_window_title": args.expected_window_title or "not-applicable",
            "capture_tool": args.capture_tool or "derived-from-window-capture",
            "capture_command": args.capture_command or "derived-from-window-capture",
            "fixture_or_injection": require_text(args.fixture_or_injection, "fixture or injection", 1200),
            "operation": require_text(args.operation, "operation", 240),
            "durable_before": require_text(args.durable_before, "durable state before", 1200),
            "durable_after": require_text(args.durable_after, "durable state after", 1200),
            "qualification_reference": require_text(args.qualification_reference, "qualification reference", 1200),
            "distribution": require_text(args.distribution, "distribution", 160),
            "architecture": require_text(args.architecture, "architecture", 160),
            "desktop_environment": require_text(args.desktop_environment, "desktop environment", 160),
            "avalonia": require_text(args.avalonia, "Avalonia version", 160),
            "dotnet_sdk": require_text(args.dotnet_sdk, ".NET SDK version", 160),
            "dotnet_runtime": require_text(args.dotnet_runtime, ".NET runtime version", 160),
        }
        require_text(context_text["expected_window_title"], "expected window title", 160)
        require_text(context_text["capture_tool"], "capture tool", 160)
        require_text(context_text["capture_command"], "capture command", 1200)
        scan_safe_text(identity, private_strings)
        scan_safe_text(context_text, private_strings)
        qualification_values = (
            args.expected_gui_process_start_time,
            args.expected_gui_exe_device,
            args.expected_gui_exe_inode,
            args.expected_gui_exe_size,
            args.expected_gui_exe_sha256,
            args.expected_display,
        )
        if args.discover_window:
            if (
                not args.expected_window_title
                or args.expected_window_pid is None
                or any(value is None for value in qualification_values)
                or args.capture_tool
                or args.capture_command
            ):
                fail(
                    "--discover-window requires the exact title, GUI PID/start time/executable "
                    "device/inode/size/SHA-256, and DISPLAY, and derives its capture command"
                )
            if (
                args.expected_window_pid <= 0
                or args.expected_gui_process_start_time <= 0
                or args.expected_gui_exe_device < 0
                or args.expected_gui_exe_inode <= 0
                or args.expected_gui_exe_size <= 0
                or args.expected_gui_exe_size > MAX_EXECUTABLE_BYTES
                or not SHA256_RE.fullmatch(args.expected_gui_exe_sha256)
            ):
                fail("the admitted GUI process identity is malformed or outside its bounds")
            if args.expected_gui_exe_sha256 != identity["gui_binary_sha256"]:
                fail("the admitted GUI executable SHA-256 does not match the reviewed production identity")
            expected_process = ProcessIdentity(
                process_id=args.expected_window_pid,
                start_time=args.expected_gui_process_start_time,
                executable_device=args.expected_gui_exe_device,
                executable_inode=args.expected_gui_exe_inode,
                executable_size=args.expected_gui_exe_size,
                executable_sha256=args.expected_gui_exe_sha256,
                display=args.expected_display,
            )
            input_data, capture_tool, capture_command, selected = capture_discovered_window(
                args.expected_window_title,
                expected_process,
            )
            input_mode = "discovered_exact_x11_client_window"
            source_window = {
                "window_id": selected.window_id,
                "process_id": expected_process.process_id,
                "process_start_time": expected_process.start_time,
                "expected_title": args.expected_window_title,
                "display": expected_process.display,
                "executable": {
                    "device": expected_process.executable_device,
                    "inode": expected_process.executable_inode,
                    "size": expected_process.executable_size,
                    "sha256_verified": True,
                },
                "unique_mapped_visible_client_verified": True,
            }
        elif args.window_id:
            if (
                not args.expected_window_title or not args.expected_window_pid
                or args.capture_tool or args.capture_command
                or any(value is not None for value in qualification_values)
            ):
                fail(
                    "--window-id requires --expected-window-title and --expected-window-pid, "
                    "and derives its own capture tool and command"
                )
            input_data, capture_tool, capture_command = capture_window(
                args.window_id,
                args.expected_window_title,
                args.expected_window_pid,
                identity["gui_binary_sha256"],
            )
            input_mode = "exact_x11_client_window"
            source_window = {
                "window_id": args.window_id,
                "process_id": args.expected_window_pid,
                "expected_title": args.expected_window_title,
                "reviewed_gui_sha256_verified": True,
            }
        else:
            if (
                args.expected_window_title or args.expected_window_pid
                or any(value is not None for value in qualification_values)
                or not args.capture_tool or not args.capture_command
            ):
                fail("--input requires --capture-tool and --capture-command, without window identity arguments")
            input_path = require_file_outside_repository(args.input, "imported PNG")
            input_data = read_regular_file(input_path, "imported PNG", private=True)
            capture_tool = args.capture_tool
            capture_command = args.capture_command
            input_mode = "imported_app_window_png"
            source_window = None
        width, height, color_type, pixels, input_chunks = decode_png(input_data, "source PNG")
        canonical = encode_canonical_png(width, height, color_type, pixels)
        out_width, out_height, out_color_type, out_pixels, output_chunks = decode_png(canonical, "normalized PNG")
        if (out_width, out_height, out_color_type, out_pixels) != (width, height, color_type, pixels):
            fail("metadata normalization changed decoded application pixels")
        if output_chunks != ("IHDR", "IDAT", "IEND"):
            fail("normalized PNG retained a disallowed or unnecessary chunk")
        png_sha256 = hashlib.sha256(canonical).hexdigest()
        pixel_sha256 = hashlib.sha256(pixels).hexdigest()
        environment = {
            "distribution": args.distribution,
            "architecture": args.architecture,
            "desktop_environment": args.desktop_environment,
            "session_type": args.session_type,
            "display_backend": args.display_backend,
            "display_scale_percent": args.display_scale_percent,
            "theme": args.theme,
            "resolution": args.resolution,
        }
        record = {
            "staging_schema_version": 1,
            "status": "staged_pending_original_resolution_privacy_review",
            "id": args.evidence_id,
            "filename": args.filename,
            "evidence_class": args.evidence_class,
            "production_identity": identity,
            "fixture_or_injection": args.fixture_or_injection,
            "operation": args.operation,
            "durable_state": {"before": args.durable_before, "after": args.durable_after},
            "environment": environment,
            "runtime": {
                "avalonia": args.avalonia,
                "dotnet_sdk": args.dotnet_sdk,
                "dotnet_runtime": args.dotnet_runtime,
            },
            "capture": {
                "timestamp": datetime.now().astimezone().isoformat(timespec="seconds"),
                "tool": capture_tool,
                "command": capture_command,
                "input_mode": input_mode,
                "source_window": source_window,
                "width": width,
                "height": height,
                "sha256": png_sha256,
                "decoded_pixel_sha256": pixel_sha256,
            },
            "normalization": {
                "application_pixels_altered": False,
                "input_chunks": list(input_chunks),
                "output_chunks": list(output_chunks),
                "statement": (
                    "Incidental PNG metadata was removed; decoded RGB/RGBA application pixels "
                    "are byte-identical."
                ),
            },
            "privacy_review": {
                "status": "pending",
                "requirement": "Inspect the staged PNG at original resolution before manifest promotion.",
            },
            "qualification_reference": args.qualification_reference,
        }
        if args.fault is not None:
            record["fault"] = args.fault
        scan_safe_text(record, private_strings)
        png_path = stage / args.filename
        record_path = stage / f"{args.filename[:-4]}.capture.json"
        if png_path.exists() or record_path.exists():
            fail("staging never overwrites an existing PNG or provenance sidecar")
        record_bytes = (json.dumps(record, indent=2, sort_keys=True) + "\n").encode("utf-8")
        write_new_file(png_path, canonical)
        try:
            write_new_file(record_path, record_bytes)
        except Exception:
            png_path.unlink(missing_ok=True)
            raise
        print(json.dumps({
            "filename": png_path.name,
            "record": record_path.name,
            "sha256": png_sha256,
            "pixel_sha256": pixel_sha256,
            "width": width,
            "height": height,
        }, sort_keys=True))
        return 0
    except (StagingError, OSError) as exc:
        print(f"Linux GUI screenshot staging failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
