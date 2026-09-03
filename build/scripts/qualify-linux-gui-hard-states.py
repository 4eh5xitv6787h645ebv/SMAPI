#!/usr/bin/env python3
"""Externally qualify one packaged Linux GUI hard state inside an admitted disposable root."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import fcntl
import hashlib
import hmac
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import re
import resource
import shutil
import signal
import socket
import stat
import struct
import subprocess
import sys
import time
from types import ModuleType
from typing import Any, NoReturn
import zipfile
import zlib


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
VALIDATOR_PATH = REPOSITORY_ROOT / "build/scripts/validate-linux-gui-hard-state-inputs.py"
BARRIER_SOURCE = REPOSITORY_ROOT / "build/scripts/linux-gui-hard-state-barrier.c"
OPERATOR_HELPER = REPOSITORY_ROOT / "build/scripts/drive-linux-gui-hard-states-atspi.py"
CONTROLLER_HELPER = REPOSITORY_ROOT / "build/scripts/arm-linux-gui-hard-state-boundary.py"
STAGER_HELPER = REPOSITORY_ROOT / "build/scripts/stage-linux-gui-screenshot.py"
CAPTURE_MODEL_PATH = REPOSITORY_ROOT / "build/scripts/linux_gui_hard_state_capture_contract.py"
CLASSIFIER_PATH = REPOSITORY_ROOT / "build/scripts/classify-linux-gui-hard-state.py"
ENVIRONMENT_VERIFIER_PATH = REPOSITORY_ROOT / "build/scripts/verify-linux-gui-capture-environment.py"
SCHEMA_VERSION = 2
BARRIER_SCENARIOS = frozenset({"C2", "C3", "E5", "E6"})
E2_SCENARIOS = frozenset({"E2-permission", "E2-read-only", "E2-disk-full", "E2-cross-device"})
FAILURE_CODES = frozenset({
    "usage", "admission", "identity", "package", "extraction", "fixture", "inventory",
    "boundary", "operator", "barrier", "timeout", "state", "capture", "cleanup", "internal",
})
BASE_LOCAL_ROUTE = (
    "release.local-folder", "release.continue", "game.choose-folder", "game.continue-valid",
    "plan.inspect", "plan.confirm", "execution.run",
)
AT_SPI_ROUTES = {
    "operation-local-run": BASE_LOCAL_ROUTE,
    "operation-local-cancel": BASE_LOCAL_ROUTE + ("execution.cancel",),
    "e2-permission": BASE_LOCAL_ROUTE + ("state.e2-permission",),
    "e2-read-only": BASE_LOCAL_ROUTE + ("state.e2-read-only",),
    "e2-disk-full": BASE_LOCAL_ROUTE + ("state.e2-disk-full",),
    "e2-cross-device": BASE_LOCAL_ROUTE + ("state.e2-cross-device",),
    "c3-terminal": BASE_LOCAL_ROUTE + ("execution.cancel", "state.c2", "terminal.c3"),
    "e5-backend-loss": BASE_LOCAL_ROUTE + ("state.e5",),
    "e6-automatic-recovery": BASE_LOCAL_ROUTE + ("terminal.e6",),
}
OBSERVATION_MILESTONES = frozenset({
    "state.e2-permission", "state.e2-read-only", "state.e2-disk-full", "state.e2-cross-device",
    "state.c2", "terminal.c3", "state.e5", "terminal.e6",
})
OBSERVATION_FACT_KEYS = frozenset({"name", "role", "visible", "enabled", "actionInterface"})
EXPECTED_OBSERVATIONS: dict[str, tuple[tuple[str, frozenset[str], bool], ...]] = {
    "state.e2-permission": (
        ("Install failed before changing files", frozenset({"heading"}), False),
        ("No mutation was reported. Check user permissions for the game folder; do not run as root.", frozenset({"label", "static", "text"}), False),
        ("Durable state: Unchanged", frozenset({"panel", "section"}), False),
        ("Recovery disposition: Not required", frozenset({"panel", "section"}), False),
        ("Next safe action: Inspect a fresh plan", frozenset({"panel", "section"}), False),
    ),
    "state.e2-read-only": (
        ("Install failed before changing files", frozenset({"heading"}), False),
        ("No mutation was reported. Check that the game filesystem is writable by your user; do not run as root.", frozenset({"label", "static", "text"}), False),
        ("Durable state: Unchanged", frozenset({"panel", "section"}), False),
        ("Recovery disposition: Not required", frozenset({"panel", "section"}), False),
        ("Next safe action: Inspect a fresh plan", frozenset({"panel", "section"}), False),
    ),
    "state.e2-disk-full": (
        ("Install failed before changing files", frozenset({"heading"}), False),
        ("No mutation was reported. Free disk space, then start a fresh verified session.", frozenset({"label", "static", "text"}), False),
        ("Durable state: Unchanged", frozenset({"panel", "section"}), False),
        ("Recovery disposition: Not required", frozenset({"panel", "section"}), False),
        ("Next safe action: Inspect a fresh plan", frozenset({"panel", "section"}), False),
    ),
    "state.e2-cross-device": (
        ("Install failed and changes were rolled back", frozenset({"heading"}), False),
        ("The exact terminal reports rollback completed. Keep the game and installer recovery workspace on a supported filesystem boundary.", frozenset({"label", "static", "text"}), False),
        ("Durable state: Rolled back", frozenset({"panel", "section"}), False),
        ("Recovery disposition: Completed", frozenset({"panel", "section"}), False),
        ("Next safe action: Inspect a fresh plan", frozenset({"panel", "section"}), False),
    ),
    "state.c2": (
        ("Cancellation requested — finishing safely", frozenset({"heading"}), False),
        ("Rollback runs without further cancellation once it begins. The result may be unchanged, fully rolled back, committed if the final safe checkpoint already passed, or recovery-required if rollback cannot finish. Keep this window open for the exact durable result.", frozenset({"label", "static", "text"}), False),
        ("Operation cancellation already requested", frozenset({"push button", "button"}), True),
    ),
    "terminal.c3": (
        ("Cancellation completed and changes were rolled back", frozenset({"heading"}), False),
        ("The exact terminal reports a rolled-back durable state.", frozenset({"label", "static", "text"}), False),
        ("Durable state: Rolled back", frozenset({"panel", "section"}), False),
        ("Recovery disposition: Completed", frozenset({"panel", "section"}), False),
        ("Next safe action: Inspect a fresh plan", frozenset({"panel", "section"}), False),
    ),
    "state.e5": (
        ("Installer state could not be confirmed; recovery is required", frozenset({"heading"}), False),
        ("A recovery session could not be prepared here. Close this screen and start a fresh installer session; do not retry the original operation.", frozenset({"label", "static", "text"}), False),
        ("Close installer without starting recovery", frozenset({"push button", "button"}), True),
    ),
    "terminal.e6": (
        ("Recovery completed; inspect again", frozenset({"heading"}), False),
        ("The prior interrupted state was recovered. Start a fresh verified session and inspect the operation again.", frozenset({"label", "static", "text"}), False),
        ("Durable state: Recovery completed", frozenset({"panel", "section"}), False),
        ("Recovery disposition: Completed", frozenset({"panel", "section"}), False),
        ("Next safe action: Inspect a fresh plan", frozenset({"panel", "section"}), False),
    ),
}
PICKER_FIELDS = {"release.local-folder": "release_folder", "game.choose-folder": "game_folder"}
MAX_ARCHIVE_ENTRIES = 20_000
MAX_ARCHIVE_ENTRY_BYTES = 512 * 1024 * 1024
MAX_ARCHIVE_EXPANDED_BYTES = 2 * 1024 * 1024 * 1024
MAX_COMPRESSION_RATIO = 100
MAX_EXECUTABLE_BYTES = 512 * 1024 * 1024
MAX_INVENTORY_ENTRIES = 20_000
MAX_INVENTORY_FILE_BYTES = 512 * 1024 * 1024
MAX_INVENTORY_TOTAL_BYTES = 1024 * 1024 * 1024
MAX_DESCENDANTS = 32
MAX_OPERATOR_LINE = 4096
MAX_OPERATOR_TRANSCRIPT = 1024 * 1024
BARRIER_MARKER_NAME = ".smapi-hard-state-disposable"
BARRIER_MARKER = b"SMAPI Linux GUI hard-state disposable root v1\n"
BOUNDARY_REQUEST_NAME = "hard-state-boundary-request.json"
SAFE_ARCHIVE_ROOT_RE = re.compile(r"^SMAPI [0-9A-Za-z._-]+ Linux installer$")
PID_LINE_RE = re.compile(rb"^SMAPI_HARD_STATE_BARRIER_V1 pid=([1-9][0-9]*) op=([0-9]{1,5})\n$")
REQUIRED_MEMFD_SEALS = (
    fcntl.F_SEAL_WRITE | fcntl.F_SEAL_GROW | fcntl.F_SEAL_SHRINK | fcntl.F_SEAL_SEAL
)


class QualificationError(Exception):
    def __init__(self, code: str):
        super().__init__(code)
        self.code = code if code in FAILURE_CODES else "internal"


def fail(code: str) -> NoReturn:
    raise QualificationError(code)


def emit(value: dict[str, Any]) -> None:
    data = (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")
    view = memoryview(data)
    while view:
        written = os.write(sys.stdout.fileno(), view)
        if written <= 0:
            raise OSError("stdout write failed")
        view = view[written:]


def load_validator() -> ModuleType:
    spec = importlib.util.spec_from_file_location("smapi_hard_state_input_validator", VALIDATOR_PATH)
    if spec is None or spec.loader is None:
        fail("admission")
    module = importlib.util.module_from_spec(spec)
    try:
        spec.loader.exec_module(module)
    except BaseException:
        fail("admission")
    return module


def load_support_module(name: str, path: Path) -> ModuleType:
    existing = sys.modules.get(name)
    if existing is not None:
        return existing
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        fail("identity")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    try:
        spec.loader.exec_module(module)
    except BaseException:
        sys.modules.pop(name, None)
        fail("identity")
    return module


def load_capture_model() -> ModuleType:
    return load_support_module("linux_gui_hard_state_capture_contract", CAPTURE_MODEL_PATH)


def load_classifier() -> ModuleType:
    load_capture_model()
    return load_support_module("smapi_linux_gui_hard_state_classifier", CLASSIFIER_PATH)


def load_environment_verifier() -> ModuleType:
    load_capture_model()
    return load_support_module("smapi_linux_gui_capture_environment", ENVIRONMENT_VERIFIER_PATH)


def read_sealed_bytes(descriptor: int, maximum: int) -> bytes:
    """Read the exact immutable object admitted by the root broker."""
    if descriptor < 3 or descriptor > 1024:
        fail("admission")
    try:
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode) or before.st_nlink != 0
            or not 0 < before.st_size <= maximum
            or fcntl.fcntl(descriptor, fcntl.F_GET_SEALS) != REQUIRED_MEMFD_SEALS
        ):
            fail("admission")
        os.lseek(descriptor, 0, os.SEEK_SET)
        raw = bytearray()
        while len(raw) <= maximum:
            block = os.read(descriptor, min(4096, maximum + 1 - len(raw)))
            if not block:
                break
            raw.extend(block)
        after = os.fstat(descriptor)
        fields = ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
        if (
            len(raw) != before.st_size or len(raw) > maximum
            or any(getattr(before, field) != getattr(after, field) for field in fields)
            or fcntl.fcntl(descriptor, fcntl.F_GET_SEALS) != REQUIRED_MEMFD_SEALS
        ):
            fail("admission")
        return bytes(raw)
    except QualificationError:
        raise
    except (OSError, ValueError):
        fail("admission")


def private_file(path: Path, content: bytes) -> None:
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW, 0o600)
    try:
        view = memoryview(content)
        while view:
            written = os.write(descriptor, view)
            if written <= 0:
                fail("internal")
            view = view[written:]
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def private_directory(path: Path) -> None:
    path.mkdir(mode=0o700)
    os.chmod(path, 0o700, follow_symlinks=False)


def hash_regular(path: Path, maximum: int, require_executable: bool = False) -> tuple[str, os.stat_result]:
    flags = os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW
    try:
        descriptor = os.open(path, flags)
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode)
            or before.st_nlink != 1
            or before.st_uid != os.geteuid()
            or before.st_size <= 0
            or before.st_size > maximum
            or before.st_mode & 0o7000
            or (require_executable and before.st_mode & 0o111 == 0)
        ):
            fail("identity")
        digest = hashlib.sha256()
        remaining = before.st_size
        while remaining:
            chunk = os.read(descriptor, min(remaining, 1024 * 1024))
            if not chunk:
                fail("identity")
            digest.update(chunk)
            remaining -= len(chunk)
        if os.read(descriptor, 1):
            fail("identity")
        after = os.fstat(descriptor)
    except QualificationError:
        raise
    except OSError:
        fail("identity")
    finally:
        try:
            os.close(descriptor)
        except (UnboundLocalError, OSError):
            pass
    fields = ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
    if any(getattr(before, field) != getattr(after, field) for field in fields):
        fail("identity")
    return digest.hexdigest(), after


def hash_trusted_helper(path: Path, maximum: int) -> tuple[str, os.stat_result]:
    descriptor = -1
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
        before = os.fstat(descriptor)
        if (
            path.resolve(strict=True) != path
            or not stat.S_ISREG(before.st_mode) or before.st_nlink != 1
            or before.st_uid not in (0, os.geteuid()) or before.st_mode & 0o022
            or before.st_size <= 0 or before.st_size > maximum
        ):
            fail("identity")
        digest = hashlib.sha256()
        while block := os.read(descriptor, 1024 * 1024):
            digest.update(block)
        after = os.fstat(descriptor)
    except QualificationError:
        raise
    except OSError:
        fail("identity")
    finally:
        if descriptor >= 0:
            os.close(descriptor)
    fields = ("st_dev", "st_ino", "st_mode", "st_uid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
    if any(getattr(before, field) != getattr(after, field) for field in fields):
        fail("identity")
    return digest.hexdigest(), after


def hash_proc_executable(pid: int, maximum: int = MAX_EXECUTABLE_BYTES) -> tuple[str, os.stat_result]:
    """Hash the object currently referenced by Linux's /proc PID executable magic link."""
    descriptor = -1
    try:
        descriptor = os.open(f"/proc/{pid}/exe", os.O_RDONLY | os.O_CLOEXEC)
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode)
            or before.st_size <= 0
            or before.st_size > maximum
            or before.st_mode & 0o111 == 0
            or before.st_mode & 0o7000
        ):
            fail("identity")
        digest = hashlib.sha256()
        remaining = before.st_size
        while remaining:
            chunk = os.read(descriptor, min(remaining, 1024 * 1024))
            if not chunk:
                fail("identity")
            digest.update(chunk)
            remaining -= len(chunk)
        if os.read(descriptor, 1):
            fail("identity")
        after = os.fstat(descriptor)
    except QualificationError:
        raise
    except OSError:
        fail("identity")
    finally:
        if descriptor >= 0:
            os.close(descriptor)
    fields = ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_size", "st_mtime_ns", "st_ctime_ns")
    if any(getattr(before, field) != getattr(after, field) for field in fields):
        fail("identity")
    return digest.hexdigest(), after


