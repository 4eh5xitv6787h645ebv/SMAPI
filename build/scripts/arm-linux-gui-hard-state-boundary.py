#!/usr/bin/env python3
"""Root-only controller for disposable-VM Linux GUI hard-state boundaries."""

from __future__ import annotations

from dataclasses import dataclass
import errno
import json
import os
from pathlib import Path, PurePosixPath
import re
import signal
import socket
import stat
import struct
import subprocess
import sys
import time
from typing import Any, Iterable


SCHEMA_VERSION = 1
SCENARIOS = frozenset({
    "E2-permission", "E2-read-only", "E2-disk-full", "E2-cross-device", "C2", "C3", "E5", "E6",
})
NO_FAULT_SCENARIOS = frozenset({"C2", "C3", "E5", "E6"})
SAFE_NAME_RE = re.compile(r"^[a-z0-9][a-z0-9._-]{0,127}$")
LOOP_DEVICE_RE = re.compile(r"^/dev/loop[0-9]{1,6}$")
MAX_REQUEST_BYTES = 32 * 1024
MAX_MOUNTS = 8
LOOP_IMAGE_BYTES = 32 * 1024 * 1024
ACK_ARMED = b'{"ok":true,"status":"armed"}\n'
ACK_CLEANED = b'{"ok":true,"status":"cleaned"}\n'
ACK_REJECTED = b'{"ok":false,"status":"rejected"}\n'
MOUNT = "/usr/bin/mount"
UMOUNT = "/usr/bin/umount"
LOSETUP = "/usr/bin/losetup"
MKFS_EXT4 = "/usr/bin/mkfs.ext4"
FIXED_ENVIRONMENT = {"PATH": "/usr/sbin:/usr/bin", "LC_ALL": "C", "LANG": "C"}


class BoundaryError(Exception):
    pass


class ShutdownRequested(BaseException):
    pass


def reject() -> None:
    raise BoundaryError()


@dataclass(frozen=True)
class Identity:
    relative_path: str
    device: int
    inode: int


@dataclass(frozen=True)
class Request:
    scenario: str
    run_uid: int
    root: Identity
    output: Identity
    game: Identity
    hold_timeout_seconds: int
    cleanup_timeout_seconds: int


@dataclass(frozen=True)
class PlanStep:
    action: str
    arguments: tuple[str, ...]
    mount_target: str | None = None


@dataclass(frozen=True)
class MountRecord:
    mount_id: int
    parent_id: int
    device: str
    target: str


def exact_object(value: Any, fields: Iterable[str]) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != set(fields):
        reject()
    return value


def integer(value: Any, minimum: int, maximum: int) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or not minimum <= value <= maximum:
        reject()
    return value


def parse_identity(value: Any) -> Identity:
    item = exact_object(value, ("relative_path", "device", "inode"))
    path = item["relative_path"]
    if not isinstance(path, str):
        reject()
    return Identity(path, integer(item["device"], 0, 2**64 - 1), integer(item["inode"], 1, 2**64 - 1))


def no_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            reject()
        result[key] = value
    return result


def validate_relative_layout(root: Identity, output: Identity, game: Identity) -> None:
    for value in (root.relative_path, output.relative_path, game.relative_path):
        path = PurePosixPath(value)
        if path.is_absolute() or str(path) != value or any(part in ("", ".", "..") for part in path.parts):
            reject()
        if any(SAFE_NAME_RE.fullmatch(part) is None for part in path.parts):
            reject()
    root_parts = PurePosixPath(root.relative_path).parts
    output_parts = PurePosixPath(output.relative_path).parts
    game_parts = PurePosixPath(game.relative_path).parts
    if (
        len(root_parts) != 1
        or output_parts != root_parts + ("output",)
        or game_parts != output_parts + ("game",)
    ):
        reject()


def parse_request(raw: bytes) -> Request:
    if not raw or len(raw) > MAX_REQUEST_BYTES:
        reject()
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=no_duplicates)
    except (UnicodeError, json.JSONDecodeError):
        reject()
    value = exact_object(
        value,
        ("schema_version", "scenario", "run_uid", "root", "output", "game", "timeouts_seconds"),
    )
    if value["schema_version"] != SCHEMA_VERSION or value["scenario"] not in SCENARIOS:
        reject()
    run_uid = integer(value["run_uid"], 1, 2**31 - 1)
    root = parse_identity(value["root"])
    output = parse_identity(value["output"])
    game = parse_identity(value["game"])
    validate_relative_layout(root, output, game)
    timeouts = exact_object(value["timeouts_seconds"], ("hold", "cleanup"))
    hold = integer(timeouts["hold"], 5, 1800)
    cleanup = integer(timeouts["cleanup"], 5, 120)
    return Request(value["scenario"], run_uid, root, output, game, hold, cleanup)


