#!/usr/bin/env python3
"""Privileged disposable-VM broker for one nonroot Linux GUI hard-state preflight."""

from __future__ import annotations

import argparse
import array
import ctypes
import fcntl
import hashlib
import json
import os
from pathlib import Path
import pwd
import re
import resource
import signal
import socket
import stat
import subprocess
import sys
import time
from typing import Any, NoReturn


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
SUPERVISOR = REPOSITORY_ROOT / "build/scripts/qualify-linux-gui-hard-states.py"
CONTROLLER = REPOSITORY_ROOT / "build/scripts/arm-linux-gui-hard-state-boundary.py"
CGROUP_ROOT = Path("/sys/fs/cgroup")
REQUEST_NAME = "hard-state-boundary-request.json"
MAX_CONTRACT_BYTES = 64 * 1024
MAX_RESULT_BYTES = 16 * 1024
MAX_REQUEST_BYTES = 32 * 1024
MAX_LEDGER_BYTES = 64 * 1024 * 1024
MAX_MOUNTINFO_BYTES = 4 * 1024 * 1024
MAX_SYSFS_BYTES = 4096
MAX_OUTPUT_ENTRIES = 65536
OUTPUT_BYTES_LIMIT = 1024 * 1024 * 1024
OUTPUT_QUOTA_ENV = "SMAPI_HARD_STATE_OUTPUT_QUOTA_TOKEN"
OUTPUT_QUOTA_MARKER = ".smapi-hard-state-output-quota-v1.json"
OUTPUT_QUOTA_PURPOSE = "smapi-hard-state-output-quota"
OUTPUT_QUOTA_IMAGE = ".smapi-hard-state-output-quota"
BLKGETSIZE64 = 0x80081272
FS_IOC_GETFLAGS = 0x80086601
FS_IOC_SETFLAGS = 0x40086602
FS_IMMUTABLE_FL = 0x00000010
SCHEMA_VERSION = 2
SAFE_COMPONENT = re.compile(r"^[a-z0-9][a-z0-9._-]{7,127}$")
PR_SET_NO_NEW_PRIVS = 38
PR_CAP_AMBIENT = 47
PR_CAP_AMBIENT_CLEAR_ALL = 4
LINUX_CAPABILITY_VERSION_3 = 0x20080522
FAILURE_CODES = frozenset({
    "usage", "admission", "identity", "package", "extraction", "fixture", "inventory",
    "boundary", "operator", "barrier", "timeout", "state", "capture", "cleanup", "internal",
})
SCENARIOS = frozenset({"E2-permission", "E2-read-only", "E2-disk-full", "E2-cross-device", "C2", "C3", "E5", "E6"})
HEX_40 = re.compile(r"^[0-9a-f]{40}$")
HEX_64 = re.compile(r"^[0-9a-f]{64}$")
TAG = re.compile(r"^fork-4eh5xitv6787h645ebv-linux-v[0-9]+\.[0-9]+\.[0-9]+-alpha\.[1-9][0-9]*$")
FAILED_KEYS = frozenset({"code", "kind", "ok", "schemaVersion", "status"})
CASE_KEYS = frozenset({
    "kind", "ok", "scenario", "schemaVersion", "status", "releaseTag", "sourceCommit",
    "sourceTree", "publicReleaseUrl", "packageSha256", "guiSha256", "backendSha256",
    "evidenceId", "fault", "environmentProfile", "visibleState", "durableAtCapture",
    "durableAfter", "exactWindowCaptured", "atspiEvidenceRecorded",
    "durableClassificationVerified", "cleanupComplete", "packageIdentityReverified",
})
TRUE_CASE_KEYS = frozenset({
    "exactWindowCaptured", "atspiEvidenceRecorded", "durableClassificationVerified",
    "cleanupComplete", "packageIdentityReverified",
})
ENVIRONMENT_PROFILES = frozenset({
    "ubuntu-24.04-gnome-x11", "ubuntu-24.04-gnome-xwayland",
    "ubuntu-24.04-kde-x11", "ubuntu-24.04-kde-xwayland",
})
CASE_EXPECTED = {
    "E2-permission": ("E2", "permission", "install-failed-unchanged", "unchanged", "unchanged"),
    "E2-read-only": ("E2", "read-only", "install-failed-unchanged", "unchanged", "unchanged"),
    "E2-disk-full": ("E2", "disk-full", "install-failed-unchanged", "unchanged", "unchanged"),
    "E2-cross-device": ("E2", "cross-device", "install-failed-rolled-back", "rolled-back", "rolled-back"),
    "C2": ("C2", None, "cancellation-finishing-safely", "applied", "rolled-back"),
    "C3": ("C3", None, "cancelled-and-rolled-back", "rolled-back", "rolled-back"),
    "E5": ("E5", None, "backend-state-unknown-recovery-required", "recovery-required", "recovery-required"),
    "E6": ("E6", None, "automatic-recovery-completed-fresh-inspection-required", "recovery-completed", "recovery-completed"),
}
REQUIRED_MEMFD_SEALS = (
    fcntl.F_SEAL_WRITE | fcntl.F_SEAL_GROW | fcntl.F_SEAL_SHRINK | fcntl.F_SEAL_SEAL
)


class BrokerError(Exception):
    pass


def no_duplicate_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise BrokerError()
        value[key] = item
    return value


class CapabilityHeader(ctypes.Structure):
    _fields_ = [("version", ctypes.c_uint32), ("pid", ctypes.c_int)]


class CapabilityData(ctypes.Structure):
    _fields_ = [
        ("effective", ctypes.c_uint32),
        ("permitted", ctypes.c_uint32),
        ("inheritable", ctypes.c_uint32),
    ]


def process_cgroup(pid: int) -> str:
    try:
        lines = Path(f"/proc/{pid}/cgroup").read_text(encoding="ascii").splitlines()
    except (OSError, UnicodeError):
        raise BrokerError() from None
    if len(lines) != 1 or not lines[0].startswith("0::"):
        raise BrokerError()
    value = lines[0][3:]
    path = Path(value)
    if not value.startswith("/") or ".." in path.parts:
        raise BrokerError()
    return value


def validate_cgroup2_mount(root: Path) -> None:
    try:
        matches = []
        for line in _read_bounded_pseudo(
            Path("/proc/self/mountinfo"), MAX_MOUNTINFO_BYTES, "utf-8",
        ).splitlines():
            before, separator, after = line.partition(" - ")
            fields = before.split()
            filesystem = after.split()
            if separator and len(fields) >= 6 and len(filesystem) >= 1 and fields[4] == os.fspath(root):
                matches.append(filesystem[0])
        metadata = root.lstat()
        if (
            matches != ["cgroup2"] or root.resolve(strict=True) != root
            or not stat.S_ISDIR(metadata.st_mode) or metadata.st_uid != 0
            or metadata.st_mode & 0o022
        ):
            raise BrokerError()
    except (OSError, UnicodeError):
        raise BrokerError() from None


