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


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
VALIDATOR_PATH = REPOSITORY_ROOT / "build/scripts/validate-linux-gui-hard-state-inputs.py"
BARRIER_SOURCE = REPOSITORY_ROOT / "build/scripts/linux-gui-hard-state-barrier.c"
OPERATOR_HELPER = REPOSITORY_ROOT / "build/scripts/drive-linux-gui-hard-states-atspi.py"
CONTROLLER_HELPER = REPOSITORY_ROOT / "build/scripts/arm-linux-gui-hard-state-boundary.py"
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


@dataclass(frozen=True)
class ProcessIdentity:
    pid: int
    start_time: int
    process_group: int
    executable_device: int
    executable_inode: int
    executable_sha256: str


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
        return ProcessIdentity(pid, started, process_group, executable.st_dev, executable.st_ino, digest)

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
    return ProcessIdentity(pid, start_time, process_group, metadata.st_dev, metadata.st_ino, digest)


def bind_any_process(pid: int, expected_group: int) -> ProcessIdentity:
    digest, _ = hash_proc_executable(pid)
    return bind_process(pid, digest, expected_group)


def identity_matches(identity: ProcessIdentity) -> bool:
    try:
        process_group, start_time = proc_stat(identity.pid)
        if (process_group, start_time) != (identity.process_group, identity.start_time):
            return False
        executable = os.stat(f"/proc/{identity.pid}/exe")
        return (executable.st_dev, executable.st_ino) == (identity.executable_device, identity.executable_inode)
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
                peer_executable.st_dev, peer_executable.st_ino, peer_digest,
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
        first = self.verify(self.receive(deadline), response_keys)
        if milestone in OBSERVATION_MILESTONES:
            capture_ready = {
                "type": "capture-ready", "version": 1, "session": self.session_id,
                "sequence": self.sequence, "milestone": milestone,
            }
            if first != capture_ready or not identity_matches(self.gui):
                fail("operator")
            self.observation_count += 1
            write_private_json(
                self.output / f"atspi-{self.suffix}-observation-{self.sequence:02d}.json",
                {"milestone": milestone, "schemaVersion": 1, "sequence": self.sequence},
            )
            self.send({**capture_ready, "type": "continue"})
            reached = self.verify(self.receive(deadline), response_keys)
        else:
            reached = first
        if reached != {
            "type": "reached", "version": 1, "session": self.session_id,
            "sequence": self.sequence, "milestone": milestone,
        }:
            fail("operator")
        self.sequence += 1

    def complete(self, deadline: float) -> None:
        if self.sequence != len(AT_SPI_ROUTES[self.route]):
            fail("operator")
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
    for key in ("DISPLAY", "WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS", "AT_SPI_BUS_ADDRESS", "XAUTHORITY", "LANG", "LC_ALL"):
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
        operator = AtspiSession(route, gui, gui_hash, control, output, operator_environment, "operation", total_deadline)
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
                    operator = AtspiSession(
                        restart_route, gui, gui_hash, control, output,
                        operator_environment, "automatic-recovery", total_deadline,
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
        enforce_output_bound(output)
        return {
            "atspiActionObserved": True,
            "accessibleStateObserved": accessible_state_observed,
            "barrierObserved": barrier_observed,
            "boundaryArmedObserved": boundary_armed_observed,
            "boundaryCleanedObserved": boundary_cleaned_observed,
            "cleanupComplete": True,
            "exactWindowCaptured": False,
            "inventoryVerified": inventory_verified,
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
    if status == "preflighted":
        result.update({
            "releaseTag": contract["release"]["tag"],
            "sourceCommit": contract["release"]["expected_commit"],
            "sourceTree": contract["release"]["expected_tree"],
            "publicReleaseUrl": contract["release"]["url"],
            "packageSha256": contract["package"]["sha256"],
            "guiSha256": contract["binaries"]["apphost_sha256"],
            "backendSha256": contract["binaries"]["backend_sha256"],
            "capturePending": True,
            "durableClassificationPending": True,
            "publicAuthorityVerificationPending": True,
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
            scenario = validator.validate_contract(contract, Path(parsed.output))
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
        emit(success_aggregate(contract, "preflighted", details))
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