def parse_cli(arguments: list[str]) -> tuple[Path, str, int]:
    if len(arguments) != 6:
        reject()
    values: dict[str, str] = {}
    for index in (0, 2, 4):
        flag = arguments[index]
        if flag not in ("--allowed-vm-prefix", "--request", "--ack-fd") or flag in values:
            reject()
        values[flag] = arguments[index + 1]
    if set(values) != {"--allowed-vm-prefix", "--request", "--ack-fd"}:
        reject()
    prefix = Path(values["--allowed-vm-prefix"])
    if not prefix.is_absolute() or str(prefix) != values["--allowed-vm-prefix"] or ".." in prefix.parts:
        reject()
    request_name = values["--request"]
    if SAFE_NAME_RE.fullmatch(request_name) is None or not request_name.endswith(".json"):
        reject()
    try:
        ack_fd = int(values["--ack-fd"], 10)
    except ValueError:
        reject()
    if str(ack_fd) != values["--ack-fd"] or ack_fd < 3 or ack_fd > 1024:
        reject()
    return prefix, request_name, ack_fd


def validate_prefix(prefix: Path) -> int:
    try:
        metadata = prefix.lstat()
        if (
            prefix.resolve(strict=True) != prefix
            or not stat.S_ISDIR(metadata.st_mode)
            or metadata.st_uid != 0
            or stat.S_IMODE(metadata.st_mode) != 0o700
        ):
            reject()
        flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
        descriptor = os.open(prefix, flags)
        opened = os.fstat(descriptor)
        if (opened.st_dev, opened.st_ino) != (metadata.st_dev, metadata.st_ino):
            os.close(descriptor)
            reject()
        return descriptor
    except BoundaryError:
        raise
    except OSError:
        reject()


def validate_directory_identity(metadata: os.stat_result, expected: Identity, run_uid: int) -> None:
    if (
        not stat.S_ISDIR(metadata.st_mode)
        or metadata.st_uid != run_uid
        or stat.S_IMODE(metadata.st_mode) != 0o700
        or (metadata.st_dev, metadata.st_ino) != (expected.device, expected.inode)
    ):
        reject()


def open_bound_directories(prefix_fd: int, request: Request) -> tuple[int, int, int]:
    descriptors: list[int] = []
    try:
        flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
        root_fd = os.open(request.root.relative_path, flags, dir_fd=prefix_fd)
        descriptors.append(root_fd)
        validate_directory_identity(os.fstat(root_fd), request.root, request.run_uid)
        output_fd = os.open("output", flags, dir_fd=root_fd)
        descriptors.append(output_fd)
        validate_directory_identity(os.fstat(output_fd), request.output, request.run_uid)
        game_fd = os.open("game", flags, dir_fd=output_fd)
        descriptors.append(game_fd)
        validate_directory_identity(os.fstat(game_fd), request.game, request.run_uid)
        return root_fd, output_fd, game_fd
    except BoundaryError:
        for descriptor in descriptors:
            os.close(descriptor)
        raise
    except OSError:
        for descriptor in descriptors:
            os.close(descriptor)
        reject()


def read_single_use_request(prefix_fd: int, name: str) -> tuple[Request, int]:
    try:
        descriptor = os.open(name, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=prefix_fd)
        try:
            before = os.fstat(descriptor)
            validate_request_file_identity(before)
            raw = bytearray()
            while len(raw) <= MAX_REQUEST_BYTES:
                block = os.read(descriptor, min(4096, MAX_REQUEST_BYTES + 1 - len(raw)))
                if not block:
                    break
                raw.extend(block)
            after = os.fstat(descriptor)
            if (
                len(raw) != before.st_size
                or (before.st_dev, before.st_ino, before.st_mode, before.st_uid, before.st_nlink,
                    before.st_size, before.st_mtime_ns, before.st_ctime_ns)
                != (after.st_dev, after.st_ino, after.st_mode, after.st_uid, after.st_nlink,
                    after.st_size, after.st_mtime_ns, after.st_ctime_ns)
            ):
                reject()
            request = parse_request(bytes(raw))
            if request.run_uid != before.st_uid:
                reject()
            return request, before.st_ino
        finally:
            os.close(descriptor)
    except BoundaryError:
        raise
    except OSError:
        reject()


