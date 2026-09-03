#!/usr/bin/env python3
"""Privileged disposable-VM broker for one nonroot Linux GUI hard-state preflight."""

from __future__ import annotations

import argparse
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
PREFLIGHT_KEYS = frozenset({
    "kind", "ok", "scenario", "schemaVersion", "status", "releaseTag", "sourceCommit",
    "sourceTree", "publicReleaseUrl", "packageSha256", "guiSha256", "backendSha256",
    "capturePending", "durableClassificationPending", "publicAuthorityVerificationPending",
    "atspiActionObserved", "accessibleStateObserved", "barrierObserved", "boundaryArmedObserved",
    "boundaryCleanedObserved", "cleanupComplete", "exactWindowCaptured", "inventoryVerified",
    "packageIdentityReverified",
})
REQUIRED_MEMFD_SEALS = (
    fcntl.F_SEAL_WRITE | fcntl.F_SEAL_GROW | fcntl.F_SEAL_SHRINK | fcntl.F_SEAL_SEAL
)


class BrokerError(Exception):
    pass


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
        for line in Path("/proc/self/mountinfo").read_text(encoding="utf-8").splitlines():
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
        "ok": False, "schemaVersion": 1, "status": "failed",
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
        contract = json.loads(bytes(raw).decode("utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError):
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
        value = json.loads(bytes(raw).decode("utf-8"))
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
    except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError):
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


def validate_result(result: bytes) -> bool:
    if len(result) > MAX_RESULT_BYTES or result.count(b"\n") != 1 or not result.endswith(b"\n"):
        raise BrokerError()
    try:
        value = json.loads(result.decode("ascii"))
    except (UnicodeError, json.JSONDecodeError):
        raise BrokerError() from None
    if not isinstance(value, dict) or value.get("kind") != "linux-gui-hard-state-qualification" or value.get("schemaVersion") != 1:
        raise BrokerError()
    if set(value) == FAILED_KEYS:
        if value["ok"] is not False or value["status"] != "failed" or value["code"] not in FAILURE_CODES:
            raise BrokerError()
        return False
    if set(value) != PREFLIGHT_KEYS:
        raise BrokerError()
    booleans = PREFLIGHT_KEYS - {
        "kind", "scenario", "schemaVersion", "status", "releaseTag", "sourceCommit", "sourceTree",
        "publicReleaseUrl", "packageSha256", "guiSha256", "backendSha256",
    }
    if (
        value["ok"] is not True or value["status"] != "preflighted"
        or any(type(value[key]) is not bool for key in booleans)
        or value["capturePending"] is not True or value["durableClassificationPending"] is not True
        or value["publicAuthorityVerificationPending"] is not True or value["exactWindowCaptured"] is not False
        or value["scenario"] not in SCENARIOS
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
    return True


def run_case(contract_path: Path, output: Path) -> tuple[bytes, bool]:
    if os.geteuid() != 0 or os.getuid() != 0:
        raise BrokerError()
    _contract, contract_bytes, root, uid, gid, total = read_bootstrap(contract_path, output)
    identities = {
        Path(__file__).resolve(): fixed_file_hash(Path(__file__).resolve(), 4 * 1024 * 1024),
        SUPERVISOR: fixed_file_hash(SUPERVISOR, 4 * 1024 * 1024),
        CONTROLLER: fixed_file_hash(CONTROLLER, 4 * 1024 * 1024),
    }
    make_namespace_private()
    root_lock_fd = -1
    root_identity = (0, 0)
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
        scope = CgroupScope(uid)
        contract_fd = sealed_memfd("smapi-hard-state-contract", contract_bytes, MAX_CONTRACT_BYTES)
        environment = child_environment(uid)
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
        succeeded = validate_result(result)
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
