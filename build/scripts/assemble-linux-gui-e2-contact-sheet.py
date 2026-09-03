#!/usr/bin/env python3
"""Deterministically assemble the four reviewed E2 source PNGs into one private contact sheet."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import struct
import sys
import zlib


ROOT = Path(__file__).resolve().parents[2]
STAGER = ROOT / "build/scripts/stage-linux-gui-screenshot.py"
ORDER = ("permission", "read-only", "disk-full", "cross-device")
MAX_FILE_BYTES = 64 * 1024 * 1024
MAX_DIMENSION = 8192
GUTTER = 16
SAFE_NAME = re.compile(r"^[a-z0-9][a-z0-9._-]{7,127}\.png$")
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


class ContactSheetError(Exception):
    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


def reject(code: str) -> None:
    raise ContactSheetError(code)


def load_png_codec():
    spec = importlib.util.spec_from_file_location("smapi_screenshot_stager", STAGER)
    if spec is None or spec.loader is None:
        reject("tool")
    module = importlib.util.module_from_spec(spec)
    try:
        spec.loader.exec_module(module)
    except BaseException:
        reject("tool")
    return module


def private_directory(path: Path) -> Path:
    if not path.is_absolute() or str(path) != str(path.absolute()) or ".." in path.parts:
        reject("directory")
    try:
        metadata = path.lstat()
        resolved = path.resolve(strict=True)
        repository = ROOT.resolve(strict=True)
    except OSError:
        reject("directory")
    if (
        resolved != path
        or path.is_symlink()
        or not stat.S_ISDIR(metadata.st_mode)
        or metadata.st_uid != os.geteuid()
        or stat.S_IMODE(metadata.st_mode) != 0o700
        or resolved == repository
        or repository in resolved.parents
    ):
        reject("directory")
    return resolved


def read_private_png(path: Path) -> bytes:
    if not path.is_absolute() or str(path) != str(path.absolute()) or ".." in path.parts:
        reject("source")
    try:
        metadata = path.lstat()
        resolved = path.resolve(strict=True)
        repository = ROOT.resolve(strict=True)
        if (
            resolved != path
            or path.is_symlink()
            or not stat.S_ISREG(metadata.st_mode)
            or metadata.st_uid != os.geteuid()
            or metadata.st_nlink != 1
            or stat.S_IMODE(metadata.st_mode) != 0o600
            or metadata.st_size < len(PNG_SIGNATURE)
            or metadata.st_size > MAX_FILE_BYTES
            or resolved == repository
            or repository in resolved.parents
        ):
            reject("source")
        descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0))
        try:
            opened = os.fstat(descriptor)
            if (opened.st_dev, opened.st_ino) != (metadata.st_dev, metadata.st_ino):
                reject("source")
            chunks: list[bytes] = []
            total = 0
            while True:
                block = os.read(descriptor, 1024 * 1024)
                if not block:
                    break
                total += len(block)
                if total > MAX_FILE_BYTES:
                    reject("source")
                chunks.append(block)
            final = os.fstat(descriptor)
        finally:
            os.close(descriptor)
        if (
            (final.st_dev, final.st_ino, final.st_size, final.st_mtime_ns)
            != (opened.st_dev, opened.st_ino, opened.st_size, opened.st_mtime_ns)
        ):
            reject("source")
        data = b"".join(chunks)
        if not data.startswith(PNG_SIGNATURE):
            reject("source")
        return data
    except ContactSheetError:
        raise
    except OSError:
        reject("source")


def rgba(width: int, height: int, color_type: int, pixels: bytes) -> bytes:
    if width < 1 or height < 1 or width > MAX_DIMENSION or height > MAX_DIMENSION:
        reject("source")
    if color_type == 6:
        return pixels
    if color_type != 2:
        reject("source")
    result = bytearray(width * height * 4)
    for source in range(0, len(pixels), 3):
        destination = source // 3 * 4
        result[destination:destination + 4] = pixels[source:source + 3] + b"\xff"
    return bytes(result)


def chunk(kind: bytes, payload: bytes) -> bytes:
    checksum = zlib.crc32(payload, zlib.crc32(kind)) & 0xFFFFFFFF
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", checksum)


def encode(width: int, height: int, pixels: bytes) -> bytes:
    row_bytes = width * 4
    scanlines = b"".join(b"\0" + pixels[row * row_bytes:(row + 1) * row_bytes] for row in range(height))
    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    result = PNG_SIGNATURE + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(scanlines, 9)) + chunk(b"IEND", b"")
    if len(result) > MAX_FILE_BYTES:
        reject("output")
    return result


def write_new(path: Path, data: bytes) -> None:
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(path, flags, 0o600)
        try:
            os.fchmod(descriptor, 0o600)
            offset = 0
            while offset < len(data):
                offset += os.write(descriptor, data[offset:])
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    except FileExistsError:
        reject("output-exists")
    except OSError:
        reject("output")


def assemble(sources: dict[str, Path], output_directory: Path, filename: str) -> tuple[bytes, dict[str, object]]:
    if tuple(sources) != ORDER or not SAFE_NAME.fullmatch(filename):
        reject("usage")
    output_directory = private_directory(output_directory)
    codec = load_png_codec()
    decoded: list[tuple[str, Path, bytes, int, int, bytes]] = []
    for fault in ORDER:
        path = sources[fault]
        data = read_private_png(path)
        try:
            width, height, color_type, pixels, _chunks = codec.decode_png(data, f"{fault} source")
        except BaseException:
            reject("source")
        decoded.append((fault, path, data, width, height, rgba(width, height, color_type, pixels)))
    cell_width = max(item[3] for item in decoded)
    cell_height = max(item[4] for item in decoded)
    width = GUTTER * 3 + cell_width * 2
    height = GUTTER * 3 + cell_height * 2
    if width > MAX_DIMENSION or height > MAX_DIMENSION:
        reject("output")
    canvas = bytearray(bytes((32, 32, 32, 255)) * width * height)
    for index, (_fault, _path, _data, source_width, source_height, pixels) in enumerate(decoded):
        column = index % 2
        row = index // 2
        left = GUTTER + column * (cell_width + GUTTER) + (cell_width - source_width) // 2
        top = GUTTER + row * (cell_height + GUTTER) + (cell_height - source_height) // 2
        for y in range(source_height):
            source_start = y * source_width * 4
            target_start = ((top + y) * width + left) * 4
            canvas[target_start:target_start + source_width * 4] = pixels[source_start:source_start + source_width * 4]
    result = encode(width, height, bytes(canvas))
    record = {
        "schema_version": 1,
        "status": "private_contact_sheet_pending_privacy_review",
        "layout": {"columns": 2, "rows": 2, "gutter_pixels": GUTTER, "order": list(ORDER)},
        "sources": [
            {
                "fault": fault,
                "filename": path.name,
                "width": source_width,
                "height": source_height,
                "png_sha256": hashlib.sha256(data).hexdigest(),
                "decoded_rgba_sha256": hashlib.sha256(pixels).hexdigest(),
            }
            for fault, path, data, source_width, source_height, pixels in decoded
        ],
        "output": {
            "filename": filename,
            "width": width,
            "height": height,
            "png_sha256": hashlib.sha256(result).hexdigest(),
            "decoded_rgba_sha256": hashlib.sha256(canvas).hexdigest(),
        },
        "editing": "The four unchanged reviewed sources were centered in fixed cells with neutral gutters; no source pixels were altered.",
    }
    output = output_directory / filename
    sidecar = output_directory / f"{filename[:-4]}.sources.json"
    if output.exists() or sidecar.exists():
        reject("output-exists")
    write_new(output, result)
    try:
        write_new(sidecar, (json.dumps(record, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii"))
    except BaseException:
        try:
            output.unlink()
        except OSError:
            pass
        raise
    return result, record


def parse(arguments: list[str]) -> tuple[dict[str, Path], Path, str]:
    parser = argparse.ArgumentParser(description=__doc__)
    for fault in ORDER:
        parser.add_argument(f"--{fault}", type=Path, required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    parser.add_argument("--filename", default="e2-filesystem-failures.png")
    values = parser.parse_args(arguments)
    return {fault: getattr(values, fault.replace("-", "_")) for fault in ORDER}, values.output_directory, values.filename


def emit(value: dict[str, object]) -> None:
    os.write(sys.stdout.fileno(), (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii"))


def main(arguments: list[str]) -> int:
    try:
        sources, directory, filename = parse(arguments)
        assemble(sources, directory, filename)
        emit({"ok": True, "schemaVersion": 1, "sourceCount": 4, "status": "assembled-private"})
        return 0
    except ContactSheetError as error:
        emit({"code": error.code, "ok": False, "schemaVersion": 1, "status": "rejected"})
        return 2
    except KeyboardInterrupt:
        emit({"code": "interrupted", "ok": False, "schemaVersion": 1, "status": "rejected"})
        return 130
    except BaseException:
        emit({"code": "internal-error", "ok": False, "schemaVersion": 1, "status": "rejected"})
        return 70


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