def validate_request_file_identity(metadata: os.stat_result) -> None:
    if (
        not stat.S_ISREG(metadata.st_mode)
        or metadata.st_uid == 0
        or metadata.st_nlink != 1
        or stat.S_IMODE(metadata.st_mode) != 0o600
        or metadata.st_size < 2
        or metadata.st_size > MAX_REQUEST_BYTES
    ):
        reject()


def consume_request(prefix_fd: int, name: str, inode: int) -> None:
    try:
        metadata = os.stat(name, dir_fd=prefix_fd, follow_symlinks=False)
        if metadata.st_ino != inode or not stat.S_ISREG(metadata.st_mode):
            reject()
        os.unlink(name, dir_fd=prefix_fd)
        os.fsync(prefix_fd)
    except BoundaryError:
        raise
    except OSError:
        reject()


def validate_ack_socket(descriptor: int, run_uid: int) -> socket.socket:
    try:
        duplicate = os.dup(descriptor)
        channel = socket.socket(fileno=duplicate)
        if channel.family != socket.AF_UNIX or channel.getsockopt(socket.SOL_SOCKET, socket.SO_TYPE) != socket.SOCK_STREAM:
            channel.close()
            reject()
        _pid, peer_uid, _gid = struct.unpack("3i", channel.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i")))
        if peer_uid not in (0, run_uid):
            channel.close()
            reject()
        channel.set_inheritable(False)
        return channel
    except BoundaryError:
        raise
    except (OSError, ValueError, struct.error):
        reject()


def build_command_plan(scenario: str, game: str) -> tuple[PlanStep, ...]:
    if scenario not in SCENARIOS or not game.startswith("/"):
        reject()
    boundary = f"{game}/.smapi-hard-state-e2"
    if scenario in NO_FAULT_SCENARIOS:
        return ()
    if scenario == "E2-permission":
        return (
            PlanStep("mkdir", (f"{boundary}/permission",)),
            PlanStep("chmod", ("000", f"{boundary}/permission")),
        )
    if scenario == "E2-read-only":
        source = f"{boundary}/read-only-source"
        target = f"{boundary}/read-only-target"
        return (
            PlanStep("mkdir", (source,)),
            PlanStep("mkdir", (target,)),
            PlanStep("exec", (MOUNT, "--bind", "--", source, target), target),
            PlanStep("exec", (MOUNT, "-o", "remount,bind,ro", "--", target), target),
        )
    if scenario == "E2-disk-full":
        image = f"{boundary}/disk-full.img"
        target = f"{boundary}/disk-full-target"
        return (
            PlanStep("mkdir", (target,)),
            PlanStep("allocate", (str(LOOP_IMAGE_BYTES), image)),
        )
    if scenario == "E2-cross-device":
        target = f"{boundary}/cross-device-target"
        return (
            PlanStep("mkdir", (target,)),
            PlanStep("exec", (MOUNT, "-t", "tmpfs", "-o", "size=8m,nosuid,nodev,noexec", "tmpfs", "--", target), target),
        )
    reject()


def disk_full_dynamic_steps(image: str, target: str, loop_device: str = "<loop-device>") -> tuple[PlanStep, ...]:
    if LOOP_DEVICE_RE.fullmatch(loop_device) is None and loop_device != "<loop-device>":
        reject()
    return (
        PlanStep("exec-capture-loop", (LOSETUP, "--find", "--show", "--nooverlap", "--", image)),
        PlanStep("exec", (MKFS_EXT4, "-q", "-F", "-m", "0", "--", loop_device)),
        PlanStep("exec", (MOUNT, "-o", "nosuid,nodev,noexec", "--", loop_device, target), target),
        PlanStep("fill", (target,)),
    )


def decode_mount_path(value: str) -> str:
    return re.sub(r"\\([0-7]{3})", lambda match: chr(int(match.group(1), 8)), value)


def parse_mountinfo(text: str, targets: set[str]) -> tuple[MountRecord, ...]:
    records: list[MountRecord] = []
    for line in text.splitlines():
        fields = line.split(" ")
        if len(fields) < 7 or "-" not in fields:
            continue
        try:
            mount_id = int(fields[0], 10)
            parent_id = int(fields[1], 10)
        except ValueError:
            continue
        target = decode_mount_path(fields[4])
        if target in targets:
            records.append(MountRecord(mount_id, parent_id, fields[2], target))
    if len(records) > MAX_MOUNTS or len({record.mount_id for record in records}) != len(records):
        reject()
    return tuple(records)


def cleanup_mount_plan(records: Iterable[MountRecord]) -> tuple[PlanStep, ...]:
    ordered = sorted(records, key=lambda item: (item.target.count("/"), item.mount_id), reverse=True)
    return tuple(PlanStep("exec", (UMOUNT, "--", record.target), record.target) for record in ordered)


def assert_private_mount_namespace() -> None:
    try:
        current = os.stat("/proc/self/ns/mnt")
        initial = os.stat("/proc/1/ns/mnt")
        if (current.st_dev, current.st_ino) == (initial.st_dev, initial.st_ino):
            reject()
    except BoundaryError:
        raise
    except OSError:
        reject()


class Controller:
    def __init__(self, prefix_fd: int, prefix: Path, request: Request, request_inode: int, game_fd: int):
        self.prefix_fd = prefix_fd
        self.prefix = prefix
        self.request = request
        self.request_inode = request_inode
        self.game_fd = game_fd
        self.game = prefix / request.game.relative_path
        self.boundary = self.game / ".smapi-hard-state-e2"
        self.boundary_fd = -1
        self.state_name = f".smapi-hard-state-controller-{request_inode}.json"
        self.log_name = f".smapi-hard-state-controller-{request_inode}.log"
        self.mounts: list[MountRecord] = []
        self.loop_device: str | None = None
        self.boundary_identity: tuple[int, int] | None = None
        self.leaf_identities: dict[str, tuple[int, int, int]] = {}
        self.state_identity: tuple[int, int] | None = None
        self.log_identity: tuple[int, int] | None = None
        self.deadline = time.monotonic() + request.cleanup_timeout_seconds

    def remaining(self) -> float:
        value = self.deadline - time.monotonic()
        if value <= 0:
            reject()
        return value

    def private_log_fd(self) -> int:
        flags = os.O_WRONLY | os.O_APPEND | os.O_CLOEXEC | os.O_NOFOLLOW
        if self.log_identity is None:
            flags |= os.O_CREAT | os.O_EXCL
        descriptor = os.open(self.log_name, flags, 0o600, dir_fd=self.prefix_fd)
        os.fchmod(descriptor, 0o600)
        metadata = os.fstat(descriptor)
        identity = (metadata.st_dev, metadata.st_ino)
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_uid != 0 or metadata.st_nlink != 1:
            os.close(descriptor)
            reject()
        if self.log_identity is None:
            self.log_identity = identity
        elif identity != self.log_identity:
            os.close(descriptor)
            reject()
        return descriptor

    def run(self, arguments: tuple[str, ...], capture_loop: bool = False) -> str | None:
        if not arguments or arguments[0] not in (MOUNT, UMOUNT, LOSETUP, MKFS_EXT4):
            reject()
        log_fd = self.private_log_fd()
        try:
            result = subprocess.run(
                arguments,
                check=False,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE if capture_loop else log_fd,
                stderr=log_fd,
                env=FIXED_ENVIRONMENT,
                timeout=self.remaining(),
                pass_fds=(() if self.boundary_fd < 0 else (self.boundary_fd,)),
            )
        finally:
            os.close(log_fd)
        if result.returncode != 0:
            reject()
        if not capture_loop:
            return None
        try:
            output = result.stdout.decode("ascii").strip()
        except (AttributeError, UnicodeError):
            reject()
        if LOOP_DEVICE_RE.fullmatch(output) is None:
            reject()
        return output

    def create_boundary(self) -> None:
        validate_directory_identity(os.fstat(self.game_fd), self.request.game, self.request.run_uid)
        os.mkdir(".smapi-hard-state-e2", 0o700, dir_fd=self.game_fd)
        flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
        self.boundary_fd = os.open(".smapi-hard-state-e2", flags, dir_fd=self.game_fd)
        os.fchown(self.boundary_fd, 0, 0)
        os.fchmod(self.boundary_fd, 0o700)
        metadata = os.fstat(self.boundary_fd)
        self.boundary_identity = (metadata.st_dev, metadata.st_ino)

    def make_leaf_directory(self, name: str, owner: int | None = None, mode: int = 0o700) -> None:
        if self.boundary_fd < 0 or SAFE_NAME_RE.fullmatch(name) is None:
            reject()
        os.mkdir(name, 0o700, dir_fd=self.boundary_fd)
        descriptor = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=self.boundary_fd)
        try:
            os.fchown(descriptor, self.request.run_uid if owner is None else owner, 0)
            os.fchmod(descriptor, mode)
            metadata = os.fstat(descriptor)
            self.leaf_identities[name] = (metadata.st_dev, metadata.st_ino, stat.S_IFMT(metadata.st_mode))
        finally:
            os.close(descriptor)

    def validate_leaf_identity(self, name: str) -> None:
        expected = self.leaf_identities.get(name)
        if expected is None:
            reject()
        metadata = os.stat(name, dir_fd=self.boundary_fd, follow_symlinks=False)
        if (metadata.st_dev, metadata.st_ino, stat.S_IFMT(metadata.st_mode)) != expected:
            reject()

    def proc_leaf(self, name: str) -> str:
        if self.boundary_fd < 0 or SAFE_NAME_RE.fullmatch(name) is None:
            reject()
        return f"/proc/self/fd/{self.boundary_fd}/{name}"

    def actual_leaf(self, name: str) -> str:
        if SAFE_NAME_RE.fullmatch(name) is None:
            reject()
        return str(self.boundary / name)

    def chown_mounted_leaf(self, name: str) -> None:
        descriptor = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=self.boundary_fd)
        try:
            os.fchown(descriptor, self.request.run_uid, 0)
            os.fchmod(descriptor, 0o700)
        finally:
            os.close(descriptor)

    def record_mount(self, target: str) -> None:
        try:
            mountinfo = Path("/proc/self/mountinfo").read_text(encoding="utf-8")
        except (OSError, UnicodeError):
            reject()
        matches = parse_mountinfo(mountinfo, {target})
        if len(matches) != 1 or any(record.mount_id == matches[0].mount_id for record in self.mounts):
            reject()
        self.mounts.append(matches[0])
        if len(self.mounts) > MAX_MOUNTS:
            reject()

    def known_mount_targets(self) -> set[str]:
        names = {
            "E2-read-only": "read-only-target",
            "E2-disk-full": "disk-full-target",
            "E2-cross-device": "cross-device-target",
        }
        name = names.get(self.request.scenario)
        return set() if name is None else {self.actual_leaf(name)}

    def arm(self) -> None:
        if self.request.scenario in NO_FAULT_SCENARIOS:
            self.write_state(False)
            return
        assert_private_mount_namespace()
        self.create_boundary()
        if self.request.scenario == "E2-permission":
            self.make_leaf_directory("permission", owner=0, mode=0)
        elif self.request.scenario == "E2-read-only":
            self.make_leaf_directory("read-only-source")
            self.make_leaf_directory("read-only-target")
            source = self.proc_leaf("read-only-source")
            target = self.proc_leaf("read-only-target")
            self.run((MOUNT, "--bind", "--", source, target))
            self.record_mount(self.actual_leaf("read-only-target"))
            self.run((MOUNT, "-o", "remount,bind,ro", "--", target))
        elif self.request.scenario == "E2-disk-full":
            self.make_leaf_directory("disk-full-target", owner=0)
            flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW
            descriptor = os.open("disk-full.img", flags, 0o600, dir_fd=self.boundary_fd)
            try:
                os.posix_fallocate(descriptor, 0, LOOP_IMAGE_BYTES)
                os.fsync(descriptor)
                metadata = os.fstat(descriptor)
                self.leaf_identities["disk-full.img"] = (
                    metadata.st_dev,
                    metadata.st_ino,
                    stat.S_IFMT(metadata.st_mode),
                )
            finally:
                os.close(descriptor)
            image = self.proc_leaf("disk-full.img")
            target = self.proc_leaf("disk-full-target")
            self.loop_device = self.run(
                (LOSETUP, "--find", "--show", "--nooverlap", "--", image),
                capture_loop=True,
            )
            self.run((MKFS_EXT4, "-q", "-F", "-m", "0", "--", self.loop_device))
            self.run((MOUNT, "-o", "nosuid,nodev,noexec", "--", self.loop_device, target))
            self.record_mount(self.actual_leaf("disk-full-target"))
            self.chown_mounted_leaf("disk-full-target")
            self.fill_filesystem("disk-full-target")
        elif self.request.scenario == "E2-cross-device":
            self.make_leaf_directory("cross-device-target", owner=0)
            target = self.proc_leaf("cross-device-target")
            self.run((MOUNT, "-t", "tmpfs", "-o", "size=8m,nosuid,nodev,noexec", "tmpfs", "--", target))
            self.record_mount(self.actual_leaf("cross-device-target"))
            self.chown_mounted_leaf("cross-device-target")
        else:
            reject()
        os.fchown(self.boundary_fd, self.request.run_uid, 0)
        os.fchmod(self.boundary_fd, 0o700)
        self.write_state(False)

    def fill_filesystem(self, target_name: str) -> None:
        target_fd = os.open(target_name, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=self.boundary_fd)
        descriptor = os.open(
            "capacity.bin",
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW,
            0o600,
            dir_fd=target_fd,
        )
        full = False
        try:
            block = b"\0" * (1024 * 1024)
            while self.remaining() > 0:
                try:
                    os.write(descriptor, block)
                    os.fsync(descriptor)
                except OSError as error:
                    if error.errno == errno.ENOSPC:
                        full = True
                        break
                    raise
        finally:
            os.close(descriptor)
            os.close(target_fd)
        if not full:
            reject()

    def write_state(self, cleaned: bool) -> None:
        value = {
            "schema_version": SCHEMA_VERSION,
            "scenario": self.request.scenario,
            "run_uid": self.request.run_uid,
            "request_inode": self.request_inode,
            "root": {"device": self.request.root.device, "inode": self.request.root.inode},
            "output": {"device": self.request.output.device, "inode": self.request.output.inode},
            "game": {"device": self.request.game.device, "inode": self.request.game.inode},
            "mounts": [record.__dict__ for record in self.mounts],
            "loop_device": self.loop_device,
            "cleanup_complete": cleaned,
        }
        data = (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")
        flags = os.O_WRONLY | os.O_CLOEXEC | os.O_NOFOLLOW
        if self.state_identity is None:
            flags |= os.O_CREAT | os.O_EXCL
        descriptor = os.open(self.state_name, flags, 0o600, dir_fd=self.prefix_fd)
        try:
            os.fchmod(descriptor, 0o600)
            metadata = os.fstat(descriptor)
            identity = (metadata.st_dev, metadata.st_ino)
            if not stat.S_ISREG(metadata.st_mode) or metadata.st_uid != 0 or metadata.st_nlink != 1:
                reject()
            if self.state_identity is None:
                self.state_identity = identity
            elif identity != self.state_identity:
                reject()
            os.ftruncate(descriptor, 0)
            written = 0
            while written < len(data):
                count = os.write(descriptor, data[written:])
                if count <= 0:
                    reject()
                written += count
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
        os.fsync(self.prefix_fd)

    def cleanup(self) -> None:
        self.deadline = time.monotonic() + self.request.cleanup_timeout_seconds
        if self.boundary_fd >= 0:
            os.fchown(self.boundary_fd, 0, 0)
            os.fchmod(self.boundary_fd, 0o700)
            discovered = parse_mountinfo(
                Path("/proc/self/mountinfo").read_text(encoding="utf-8"),
                self.known_mount_targets(),
            )
            for record in discovered:
                if all(existing.mount_id != record.mount_id for existing in self.mounts):
                    self.mounts.append(record)
            if len(self.mounts) > MAX_MOUNTS:
                reject()
            self.write_state(False)
        for step in cleanup_mount_plan(self.mounts):
            current = parse_mountinfo(Path("/proc/self/mountinfo").read_text(encoding="utf-8"), {step.mount_target or ""})
            expected = next(record for record in self.mounts if record.target == step.mount_target)
            if len(current) != 1 or current[0].mount_id != expected.mount_id:
                reject()
            target_name = Path(expected.target).name
            self.run((UMOUNT, "--", self.proc_leaf(target_name)))
            after = parse_mountinfo(Path("/proc/self/mountinfo").read_text(encoding="utf-8"), {expected.target})
            if after:
                reject()
        if self.loop_device is not None:
            self.run((LOSETUP, "--detach", "--", self.loop_device))
        if self.boundary_fd >= 0:
            try:
                permission_fd = os.open("permission", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=self.boundary_fd)
                try:
                    os.fchmod(permission_fd, 0o700)
                finally:
                    os.close(permission_fd)
            except FileNotFoundError:
                pass
            try:
                if "disk-full.img" in self.leaf_identities:
                    self.validate_leaf_identity("disk-full.img")
                    os.unlink("disk-full.img", dir_fd=self.boundary_fd)
            except OSError as error:
                if error.errno != errno.ENOENT:
                    raise
        if self.boundary_fd >= 0:
            for name in ("permission", "read-only-target", "read-only-source", "disk-full-target", "cross-device-target"):
                if name in self.leaf_identities:
                    self.validate_leaf_identity(name)
                    os.rmdir(name, dir_fd=self.boundary_fd)
            boundary = os.stat(".smapi-hard-state-e2", dir_fd=self.game_fd, follow_symlinks=False)
            if (boundary.st_dev, boundary.st_ino) != self.boundary_identity:
                reject()
            os.close(self.boundary_fd)
            self.boundary_fd = -1
            os.rmdir(".smapi-hard-state-e2", dir_fd=self.game_fd)
        self.write_state(True)


def receive_cleanup(channel: socket.socket, timeout: int) -> bool:
    channel.settimeout(timeout)
    data = bytearray()
    try:
        while len(data) <= 16 and not data.endswith(b"\n"):
            block = channel.recv(17 - len(data))
            if not block:
                return False
            data.extend(block)
    except (OSError, socket.timeout):
        return False
    return bytes(data) == b"cleanup\n"


def send_ack(channel: socket.socket | None, value: bytes) -> None:
    if channel is None:
        return
    try:
        channel.sendall(value)
    except OSError:
        pass


def main(arguments: list[str]) -> int:
    channel: socket.socket | None = None
    prefix_fd = root_fd = output_fd = game_fd = -1
    controller: Controller | None = None
    if os.geteuid() != 0:
        return 77
    try:
        prefix, request_name, ack_fd = parse_cli(arguments)
        prefix_fd = validate_prefix(prefix)
        request, request_inode = read_single_use_request(prefix_fd, request_name)
        channel = validate_ack_socket(ack_fd, request.run_uid)
        root_fd, output_fd, game_fd = open_bound_directories(prefix_fd, request)
        consume_request(prefix_fd, request_name, request_inode)
        controller = Controller(prefix_fd, prefix, request, request_inode, game_fd)
        previous_handlers = {
            signal.SIGINT: signal.getsignal(signal.SIGINT),
            signal.SIGTERM: signal.getsignal(signal.SIGTERM),
        }
        def stop(_signum: int, _frame: Any) -> None:
            raise ShutdownRequested()
        signal.signal(signal.SIGINT, stop)
        signal.signal(signal.SIGTERM, stop)
        try:
            controller.arm()
            send_ack(channel, ACK_ARMED)
            requested = receive_cleanup(channel, request.hold_timeout_seconds)
            controller.cleanup()
            if not requested:
                send_ack(channel, ACK_REJECTED)
                return 2
            send_ack(channel, ACK_CLEANED)
            return 0
        finally:
            signal.signal(signal.SIGINT, previous_handlers[signal.SIGINT])
            signal.signal(signal.SIGTERM, previous_handlers[signal.SIGTERM])
    except ShutdownRequested:
        if controller is not None:
            try:
                controller.cleanup()
            except BaseException:
                pass
        send_ack(channel, ACK_REJECTED)
        return 130
    except BoundaryError:
        if controller is not None:
            try:
                controller.cleanup()
            except BaseException:
                pass
        send_ack(channel, ACK_REJECTED)
        return 2
    except BaseException:
        if controller is not None:
            try:
                controller.cleanup()
            except BaseException:
                pass
        send_ack(channel, ACK_REJECTED)
        return 70
    finally:
        if channel is not None:
            channel.close()
        for descriptor in (game_fd, output_fd, root_fd, prefix_fd):
            if descriptor >= 0:
                try:
                    os.close(descriptor)
                except OSError:
                    pass


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