class CgroupScope:
    """Exact cgroup-v2 containment for every detached process in one qualification."""

    def __init__(self, _run_uid: int, root: Path = CGROUP_ROOT, *, validate_mount: bool = True):
        if validate_mount:
            validate_cgroup2_mount(root)
        base = root
        try:
            if base.resolve(strict=True) != base or root not in (base, *base.parents):
                raise BrokerError()
            base_metadata = base.lstat()
            if (
                not stat.S_ISDIR(base_metadata.st_mode) or base_metadata.st_uid != 0
                or base_metadata.st_mode & 0o022
            ):
                raise BrokerError()
            self.base_fd = os.open(base, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
            self.name = ""
            for _attempt in range(8):
                candidate = f"smapi-hard-state-{os.urandom(12).hex()}"
                try:
                    os.mkdir(candidate, 0o700, dir_fd=self.base_fd)
                    self.name = candidate
                    break
                except FileExistsError:
                    continue
            if not self.name:
                raise BrokerError()
            self.fd = os.open(
                self.name,
                os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
                dir_fd=self.base_fd,
            )
            os.chmod(self.name, 0o700, dir_fd=self.base_fd, follow_symlinks=False)
            metadata = os.fstat(self.fd)
            if not stat.S_ISDIR(metadata.st_mode) or metadata.st_uid != 0 or stat.S_IMODE(metadata.st_mode) != 0o700:
                raise BrokerError()
            self.identity = (metadata.st_dev, metadata.st_ino)
            self.relative = f"/{self.name}"
            for control in ("cgroup.procs", "cgroup.events", "cgroup.kill"):
                access = os.O_RDONLY if control == "cgroup.events" else os.O_WRONLY
                descriptor = os.open(control, access | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=self.fd)
                try:
                    control_metadata = os.fstat(descriptor)
                    if not stat.S_ISREG(control_metadata.st_mode) or control_metadata.st_uid != 0:
                        raise BrokerError()
                finally:
                    os.close(descriptor)
        except BrokerError:
            self._abandon()
            raise
        except OSError:
            self._abandon()
            raise BrokerError() from None

    def _abandon(self) -> None:
        descriptor = getattr(self, "fd", -1)
        if descriptor >= 0:
            os.close(descriptor)
            self.fd = -1
        base_descriptor = getattr(self, "base_fd", -1)
        name = getattr(self, "name", "")
        if base_descriptor >= 0 and name:
            try:
                os.rmdir(name, dir_fd=base_descriptor)
            except OSError:
                pass
        if base_descriptor >= 0:
            os.close(base_descriptor)
            self.base_fd = -1

    def validate(self) -> None:
        if self.fd < 0:
            raise BrokerError()
        metadata = os.fstat(self.fd)
        current = os.stat(self.name, dir_fd=self.base_fd, follow_symlinks=False)
        if (
            (metadata.st_dev, metadata.st_ino) != self.identity
            or (current.st_dev, current.st_ino) != self.identity
            or not stat.S_ISDIR(current.st_mode) or current.st_uid != 0
            or stat.S_IMODE(current.st_mode) != 0o700
        ):
            raise BrokerError()

    def _write(self, control: str, value: bytes) -> None:
        self.validate()
        descriptor = -1
        try:
            descriptor = os.open(control, os.O_WRONLY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=self.fd)
            metadata = os.fstat(descriptor)
            if not stat.S_ISREG(metadata.st_mode) or metadata.st_uid != 0:
                raise BrokerError()
            if os.write(descriptor, value) != len(value):
                raise BrokerError()
        except BrokerError:
            raise
        except OSError:
            raise BrokerError() from None
        finally:
            if descriptor >= 0:
                os.close(descriptor)

    def _populated(self) -> bool:
        self.validate()
        descriptor = -1
        try:
            descriptor = os.open("cgroup.events", os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=self.fd)
            raw = os.read(descriptor, 4097)
            if len(raw) > 4096:
                raise BrokerError()
            fields: dict[str, str] = {}
            for line in raw.decode("ascii").splitlines():
                key, separator, value = line.partition(" ")
                if not separator or key in fields:
                    raise BrokerError()
                fields[key] = value
            if fields.get("populated") not in ("0", "1"):
                raise BrokerError()
            return fields["populated"] == "1"
        except BrokerError:
            raise
        except (OSError, UnicodeError):
            raise BrokerError() from None
        finally:
            if descriptor >= 0:
                os.close(descriptor)

    def join_current(self) -> None:
        self._write("cgroup.procs", f"{os.getpid()}\n".encode("ascii"))
        if process_cgroup(os.getpid()) != self.relative:
            os._exit(126)

    def kill_and_remove(self, deadline: float) -> None:
        if self.fd < 0:
            raise BrokerError()
        self._write("cgroup.kill", b"1\n")
        while self._populated() and time.monotonic() < deadline:
            time.sleep(0.02)
        if self._populated():
            raise BrokerError()
        self.validate()
        os.close(self.fd)
        self.fd = -1
        try:
            os.rmdir(self.name, dir_fd=self.base_fd)
        except OSError:
            raise BrokerError() from None
        os.close(self.base_fd)
        self.base_fd = -1


class SilentParser(argparse.ArgumentParser):
    def error(self, _message: str) -> NoReturn:
        raise BrokerError()


def emit_failure() -> None:
    payload = {
        "code": "broker", "kind": "linux-gui-hard-state-qualification",
        "ok": False, "schemaVersion": SCHEMA_VERSION, "status": "failed",
    }
    os.write(sys.stdout.fileno(), (json.dumps(payload, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii"))


def parse_arguments(arguments: list[str]) -> tuple[Path, Path]:
    parser = SilentParser(add_help=False)
    parser.add_argument("--contract", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--execute", action="store_true", required=True)
    values = parser.parse_args(arguments)
    paths = []
    for raw in (values.contract, values.output):
        path = Path(raw)
        if not path.is_absolute() or os.fspath(path) != raw or ".." in path.parts:
            raise BrokerError()
        paths.append(path)
    return paths[0], paths[1]


def fixed_file_hash(path: Path, maximum: int) -> str:
    descriptor = -1
    try:
        if path.resolve(strict=True) != path:
            raise BrokerError()
        descriptor = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode) or before.st_nlink != 1
            or before.st_uid != 0 or before.st_size <= 0 or before.st_size > maximum
            or before.st_mode & 0o022
        ):
            raise BrokerError()
        digest = hashlib.sha256()
        while block := os.read(descriptor, 1024 * 1024):
            digest.update(block)
        after = os.fstat(descriptor)
        fields = ("st_dev", "st_ino", "st_mode", "st_uid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
        if any(getattr(before, field) != getattr(after, field) for field in fields):
            raise BrokerError()
        return digest.hexdigest()
    except BrokerError:
        raise
    except OSError:
        raise BrokerError() from None
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def admitted_primary_gid(uid: int, contract_gid: int, root_gid: int) -> int:
    try:
        primary_gid = pwd.getpwuid(uid).pw_gid
    except KeyError:
        raise BrokerError() from None
    if uid <= 0 or primary_gid <= 0 or contract_gid != primary_gid or root_gid != primary_gid:
        raise BrokerError()
    return primary_gid


def read_bootstrap(contract_path: Path, output: Path) -> tuple[dict[str, Any], bytes, Path, int, int, int]:
    descriptor = -1
    try:
        descriptor = os.open(contract_path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
        metadata = os.fstat(descriptor)
        if (
            not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1
            or metadata.st_uid == 0 or stat.S_IMODE(metadata.st_mode) != 0o600
            or metadata.st_size <= 0 or metadata.st_size > MAX_CONTRACT_BYTES
        ):
            raise BrokerError()
        raw = bytearray()
        while len(raw) <= MAX_CONTRACT_BYTES:
            block = os.read(descriptor, min(4096, MAX_CONTRACT_BYTES + 1 - len(raw)))
            if not block:
                break
            raw.extend(block)
        after = os.fstat(descriptor)
        fields = ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
        if (
            len(raw) != metadata.st_size or len(raw) > MAX_CONTRACT_BYTES
            or any(getattr(metadata, field) != getattr(after, field) for field in fields)
        ):
            raise BrokerError()
        contract = json.loads(
            bytes(raw).decode("utf-8"), object_pairs_hook=no_duplicate_object,
        )
        canonical = (json.dumps(contract, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
        if bytes(raw) != canonical:
            raise BrokerError()
    except (OSError, UnicodeError, json.JSONDecodeError, BrokerError):
        raise BrokerError() from None
    finally:
        if descriptor >= 0:
            os.close(descriptor)
    try:
        root = Path(contract["isolation"]["disposable_root"])
        total = contract["timeouts_seconds"]["total"]
        if (
            not root.is_absolute() or str(root) != contract["isolation"]["disposable_root"]
            or output.parent != root or SAFE_COMPONENT.fullmatch(root.name) is None
            or SAFE_COMPONENT.fullmatch(output.name) is None
            or isinstance(total, bool) or not isinstance(total, int) or not 25 <= total <= 1800
        ):
            raise BrokerError()
        prefix_status = root.parent.lstat()
        root_status = root.lstat()
        if (
            root.parent.resolve(strict=True) != root.parent
            or root.resolve(strict=True) != root
            or not stat.S_ISDIR(prefix_status.st_mode) or prefix_status.st_uid != 0
            or stat.S_IMODE(prefix_status.st_mode) != 0o711
            or not stat.S_ISDIR(root_status.st_mode) or root_status.st_uid != metadata.st_uid
            or stat.S_IMODE(root_status.st_mode) != 0o700
        ):
            raise BrokerError()
        primary_gid = admitted_primary_gid(metadata.st_uid, metadata.st_gid, root_status.st_gid)
    except (KeyError, TypeError, OSError):
        raise BrokerError() from None
    return contract, bytes(raw), root, metadata.st_uid, primary_gid, total


def acquire_root_lock(root: Path, run_uid: int, run_gid: int) -> tuple[int, tuple[int, int]]:
    """Hold an exclusive inode lock so one disposable case root has exactly one broker."""
    descriptor = -1
    try:
        descriptor = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
        opened = os.fstat(descriptor)
        named = root.lstat()
        if (
            root.resolve(strict=True) != root
            or not stat.S_ISDIR(opened.st_mode) or not stat.S_ISDIR(named.st_mode)
            or opened.st_uid != run_uid or opened.st_gid != run_gid
            or stat.S_IMODE(opened.st_mode) != 0o700
            or (opened.st_dev, opened.st_ino) != (named.st_dev, named.st_ino)
        ):
            raise BrokerError()
        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        repeated = os.fstat(descriptor)
        current = root.lstat()
        if (
            (repeated.st_dev, repeated.st_ino) != (opened.st_dev, opened.st_ino)
            or (current.st_dev, current.st_ino) != (opened.st_dev, opened.st_ino)
        ):
            raise BrokerError()
        return descriptor, (opened.st_dev, opened.st_ino)
    except BrokerError:
        if descriptor >= 0:
            os.close(descriptor)
        raise
    except OSError:
        if descriptor >= 0:
            os.close(descriptor)
        raise BrokerError() from None


def sealed_memfd(name: str, content: bytes, maximum: int) -> int:
    """Copy admitted bytes to an immutable anonymous file and verify every required seal."""
    if (
        not hasattr(os, "memfd_create") or not content or len(content) > maximum
        or not re.fullmatch(r"[a-z0-9-]{1,48}", name)
    ):
        raise BrokerError()
    descriptor = -1
    try:
        descriptor = os.memfd_create(name, os.MFD_CLOEXEC | os.MFD_ALLOW_SEALING)
        view = memoryview(content)
        while view:
            written = os.write(descriptor, view)
            if written <= 0:
                raise BrokerError()
            view = view[written:]
        os.fsync(descriptor)
        fcntl.fcntl(descriptor, fcntl.F_ADD_SEALS, REQUIRED_MEMFD_SEALS)
        if fcntl.fcntl(descriptor, fcntl.F_GET_SEALS) != REQUIRED_MEMFD_SEALS:
            raise BrokerError()
        metadata = os.fstat(descriptor)
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_size != len(content) or metadata.st_nlink != 0:
            raise BrokerError()
        os.lseek(descriptor, 0, os.SEEK_SET)
        return descriptor
    except BrokerError:
        if descriptor >= 0:
            os.close(descriptor)
        raise
    except (AttributeError, OSError):
        if descriptor >= 0:
            os.close(descriptor)
        raise BrokerError() from None


def child_environment(uid: int) -> dict[str, str]:
    result = {"PATH": "/usr/bin:/bin", "LANG": "C.UTF-8"}
    for name in ("DISPLAY", "WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS", "AT_SPI_BUS_ADDRESS", "XAUTHORITY"):
        value = os.environ.get(name)
        if value is not None:
            result[name] = value
    session_type = os.environ.get("XDG_SESSION_TYPE")
    if session_type is not None:
        if session_type not in {"x11", "wayland"}:
            raise BrokerError()
        result["XDG_SESSION_TYPE"] = session_type
    current_desktop = os.environ.get("XDG_CURRENT_DESKTOP")
    if current_desktop is not None:
        if current_desktop not in {"GNOME", "ubuntu:GNOME", "KDE", "KDE:Plasma"}:
            raise BrokerError()
        result["XDG_CURRENT_DESKTOP"] = current_desktop
    runtime = Path(f"/run/user/{uid}")
    try:
        metadata = runtime.lstat()
        if (
            runtime.resolve(strict=True) != runtime or not stat.S_ISDIR(metadata.st_mode)
            or metadata.st_uid != uid or stat.S_IMODE(metadata.st_mode) != 0o700
        ):
            raise BrokerError()
    except OSError:
        raise BrokerError() from None
    result["XDG_RUNTIME_DIR"] = os.fspath(runtime)
    return result


def drop_to(uid: int, gid: int) -> None:
    os.setgroups([])
    os.setresgid(gid, gid, gid)
    os.setresuid(uid, uid, uid)
    libc = ctypes.CDLL(None, use_errno=True)
    header = CapabilityHeader(LINUX_CAPABILITY_VERSION_3, 0)
    empty = (CapabilityData * 2)()
    if (
        libc.capset(ctypes.byref(header), ctypes.byref(empty)) != 0
        or libc.prctl(PR_CAP_AMBIENT, PR_CAP_AMBIENT_CLEAR_ALL, 0, 0, 0) != 0
        or libc.prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0
    ):
        os._exit(126)


def make_namespace_private() -> None:
    if not hasattr(os, "unshare") or not hasattr(os, "CLONE_NEWNS"):
        raise BrokerError()
    os.unshare(os.CLONE_NEWNS)
    result = subprocess.run(
        ["/usr/bin/mount", "--make-rprivate", "/"],
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        env={"PATH": "/usr/bin:/bin", "LANG": "C"}, timeout=10, check=False,
    )
    if result.returncode != 0:
        raise BrokerError()


def _run_root_command(command: list[str], timeout: int = 30) -> None:
    """Run one fixed privileged filesystem command without inheriting caller-controlled state."""
    try:
        result = subprocess.run(
            command,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            env={"PATH": "/usr/sbin:/usr/bin:/bin", "LANG": "C", "LC_ALL": "C"},
            timeout=timeout,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        raise BrokerError() from None
    if result.returncode != 0:
        raise BrokerError()


def _mountinfo_unescape(value: str) -> str:
    def replace(match: re.Match[str]) -> str:
        return chr(int(match.group(1), 8))

    return re.sub(r"\\([0-7]{3})", replace, value)


def _read_bounded_pseudo(path: Path, maximum: int, encoding: str) -> str:
    descriptor = -1
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW | os.O_NONBLOCK)
        raw = bytearray()
        while len(raw) <= maximum:
            block = os.read(descriptor, min(4096, maximum + 1 - len(raw)))
            if not block:
                break
            raw.extend(block)
        if not raw or len(raw) > maximum:
            raise BrokerError()
        return bytes(raw).decode(encoding)
    except FileNotFoundError:
        raise
    except BrokerError:
        raise
    except (OSError, UnicodeError):
        raise BrokerError() from None
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def mounted_output_record(output: Path) -> tuple[str, str, frozenset[str], tuple[int, int]]:
    """Return the single exact mount record for output, rejecting aliases and ambiguity."""
    try:
        records: list[tuple[str, str, frozenset[str], tuple[int, int]]] = []
        for line in _read_bounded_pseudo(
            Path("/proc/self/mountinfo"), MAX_MOUNTINFO_BYTES, "utf-8",
        ).splitlines():
            before, separator, after = line.partition(" - ")
            fields = before.split()
            filesystem = after.split()
            if not separator or len(fields) < 6 or len(filesystem) < 3:
                continue
            if _mountinfo_unescape(fields[4]) != os.fspath(output):
                continue
            major_text, colon, minor_text = fields[2].partition(":")
            if not colon:
                raise BrokerError()
            options = frozenset(fields[5].split(",")) | frozenset(filesystem[2].split(","))
            records.append((filesystem[0], _mountinfo_unescape(filesystem[1]), options, (int(major_text), int(minor_text))))
        if len(records) != 1:
            raise BrokerError()
        return records[0]
    except BrokerError:
        raise
    except (OSError, UnicodeError, ValueError):
        raise BrokerError() from None


def _backing_file_from_sysfs(device: tuple[int, int]) -> Path:
    try:
        raw = _read_bounded_pseudo(
            Path(f"/sys/dev/block/{device[0]}:{device[1]}/loop/backing_file"),
            MAX_SYSFS_BYTES,
            "utf-8",
        ).rstrip("\n")
    except (OSError, UnicodeError):
        raise BrokerError() from None
    if not raw or "\x00" in raw:
        raise BrokerError()
    decoded = _mountinfo_unescape(raw)
    path = Path(decoded if decoded.startswith("/") else "/" + decoded)
    if not path.is_absolute() or ".." in path.parts:
        raise BrokerError()
    return path


def _loop_autoclear(device: tuple[int, int]) -> bool:
    try:
        raw = _read_bounded_pseudo(
            Path(f"/sys/dev/block/{device[0]}:{device[1]}/loop/autoclear"),
            MAX_SYSFS_BYTES,
            "ascii",
        )
        return raw.strip() == "1"
    except (OSError, UnicodeError):
        raise BrokerError() from None


def _loop_still_backs(device: tuple[int, int], image_path: Path) -> bool:
    path = Path(f"/sys/dev/block/{device[0]}:{device[1]}/loop/backing_file")
    try:
        raw = _read_bounded_pseudo(path, MAX_SYSFS_BYTES, "utf-8").rstrip("\n")
    except FileNotFoundError:
        return False
    except (OSError, UnicodeError):
        raise BrokerError() from None
    decoded = _mountinfo_unescape(raw)
    current = Path(decoded if decoded.startswith("/") else "/" + decoded)
    return current == image_path


def logical_output_bytes(root: Path, limit: int, expected_uid: int) -> int:
    """Count logical file bytes without following links or crossing the quota filesystem."""
    if isinstance(limit, bool) or not isinstance(limit, int) or limit <= 0:
        raise BrokerError()
    root_fd = -1
    descriptors: list[tuple[int, int]] = []
    try:
        root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
        root_status = os.fstat(root_fd)
        if (
            not stat.S_ISDIR(root_status.st_mode) or root_status.st_uid != expected_uid
            or stat.S_IMODE(root_status.st_mode) != 0o700
        ):
            raise BrokerError()
        descriptors.append((root_fd, 0))
        root_fd = -1
        total = 0
        entries = 0
        while descriptors:
            directory_fd, depth = descriptors.pop()
            try:
                if depth > 64:
                    raise BrokerError()
                names = os.listdir(directory_fd)
                entries += len(names)
                if entries > MAX_OUTPUT_ENTRIES:
                    raise BrokerError()
                for name in names:
                    metadata = os.stat(name, dir_fd=directory_fd, follow_symlinks=False)
                    if metadata.st_dev != root_status.st_dev or metadata.st_uid != expected_uid:
                        raise BrokerError()
                    if stat.S_ISDIR(metadata.st_mode):
                        child = os.open(
                            name,
                            os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
                            dir_fd=directory_fd,
                        )
                        opened = os.fstat(child)
                        if (
                            (opened.st_dev, opened.st_ino) != (metadata.st_dev, metadata.st_ino)
                            or opened.st_uid != expected_uid
                        ):
                            os.close(child)
                            raise BrokerError()
                        descriptors.append((child, depth + 1))
                    elif stat.S_ISREG(metadata.st_mode):
                        if metadata.st_nlink != 1 or metadata.st_size < 0:
                            raise BrokerError()
                        total += metadata.st_size
                        if total > limit:
                            raise BrokerError()
                    else:
                        raise BrokerError()
            finally:
                os.close(directory_fd)
        return total
    except BrokerError:
        raise
    except OSError:
        raise BrokerError() from None
    finally:
        if root_fd >= 0:
            os.close(root_fd)
        for descriptor, _depth in descriptors:
            os.close(descriptor)


def validate_output_mount_facts(
    filesystem: str,
    options: frozenset[str],
    device: tuple[int, int],
    source_status: os.stat_result,
    block_bytes: int,
    autoclear: bool,
    backing: Path,
    image_path: Path,
    output_status: os.stat_result,
    values: os.statvfs_result,
    run_uid: int,
    run_gid: int,
    limit: int,
) -> None:
    if (
        filesystem != "ext4" or not {"rw", "nosuid", "nodev"}.issubset(options) or "noexec" in options
        or not stat.S_ISBLK(source_status.st_mode) or source_status.st_uid != 0
        or (os.major(source_status.st_rdev), os.minor(source_status.st_rdev)) != device
        or block_bytes != limit or not autoclear or backing != image_path
        or not stat.S_ISDIR(output_status.st_mode) or output_status.st_uid != run_uid or output_status.st_gid != run_gid
        or stat.S_IMODE(output_status.st_mode) != 0o700
        or values.f_frsize <= 0 or values.f_blocks <= 0
        or values.f_frsize * values.f_blocks > limit
    ):
        raise BrokerError()


class OutputQuota:
    """A private, kernel-enforced ext4 output bound with exact, recoverable teardown."""

    def __init__(self, root: Path, output: Path, run_uid: int, run_gid: int, limit: int):
        if limit != OUTPUT_BYTES_LIMIT or output.parent != root:
            raise BrokerError()
        self.root = root
        self.output = output
        self.run_uid = run_uid
        self.run_gid = run_gid
        self.limit = limit
        self.token = os.urandom(32).hex()
        self.root_fd = -1
        self.root_identity: tuple[int, int] | None = None
        self.root_restricted = False
        self.image_fd = -1
        self.image_name = ""
        self.image_path: Path | None = None
        self.image_identity: tuple[int, int, int] | None = None
        self.output_identity: tuple[int, int] | None = None
        self.mount_device: tuple[int, int] | None = None
        self.mounted = False
        self.marker_identity: os.stat_result | None = None
        try:
            self._prepare()
        except BaseException:
            self.close(preserve=False, suppress=True)
            raise

    def _prepare(self) -> None:
        self.root_fd = os.open(self.root, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
        root_status = os.fstat(self.root_fd)
        current = self.root.lstat()
        if (
            (root_status.st_dev, root_status.st_ino) != (current.st_dev, current.st_ino)
            or root_status.st_uid != self.run_uid or root_status.st_gid != self.run_gid
            or stat.S_IMODE(root_status.st_mode) != 0o700
        ):
            raise BrokerError()
        self.root_identity = (root_status.st_dev, root_status.st_ino)
        self._restrict_root()
        try:
            os.stat(self.output.name, dir_fd=self.root_fd, follow_symlinks=False)
            raise BrokerError()
        except FileNotFoundError:
            pass
        prefix = self.root.parent
        prefix_fd = os.open(prefix, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
        try:
            prefix_status = os.fstat(prefix_fd)
            if prefix_status.st_uid != 0 or stat.S_IMODE(prefix_status.st_mode) != 0o711:
                raise BrokerError()
            for _attempt in range(8):
                name = f"{OUTPUT_QUOTA_IMAGE}-{os.urandom(12).hex()}.ext4"
                try:
                    self.image_fd = os.open(
                        name,
                        os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW,
                        0o600,
                        dir_fd=prefix_fd,
                    )
                    self.image_name = name
                    self.image_path = prefix / name
                    break
                except FileExistsError:
                    continue
            if self.image_fd < 0 or self.image_path is None:
                raise BrokerError()
            os.posix_fallocate(self.image_fd, 0, self.limit)
            os.fsync(self.image_fd)
            image_status = os.fstat(self.image_fd)
            if (
                not stat.S_ISREG(image_status.st_mode) or image_status.st_uid != 0
                or image_status.st_nlink != 1 or stat.S_IMODE(image_status.st_mode) != 0o600
                or image_status.st_size != self.limit
            ):
                raise BrokerError()
            self.image_identity = (image_status.st_dev, image_status.st_ino, image_status.st_size)
        finally:
            os.close(prefix_fd)

        _run_root_command(["/usr/sbin/mkfs.ext4", "-q", "-F", "-m", "0", os.fspath(self.image_path)], 120)
        self._validate_image()
        os.mkdir(self.output.name, 0o700, dir_fd=self.root_fd)
        os.chown(self.output.name, self.run_uid, self.run_gid, dir_fd=self.root_fd, follow_symlinks=False)
        underlying = os.stat(self.output.name, dir_fd=self.root_fd, follow_symlinks=False)
        self.output_identity = (underlying.st_dev, underlying.st_ino)
        _run_root_command([
            "/usr/bin/mount", "-t", "ext4", "-o", "loop,nosuid,nodev",
            os.fspath(self.image_path), os.fspath(self.output),
        ])
        self.mounted = True
        os.chown(self.output, self.run_uid, self.run_gid)
        os.chmod(self.output, 0o700)
        self.validate()
        self._write_marker()
        self._restore_root()

    def _restrict_root(self) -> None:
        if self.root_fd < 0 or self.root_identity is None:
            raise BrokerError()
        opened = os.fstat(self.root_fd)
        named = self.root.lstat()
        if (
            (opened.st_dev, opened.st_ino) != self.root_identity
            or (named.st_dev, named.st_ino) != self.root_identity
            or opened.st_uid != self.run_uid or opened.st_gid != self.run_gid
            or stat.S_IMODE(opened.st_mode) != 0o700
        ):
            raise BrokerError()
        os.fchmod(self.root_fd, 0o700)
        os.fchown(self.root_fd, 0, 0)
        self.root_restricted = True
        repeated = os.fstat(self.root_fd)
        if repeated.st_uid != 0 or repeated.st_gid != 0 or stat.S_IMODE(repeated.st_mode) != 0o700:
            raise BrokerError()

    def _restore_root(self) -> None:
        if not self.root_restricted:
            return
        opened = os.fstat(self.root_fd)
        named = self.root.lstat()
        if (
            self.root_identity is None
            or (opened.st_dev, opened.st_ino) != self.root_identity
            or (named.st_dev, named.st_ino) != self.root_identity
            or opened.st_uid != 0 or opened.st_gid != 0 or stat.S_IMODE(opened.st_mode) != 0o700
        ):
            raise BrokerError()
        os.fchmod(self.root_fd, 0o700)
        os.fchown(self.root_fd, self.run_uid, self.run_gid)
        self.root_restricted = False
        repeated = os.fstat(self.root_fd)
        if (
            repeated.st_uid != self.run_uid or repeated.st_gid != self.run_gid
            or stat.S_IMODE(repeated.st_mode) != 0o700
        ):
            raise BrokerError()

    def _validate_image(self) -> None:
        if self.image_fd < 0 or self.image_path is None or self.image_identity is None:
            raise BrokerError()
        opened = os.fstat(self.image_fd)
        named = self.image_path.lstat()
        current = (opened.st_dev, opened.st_ino, opened.st_size)
        if (
            current != self.image_identity
            or (named.st_dev, named.st_ino, named.st_size) != self.image_identity
            or not stat.S_ISREG(named.st_mode) or named.st_uid != 0 or named.st_nlink != 1
            or stat.S_IMODE(named.st_mode) != 0o600
        ):
            raise BrokerError()

    def validate(self) -> None:
        self._validate_image()
        filesystem, source, options, device = mounted_output_record(self.output)
        try:
            source_path = Path(source)
            source_status = source_path.stat()
            source_fd = os.open(source_path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
            try:
                size = array.array("Q", [0])
                fcntl.ioctl(source_fd, BLKGETSIZE64, size, True)
            finally:
                os.close(source_fd)
            backing = _backing_file_from_sysfs(device)
            status = self.output.lstat()
            values = os.statvfs(self.output)
            validate_output_mount_facts(
                filesystem, options, device, source_status, size[0], _loop_autoclear(device),
                backing, self.image_path, status, values, self.run_uid, self.run_gid, self.limit,
            )
        except BrokerError:
            raise
        except OSError:
            raise BrokerError() from None
        self.mount_device = device

    def _write_marker(self) -> None:
        if self.image_identity is None or self.mount_device is None:
            raise BrokerError()
        status = self.output.lstat()
        payload = {
            "filesystem": "ext4",
            "imageDevice": self.image_identity[0],
            "imageInode": self.image_identity[1],
            "imageSize": self.image_identity[2],
            "limitBytes": self.limit,
            "mountDeviceMajor": self.mount_device[0],
            "mountDeviceMinor": self.mount_device[1],
            "outputDevice": status.st_dev,
            "outputInode": status.st_ino,
            "purpose": OUTPUT_QUOTA_PURPOSE,
            "schemaVersion": 1,
            "token": self.token,
        }
        raw = (json.dumps(payload, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")
        output_fd = os.open(self.output, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
        descriptor = -1
        try:
            descriptor = os.open(
                OUTPUT_QUOTA_MARKER,
                os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW,
                0o444,
                dir_fd=output_fd,
            )
            if os.write(descriptor, raw) != len(raw):
                raise BrokerError()
            os.fsync(descriptor)
            os.fchmod(descriptor, 0o444)
            flags = array.array("I", [0])
            fcntl.ioctl(descriptor, FS_IOC_GETFLAGS, flags, True)
            flags[0] |= FS_IMMUTABLE_FL
            fcntl.ioctl(descriptor, FS_IOC_SETFLAGS, flags, True)
            self.marker_identity = os.fstat(descriptor)
            if (
                not stat.S_ISREG(self.marker_identity.st_mode) or self.marker_identity.st_uid != 0
                or self.marker_identity.st_nlink != 1 or stat.S_IMODE(self.marker_identity.st_mode) != 0o444
                or self.marker_identity.st_size != len(raw)
            ):
                raise BrokerError()
        except BrokerError:
            raise
        except OSError:
            raise BrokerError() from None
        finally:
            if descriptor >= 0:
                os.close(descriptor)
            os.close(output_fd)

    def _remove_marker(self) -> None:
        if self.marker_identity is None:
            return
        output_fd = descriptor = -1
        try:
            output_fd = os.open(self.output, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
            descriptor = os.open(OUTPUT_QUOTA_MARKER, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=output_fd)
            status = os.fstat(descriptor)
            if any(getattr(status, field) != getattr(self.marker_identity, field) for field in STABLE_NAMED_FIELDS):
                raise BrokerError()
            flags = array.array("I", [0])
            fcntl.ioctl(descriptor, FS_IOC_GETFLAGS, flags, True)
            flags[0] &= ~FS_IMMUTABLE_FL
            fcntl.ioctl(descriptor, FS_IOC_SETFLAGS, flags, True)
            cleared = os.fstat(descriptor)
            stable_after_flag_change = (
                "st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size", "st_mtime_ns",
            )
            if any(
                getattr(cleared, field) != getattr(self.marker_identity, field)
                for field in stable_after_flag_change
            ):
                raise BrokerError()
            self.marker_identity = cleared
            os.close(descriptor)
            descriptor = -1
            if not unlink_matching_at(output_fd, OUTPUT_QUOTA_MARKER, self.marker_identity):
                raise BrokerError()
            self.marker_identity = None
        except BrokerError:
            raise
        except OSError:
            raise BrokerError() from None
        finally:
            if descriptor >= 0:
                os.close(descriptor)
            if output_fd >= 0:
                os.close(output_fd)

    def _preserve(self, expected_logical_bytes: int) -> tuple[Path, tuple[int, int]]:
        retained_name = f".{self.output.name}.retained-{os.urandom(12).hex()}"
        os.mkdir(retained_name, 0o700, dir_fd=self.root_fd)
        os.chown(retained_name, self.run_uid, self.run_gid, dir_fd=self.root_fd, follow_symlinks=False)
        retained = self.root / retained_name
        retained_fd = os.open(retained_name, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=self.root_fd)
        opened = os.fstat(retained_fd)
        retained_identity = (opened.st_dev, opened.st_ino)

        def enter_nonroot() -> None:
            drop_to(self.run_uid, self.run_gid)

        try:
            result = subprocess.run(
                [
                    "/usr/bin/cp", "-R", "--no-dereference", "--one-file-system",
                    "--preserve=mode,timestamps,links", "--no-preserve=ownership",
                    os.fspath(self.output) + "/.", os.fspath(retained),
                ],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                env={"PATH": "/usr/bin:/bin", "LANG": "C", "LC_ALL": "C"},
                preexec_fn=enter_nonroot,
                timeout=120,
                check=False,
            )
            if result.returncode != 0:
                raise BrokerError()
            repeated = os.fstat(retained_fd)
            named = retained.lstat()
            if (
                (repeated.st_dev, repeated.st_ino) != retained_identity
                or (named.st_dev, named.st_ino) != retained_identity
                or repeated.st_uid != self.run_uid or repeated.st_gid != self.run_gid
                or stat.S_IMODE(repeated.st_mode) != 0o700
            ):
                raise BrokerError()
            if logical_output_bytes(retained, self.limit, self.run_uid) != expected_logical_bytes:
                raise BrokerError()
            return retained, retained_identity
        except BrokerError:
            raise
        except (OSError, subprocess.TimeoutExpired):
            raise BrokerError() from None
        finally:
            os.close(retained_fd)

    def close(self, *, preserve: bool, suppress: bool = False) -> None:
        error = False
        retained: tuple[Path, tuple[int, int]] | None = None
        if self.mounted:
            try:
                self.validate()
                self._remove_marker()
                if preserve:
                    logical_bytes = logical_output_bytes(self.output, self.limit, self.run_uid)
                    retained = self._preserve(logical_bytes)
            except BrokerError:
                error = True
            try:
                self._restrict_root()
            except (BrokerError, OSError):
                error = True
            try:
                _run_root_command(["/usr/bin/umount", os.fspath(self.output)])
                self.mounted = False
                try:
                    mounted_output_record(self.output)
                    error = True
                except BrokerError:
                    pass
                if (
                    self.mount_device is not None and self.image_path is not None
                    and _loop_still_backs(self.mount_device, self.image_path)
                ):
                    error = True
            except BrokerError:
                error = True
        if not self.mounted and self.root_fd >= 0 and self.output_identity is not None:
            try:
                current = os.stat(self.output.name, dir_fd=self.root_fd, follow_symlinks=False)
                if (current.st_dev, current.st_ino) != self.output_identity or not stat.S_ISDIR(current.st_mode):
                    raise BrokerError()
                os.rmdir(self.output.name, dir_fd=self.root_fd)
                if retained is not None:
                    retained_path, retained_identity = retained
                    current_retained = os.stat(retained_path.name, dir_fd=self.root_fd, follow_symlinks=False)
                    if (current_retained.st_dev, current_retained.st_ino) != retained_identity:
                        raise BrokerError()
                    os.rename(retained_path.name, self.output.name, src_dir_fd=self.root_fd, dst_dir_fd=self.root_fd)
                    final = os.stat(self.output.name, dir_fd=self.root_fd, follow_symlinks=False)
                    if final.st_uid != self.run_uid or final.st_gid != self.run_gid or stat.S_IMODE(final.st_mode) != 0o700:
                        raise BrokerError()
            except (BrokerError, OSError):
                error = True
        if self.image_fd >= 0:
            try:
                self._validate_image()
                os.close(self.image_fd)
                self.image_fd = -1
                if self.image_path is None or self.image_identity is None:
                    raise BrokerError()
                prefix_fd = os.open(self.image_path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
                try:
                    expected = self.image_path.lstat()
                    if (expected.st_dev, expected.st_ino, expected.st_size) != self.image_identity:
                        raise BrokerError()
                    if not unlink_matching_at(prefix_fd, self.image_path.name, expected):
                        raise BrokerError()
                finally:
                    os.close(prefix_fd)
            except (BrokerError, OSError):
                error = True
            finally:
                if self.image_fd >= 0:
                    try:
                        os.close(self.image_fd)
                    except OSError:
                        error = True
                    self.image_fd = -1
        if self.root_fd >= 0:
            try:
                self._restore_root()
            except (BrokerError, OSError):
                error = True
            try:
                os.close(self.root_fd)
            except OSError:
                error = True
            self.root_fd = -1
        if error and not suppress:
            raise BrokerError()


STABLE_NAMED_FIELDS = (
    "st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns",
)


def unlink_matching_at(directory_fd: int, name: str, expected: os.stat_result) -> bool:
    """Unlink only the exact already-opened regular object; never fall back to a broad pathname delete."""
    try:
        current = os.stat(name, dir_fd=directory_fd, follow_symlinks=False)
        if (
            not stat.S_ISREG(current.st_mode)
            or any(getattr(expected, field) != getattr(current, field) for field in STABLE_NAMED_FIELDS)
        ):
            return False
        os.unlink(name, dir_fd=directory_fd)
        os.fsync(directory_fd)
        return True
    except FileNotFoundError:
        return False
    except OSError:
        return False


def read_and_consume_request(request: Path, root: Path, output: Path, run_uid: int) -> tuple[bytes, str, int]:
    """Read and atomically consume the exact request object selected by the broker."""
    descriptor = root_fd = -1
    metadata: os.stat_result | None = None
    consumed = False
    try:
        root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
        root_metadata = os.fstat(root_fd)
        if (
            not stat.S_ISDIR(root_metadata.st_mode) or root_metadata.st_uid != run_uid
            or stat.S_IMODE(root_metadata.st_mode) != 0o700 or request.parent != root
            or request.name != REQUEST_NAME
        ):
            raise BrokerError()
        descriptor = os.open(request.name, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=root_fd)
        metadata = os.fstat(descriptor)
        if (
            not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1
            or stat.S_IMODE(metadata.st_mode) != 0o600 or metadata.st_uid != run_uid
            or metadata.st_size <= 0 or metadata.st_size > MAX_REQUEST_BYTES
        ):
            raise BrokerError()
        raw = bytearray()
        while len(raw) <= MAX_REQUEST_BYTES:
            block = os.read(descriptor, min(4096, MAX_REQUEST_BYTES + 1 - len(raw)))
            if not block:
                break
            raw.extend(block)
        after = os.fstat(descriptor)
        if (
            len(raw) != metadata.st_size or len(raw) > MAX_REQUEST_BYTES
            or any(getattr(metadata, field) != getattr(after, field) for field in STABLE_NAMED_FIELDS)
        ):
            raise BrokerError()
        value = json.loads(
            bytes(raw).decode("utf-8"), object_pairs_hook=no_duplicate_object,
        )
        canonical = (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
        if bytes(raw) != canonical:
            raise BrokerError()
        relative = value["supervisor_socket"]["relative_path"]
        expected_prefix = f"{root.name}/{output.name}/control/"
        if not isinstance(relative, str) or not relative.startswith(expected_prefix) or "/" in relative[len(expected_prefix):]:
            raise BrokerError()
        named = os.stat(request.name, dir_fd=root_fd, follow_symlinks=False)
        if any(getattr(metadata, field) != getattr(named, field) for field in STABLE_NAMED_FIELDS):
            raise BrokerError()
        os.unlink(request.name, dir_fd=root_fd)
        os.fsync(root_fd)
        consumed = True
        return bytes(raw), relative, metadata.st_ino
    except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError, BrokerError):
        raise BrokerError() from None
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        if not consumed and root_fd >= 0 and metadata is not None:
            unlink_matching_at(root_fd, request.name, metadata)
        if root_fd >= 0:
            os.close(root_fd)


def cleanup_controller_ledgers(
    prefix: Path,
    request_inode: int,
    *,
    _prefix_uid: int = 0,
    _ledger_uid: int = 0,
) -> None:
    """Remove only the two root-owned controller ledgers bound to the admitted request object."""
    if request_inode <= 0:
        return
    prefix_fd = -1
    try:
        prefix_status = prefix.lstat()
        if (
            prefix.resolve(strict=True) != prefix or not stat.S_ISDIR(prefix_status.st_mode)
            or prefix_status.st_uid != _prefix_uid or stat.S_IMODE(prefix_status.st_mode) != 0o711
        ):
            raise BrokerError()
        prefix_fd = os.open(prefix, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
        for suffix in ("json", "log"):
            name = f".smapi-hard-state-controller-{request_inode}.{suffix}"
            try:
                before = os.stat(name, dir_fd=prefix_fd, follow_symlinks=False)
            except FileNotFoundError:
                continue
            if (
                not stat.S_ISREG(before.st_mode) or before.st_uid != _ledger_uid or before.st_nlink != 1
                or stat.S_IMODE(before.st_mode) != 0o600 or before.st_size > MAX_LEDGER_BYTES
            ):
                raise BrokerError()
            descriptor = os.open(name, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=prefix_fd)
            try:
                opened = os.fstat(descriptor)
                if any(getattr(before, field) != getattr(opened, field) for field in STABLE_NAMED_FIELDS):
                    raise BrokerError()
            finally:
                os.close(descriptor)
            if not unlink_matching_at(prefix_fd, name, opened):
                raise BrokerError()
    except BrokerError:
        raise
    except OSError:
        raise BrokerError() from None
    finally:
        if prefix_fd >= 0:
            os.close(prefix_fd)


def cleanup_residual_request(root: Path, run_uid: int) -> None:
    """Remove a residual request only after capturing and rechecking its exact private identity."""
    root_fd = descriptor = -1
    try:
        root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
        root_status = os.fstat(root_fd)
        if root_status.st_uid != run_uid or stat.S_IMODE(root_status.st_mode) != 0o700:
            raise BrokerError()
        try:
            descriptor = os.open(REQUEST_NAME, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=root_fd)
        except FileNotFoundError:
            return
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode) or before.st_uid != run_uid or before.st_nlink != 1
            or stat.S_IMODE(before.st_mode) != 0o600 or not 0 < before.st_size <= MAX_REQUEST_BYTES
        ):
            raise BrokerError()
        if not unlink_matching_at(root_fd, REQUEST_NAME, before):
            raise BrokerError()
    except BrokerError:
        raise
    except OSError:
        raise BrokerError() from None
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        if root_fd >= 0:
            os.close(root_fd)


def process_start_time(pid: int) -> int:
    try:
        fields = Path(f"/proc/{pid}/stat").read_text(encoding="ascii").rsplit(")", 1)[1].split()
        value = int(fields[19])
        if value <= 0:
            raise BrokerError()
        return value
    except (OSError, UnicodeError, ValueError, IndexError):
        raise BrokerError() from None


def signal_exact(process: subprocess.Popen[bytes], signum: int) -> None:
    if process.poll() is not None:
        return
    descriptor = -1
    try:
        descriptor = os.pidfd_open(process.pid, 0)
        signal.pidfd_send_signal(descriptor, signum)
    except OSError:
        raise BrokerError() from None
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def validate_result(result: bytes, contract: dict[str, Any] | None = None) -> bool:
    if len(result) > MAX_RESULT_BYTES or result.count(b"\n") != 1 or not result.endswith(b"\n"):
        raise BrokerError()
    try:
        value = json.loads(result.decode("ascii"), object_pairs_hook=no_duplicate_object)
    except (UnicodeError, json.JSONDecodeError, BrokerError):
        raise BrokerError() from None
    if (
        not isinstance(value, dict)
        or value.get("kind") != "linux-gui-hard-state-qualification"
        or type(value.get("schemaVersion")) is not int
        or value["schemaVersion"] != SCHEMA_VERSION
        or (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii") != result
    ):
        raise BrokerError()
    if set(value) == FAILED_KEYS:
        if value["ok"] is not False or value["status"] != "failed" or value["code"] not in FAILURE_CODES:
            raise BrokerError()
        return False
    if set(value) != CASE_KEYS:
        raise BrokerError()
    scenario = value.get("scenario")
    expected = CASE_EXPECTED.get(scenario) if isinstance(scenario, str) else None
    if (
        value["ok"] is not True or value["status"] != "captured_pending_privacy_and_public_authority"
        or any(value[key] is not True for key in TRUE_CASE_KEYS)
        or expected is None
        or (
            value["evidenceId"], value["fault"], value["visibleState"],
            value["durableAtCapture"], value["durableAfter"],
        ) != expected
        or not isinstance(value["environmentProfile"], str)
        or value["environmentProfile"] not in ENVIRONMENT_PROFILES
        or not isinstance(value["releaseTag"], str) or TAG.fullmatch(value["releaseTag"]) is None
        or value["publicReleaseUrl"] != f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/{value['releaseTag']}"
        or not isinstance(value["sourceCommit"], str) or HEX_40.fullmatch(value["sourceCommit"]) is None
        or not isinstance(value["sourceTree"], str) or HEX_40.fullmatch(value["sourceTree"]) is None
        or any(
            not isinstance(value[key], str) or HEX_64.fullmatch(value[key]) is None
            for key in ("packageSha256", "guiSha256", "backendSha256")
        )
    ):
        raise BrokerError()
    if contract is not None:
        try:
            expected_identity = {
                "scenario": contract["scenario"],
                "environmentProfile": contract["capture"]["environment_profile"],
                "releaseTag": contract["release"]["tag"],
                "publicReleaseUrl": contract["release"]["url"],
                "sourceCommit": contract["release"]["expected_commit"],
                "sourceTree": contract["release"]["expected_tree"],
                "packageSha256": contract["package"]["sha256"],
                "guiSha256": contract["binaries"]["apphost_sha256"],
                "backendSha256": contract["binaries"]["backend_sha256"],
            }
        except (KeyError, TypeError):
            raise BrokerError() from None
        if any(value[key] != expected for key, expected in expected_identity.items()):
            raise BrokerError()
    return True


def run_case(contract_path: Path, output: Path) -> tuple[bytes, bool]:
    if os.geteuid() != 0 or os.getuid() != 0:
        raise BrokerError()
    contract, contract_bytes, root, uid, gid, total = read_bootstrap(contract_path, output)
    try:
        output_limit = contract["resource_limits"]["output_bytes"]
    except (KeyError, TypeError):
        raise BrokerError() from None
    if isinstance(output_limit, bool) or not isinstance(output_limit, int) or output_limit != OUTPUT_BYTES_LIMIT:
        raise BrokerError()
    identities = {
        Path(__file__).resolve(): fixed_file_hash(Path(__file__).resolve(), 4 * 1024 * 1024),
        SUPERVISOR: fixed_file_hash(SUPERVISOR, 4 * 1024 * 1024),
        CONTROLLER: fixed_file_hash(CONTROLLER, 4 * 1024 * 1024),
    }
    make_namespace_private()
    root_lock_fd = -1
    root_identity = (0, 0)
    quota: OutputQuota | None = None
    scope: CgroupScope | None = None
    contract_fd = -1
    request_fd = -1
    request_inode = 0
    supervisor: subprocess.Popen[bytes] | None = None
    controller: subprocess.Popen[bytes] | None = None
    broker_socket: socket.socket | None = None
    supervisor_socket: socket.socket | None = None
    deadline = time.monotonic() + total
    request = root / REQUEST_NAME
    try:
        root_lock_fd, root_identity = acquire_root_lock(root, uid, gid)
        current_root = root.lstat()
        if (current_root.st_dev, current_root.st_ino) != root_identity:
            raise BrokerError()
        quota = OutputQuota(root, output, uid, gid, output_limit)
        scope = CgroupScope(uid)
        contract_fd = sealed_memfd("smapi-hard-state-contract", contract_bytes, MAX_CONTRACT_BYTES)
        environment = child_environment(uid)
        environment[OUTPUT_QUOTA_ENV] = quota.token
        broker_socket, supervisor_socket = socket.socketpair(socket.AF_UNIX, socket.SOCK_STREAM | socket.SOCK_CLOEXEC)

        def enter_scope_and_drop() -> None:
            if scope is None:
                os._exit(126)
            scope.join_current()
            drop_to(uid, gid)

        supervisor = subprocess.Popen(
            [
                sys.executable, os.fspath(SUPERVISOR), "--contract-fd", str(contract_fd),
                "--output", os.fspath(output), "--execute", "--broker-fd", str(supervisor_socket.fileno()),
            ],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            env=environment, preexec_fn=enter_scope_and_drop, start_new_session=True,
            pass_fds=(supervisor_socket.fileno(), contract_fd),
        )
        supervisor_socket.close()
        while time.monotonic() < deadline:
            if request.exists():
                request_bytes, socket_relative, source_request_inode = read_and_consume_request(
                    request, root, output, uid,
                )
                request_fd = sealed_memfd("smapi-hard-state-request", request_bytes, MAX_REQUEST_BYTES)
                request_inode = source_request_inode

                def enter_scope_and_limit_controller() -> None:
                    if scope is None:
                        os._exit(126)
                    scope.join_current()
                    resource.setrlimit(resource.RLIMIT_FSIZE, (MAX_LEDGER_BYTES, MAX_LEDGER_BYTES))

                controller = subprocess.Popen(
                    [
                        sys.executable, os.fspath(CONTROLLER),
                        "--allowed-vm-prefix", os.fspath(root.parent),
                        "--request-fd", str(request_fd),
                        "--request-source-inode", str(request_inode),
                        "--supervisor-socket", socket_relative,
                    ],
                    stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    env={"PATH": "/usr/sbin:/usr/bin", "LANG": "C", "LC_ALL": "C"},
                    preexec_fn=enter_scope_and_limit_controller, start_new_session=True,
                    pass_fds=(request_fd,),
                )
                controller_start = process_start_time(controller.pid)
                message = {
                    "controller_pid": controller.pid,
                    "controller_request_fd": request_fd,
                    "controller_script_sha256": identities[CONTROLLER],
                    "controller_start_time": controller_start,
                    "request_source_inode": request_inode,
                }
                broker_socket.sendall(
                    (json.dumps(message, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii"),
                )
                broker_socket.shutdown(socket.SHUT_WR)
                break
            if supervisor.poll() is not None:
                break
            time.sleep(0.02)
        if controller is None:
            raise BrokerError()
        try:
            result, supervisor_stderr = supervisor.communicate(timeout=max(0.01, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            raise BrokerError() from None
        if supervisor_stderr:
            raise BrokerError()
        try:
            controller.wait(timeout=max(0.01, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            raise BrokerError() from None
        if supervisor.returncode not in (0, 2, 70, 130):
            raise BrokerError()
        succeeded = validate_result(result, contract)
        if succeeded != (supervisor.returncode == 0):
            raise BrokerError()
        if controller.returncode not in ((0,) if succeeded else (0, 2, 130)):
            raise BrokerError()
        if any(fixed_file_hash(path, 4 * 1024 * 1024) != digest for path, digest in identities.items()):
            raise BrokerError()
        return result, succeeded
    finally:
        if broker_socket is not None:
            broker_socket.close()
        if supervisor_socket is not None:
            supervisor_socket.close()
        cleanup_failed = False
        if supervisor is not None and supervisor.poll() is None:
            try:
                signal_exact(supervisor, signal.SIGINT)
                supervisor.wait(timeout=2)
            except (BrokerError, subprocess.TimeoutExpired):
                cleanup_failed = True
        if controller is not None and controller.poll() is None:
            try:
                signal_exact(controller, signal.SIGTERM)
                controller.wait(timeout=2)
            except (BrokerError, subprocess.TimeoutExpired):
                cleanup_failed = True
        if scope is not None:
            try:
                scope.kill_and_remove(time.monotonic() + 5)
            except BrokerError:
                cleanup_failed = True
        for process in (supervisor, controller):
            if process is None:
                continue
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                cleanup_failed = True
        if quota is not None:
            try:
                quota.close(preserve=True)
            except BrokerError:
                cleanup_failed = True
        try:
            cleanup_residual_request(root, uid)
            cleanup_controller_ledgers(root.parent, request_inode)
        except BrokerError:
            cleanup_failed = True
        for descriptor in (contract_fd, request_fd):
            if descriptor >= 0:
                try:
                    os.close(descriptor)
                except OSError:
                    cleanup_failed = True
        if root_lock_fd >= 0:
            try:
                current_root = root.lstat()
                if (current_root.st_dev, current_root.st_ino) != root_identity:
                    cleanup_failed = True
                os.close(root_lock_fd)
                root_lock_fd = -1
            except OSError:
                cleanup_failed = True
        if cleanup_failed:
            raise BrokerError()


def main(arguments: list[str]) -> int:
    try:
        contract, output = parse_arguments(arguments)
        result, succeeded = run_case(contract, output)
        os.write(sys.stdout.fileno(), result)
        return 0 if succeeded else 2
    except BaseException:
        emit_failure()
        return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