def validate_archive_name(name: str, expected_root: str) -> PurePosixPath:
    if not name or "\\" in name or "\x00" in name or name.startswith("/"):
        fail("extraction")
    path = PurePosixPath(name.rstrip("/"))
    if not path.parts or any(part in ("", ".", "..") for part in path.parts) or path.parts[0] != expected_root:
        fail("extraction")
    return path


def secure_extract(
    package: Path,
    destination: Path,
    version: str,
    expected_identity: os.stat_result | None = None,
) -> Path:
    expected_root = f"SMAPI {version} Linux installer"
    if SAFE_ARCHIVE_ROOT_RE.fullmatch(expected_root) is None:
        fail("extraction")
    private_directory(destination)
    seen: set[str] = set()
    insensitive: set[str] = set()
    expanded = 0
    package_descriptor = -1
    try:
        package_descriptor = os.open(package, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
        opened = os.fstat(package_descriptor)
        if expected_identity is not None:
            fields = ("st_dev", "st_ino", "st_mode", "st_uid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
            if any(getattr(opened, field) != getattr(expected_identity, field) for field in fields):
                fail("identity")
        with os.fdopen(os.dup(package_descriptor), "rb", closefd=True) as package_stream, zipfile.ZipFile(package_stream, "r") as archive:
            entries = archive.infolist()
            if not entries or len(entries) > MAX_ARCHIVE_ENTRIES:
                fail("extraction")
            validated: list[tuple[zipfile.ZipInfo, PurePosixPath, bool, int]] = []
            for entry in entries:
                relative = validate_archive_name(entry.filename, expected_root)
                canonical = relative.as_posix()
                folded = canonical.casefold()
                if canonical in seen or folded in insensitive or entry.flag_bits & 1:
                    fail("extraction")
                seen.add(canonical)
                insensitive.add(folded)
                is_directory = entry.is_dir() or entry.filename.endswith("/")
                unix_type = (entry.external_attr >> 16) & 0o170000
                if unix_type not in (0, stat.S_IFDIR if is_directory else stat.S_IFREG):
                    fail("extraction")
                if entry.file_size < 0 or entry.file_size > MAX_ARCHIVE_ENTRY_BYTES or entry.compress_size < 0:
                    fail("extraction")
                expanded += entry.file_size
                if expanded > MAX_ARCHIVE_EXPANDED_BYTES:
                    fail("extraction")
                if entry.file_size > 0 and entry.compress_size == 0:
                    fail("extraction")
                if entry.compress_size > 0 and entry.file_size > entry.compress_size * MAX_COMPRESSION_RATIO:
                    fail("extraction")
                mode = (entry.external_attr >> 16) & 0o777
                validated.append((entry, relative, is_directory, 0o755 if mode & 0o111 else 0o644))

            for entry, relative, is_directory, mode in sorted(validated, key=lambda item: (len(item[1].parts), item[1].as_posix())):
                target = destination.joinpath(*relative.parts)
                if is_directory:
                    target.mkdir(mode=0o700, parents=True, exist_ok=True)
                    if target.is_symlink():
                        fail("extraction")
                    os.chmod(target, 0o700, follow_symlinks=False)
                    continue
                target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                if target.parent.is_symlink():
                    fail("extraction")
                descriptor = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW, 0o600)
                try:
                    total = 0
                    with archive.open(entry, "r") as source:
                        while True:
                            chunk = source.read(min(1024 * 1024, entry.file_size - total + 1))
                            if not chunk:
                                break
                            total += len(chunk)
                            if total > entry.file_size:
                                fail("extraction")
                            view = memoryview(chunk)
                            while view:
                                written = os.write(descriptor, view)
                                if written <= 0:
                                    fail("extraction")
                                view = view[written:]
                    if total != entry.file_size:
                        fail("extraction")
                    os.fchmod(descriptor, mode)
                    os.fsync(descriptor)
                finally:
                    os.close(descriptor)
        final = os.fstat(package_descriptor)
        fields = ("st_dev", "st_ino", "st_mode", "st_uid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
        if any(getattr(opened, field) != getattr(final, field) for field in fields):
            fail("identity")
    except QualificationError:
        raise
    except (OSError, ValueError, zipfile.BadZipFile, RuntimeError):
        fail("extraction")
    finally:
        if package_descriptor >= 0:
            os.close(package_descriptor)
    package_root = destination / expected_root
    try:
        metadata = package_root.lstat()
        if package_root.resolve(strict=True) != package_root or not stat.S_ISDIR(metadata.st_mode):
            fail("extraction")
    except OSError:
        fail("extraction")
    return package_root


def seed_game(marker: Path, expected_size: int, expected_hash: str, output: Path) -> Path:
    game = output / "game"
    if not game.exists():
        private_directory(game)
    try:
        game_status = game.lstat()
        if (
            game.resolve(strict=True) != game
            or not stat.S_ISDIR(game_status.st_mode)
            or game_status.st_uid != os.geteuid()
            or stat.S_IMODE(game_status.st_mode) != 0o700
            or any(game.iterdir())
        ):
            fail("fixture")
    except OSError:
        fail("fixture")
    source_hash, source = hash_regular(marker, 16 * 1024 * 1024)
    if source.st_size != expected_size or source_hash != expected_hash:
        fail("fixture")
    target = game / "Stardew Valley.dll"
    source_fd = os.open(marker, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
    target_fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW, 0o600)
    try:
        opened = os.fstat(source_fd)
        stable_fields = (
            "st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size",
            "st_mtime_ns", "st_ctime_ns",
        )
        if any(getattr(source, field) != getattr(opened, field) for field in stable_fields):
            fail("fixture")
        copied = 0
        digest = hashlib.sha256()
        while copied < source.st_size:
            chunk = os.read(source_fd, min(1024 * 1024, source.st_size - copied))
            if not chunk:
                fail("fixture")
            digest.update(chunk)
            view = memoryview(chunk)
            while view:
                written = os.write(target_fd, view)
                if written <= 0:
                    fail("fixture")
                view = view[written:]
            copied += len(chunk)
        os.fsync(target_fd)
        final = os.fstat(source_fd)
        if any(getattr(opened, field) != getattr(final, field) for field in stable_fields):
            fail("fixture")
    finally:
        os.close(source_fd)
        os.close(target_fd)
    if copied != expected_size or digest.hexdigest() != expected_hash:
        fail("fixture")
    runtime = ".NETCoreApp,Version=v6.0/linux-x64"
    deps = json.dumps({"runtimeTarget": {"name": runtime}, "targets": {runtime: {}}}, sort_keys=True, separators=(",", ":")).encode("ascii") + b"\n"
    private_file(game / "Stardew Valley.deps.json", deps)
    private_file(game / "StardewValley", b"#!/bin/sh\nexit 0\n")
    os.chmod(game / "StardewValley", 0o700, follow_symlinks=False)
    private_file(game / "unrelated-fixture-sentinel.bin", b"unrelated fixture sentinel v1\n")
    private_file(game / BARRIER_MARKER_NAME, BARRIER_MARKER)
    private_directory(game / "smapi-internal")
    return game


def inventory(
    root: Path,
    allow_cross_device: bool = False,
    opaque_paths: frozenset[str] = frozenset(),
) -> tuple[list[dict[str, Any]], str]:
    root_stat = root.lstat()
    if root.resolve(strict=True) != root or not stat.S_ISDIR(root_stat.st_mode):
        fail("inventory")
    values: list[dict[str, Any]] = []
    total_bytes = 0
    stack = [(root, "")]
    while stack:
        directory, prefix = stack.pop()
        try:
            entries = sorted(os.scandir(directory), key=lambda item: item.name, reverse=True)
        except OSError:
            fail("inventory")
        for entry in entries:
            relative = f"{prefix}/{entry.name}" if prefix else entry.name
            parts = PurePosixPath(relative).parts
            if len(values) >= MAX_INVENTORY_ENTRIES or not parts or any(part in ("", ".", "..") for part in parts):
                fail("inventory")
            metadata = entry.stat(follow_symlinks=False)
            if metadata.st_dev != root_stat.st_dev and not (allow_cross_device and (relative == "smapi-internal" or relative.startswith("smapi-internal/"))):
                fail("inventory")
            common = {
                "path": relative,
                "mode": stat.S_IMODE(metadata.st_mode),
                "uid": metadata.st_uid,
                "gid": metadata.st_gid,
                "device": metadata.st_dev,
                "inode": metadata.st_ino,
                "links": metadata.st_nlink,
                "size": metadata.st_size,
            }
            if stat.S_ISDIR(metadata.st_mode):
                values.append({**common, "type": "directory"})
                if relative not in opaque_paths:
                    stack.append((Path(entry.path), relative))
            elif stat.S_ISREG(metadata.st_mode):
                if metadata.st_nlink != 1 or metadata.st_size > MAX_INVENTORY_FILE_BYTES:
                    fail("inventory")
                total_bytes += metadata.st_size
                if total_bytes > MAX_INVENTORY_TOTAL_BYTES:
                    fail("inventory")
                digest, after = hash_regular(Path(entry.path), MAX_INVENTORY_FILE_BYTES)
                if (after.st_dev, after.st_ino) != (metadata.st_dev, metadata.st_ino):
                    fail("inventory")
                values.append({**common, "type": "file", "sha256": digest})
            else:
                fail("inventory")
    values.sort(key=lambda item: item["path"])
    encoded = json.dumps(values, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return values, hashlib.sha256(encoded).hexdigest()


def restoration_digest(values: list[dict[str, Any]]) -> str:
    """Compare fixture/managed content while excluding expected private transaction bookkeeping."""
    projected = []
    for value in values:
        path = value["path"]
        if path == ".smapi-installer" or path.startswith(".smapi-installer/"):
            continue
        projected.append({key: nested for key, nested in value.items() if key != "inode"})
    return hashlib.sha256(json.dumps(projected, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()


def e2_terminal_digest(scenario: str, values: list[dict[str, Any]]) -> str:
    """Project only workload-owned terminal content that is readable for the armed boundary."""
    if scenario not in E2_SCENARIOS:
        fail("inventory")
    projected = []
    for value in values:
        path = value["path"]
        if path == ".smapi-installer" or path.startswith(".smapi-installer/"):
            continue
        if scenario == "E2-disk-full" and path == "capacity.bin":
            continue
        if scenario == "E2-permission" and (path == "smapi-internal" or path.startswith("smapi-internal/")):
            continue
        projected.append({
            key: nested for key, nested in value.items()
            if key != "inode" and not (scenario == "E2-cross-device" and key == "device")
        })
    return hashlib.sha256(json.dumps(projected, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()


def e2_restored_digest(scenario: str, values: list[dict[str, Any]]) -> str:
    """Project the correct underlying post-unmount view for one real E2 boundary."""
    if scenario in ("E2-read-only", "E2-disk-full"):
        return restoration_digest(values)
    if scenario == "E2-permission":
        return restoration_digest(values)
    if scenario == "E2-cross-device":
        projected = [{key: nested for key, nested in value.items() if key not in ("device", "inode")} for value in values]
        return hashlib.sha256(json.dumps(projected, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()
    fail("inventory")


def enforce_output_bound(root: Path) -> None:
    """Fail closed if private evidence exceeds its fixed file/count/byte envelope."""
    count = 0
    total = 0
    for directory, names, files in os.walk(root, followlinks=False):
        for name in (*names, *files):
            count += 1
            if count > MAX_INVENTORY_ENTRIES:
                fail("inventory")
            path = Path(directory) / name
            try:
                metadata = path.lstat()
            except OSError:
                fail("inventory")
            if stat.S_ISLNK(metadata.st_mode) or not (stat.S_ISDIR(metadata.st_mode) or stat.S_ISREG(metadata.st_mode)):
                fail("inventory")
            if stat.S_ISREG(metadata.st_mode):
                if metadata.st_size > MAX_INVENTORY_FILE_BYTES:
                    fail("inventory")
                total += metadata.st_size
                if total > MAX_INVENTORY_TOTAL_BYTES:
                    fail("inventory")


def write_private_json(path: Path, value: Any) -> None:
    private_file(path, json.dumps(value, sort_keys=True, separators=(",", ":")).encode("utf-8") + b"\n")


def read_bounded_regular(
    path: Path,
    maximum: int,
    allow_empty: bool = False,
    require_private_mode: bool = True,
) -> bytes:
    descriptor = -1
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW | os.O_NONBLOCK)
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode) or before.st_nlink != 1
            or before.st_uid != os.geteuid()
            or (
                stat.S_IMODE(before.st_mode) != 0o600
                if require_private_mode else bool(stat.S_IMODE(before.st_mode) & 0o022)
            )
            or before.st_size > maximum or (before.st_size == 0 and not allow_empty)
        ):
            fail("capture")
        data = bytearray()
        while len(data) < before.st_size:
            block = os.read(descriptor, min(1024 * 1024, before.st_size - len(data)))
            if not block:
                fail("capture")
            data.extend(block)
        if os.read(descriptor, 1):
            fail("capture")
        after = os.fstat(descriptor)
        fields = ("st_dev", "st_ino", "st_mode", "st_uid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
        if any(getattr(before, field) != getattr(after, field) for field in fields):
            fail("capture")
        return bytes(data)
    except QualificationError:
        raise
    except OSError:
        fail("capture")
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def strict_json(raw: bytes, *, ascii_only: bool = False) -> Any:
    def reject_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                fail("capture")
            result[key] = value
        return result

    try:
        text = raw.decode("ascii" if ascii_only else "utf-8", errors="strict")
        return json.loads(
            text,
            object_pairs_hook=reject_duplicates,
            parse_constant=lambda _value: fail("capture"),
        )
    except QualificationError:
        raise
    except (UnicodeError, json.JSONDecodeError, RecursionError):
        fail("capture")


def validate_canonical_png(data: bytes) -> tuple[int, int, str]:
    signature = b"\x89PNG\r\n\x1a\n"
    if not data.startswith(signature):
        fail("capture")
    offset = len(signature)
    chunks: list[tuple[bytes, bytes]] = []
    while offset < len(data):
        if len(chunks) >= 3 or len(data) - offset < 12:
            fail("capture")
        length = struct.unpack(">I", data[offset:offset + 4])[0]
        kind = data[offset + 4:offset + 8]
        end = offset + 12 + length
        if end > len(data):
            fail("capture")
        payload = data[offset + 8:offset + 8 + length]
        expected_crc = struct.unpack(">I", data[offset + 8 + length:end])[0]
        if zlib.crc32(payload, zlib.crc32(kind)) & 0xffffffff != expected_crc:
            fail("capture")
        chunks.append((kind, payload))
        offset = end
    if tuple(kind for kind, _payload in chunks) != (b"IHDR", b"IDAT", b"IEND"):
        fail("capture")
    header, compressed, end_payload = (payload for _kind, payload in chunks)
    if len(header) != 13 or end_payload:
        fail("capture")
    width, height, depth, color_type, compression, filtering, interlace = struct.unpack(">IIBBBBB", header)
    channels = 3 if color_type == 2 else 4 if color_type == 6 else 0
    if (
        not 0 < width <= 32768 or not 0 < height <= 32768
        or width * height > 64_000_000 or depth != 8 or channels == 0
        or compression != 0 or filtering != 0 or interlace != 0
    ):
        fail("capture")
    expected_size = height * (1 + width * channels)
    if expected_size > 256 * 1024 * 1024:
        fail("capture")
    decompressor = zlib.decompressobj()
    try:
        scanlines = decompressor.decompress(compressed, expected_size + 1)
        scanlines += decompressor.flush(max(0, expected_size + 1 - len(scanlines)))
    except (ValueError, zlib.error):
        fail("capture")
    row_size = width * channels
    if (
        len(scanlines) != expected_size or not decompressor.eof
        or decompressor.unused_data or decompressor.unconsumed_tail
        or any(scanlines[row * (row_size + 1)] != 0 for row in range(height))
    ):
        fail("capture")
    pixels = b"".join(
        scanlines[row * (row_size + 1) + 1:(row + 1) * (row_size + 1)]
        for row in range(height)
    )
    canonical = (
        signature
        + struct.pack(">I", len(header)) + b"IHDR" + header
        + struct.pack(">I", zlib.crc32(header, zlib.crc32(b"IHDR")) & 0xffffffff)
        + struct.pack(">I", len(compressed)) + b"IDAT" + compressed
        + struct.pack(">I", zlib.crc32(compressed, zlib.crc32(b"IDAT")) & 0xffffffff)
        + struct.pack(">I", 0) + b"IEND"
        + struct.pack(">I", zlib.crc32(b"", zlib.crc32(b"IEND")) & 0xffffffff)
    )
    if canonical != data or zlib.compress(scanlines, 9) != compressed:
        fail("capture")
    return width, height, hashlib.sha256(pixels).hexdigest()


@dataclass(frozen=True)
class ProcessIdentity:
    pid: int
    start_time: int
    process_group: int
    executable_device: int
    executable_inode: int
    executable_size: int
    executable_sha256: str


def read_runtime_metadata(package_root: Path) -> tuple[str, str, str]:
    deps_path = package_root / "internal/linux/SMAPI.Installer.Gui.deps.json"
    runtime_path = package_root / "internal/linux/SMAPI.Installer.Gui.runtimeconfig.json"
    try:
        deps = json.loads(
            read_bounded_regular(deps_path, 32 * 1024 * 1024, require_private_mode=False).decode("utf-8")
        )
        runtime = json.loads(
            read_bounded_regular(runtime_path, 1024 * 1024, require_private_mode=False).decode("utf-8")
        )
        libraries = deps["libraries"]
        runtime_target = deps["runtimeTarget"]["name"]
        included = runtime["runtimeOptions"]["includedFrameworks"]
        avalonia_versions = {
            key.split("/", 1)[1]
            for key in libraries
            if isinstance(key, str) and key.startswith("Avalonia/") and "/" in key
        }
        frameworks = [
            item["version"] for item in included
            if isinstance(item, dict) and item.get("name") == "Microsoft.NETCore.App"
        ]
        if (
            not isinstance(libraries, dict) or len(avalonia_versions) != 1
            or not isinstance(runtime_target, str)
            or re.fullmatch(r"\.NETCoreApp,Version=v[0-9]+\.[0-9]+/linux-x64", runtime_target) is None
            or len(frameworks) != 1 or not isinstance(frameworks[0], str)
            or re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", frameworks[0]) is None
        ):
            fail("capture")
        avalonia = next(iter(avalonia_versions))
        if re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:[-.][0-9A-Za-z.-]+)?", avalonia) is None:
            fail("capture")
        return (
            f"Avalonia {avalonia}",
            "not required by self-contained release package",
            f"Microsoft.NETCore.App {frameworks[0]}",
        )
    except QualificationError:
        raise
    except (KeyError, TypeError, UnicodeError, json.JSONDecodeError):
        fail("capture")


class CaptureCoordinator:
    """Bind one durable classification and exact-window PNG to one closed scenario milestone."""

    def __init__(
        self,
        contract: dict[str, Any],
        output: Path,
        package_root: Path,
        game: Path,
        before_entries: list[dict[str, Any]],
        gui: ProcessIdentity,
        gui_sha256: str,
        backend_sha256: str,
        environment: dict[str, str],
    ):
        model = load_capture_model()
        classifier = load_classifier()
        try:
            spec = model.capture_spec(contract["scenario"])
            profile = model.environment_profile(contract["capture"]["environment_profile"])
        except (KeyError, TypeError, ValueError):
            fail("capture")
        if environment.get("DISPLAY") is None:
            fail("capture")
        try:
            environment_facts = load_environment_verifier().verify_capture_environment(
                profile.profile_id.value,
                _environment_reader=lambda: environment,
            )
        except BaseException:
            fail("capture")
        self.contract = contract
        self.output = output
        self.package_root = package_root
        self.game = game
        self.before_entries = before_entries
        self.gui = gui
        self.gui_sha256 = gui_sha256
        self.backend_sha256 = backend_sha256
        self.environment = environment
        self.environment_facts = environment_facts
        self.spec = spec
        self.profile = profile
        self.classifier = classifier
        self.capture_milestone = spec.capture_milestone.value
        self.required_terminal_milestone = spec.required_terminal_milestone.value
        self.barrier_observed = False
        self.backend_loss_observed = False
        self.fresh_session_observed = False
        self.captured = False
        self.durable_at_capture: str | None = None

    def _classification_digest(self, values: list[dict[str, Any]], *, capture: bool) -> str:
        scenario = self.contract["scenario"]
        if scenario in E2_SCENARIOS:
            return e2_terminal_digest(scenario, values) if capture else e2_restored_digest(scenario, values)
        return restoration_digest(values)

    def _classify(self, phase: str, before_digest: str, current_digest: str) -> str:
        try:
            summary = self.classifier.inspect_transaction_store(self.game)
            durable = self.classifier.classify_scenario(
                self.contract["scenario"], phase=phase,
                before_digest=before_digest, current_digest=current_digest,
                barrier_observed=self.barrier_observed,
                backend_loss_observed=self.backend_loss_observed,
                fresh_session_observed=self.fresh_session_observed,
                summary=summary,
            )
        except BaseException:
            fail("state")
        expected = self.spec.durable_at_capture if phase == "capture" else self.spec.durable_after
        if durable is not expected:
            fail("state")
        return durable.value

    def _production_identity(self) -> dict[str, str]:
        package_name = Path(self.contract["package"]["path"]).name
        tag = self.contract["release"]["tag"]
        return {
            "source_commit": self.contract["release"]["expected_commit"],
            "source_tree": self.contract["release"]["expected_tree"],
            "release_tag": tag,
            "package_url": f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/{tag}/{package_name}",
            "package_sha256": self.contract["package"]["sha256"],
            "public_release_url": self.contract["release"]["url"],
            "gui_binary_sha256": self.gui_sha256,
            "backend_binary_sha256": self.backend_sha256,
        }

    def _stage(self, deadline: float) -> None:
        control = self.output / "capture-control"
        stage = self.output / "capture"
        private_directory(control)
        private_directory(stage)
        identity_path = control / "production-identity.json"
        private_strings_path = control / "private-strings.txt"
        write_private_json(identity_path, self._production_identity())
        private_values = {
            os.fspath(self.output), os.fspath(self.game),
            os.fspath(Path(self.contract["package"]["path"])), socket.gethostname(),
        }
        private_file(
            private_strings_path,
            ("\n".join(sorted(value for value in private_values if len(value) >= 4)) + "\n").encode("utf-8"),
        )
        avalonia, dotnet_sdk, dotnet_runtime = read_runtime_metadata(self.package_root)
        facts = self.environment_facts
        arguments = [
            sys.executable, os.fspath(STAGER_HELPER), "--discover-window",
            "--expected-window-title", self.spec.window_title,
            "--expected-window-pid", str(self.gui.pid),
            "--expected-gui-process-start-time", str(self.gui.start_time),
            "--expected-gui-exe-device", str(self.gui.executable_device),
            "--expected-gui-exe-inode", str(self.gui.executable_inode),
            "--expected-gui-exe-size", str(self.gui.executable_size),
            "--expected-gui-exe-sha256", self.gui_sha256,
            "--expected-display", self.environment["DISPLAY"],
            "--stage-directory", os.fspath(stage),
            "--filename", f"{self.spec.output_basename}.png",
            "--evidence-id", self.spec.evidence_id.value,
            "--evidence-class", "real_qualification",
            "--production-identity", os.fspath(identity_path),
            "--private-strings-file", os.fspath(private_strings_path),
            "--fixture-or-injection", self.spec.boundary_trigger.value,
            "--operation", self.spec.operation.value,
            "--durable-before", self.spec.durable_before.value,
            "--durable-after", self.spec.durable_at_capture.value,
            "--qualification-reference", self.spec.docs_anchor,
            "--distribution", f"{facts.distribution} {facts.distribution_version}",
            "--architecture", facts.architecture,
            "--desktop-environment", facts.desktop,
            "--session-type", facts.session,
            "--display-backend", facts.window_backend,
            "--display-scale-percent", str(facts.scale_percent),
            "--theme", facts.theme,
            "--resolution", f"{facts.resolution_width}x{facts.resolution_height}",
            "--avalonia", avalonia,
            "--dotnet-sdk", dotnet_sdk,
            "--dotnet-runtime", dotnet_runtime,
        ]
        if self.spec.fault is not None:
            arguments.extend(("--fault", self.spec.fault.value))
        helper_before, _ = hash_trusted_helper(STAGER_HELPER, 4 * 1024 * 1024)
        stdout_path = control / "stager.stdout.log"
        stderr_path = control / "stager.stderr.log"
        stdout = open(stdout_path, "xb", buffering=0)
        stderr = open(stderr_path, "xb", buffering=0)
        os.chmod(stdout.fileno(), 0o600)
        os.chmod(stderr.fileno(), 0o600)
        try:
            try:
                result = subprocess.run(
                    arguments, stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr,
                    env=self.environment, timeout=max(0.01, deadline - time.monotonic()), check=False,
                    preexec_fn=lambda: resource.setrlimit(
                        resource.RLIMIT_FSIZE,
                        (64 * 1024 * 1024, 64 * 1024 * 1024),
                    ),
                )
            except (OSError, subprocess.TimeoutExpired):
                fail("capture")
        finally:
            stdout.close()
            stderr.close()
        helper_after, _ = hash_trusted_helper(STAGER_HELPER, 4 * 1024 * 1024)
        if helper_after != helper_before or not identity_matches(self.gui) or result.returncode != 0:
            fail("capture")
        stdout_bytes = read_bounded_regular(stdout_path, 64 * 1024)
        if read_bounded_regular(stderr_path, 64 * 1024, allow_empty=True):
            fail("capture")
        emitted = strict_json(stdout_bytes, ascii_only=True)
        expected_png = f"{self.spec.output_basename}.png"
        expected_record = f"{self.spec.output_basename}.capture.json"
        if (
            stdout_bytes.count(b"\n") != 1 or not stdout_bytes.endswith(b"\n")
            or not isinstance(emitted, dict)
            or set(emitted) != {"filename", "record", "sha256", "pixel_sha256", "width", "height"}
            or emitted["filename"] != expected_png or emitted["record"] != expected_record
            or set(os.listdir(stage)) != {expected_png, expected_record}
        ):
            fail("capture")
        png = read_bounded_regular(stage / expected_png, 64 * 1024 * 1024)
        record_raw = read_bounded_regular(stage / expected_record, 1024 * 1024)
        record = strict_json(record_raw)
        if not isinstance(record, dict):
            fail("capture")
        width, height, pixel_sha256 = validate_canonical_png(png)
        expected_identity = self._production_identity()
        capture = record.get("capture")
        source_window = capture.get("source_window") if isinstance(capture, dict) else None
        environment_record = record.get("environment")
        runtime_record = record.get("runtime")
        normalization = record.get("normalization")
        privacy = record.get("privacy_review")
        executable = source_window.get("executable") if isinstance(source_window, dict) else None
        expected_environment = {
            "distribution": f"{facts.distribution} {facts.distribution_version}",
            "architecture": facts.architecture,
            "desktop_environment": facts.desktop,
            "session_type": facts.session,
            "display_backend": facts.window_backend,
            "display_scale_percent": facts.scale_percent,
            "theme": facts.theme,
            "resolution": f"{facts.resolution_width}x{facts.resolution_height}",
        }
        expected_runtime = {
            "avalonia": avalonia,
            "dotnet_sdk": dotnet_sdk,
            "dotnet_runtime": dotnet_runtime,
        }
        expected_keys = {
            "staging_schema_version", "status", "id", "filename", "evidence_class",
            "production_identity", "fixture_or_injection", "operation", "durable_state",
            "environment", "runtime", "capture", "normalization", "privacy_review",
            "qualification_reference",
        } | ({"fault"} if self.spec.fault is not None else set())
        if (
            set(record) != expected_keys
            or record_raw != (json.dumps(record, indent=2, sort_keys=True) + "\n").encode("utf-8")
            or record.get("staging_schema_version") != 1
            or hashlib.sha256(png).hexdigest() != emitted["sha256"]
            or emitted["pixel_sha256"] != pixel_sha256
            or emitted["width"] != width or emitted["height"] != height
            or record.get("status") != "staged_pending_original_resolution_privacy_review"
            or record.get("id") != self.spec.evidence_id.value
            or record.get("filename") != expected_png
            or record.get("evidence_class") != "real_qualification"
            or record.get("production_identity") != expected_identity
            or record.get("fixture_or_injection") != self.spec.boundary_trigger.value
            or record.get("operation") != self.spec.operation.value
            or record.get("durable_state") != {
                "before": self.spec.durable_before.value,
                "after": self.spec.durable_at_capture.value,
            }
            or environment_record != expected_environment
            or runtime_record != expected_runtime
            or record.get("qualification_reference") != self.spec.docs_anchor
            or not isinstance(capture, dict)
            or set(capture) != {
                "timestamp", "tool", "command", "input_mode", "source_window",
                "width", "height", "sha256", "decoded_pixel_sha256",
            }
            or not isinstance(capture.get("timestamp"), str)
            or re.fullmatch(
                r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:Z|[+-][0-9]{2}:[0-9]{2})",
                capture["timestamp"],
            ) is None
            or not isinstance(capture.get("tool"), str) or not 0 < len(capture["tool"]) <= 160
            or capture.get("input_mode") != "discovered_exact_x11_client_window"
            or capture.get("width") != width or capture.get("height") != height
            or capture.get("sha256") != emitted["sha256"]
            or capture.get("decoded_pixel_sha256") != pixel_sha256
            or not isinstance(source_window, dict)
            or set(source_window) != {
                "window_id", "process_id", "process_start_time", "expected_title",
                "display", "executable", "unique_mapped_visible_client_verified",
            }
            or not isinstance(source_window.get("window_id"), str)
            or re.fullmatch(r"(?:0x[0-9a-fA-F]+|[1-9][0-9]*)", source_window["window_id"]) is None
            or source_window.get("process_id") != self.gui.pid
            or source_window.get("process_start_time") != self.gui.start_time
            or source_window.get("expected_title") != self.spec.window_title
            or source_window.get("display") != self.environment["DISPLAY"]
            or source_window.get("unique_mapped_visible_client_verified") is not True
            or not isinstance(executable, dict)
            or executable != {
                "device": self.gui.executable_device,
                "inode": self.gui.executable_inode,
                "size": self.gui.executable_size,
                "sha256_verified": True,
            }
            or capture.get("command") != (
                f"import -window {source_window.get('window_id')} png32:<private-temporary-file>; "
                "canonical metadata normalization"
            )
            or normalization != {
                "application_pixels_altered": False,
                "input_chunks": ["IHDR", "IDAT", "IEND"],
                "output_chunks": ["IHDR", "IDAT", "IEND"],
                "statement": (
                    "Incidental PNG metadata was removed; decoded RGB/RGBA application pixels "
                    "are byte-identical."
                ),
            }
            or privacy != {
                "status": "pending",
                "requirement": "Inspect the staged PNG at original resolution before manifest promotion.",
            }
            or (self.spec.fault is None and "fault" in record)
            or (self.spec.fault is not None and record.get("fault") != self.spec.fault.value)
        ):
            fail("capture")

    def capture(self, milestone: str, _observations: list[dict[str, Any]], deadline: float) -> None:
        if self.captured or milestone != self.capture_milestone or not identity_matches(self.gui):
            fail("capture")
        current, _digest = inventory(
            self.game,
            allow_cross_device=self.contract["scenario"] == "E2-cross-device",
            opaque_paths=(
                frozenset({"smapi-internal"})
                if self.contract["scenario"] == "E2-permission" else frozenset()
            ),
        )
        self.durable_at_capture = self._classify(
            "capture",
            self._classification_digest(self.before_entries, capture=True),
            self._classification_digest(current, capture=True),
        )
        self._stage(deadline)
        self.captured = True

    def rebind_fresh_session(
        self,
        gui: ProcessIdentity,
        environment: dict[str, str],
    ) -> None:
        if (
            self.contract["scenario"] != "E6" or self.captured
            or identity_matches(self.gui) or environment.get("DISPLAY") is None
        ):
            fail("capture")
        self.gui = gui
        self.environment = environment
        try:
            self.environment_facts = load_environment_verifier().verify_capture_environment(
                self.profile.profile_id.value,
                _environment_reader=lambda: environment,
            )
        except BaseException:
            fail("capture")
        self.fresh_session_observed = True

    def verify_after(
        self,
        before_entries: list[dict[str, Any]],
        current_entries: list[dict[str, Any]],
    ) -> str:
        if not self.captured or self.durable_at_capture != self.spec.durable_at_capture.value:
            fail("capture")
        return self._classify(
            "after",
            self._classification_digest(before_entries, capture=False),
            self._classification_digest(current_entries, capture=False),
        )


class BrokerChannel:
    """One-way authenticated binding from the root broker to its exact controller child."""

    def __init__(self, descriptor: int):
        try:
            metadata = os.fstat(descriptor)
            if not stat.S_ISSOCK(metadata.st_mode):
                fail("boundary")
            self.socket = socket.socket(fileno=descriptor)
            if self.socket.family != socket.AF_UNIX or self.socket.type & socket.SOCK_STREAM == 0:
                fail("boundary")
            peer_pid, peer_uid, _ = struct.unpack(
                "3i", self.socket.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12),
            )
            if peer_pid != os.getppid() or peer_uid != 0:
                fail("boundary")
            self.peer_pid = peer_pid
        except QualificationError:
            raise
        except (OSError, ValueError, struct.error):
            fail("boundary")

    def receive(
        self,
        prefix: Path,
        socket_relative: str,
        expected_request_source_inode: int,
        deadline: float,
    ) -> ProcessIdentity:
        self.socket.settimeout(max(0.01, deadline - time.monotonic()))
        data = bytearray()
        try:
            while not data.endswith(b"\n") and len(data) <= 512:
                block = self.socket.recv(513 - len(data))
                if not block:
                    fail("boundary")
                data.extend(block)
            value = json.loads(bytes(data).decode("ascii"))
        except QualificationError:
            raise
        except (OSError, UnicodeError, json.JSONDecodeError):
            fail("boundary")
        if not isinstance(value, dict) or set(value) != {
            "controller_pid", "controller_request_fd", "controller_script_sha256",
            "controller_start_time", "request_source_inode",
        }:
            fail("boundary")
        pid = value["controller_pid"]
        request_fd = value["controller_request_fd"]
        started = value["controller_start_time"]
        request_source_inode = value["request_source_inode"]
        script_hash = value["controller_script_sha256"]
        if (
            isinstance(pid, bool) or not isinstance(pid, int) or pid <= 1
            or isinstance(request_fd, bool) or not isinstance(request_fd, int) or not 3 <= request_fd <= 1024
            or isinstance(started, bool) or not isinstance(started, int) or started <= 0
            or request_source_inode != expected_request_source_inode
            or not isinstance(script_hash, str) or re.fullmatch(r"[0-9a-f]{64}", script_hash) is None
        ):
            fail("boundary")
        arguments = [
            sys.executable, os.fspath(CONTROLLER_HELPER),
            "--allowed-vm-prefix", os.fspath(prefix),
            "--request-fd", str(request_fd),
            "--request-source-inode", str(request_source_inode),
            "--supervisor-socket", socket_relative,
        ]
        expected_script_hash, _ = hash_trusted_helper(CONTROLLER_HELPER, 4 * 1024 * 1024)
        try:
            command_line = Path(f"/proc/{pid}/cmdline").read_bytes()
        except OSError:
            fail("boundary")
        expected_command_line = b"\0".join(os.fsencode(value) for value in arguments) + b"\0"
        process_group, observed_start = proc_stat(pid)
        digest, executable = hash_proc_executable(pid)
        own_digest, _ = hash_proc_executable(os.getpid())
        if (
            script_hash != expected_script_hash or observed_start != started
            or command_line != expected_command_line or digest != own_digest
        ):
            fail("boundary")
        return ProcessIdentity(
            pid, started, process_group,
            executable.st_dev, executable.st_ino, executable.st_size, digest,
        )

    def close(self) -> None:
        self.socket.close()


def proc_stat(pid: int) -> tuple[int, int]:
    try:
        data = Path(f"/proc/{pid}/stat").read_text(encoding="ascii")
        fields = data[data.rfind(")") + 2:].split()
        return int(fields[2]), int(fields[19])
    except (OSError, ValueError, IndexError):
        fail("identity")


def proc_uids_and_caps(pid: int) -> tuple[int, int, int]:
    try:
        fields: dict[str, str] = {}
        for line in Path(f"/proc/{pid}/status").read_text(encoding="ascii").splitlines():
            if ":" in line:
                key, value = line.split(":", 1)
                fields[key] = value.strip()
        uids = [int(value) for value in fields["Uid"].split()]
        return uids[0], uids[1], int(fields["CapEff"], 16)
    except (OSError, ValueError, KeyError, IndexError):
        fail("identity")


def proc_all_capabilities(pid: int) -> tuple[int, int, int, int]:
    try:
        values: dict[str, int] = {}
        for line in Path(f"/proc/{pid}/status").read_text(encoding="ascii").splitlines():
            key, separator, value = line.partition(":")
            if separator and key in ("CapInh", "CapPrm", "CapEff", "CapAmb"):
                values[key] = int(value.strip(), 16)
        return values["CapInh"], values["CapPrm"], values["CapEff"], values["CapAmb"]
    except (OSError, ValueError, KeyError):
        fail("identity")


def bind_process(pid: int, expected_hash: str, expected_group: int) -> ProcessIdentity:
    process_group, start_time = proc_stat(pid)
    real_uid, effective_uid, capabilities = proc_uids_and_caps(pid)
    if real_uid != os.geteuid() or effective_uid != os.geteuid() or effective_uid == 0 or capabilities != 0 or process_group != expected_group:
        fail("identity")
    digest, metadata = hash_proc_executable(pid)
    if digest != expected_hash:
        fail("identity")
    return ProcessIdentity(
        pid, start_time, process_group,
        metadata.st_dev, metadata.st_ino, metadata.st_size, digest,
    )


def bind_any_process(pid: int, expected_group: int) -> ProcessIdentity:
    digest, _ = hash_proc_executable(pid)
    return bind_process(pid, digest, expected_group)


def identity_matches(identity: ProcessIdentity) -> bool:
    try:
        process_group, start_time = proc_stat(identity.pid)
        if (process_group, start_time) != (identity.process_group, identity.start_time):
            return False
        executable = os.stat(f"/proc/{identity.pid}/exe")
        return (
            executable.st_dev, executable.st_ino, executable.st_size
        ) == (
            identity.executable_device, identity.executable_inode, identity.executable_size
        )
    except QualificationError:
        return False
    except OSError:
        return False


def descendant_pids(root_pid: int) -> list[int]:
    observed: list[int] = []
    pending = [root_pid]
    while pending:
        parent = pending.pop()
        try:
            text = Path(f"/proc/{parent}/task/{parent}/children").read_text(encoding="ascii").strip()
        except OSError:
            continue
        children = [] if not text else [int(value) for value in text.split()]
        for child in children:
            if child in observed:
                fail("identity")
            observed.append(child)
            if len(observed) > MAX_DESCENDANTS:
                fail("identity")
            pending.append(child)
    return observed


def process_group_pids(process_group: int) -> list[int]:
    members: list[int] = []
    try:
        entries = os.scandir("/proc")
    except OSError:
        fail("identity")
    with entries:
        for entry in entries:
            if not entry.name.isascii() or not entry.name.isdigit():
                continue
            pid = int(entry.name)
            try:
                group, _ = proc_stat(pid)
            except QualificationError:
                continue
            if group == process_group:
                members.append(pid)
                if len(members) > MAX_DESCENDANTS + 1:
                    fail("identity")
    return sorted(members)


def bind_exact_app_tree(root: ProcessIdentity, known: list[ProcessIdentity]) -> list[ProcessIdentity]:
    if not identity_matches(root):
        fail("identity")
    members = process_group_pids(root.process_group)
    by_pid = {root.pid: root, **{value.pid: value for value in known}}
    if set(members) != set(by_pid):
        fail("identity")
    for pid in members:
        if not identity_matches(by_pid[pid]):
            fail("identity")
    return [by_pid[pid] for pid in members if pid != root.pid]


def find_bound_descendant(root_pid: int, expected_hash: str, process_group: int, deadline: float) -> ProcessIdentity:
    while time.monotonic() < deadline:
        for pid in descendant_pids(root_pid):
            try:
                return bind_process(pid, expected_hash, process_group)
            except QualificationError:
                continue
        time.sleep(0.05)
    fail("timeout")


def pidfd_signal(identity: ProcessIdentity, signum: int) -> None:
    if not identity_matches(identity) or not hasattr(os, "pidfd_open") or not hasattr(signal, "pidfd_send_signal"):
        fail("identity")
    try:
        descriptor = os.pidfd_open(identity.pid, 0)
        try:
            if not identity_matches(identity):
                fail("identity")
            signal.pidfd_send_signal(descriptor, signum)
        finally:
            os.close(descriptor)
    except QualificationError:
        raise
    except OSError:
        fail("identity")


class BarrierServer:
    def __init__(self, control: Path):
        self.path = control / "barrier.sock"
        self.socket = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        old_umask = os.umask(0o177)
        try:
            self.socket.bind(os.fspath(self.path))
        finally:
            os.umask(old_umask)
        os.chmod(self.path, 0o600, follow_symlinks=False)
        self.socket.listen(1)
        self.connection: socket.socket | None = None
        self.controller_identity: ProcessIdentity | None = None

    def wait(self, backend: ProcessIdentity, deadline: float) -> int:
        self.socket.settimeout(max(0.01, deadline - time.monotonic()))
        try:
            connection, _ = self.socket.accept()
            connection.settimeout(max(0.01, deadline - time.monotonic()))
            credentials = connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12)
            peer_pid, peer_uid, _peer_gid = struct.unpack("3i", credentials)
            if peer_pid != backend.pid or peer_uid != os.geteuid() or not identity_matches(backend):
                connection.close()
                fail("barrier")
            data = b""
            while not data.endswith(b"\n") and len(data) <= 96:
                chunk = connection.recv(97 - len(data))
                if not chunk:
                    fail("barrier")
                data += chunk
            match = PID_LINE_RE.fullmatch(data)
            if match is None or int(match.group(1)) != backend.pid or int(match.group(2)) >= 20_000:
                fail("barrier")
            self.connection = connection
            return int(match.group(2))
        except QualificationError:
            raise
        except (OSError, ValueError):
            fail("barrier")

    def release(self) -> None:
        if self.connection is None:
            fail("barrier")
        try:
            self.connection.sendall(b"release\n")
            self.connection.shutdown(socket.SHUT_WR)
        except OSError:
            fail("barrier")
        finally:
            self.connection.close()
            self.connection = None

    def close(self) -> None:
        if self.connection is not None:
            self.connection.close()
            self.connection = None
        self.socket.close()
        try:
            self.path.unlink()
        except FileNotFoundError:
            pass


class BoundarySession:
    """Nonroot endpoint for the fixed root controller in the broker's shared mount namespace."""

    ACKS = {
        "prepared": b'{"ok":true,"status":"prepared"}\n',
        "ready": b'{"ok":true,"status":"ready"}\n',
        "armed": b'{"ok":true,"status":"armed"}\n',
        "cleaned": b'{"ok":true,"status":"cleaned"}\n',
    }

    def __init__(
        self,
        contract: dict[str, Any],
        output: Path,
        game: Path,
        control: Path,
        deadline: float,
        broker_channel: BrokerChannel | None = None,
        *,
        _test_controller_uid: int = 0,
    ):
        root = Path(contract["isolation"]["disposable_root"])
        try:
            prefix = root.parent
            prefix_status = prefix.lstat()
            if (
                prefix.resolve(strict=True) != prefix
                or prefix_status.st_uid != _test_controller_uid
                or not stat.S_ISDIR(prefix_status.st_mode)
                or stat.S_IMODE(prefix_status.st_mode) != (0o711 if _test_controller_uid == 0 else 0o700)
                or output.parent != root
            ):
                fail("boundary")
        except OSError:
            fail("boundary")
        self.path = control / f"boundary-{os.urandom(8).hex()}.sock"
        if len(os.fsencode(self.path)) >= 100:
            fail("boundary")
        self.listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        old_umask = os.umask(0o177)
        try:
            self.listener.bind(os.fspath(self.path))
        finally:
            os.umask(old_umask)
        os.chmod(self.path, 0o600, follow_symlinks=False)
        socket_status = self.path.lstat()
        if (
            not stat.S_ISSOCK(socket_status.st_mode)
            or socket_status.st_uid != os.geteuid()
            or stat.S_IMODE(socket_status.st_mode) != 0o600
        ):
            fail("boundary")
        self.listener.listen(1)
        self.connection: socket.socket | None = None
        self.phase = "request"
        process_group, start_time = proc_stat(os.getpid())
        del process_group
        namespace = Path(f"/proc/{os.getpid()}/ns/mnt").stat()
        identities = {name: path.lstat() for name, path in (("root", root), ("output", output), ("game", game))}
        request = {
            "schema_version": 1,
            "scenario": contract["scenario"],
            "run_uid": os.geteuid(),
            "root": {"relative_path": root.name, "device": identities["root"].st_dev, "inode": identities["root"].st_ino},
            "output": {"relative_path": f"{root.name}/{output.name}", "device": identities["output"].st_dev, "inode": identities["output"].st_ino},
            "game": {"relative_path": f"{root.name}/{output.name}/game", "device": identities["game"].st_dev, "inode": identities["game"].st_ino},
            "supervisor": {
                "pid": os.getpid(), "start_time": start_time,
                "mount_namespace_device": namespace.st_dev, "mount_namespace_inode": namespace.st_ino,
            },
            "supervisor_socket": {
                "relative_path": f"{root.name}/{output.name}/control/{self.path.name}",
                "device": socket_status.st_dev, "inode": socket_status.st_ino,
            },
            "timeouts_seconds": {
                "hold": contract["timeouts_seconds"]["total"],
                "cleanup": contract["timeouts_seconds"]["cleanup"],
            },
        }
        request_path = root / BOUNDARY_REQUEST_NAME
        private_file(request_path, canonical_message(request) + b"\n")
        try:
            request_status = request_path.lstat()
            if not stat.S_ISREG(request_status.st_mode) or request_status.st_uid != os.geteuid():
                fail("boundary")
        except OSError:
            fail("boundary")
        try:
            expected_controller = None
            if _test_controller_uid == 0:
                if broker_channel is None:
                    fail("boundary")
                expected_controller = broker_channel.receive(
                    prefix,
                    request["supervisor_socket"]["relative_path"],
                    request_status.st_ino,
                    deadline,
                )
            self.listener.settimeout(max(0.01, deadline - time.monotonic()))
            connection, _ = self.listener.accept()
            credentials = connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12)
            peer_pid, peer_uid, _ = struct.unpack("3i", credentials)
            peer_namespace = Path(f"/proc/{peer_pid}/ns/mnt").stat()
            peer_real_uid, peer_effective_uid, _ = proc_uids_and_caps(peer_pid)
            peer_group, peer_start = proc_stat(peer_pid)
            peer_digest, peer_executable = hash_proc_executable(peer_pid)
            own_digest, _ = hash_proc_executable(os.getpid())
            if (
                peer_uid != _test_controller_uid
                or peer_real_uid != _test_controller_uid or peer_effective_uid != _test_controller_uid
                or (peer_namespace.st_dev, peer_namespace.st_ino) != (namespace.st_dev, namespace.st_ino)
                or peer_digest != own_digest
                or (expected_controller is not None and (
                    peer_pid != expected_controller.pid or peer_start != expected_controller.start_time
                    or not identity_matches(expected_controller)
                ))
            ):
                connection.close()
                fail("boundary")
            self.controller_identity = ProcessIdentity(
                peer_pid, peer_start, peer_group,
                peer_executable.st_dev, peer_executable.st_ino, peer_executable.st_size, peer_digest,
            )
            self.connection = connection
            self.expect("prepared", deadline)
            self.phase = "prepared"
        except BaseException:
            self.close()
            raise

    def receive(self, deadline: float) -> bytes:
        if self.connection is None:
            fail("boundary")
        self.connection.settimeout(max(0.01, deadline - time.monotonic()))
        data = bytearray()
        try:
            while not data.endswith(b"\n") and len(data) <= 64:
                chunk = self.connection.recv(65 - len(data))
                if not chunk:
                    fail("boundary")
                data.extend(chunk)
        except QualificationError:
            raise
        except OSError:
            fail("boundary")
        return bytes(data)

    def expect(self, stage: str, deadline: float) -> None:
        if stage not in self.ACKS or self.receive(deadline) != self.ACKS[stage]:
            fail("boundary")

    def transition(self, command: bytes, expected: str, deadline: float) -> None:
        if (
            self.connection is None or self.controller_identity is None
            or not identity_matches(self.controller_identity)
            or command not in (b"seeded\n", b"arm\n", b"cleanup\n")
        ):
            fail("boundary")
        try:
            self.connection.sendall(command)
        except OSError:
            fail("boundary")
        self.expect(expected, deadline)
        self.phase = expected

    def seeded(self, deadline: float) -> None:
        if self.phase != "prepared":
            fail("boundary")
        self.transition(b"seeded\n", "ready", deadline)

    def arm(self, deadline: float) -> None:
        if self.phase != "ready":
            fail("boundary")
        self.transition(b"arm\n", "armed", deadline)

    def cleanup(self, deadline: float) -> None:
        if self.phase in ("ready", "armed"):
            self.transition(b"cleanup\n", "cleaned", deadline)
        if self.phase != "cleaned":
            fail("boundary")
        self.close()

    def close(self) -> None:
        if self.connection is not None:
            self.connection.close()
            self.connection = None
        self.listener.close()
        try:
            self.path.unlink()
        except FileNotFoundError:
            pass


def canonical_message(value: dict[str, Any]) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("ascii")


def signed_message(token: bytes, value: dict[str, Any]) -> dict[str, Any]:
    result = dict(value)
    result["proof"] = hmac.new(token, canonical_message(value), hashlib.sha256).hexdigest()
    return result


def validate_observation_facts(milestone: str, value: Any) -> list[dict[str, Any]]:
    expected = EXPECTED_OBSERVATIONS.get(milestone)
    if not isinstance(value, list) or expected is None or len(value) != len(expected):
        fail("operator")
    result: list[dict[str, Any]] = []
    for fact, (name, roles, action_interface) in zip(value, expected, strict=True):
        if (
            not isinstance(fact, dict) or set(fact) != OBSERVATION_FACT_KEYS
            or fact.get("name") != name or fact.get("role") not in roles
            or fact.get("visible") is not True
            or type(fact.get("enabled")) is not bool
            or fact.get("actionInterface") is not action_interface
            or (action_interface and fact.get("enabled") is not True)
        ):
            fail("operator")
        result.append(dict(fact))
    return result


class AtspiSession:
    """Authenticated controller for the separately reviewed AT-SPI action helper."""

    def __init__(
        self,
        route: str,
        gui: ProcessIdentity,
        gui_sha256: str,
        control: Path,
        output: Path,
        environment: dict[str, str],
        suffix: str,
        deadline: float,
        capture_coordinator: Any | None = None,
    ):
        if route not in AT_SPI_ROUTES or not re.fullmatch(r"[a-z0-9_-]{1,24}", suffix):
            fail("operator")
        helper_hash, _ = hash_trusted_helper(OPERATOR_HELPER, 4 * 1024 * 1024)
        self.helper_hash = helper_hash
        self.output = output
        self.suffix = suffix
        self.route = route
        self.gui = gui
        self.gui_sha256 = gui_sha256
        self.session_id = f"hard_state_{os.urandom(12).hex()}"
        self.token = os.urandom(32)
        self.token_path = control / f"atspi-{suffix}.token"
        self.socket_path = control / f"atspi-{suffix}.sock"
        self.trace_path = output / f"atspi-{suffix}.trace.jsonl"
        private_file(self.token_path, self.token.hex().encode("ascii") + b"\n")
        self.listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        old_umask = os.umask(0o177)
        try:
            self.listener.bind(os.fspath(self.socket_path))
        finally:
            os.umask(old_umask)
        os.chmod(self.socket_path, 0o600, follow_symlinks=False)
        self.listener.listen(1)
        stderr = open(output / f"atspi-{suffix}.stderr.log", "xb", buffering=0)
        self.stdout_path = output / f"atspi-{suffix}.stdout.log"
        stdout = open(self.stdout_path, "xb", buffering=0)
        os.chmod(stderr.fileno(), 0o600)
        os.chmod(stdout.fileno(), 0o600)
        self.stderr = stderr
        self.stdout = stdout
        self.process: subprocess.Popen[bytes] | None = None
        self.identity: ProcessIdentity | None = None
        self.connection: socket.socket | None = None
        self.buffer = bytearray()
        self.sequence = 0
        self.observation_count = 0
        self.capture_coordinator = capture_coordinator
        self.capture_count = 0
        self.reached_milestones: set[str] = set()
        try:
            self.process = subprocess.Popen(
                [
                    sys.executable, os.fspath(OPERATOR_HELPER),
                    "--supervisor-socket", os.fspath(self.socket_path),
                    "--token-file", os.fspath(self.token_path),
                    "--session-id", self.session_id,
                    "--trace-file", os.fspath(self.trace_path),
                ],
                stdin=subprocess.DEVNULL,
                stdout=stdout,
                stderr=stderr,
                env=environment,
                start_new_session=True,
                preexec_fn=lambda: resource.setrlimit(resource.RLIMIT_FSIZE, (64 * 1024 * 1024, 64 * 1024 * 1024)),
            )
            self.identity = bind_any_process(self.process.pid, self.process.pid)
            self.listener.settimeout(max(0.01, deadline - time.monotonic()))
            connection, _ = self.listener.accept()
            credentials = connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12)
            peer_pid, peer_uid, _ = struct.unpack("3i", credentials)
            if peer_pid != self.identity.pid or peer_uid != os.geteuid() or not identity_matches(self.identity):
                connection.close()
                fail("operator")
            connection.settimeout(max(0.01, deadline - time.monotonic()))
            self.connection = connection
            hello = self.receive(deadline)
            body = self.verify(hello, {"type", "version", "session", "nonce"})
            nonce = body.get("nonce")
            if (
                body.get("type") != "hello" or body.get("version") != 1
                or body.get("session") != self.session_id
                or not isinstance(nonce, str) or re.fullmatch(r"[0-9a-f]{32}", nonce) is None
            ):
                fail("operator")
            self.send({
                "type": "admit", "version": 1, "session": self.session_id,
                "nonce": nonce, "route": route, "gui_pid": gui.pid,
                "gui_sha256": gui_sha256,
            })
        except BaseException:
            self.close(min(deadline, time.monotonic() + 2), require_success=False)
            raise

    def send(self, body: dict[str, Any]) -> None:
        if self.connection is None:
            fail("operator")
        payload = canonical_message(signed_message(self.token, body)) + b"\n"
        if len(payload) > 16 * 1024:
            fail("operator")
        try:
            self.connection.sendall(payload)
        except OSError:
            fail("operator")

    def receive(self, deadline: float) -> dict[str, Any]:
        if self.connection is None:
            fail("operator")
        self.connection.settimeout(max(0.01, deadline - time.monotonic()))
        try:
            while b"\n" not in self.buffer:
                if len(self.buffer) >= 16 * 1024:
                    fail("operator")
                chunk = self.connection.recv(16 * 1024 - len(self.buffer))
                if not chunk:
                    fail("operator")
                self.buffer.extend(chunk)
            line, _, remainder = self.buffer.partition(b"\n")
            self.buffer = bytearray(remainder)
            value = json.loads(line.decode("ascii"))
        except QualificationError:
            raise
        except (OSError, UnicodeError, json.JSONDecodeError):
            fail("operator")
        if not isinstance(value, dict):
            fail("operator")
        return value

    def verify(self, value: dict[str, Any], exact_body_keys: set[str]) -> dict[str, Any]:
        if set(value) != exact_body_keys | {"proof"} or not isinstance(value.get("proof"), str):
            fail("operator")
        body = {key: nested for key, nested in value.items() if key != "proof"}
        expected = hmac.new(self.token, canonical_message(body), hashlib.sha256).hexdigest()
        if not hmac.compare_digest(value["proof"], expected):
            fail("operator")
        return body

    def advance(self, milestone: str, deadline: float, release_folder: Path, game: Path) -> None:
        route = AT_SPI_ROUTES[self.route]
        if self.sequence >= len(route) or route[self.sequence] != milestone or not identity_matches(self.gui):
            fail("operator")
        body: dict[str, Any] = {
            "type": "advance", "version": 1, "session": self.session_id,
            "sequence": self.sequence, "milestone": milestone,
        }
        if PICKER_FIELDS.get(milestone) == "release_folder":
            body["release_folder"] = os.fspath(release_folder)
        elif PICKER_FIELDS.get(milestone) == "game_folder":
            body["game_folder"] = os.fspath(game)
        if milestone == "plan.inspect":
            body["operation"] = "install"
        self.send(body)
        response_keys = {"type", "version", "session", "sequence", "milestone"}
        first = self.verify(
            self.receive(deadline),
            response_keys | ({"observations"} if milestone in OBSERVATION_MILESTONES else set()),
        )
        if milestone in OBSERVATION_MILESTONES:
            capture_ready = {
                "type": "capture-ready", "version": 1, "session": self.session_id,
                "sequence": self.sequence, "milestone": milestone,
            }
            observations = validate_observation_facts(milestone, first.get("observations"))
            if (
                {key: value for key, value in first.items() if key != "observations"} != capture_ready
                or not identity_matches(self.gui)
            ):
                fail("operator")
            self.observation_count += 1
            write_private_json(
                self.output / f"atspi-{self.suffix}-observation-{self.sequence:02d}.json",
                {
                    "milestone": milestone, "observations": observations,
                    "schemaVersion": 1, "sequence": self.sequence,
                },
            )
            if (
                self.capture_coordinator is not None
                and milestone == self.capture_coordinator.capture_milestone
            ):
                self.capture_coordinator.capture(milestone, observations, deadline)
                self.capture_count += 1
            self.send({**capture_ready, "type": "continue"})
            reached = self.verify(self.receive(deadline), response_keys)
        else:
            reached = first
        if reached != {
            "type": "reached", "version": 1, "session": self.session_id,
            "sequence": self.sequence, "milestone": milestone,
        }:
            fail("operator")
        self.reached_milestones.add(milestone)
        self.sequence += 1

    def complete(self, deadline: float) -> None:
        if self.sequence != len(AT_SPI_ROUTES[self.route]):
            fail("operator")
        if self.capture_coordinator is not None and (
            self.capture_count != 1
            or self.capture_coordinator.required_terminal_milestone not in self.reached_milestones
        ):
            fail("capture")
        body = {"type": "complete", "version": 1, "session": self.session_id, "sequence": self.sequence}
        self.send(body)
        completed = self.verify(self.receive(deadline), {"type", "version", "session", "sequence"})
        if completed != {**body, "type": "completed"}:
            fail("operator")
        self.close(deadline, require_success=True)

    def close(self, deadline: float, require_success: bool) -> None:
        if self.connection is not None:
            self.connection.close()
            self.connection = None
        self.listener.close()
        try:
            self.socket_path.unlink()
        except FileNotFoundError:
            pass
        try:
            if self.process is not None:
                self.process.wait(timeout=max(0.01, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            if self.identity is not None and identity_matches(self.identity):
                pidfd_signal(self.identity, signal.SIGKILL)
            try:
                if self.process is None:
                    fail("cleanup")
                self.process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                if require_success:
                    fail("cleanup")
        self.stderr.close()
        self.stdout.close()
        if require_success and (self.process is None or self.process.returncode != 0):
            fail("operator")
        try:
            stdout_metadata = self.stdout_path.stat()
            trace_metadata = self.trace_path.stat()
        except OSError:
            fail("operator")
        if (
            require_success
            and (stdout_metadata.st_size != 0 or stat.S_IMODE(trace_metadata.st_mode) != 0o600)
        ):
            fail("operator")
        after, _ = hash_trusted_helper(OPERATOR_HELPER, 4 * 1024 * 1024)
        if after != self.helper_hash:
            fail("identity")


def compile_barrier(output: Path) -> Path:
    compiler = shutil.which("cc") or shutil.which("gcc")
    if compiler is None:
        fail("barrier")
    source_hash, _ = hash_trusted_helper(BARRIER_SOURCE, 4 * 1024 * 1024)
    library = output / "hard-state-barrier.so"
    log = open(output / "barrier-compile.log", "xb", buffering=0)
    os.chmod(log.fileno(), 0o600)
    try:
        result = subprocess.run(
            [compiler, "-std=c11", "-O2", "-fPIC", "-shared", "-Wall", "-Wextra", "-Werror", BARRIER_SOURCE, "-o", library],
            stdin=subprocess.DEVNULL,
            stdout=log,
            stderr=log,
            timeout=30,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        fail("barrier")
    finally:
        log.close()
    if result.returncode != 0:
        fail("barrier")
    after_hash, _ = hash_trusted_helper(BARRIER_SOURCE, 4 * 1024 * 1024)
    if after_hash != source_hash:
        fail("identity")
    os.chmod(library, 0o500, follow_symlinks=False)
    hash_regular(library, 16 * 1024 * 1024, require_executable=True)
    return library


def minimal_environment(output: Path, barrier: Path | None, game: Path, control: Path, pid_file: Path) -> dict[str, str]:
    environment: dict[str, str] = {}
    for key in (
        "DISPLAY", "WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS", "AT_SPI_BUS_ADDRESS",
        "XAUTHORITY", "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP", "LANG", "LC_ALL",
    ):
        if key in os.environ:
            environment[key] = os.environ[key]
    runtime_value = os.environ.get("XDG_RUNTIME_DIR")
    if runtime_value is None:
        fail("admission")
    runtime = Path(runtime_value)
    try:
        runtime_status = runtime.lstat()
        if (
            runtime.resolve(strict=True) != runtime
            or not stat.S_ISDIR(runtime_status.st_mode)
            or runtime_status.st_uid != os.geteuid()
            or stat.S_IMODE(runtime_status.st_mode) != 0o700
        ):
            fail("admission")
    except OSError:
        fail("admission")
    for name in ("home", "config", "data", "cache", "tmp"):
        private_directory(output / name)
    environment.update({
        "HOME": os.fspath(output / "home"),
        "XDG_CONFIG_HOME": os.fspath(output / "config"),
        "XDG_DATA_HOME": os.fspath(output / "data"),
        "XDG_CACHE_HOME": os.fspath(output / "cache"),
        "XDG_RUNTIME_DIR": os.fspath(runtime),
        "TMPDIR": os.fspath(output / "tmp"),
        "PATH": "/usr/bin:/bin",
    })
    if barrier is not None:
        environment.update({
            "LD_PRELOAD": os.fspath(barrier),
            "SMAPI_LINUX_GUI_HARD_STATE_ROOT": os.fspath(game),
            "SMAPI_LINUX_GUI_HARD_STATE_PID_FILE": os.fspath(pid_file),
            "SMAPI_LINUX_GUI_HARD_STATE_SOCKET": os.fspath(control / "barrier.sock"),
            "SMAPI_LINUX_GUI_HARD_STATE_TIMEOUT_MS": "30000",
        })
    return environment


def launch_gui(launcher: Path, environment: dict[str, str], output: Path) -> subprocess.Popen[bytes]:
    stdout = open(output / "gui.stdout.log", "xb", buffering=0)
    stderr = open(output / "gui.stderr.log", "xb", buffering=0)
    os.chmod(stdout.fileno(), 0o600)
    os.chmod(stderr.fileno(), 0o600)
    try:
        process = subprocess.Popen(
            [launcher],
            stdin=subprocess.DEVNULL,
            stdout=stdout,
            stderr=stderr,
            env=environment,
            start_new_session=True,
            preexec_fn=lambda: resource.setrlimit(resource.RLIMIT_FSIZE, (256 * 1024 * 1024, 256 * 1024 * 1024)),
        )
    except OSError:
        stdout.close()
        stderr.close()
        fail("identity")
    process._smapi_logs = (stdout, stderr)  # type: ignore[attr-defined]
    return process


def cleanup_processes(launcher: subprocess.Popen[bytes] | None, identities: list[ProcessIdentity], deadline: float) -> bool:
    clean = True
    unique = list({value.pid: value for value in identities}.values())
    if launcher is not None:
        root = next((value for value in unique if value.pid == launcher.pid), None)
        if root is None:
            clean = False
        elif identity_matches(root):
            known = {value.pid for value in unique}
            try:
                for pid in process_group_pids(root.process_group):
                    if pid not in known:
                        # Capture an exact identity so even an unexpected child is contained,
                        # but retain cleanup failure instead of silently accepting it.
                        unique.append(bind_any_process(pid, root.process_group))
                        known.add(pid)
                        clean = False
            except QualificationError:
                clean = False
    for identity in reversed(unique):
        if identity_matches(identity):
            try:
                pidfd_signal(identity, signal.SIGTERM)
            except QualificationError:
                clean = False
    grace = min(deadline, time.monotonic() + 2)
    while time.monotonic() < grace and any(identity_matches(value) for value in unique):
        time.sleep(0.05)
    for identity in reversed(unique):
        if identity_matches(identity):
            try:
                pidfd_signal(identity, signal.SIGKILL)
            except QualificationError:
                clean = False
    if unique:
        try:
            remaining = process_group_pids(unique[0].process_group)
            for pid in remaining:
                known = next((value for value in unique if value.pid == pid), None)
                if known is None:
                    known = bind_any_process(pid, unique[0].process_group)
                    clean = False
                if identity_matches(known):
                    pidfd_signal(known, signal.SIGKILL)
        except QualificationError:
            clean = False
    if launcher is not None:
        try:
            launcher.wait(timeout=max(0.01, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            clean = False
        for stream in getattr(launcher, "_smapi_logs", ()):
            stream.close()
    return clean and not any(identity_matches(value) for value in unique)


def advance_operation_to_confirmation(session: AtspiSession, deadline: float, release_folder: Path, game: Path) -> None:
    for milestone in (
        "release.local-folder", "release.continue", "game.choose-folder", "game.continue-valid",
        "plan.inspect", "plan.confirm",
    ):
        session.advance(milestone, deadline, release_folder, game)


def qualification_routes(scenario: str) -> tuple[str, str | None]:
    if scenario in E2_SCENARIOS:
        return scenario.casefold(), None
    if scenario in ("C2", "C3"):
        return "c3-terminal", None
    if scenario == "E5":
        return "e5-backend-loss", None
    if scenario == "E6":
        return "e5-backend-loss", "e6-automatic-recovery"
    fail("admission")


def execute_case(contract: dict[str, Any], output: Path, broker_channel: BrokerChannel | None = None) -> dict[str, Any]:
    if os.geteuid() == 0:
        fail("admission")
    real_uid, effective_uid, capabilities = proc_uids_and_caps(os.getpid())
    if (
        real_uid != os.geteuid() or effective_uid != os.geteuid() or capabilities != 0
        or any(proc_all_capabilities(os.getpid()))
    ):
        fail("admission")
    scenario = contract["scenario"]
    timeouts = contract["timeouts_seconds"]
    total_deadline = time.monotonic() + timeouts["total"]
    package = Path(contract["package"]["path"])
    package_hash_before, package_identity = hash_regular(package, 4 * 1024 * 1024 * 1024)
    if package_hash_before != contract["package"]["sha256"]:
        fail("identity")
    package_root = secure_extract(package, output / "package", contract["release"]["version"], package_identity)
    gui_path = package_root / "internal/linux/SMAPI.Installer.Gui"
    backend_path = package_root / "internal/linux/SMAPI.Installer"
    launcher_path = package_root / "install on Linux (graphical).sh"
    gui_hash, _ = hash_regular(gui_path, MAX_EXECUTABLE_BYTES, require_executable=True)
    backend_hash, _ = hash_regular(backend_path, MAX_EXECUTABLE_BYTES, require_executable=True)
    hash_regular(launcher_path, 1024 * 1024, require_executable=True)
    if gui_hash != contract["binaries"]["apphost_sha256"] or backend_hash != contract["binaries"]["backend_sha256"]:
        fail("identity")
    game = output / "game"
    private_directory(game)
    control = output / "control"
    private_directory(control)
    underlying_before, underlying_before_digest = inventory(game)
    if underlying_before:
        fail("inventory")
    write_private_json(
        output / "inventory-underlying-before.json",
        {"digest": underlying_before_digest, "entries": underlying_before},
    )
    boundary_session = BoundarySession(
        contract, output, game, control,
        min(total_deadline, time.monotonic() + timeouts["startup"]), broker_channel,
    )
    barrier_server: BarrierServer | None = None
    try:
        game = seed_game(
            Path(contract["game_marker"]["path"]),
            contract["game_marker"]["size_bytes"],
            contract["game_marker"]["sha256"],
            output,
        )
        boundary_session.seeded(min(total_deadline, time.monotonic() + timeouts["startup"]))
        before, before_digest = inventory(game, allow_cross_device=scenario == "E2-cross-device")
        write_private_json(output / "inventory-before.json", {"digest": before_digest, "entries": before})
        pid_file = control / "backend.pid"
        barrier = compile_barrier(output) if scenario in BARRIER_SCENARIOS else None
        barrier_server = BarrierServer(control) if barrier is not None else None
        environment = minimal_environment(output, barrier, game, control, pid_file)
    except BaseException:
        if barrier_server is not None:
            barrier_server.close()
        boundary_session.close()
        raise
    launcher: subprocess.Popen[bytes] | None = None
    identities: list[ProcessIdentity] = []
    operator: AtspiSession | None = None
    capture_coordinator: CaptureCoordinator | None = None
    boundary_armed_observed = False
    boundary_cleaned_observed = False
    accessible_state_observed = False
    inventory_verified = False
    try:
        launcher = launch_gui(launcher_path, environment, output)
        process_group = launcher.pid
        launcher_identity = bind_any_process(launcher.pid, process_group)
        identities.append(launcher_identity)
        gui = find_bound_descendant(launcher.pid, gui_hash, process_group, min(total_deadline, time.monotonic() + timeouts["startup"]))
        identities.append(gui)
        operator_environment = {key: value for key, value in environment.items() if not key.startswith("SMAPI_LINUX_GUI_HARD_STATE_") and key != "LD_PRELOAD"}
        route, restart_route = qualification_routes(scenario)
        capture_coordinator = CaptureCoordinator(
            contract, output, package_root, game, before, gui, gui_hash, backend_hash,
            operator_environment,
        )
        operator = AtspiSession(
            route, gui, gui_hash, control, output, operator_environment, "operation",
            total_deadline, None if scenario == "E6" else capture_coordinator,
        )
        advance_operation_to_confirmation(operator, total_deadline, package.parent, game)
        backend = find_bound_descendant(gui.pid, backend_hash, process_group, min(total_deadline, time.monotonic() + timeouts["startup"]))
        identities.append(backend)
        bind_exact_app_tree(launcher_identity, [gui, backend])
        if barrier is not None:
            private_file(pid_file, f"{backend.pid}\n".encode("ascii"))
        boundary_session.arm(min(total_deadline, time.monotonic() + timeouts["operation"]))
        boundary_armed_observed = True
        operator.advance("execution.run", total_deadline, package.parent, game)
        barrier_observed = False
        if barrier_server is not None:
            barrier_server.wait(backend, min(total_deadline, time.monotonic() + timeouts["operation"]))
            barrier_observed = True
            capture_coordinator.barrier_observed = True
            if scenario in ("C2", "C3"):
                if operator is None:
                    fail("operator")
                operator.advance("execution.cancel", total_deadline, package.parent, game)
                operator.advance("state.c2", total_deadline, package.parent, game)
                accessible_state_observed = True
                barrier_server.release()
                operator.advance("terminal.c3", total_deadline, package.parent, game)
                operator.complete(total_deadline)
                operator = None
            else:
                # The durable Applied record is complete and synced. Kill only the exact backend;
                # keeping the GUI alive lets the product truthfully surface backend loss for E5.
                bind_exact_app_tree(launcher_identity, [gui, backend])
                pidfd_signal(backend, signal.SIGKILL)
                settle = min(total_deadline, time.monotonic() + timeouts["settlement"])
                while identity_matches(backend) and time.monotonic() < settle:
                    time.sleep(0.05)
                if identity_matches(backend):
                    fail("cleanup")
                capture_coordinator.backend_loss_observed = True
                if operator is None:
                    fail("operator")
                operator.advance("state.e5", total_deadline, package.parent, game)
                accessible_state_observed = True
                operator.complete(total_deadline)
                operator = None

                if scenario == "E6":
                    interrupted, interrupted_digest = inventory(game)
                    write_private_json(
                        output / "inventory-interrupted.json",
                        {"digest": interrupted_digest, "entries": interrupted},
                    )
                    # The fresh ordinary flow performs automatic recovery; never invent a recovery action.
                    if not cleanup_processes(launcher, identities, min(total_deadline, time.monotonic() + timeouts["cleanup"])):
                        fail("cleanup")
                    launcher = None
                    identities.clear()
                    restart_root = output / "restart"
                    private_directory(restart_root)
                    restart_environment = minimal_environment(restart_root, None, game, control, pid_file)
                    launcher = launch_gui(launcher_path, restart_environment, restart_root)
                    process_group = launcher.pid
                    launcher_identity = bind_any_process(launcher.pid, process_group)
                    identities.append(launcher_identity)
                    gui = find_bound_descendant(launcher.pid, gui_hash, process_group, min(total_deadline, time.monotonic() + timeouts["startup"]))
                    identities.append(gui)
                    operator_environment = {
                        key: value for key, value in restart_environment.items()
                        if not key.startswith("SMAPI_LINUX_GUI_HARD_STATE_") and key != "LD_PRELOAD"
                    }
                    if restart_route is None:
                        fail("operator")
                    capture_coordinator.rebind_fresh_session(gui, operator_environment)
                    operator = AtspiSession(
                        restart_route, gui, gui_hash, control, output,
                        operator_environment, "automatic-recovery", total_deadline,
                        capture_coordinator,
                    )
                    advance_operation_to_confirmation(operator, total_deadline, package.parent, game)
                    backend = find_bound_descendant(
                        gui.pid, backend_hash, process_group,
                        min(total_deadline, time.monotonic() + timeouts["startup"]),
                    )
                    identities.append(backend)
                    bind_exact_app_tree(launcher_identity, [gui, backend])
                    operator.advance("execution.run", total_deadline, package.parent, game)
                    operator.advance("terminal.e6", total_deadline, package.parent, game)
                    operator.complete(total_deadline)
                    operator = None
        elif scenario in E2_SCENARIOS:
            if operator is None:
                fail("operator")
            operator.advance(f"state.{scenario.casefold()}", total_deadline, package.parent, game)
            accessible_state_observed = True
            operator.complete(total_deadline)
            operator = None
        after, after_digest = inventory(
            game,
            allow_cross_device=scenario == "E2-cross-device",
            opaque_paths=frozenset({"smapi-internal"}) if scenario == "E2-permission" else frozenset(),
        )
        write_private_json(output / "inventory-after.json", {"digest": after_digest, "entries": after})
        package_hash_after, _ = hash_regular(package, 4 * 1024 * 1024 * 1024)
        gui_hash_after, _ = hash_regular(gui_path, MAX_EXECUTABLE_BYTES, require_executable=True)
        backend_hash_after, _ = hash_regular(backend_path, MAX_EXECUTABLE_BYTES, require_executable=True)
        if (package_hash_after, gui_hash_after, backend_hash_after) != (package_hash_before, gui_hash, backend_hash):
            fail("identity")
        if scenario in ("C2", "C3", "E6"):
            if restoration_digest(before) != restoration_digest(after):
                fail("inventory")
            inventory_verified = True
        elif scenario in E2_SCENARIOS and e2_terminal_digest(scenario, before) != e2_terminal_digest(scenario, after):
            fail("inventory")
        cleanup_complete = cleanup_processes(
            launcher, identities, min(total_deadline, time.monotonic() + timeouts["cleanup"]),
        )
        if not cleanup_complete:
            fail("cleanup")
        launcher = None
        boundary_session.cleanup(min(total_deadline, time.monotonic() + timeouts["cleanup"]))
        boundary_cleaned_observed = True
        restored, restored_digest = inventory(game)
        write_private_json(output / "inventory-restored.json", {"digest": restored_digest, "entries": restored})
        if scenario in E2_SCENARIOS:
            expected = underlying_before if scenario in ("E2-read-only", "E2-disk-full") else before
            if e2_restored_digest(scenario, expected) != e2_restored_digest(scenario, restored):
                fail("inventory")
            inventory_verified = True
        if capture_coordinator is None:
            fail("capture")
        expected_before = (
            underlying_before
            if scenario in ("E2-read-only", "E2-disk-full") else before
        )
        durable_after = capture_coordinator.verify_after(expected_before, restored)
        enforce_output_bound(output)
        return {
            "evidenceId": capture_coordinator.spec.evidence_id.value,
            "fault": (
                None if capture_coordinator.spec.fault is None
                else capture_coordinator.spec.fault.value
            ),
            "environmentProfile": capture_coordinator.profile.profile_id.value,
            "visibleState": capture_coordinator.spec.visible_state.value,
            "durableAtCapture": capture_coordinator.durable_at_capture,
            "durableAfter": durable_after,
            "atspiEvidenceRecorded": accessible_state_observed,
            "cleanupComplete": True,
            "exactWindowCaptured": capture_coordinator.captured,
            "durableClassificationVerified": inventory_verified or scenario == "E5",
            "packageIdentityReverified": True,
        }
    finally:
        if operator is not None:
            operator.close(min(total_deadline, time.monotonic() + timeouts["cleanup"]), require_success=False)
        if launcher is not None:
            cleanup_processes(launcher, identities, min(total_deadline, time.monotonic() + timeouts["cleanup"]))
        if barrier_server is not None:
            barrier_server.close()
        if not boundary_cleaned_observed:
            try:
                boundary_session.cleanup(min(total_deadline, time.monotonic() + timeouts["cleanup"]))
            except QualificationError:
                boundary_session.close()


def success_aggregate(contract: dict[str, Any], status: str, details: dict[str, Any] | None = None) -> dict[str, Any]:
    result: dict[str, Any] = {
        "kind": "linux-gui-hard-state-qualification",
        "ok": True,
        "scenario": contract["scenario"],
        "schemaVersion": SCHEMA_VERSION,
        "status": status,
    }
    if status == "captured_pending_privacy_and_public_authority":
        result.update({
            "releaseTag": contract["release"]["tag"],
            "sourceCommit": contract["release"]["expected_commit"],
            "sourceTree": contract["release"]["expected_tree"],
            "publicReleaseUrl": contract["release"]["url"],
            "packageSha256": contract["package"]["sha256"],
            "guiSha256": contract["binaries"]["apphost_sha256"],
            "backendSha256": contract["binaries"]["backend_sha256"],
        })
        result.update(details or {})
    return result


def failure_aggregate(code: str) -> dict[str, Any]:
    return {
        "code": code if code in FAILURE_CODES else "internal",
        "kind": "linux-gui-hard-state-qualification",
        "ok": False,
        "schemaVersion": SCHEMA_VERSION,
        "status": "failed",
    }


class SilentArgumentParser(argparse.ArgumentParser):
    def error(self, _message: str) -> NoReturn:
        fail("usage")


def parse_cli(arguments: list[str]) -> argparse.Namespace:
    parser = SilentArgumentParser(add_help=False)
    parser.add_argument("--contract")
    parser.add_argument("--contract-fd", type=int)
    parser.add_argument("--output")
    parser.add_argument("--broker-fd", type=int)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--execute", action="store_true")
    mode.add_argument("--admission-only", action="store_true")
    parsed = parser.parse_args(arguments)
    if parsed.output is None:
        fail("usage")
    if (
        parsed.execute and (
            parsed.contract is not None or parsed.contract_fd is None
            or not 3 <= parsed.contract_fd <= 1024
            or parsed.broker_fd is None or not 3 <= parsed.broker_fd <= 1024
            or parsed.contract_fd == parsed.broker_fd
        )
    ) or (
        parsed.admission_only and (
            parsed.contract is None or parsed.contract_fd is not None or parsed.broker_fd is not None
        )
    ):
        fail("usage")
    for value in ((parsed.contract,) if parsed.contract is not None else ()) + (parsed.output,):
        path = Path(value)
        if not path.is_absolute() or os.fspath(path) != value or ".." in path.parts:
            fail("usage")
    return parsed


def main(arguments: list[str]) -> int:
    try:
        parsed = parse_cli(arguments)
        validator = load_validator()
        try:
            contract = (
                validator.parse_contract_bytes(read_sealed_bytes(parsed.contract_fd, 64 * 1024))
                if parsed.execute
                else validator.read_contract(Path(parsed.contract))
            )
            scenario = validator.validate_contract(
                contract,
                Path(parsed.output),
                require_prepared_output=parsed.execute,
            )
        except getattr(validator, "InputError", Exception):
            fail("admission")
        if contract.get("scenario") != scenario:
            fail("admission")
        if parsed.admission_only:
            emit(success_aggregate(contract, "admitted"))
            return 0
        broker_channel = BrokerChannel(parsed.broker_fd)
        try:
            details = execute_case(contract, Path(parsed.output), broker_channel)
        finally:
            broker_channel.close()
        emit(success_aggregate(contract, "captured_pending_privacy_and_public_authority", details))
        return 0
    except QualificationError as error:
        emit(failure_aggregate(error.code))
        return 2
    except KeyboardInterrupt:
        emit(failure_aggregate("timeout"))
        return 130
    except BaseException:
        emit(failure_aggregate("internal"))
        return 70


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
