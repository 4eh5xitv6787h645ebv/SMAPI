#!/usr/bin/env python3
"""Root-only controller for disposable-VM Linux GUI hard-state boundaries."""

from __future__ import annotations

from dataclasses import dataclass
import errno
import fcntl
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
SAFE_OUTPUT_NAME_RE = re.compile(r"^[a-z0-9][a-z0-9._-]{7,127}$")
LOOP_DEVICE_RE = re.compile(r"^/dev/loop[0-9]{1,6}$")
MAX_REQUEST_BYTES = 32 * 1024
MAX_MOUNTS = 8
LOOP_IMAGE_BYTES = 32 * 1024 * 1024
ACK_ARMED = b'{"ok":true,"status":"armed"}\n'
ACK_PREPARED = b'{"ok":true,"status":"prepared"}\n'
ACK_READY = b'{"ok":true,"status":"ready"}\n'
ACK_CLEANED = b'{"ok":true,"status":"cleaned"}\n'
ACK_REJECTED = b'{"ok":false,"status":"rejected"}\n'
MOUNT = "/usr/bin/mount"
UMOUNT = "/usr/bin/umount"
MKFS_EXT4 = "/usr/bin/mkfs.ext4"
FIXED_ENVIRONMENT = {"PATH": "/usr/sbin:/usr/bin", "LC_ALL": "C", "LANG": "C"}
REQUIRED_MEMFD_SEALS = (
    fcntl.F_SEAL_WRITE | fcntl.F_SEAL_GROW | fcntl.F_SEAL_SHRINK | fcntl.F_SEAL_SEAL
)


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
class SupervisorIdentity:
    pid: int
    start_time: int
    mount_namespace_device: int
    mount_namespace_inode: int


@dataclass(frozen=True)
class SocketIdentity:
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
    supervisor: SupervisorIdentity
    supervisor_socket: SocketIdentity
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


def parse_supervisor_identity(value: Any) -> SupervisorIdentity:
    item = exact_object(
        value,
        ("pid", "start_time", "mount_namespace_device", "mount_namespace_inode"),
    )
    return SupervisorIdentity(
        integer(item["pid"], 2, 2**31 - 1),
        integer(item["start_time"], 1, 2**64 - 1),
        integer(item["mount_namespace_device"], 0, 2**64 - 1),
        integer(item["mount_namespace_inode"], 1, 2**64 - 1),
    )


def parse_socket_identity(value: Any) -> SocketIdentity:
    item = exact_object(value, ("relative_path", "device", "inode"))
    path = item["relative_path"]
    if not isinstance(path, str):
        reject()
    return SocketIdentity(
        path,
        integer(item["device"], 0, 2**64 - 1),
        integer(item["inode"], 1, 2**64 - 1),
    )


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
        or SAFE_OUTPUT_NAME_RE.fullmatch(root_parts[0]) is None
        or len(output_parts) != 2
        or SAFE_OUTPUT_NAME_RE.fullmatch(output_parts[-1]) is None
        or output_parts[:-1] != root_parts
        or game_parts != output_parts + ("game",)
    ):
        reject()


def validate_socket_layout(socket_identity: SocketIdentity, output: Identity) -> None:
    path = PurePosixPath(socket_identity.relative_path)
    output_parts = PurePosixPath(output.relative_path).parts
    if (
        path.is_absolute()
        or str(path) != socket_identity.relative_path
        or len(path.parts) != 4
        or path.parts[:-2] != output_parts
        or path.parts[-2] != "control"
        or SAFE_NAME_RE.fullmatch(path.parts[-1]) is None
        or not path.parts[-1].endswith(".sock")
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
        (
            "schema_version", "scenario", "run_uid", "root", "output", "game",
            "supervisor", "supervisor_socket", "timeouts_seconds",
        ),
    )
    if value["schema_version"] != SCHEMA_VERSION or value["scenario"] not in SCENARIOS:
        reject()
    run_uid = integer(value["run_uid"], 1, 2**31 - 1)
    root = parse_identity(value["root"])
    output = parse_identity(value["output"])
    game = parse_identity(value["game"])
    supervisor = parse_supervisor_identity(value["supervisor"])
    supervisor_socket = parse_socket_identity(value["supervisor_socket"])
    validate_relative_layout(root, output, game)
    validate_socket_layout(supervisor_socket, output)
    timeouts = exact_object(value["timeouts_seconds"], ("hold", "cleanup"))
    hold = integer(timeouts["hold"], 5, 1800)
    cleanup = integer(timeouts["cleanup"], 5, 120)
    return Request(value["scenario"], run_uid, root, output, game, supervisor, supervisor_socket, hold, cleanup)


def parse_cli(arguments: list[str]) -> tuple[Path, int, int, tuple[str, str, str, str]]:
    if len(arguments) != 8:
        reject()
    values: dict[str, str] = {}
    for index in (0, 2, 4, 6):
        flag = arguments[index]
        if flag not in ("--allowed-vm-prefix", "--request-fd", "--request-source-inode", "--supervisor-socket") or flag in values:
            reject()
        values[flag] = arguments[index + 1]
    if set(values) != {"--allowed-vm-prefix", "--request-fd", "--request-source-inode", "--supervisor-socket"}:
        reject()
    prefix = Path(values["--allowed-vm-prefix"])
    if not prefix.is_absolute() or str(prefix) != values["--allowed-vm-prefix"] or ".." in prefix.parts:
        reject()
    try:
        request_fd = int(values["--request-fd"], 10)
        request_source_inode = int(values["--request-source-inode"], 10)
    except ValueError:
        reject()
    if not 3 <= request_fd <= 1024 or request_source_inode <= 0:
        reject()
    socket_path = PurePosixPath(values["--supervisor-socket"])
    if (
        socket_path.is_absolute()
        or str(socket_path) != values["--supervisor-socket"]
        or len(socket_path.parts) != 4
        or socket_path.parts[-2] != "control"
        or SAFE_OUTPUT_NAME_RE.fullmatch(socket_path.parts[1]) is None
        or SAFE_NAME_RE.fullmatch(socket_path.parts[-1]) is None
        or not socket_path.parts[-1].endswith(".sock")
    ):
        reject()
    return prefix, request_fd, request_source_inode, socket_path.parts


def validate_prefix(prefix: Path) -> int:
    try:
        metadata = prefix.lstat()
        if prefix.resolve(strict=True) != prefix:
            reject()
        validate_prefix_metadata(metadata)
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


def validate_prefix_metadata(metadata: os.stat_result) -> None:
    if (
        not stat.S_ISDIR(metadata.st_mode)
        or metadata.st_uid != 0
        or stat.S_IMODE(metadata.st_mode) != 0o711
    ):
        reject()


def validate_directory_identity(metadata: os.stat_result, expected: Identity, run_uid: int) -> None:
    if (
        not stat.S_ISDIR(metadata.st_mode)
        or metadata.st_uid != run_uid
        or stat.S_IMODE(metadata.st_mode) != 0o700
        or (metadata.st_dev, metadata.st_ino) != (expected.device, expected.inode)
    ):
        reject()


def open_request_root(prefix_fd: int, name: str) -> int:
    try:
        flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
        return os.open(name, flags, dir_fd=prefix_fd)
    except OSError:
        reject()


def open_bound_directories(root_fd: int, request: Request) -> tuple[int, int]:
    descriptors: list[int] = []
    try:
        flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
        validate_directory_identity(os.fstat(root_fd), request.root, request.run_uid)
        output_name = PurePosixPath(request.output.relative_path).name
        output_fd = os.open(output_name, flags, dir_fd=root_fd)
        descriptors.append(output_fd)
        validate_directory_identity(os.fstat(output_fd), request.output, request.run_uid)
        game_fd = os.open("game", flags, dir_fd=output_fd)
        descriptors.append(game_fd)
        validate_directory_identity(os.fstat(game_fd), request.game, request.run_uid)
        return output_fd, game_fd
    except BoundaryError:
        for descriptor in descriptors:
            os.close(descriptor)
        raise
    except OSError:
        for descriptor in descriptors:
            os.close(descriptor)
        reject()


def read_sealed_request(descriptor: int) -> Request:
    try:
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode) or before.st_nlink != 0
            or not 0 < before.st_size <= MAX_REQUEST_BYTES
            or fcntl.fcntl(descriptor, fcntl.F_GET_SEALS) != REQUIRED_MEMFD_SEALS
        ):
            reject()
        os.lseek(descriptor, 0, os.SEEK_SET)
        raw = bytearray()
        while len(raw) <= MAX_REQUEST_BYTES:
            block = os.read(descriptor, min(4096, MAX_REQUEST_BYTES + 1 - len(raw)))
            if not block:
                break
            raw.extend(block)
        after = os.fstat(descriptor)
        fields = ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
        if (
            len(raw) != before.st_size or len(raw) > MAX_REQUEST_BYTES
            or any(getattr(before, field) != getattr(after, field) for field in fields)
            or fcntl.fcntl(descriptor, fcntl.F_GET_SEALS) != REQUIRED_MEMFD_SEALS
        ):
            reject()
        return parse_request(bytes(raw))
    except BoundaryError:
        raise
    except (OSError, ValueError):
        reject()


def connect_supervisor_socket(
    output_fd: int,
    request: Request,
    cli_parts: tuple[str, str, str, str],
) -> socket.socket:
    control_fd = -1
    try:
        expected_parts = PurePosixPath(request.supervisor_socket.relative_path).parts
        if cli_parts != expected_parts:
            reject()
        control_fd = os.open(
            "control",
            os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
            dir_fd=output_fd,
        )
        control = os.fstat(control_fd)
        if (
            not stat.S_ISDIR(control.st_mode)
            or control.st_uid != request.run_uid
            or stat.S_IMODE(control.st_mode) != 0o700
        ):
            reject()
        socket_name = expected_parts[-1]
        before = os.stat(socket_name, dir_fd=control_fd, follow_symlinks=False)
        if (
            not stat.S_ISSOCK(before.st_mode)
            or before.st_uid != request.run_uid
            or before.st_nlink != 1
            or stat.S_IMODE(before.st_mode) != 0o600
            or (before.st_dev, before.st_ino)
            != (request.supervisor_socket.device, request.supervisor_socket.inode)
        ):
            reject()
        channel = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM | socket.SOCK_CLOEXEC)
        channel.settimeout(request.cleanup_timeout_seconds)
        channel.connect(f"/proc/self/fd/{control_fd}/{socket_name}")
        after = os.stat(socket_name, dir_fd=control_fd, follow_symlinks=False)
        if (after.st_dev, after.st_ino, after.st_mode, after.st_uid) != (
            before.st_dev, before.st_ino, before.st_mode, before.st_uid,
        ):
            channel.close()
            reject()
        peer_pid, peer_uid, _gid = struct.unpack(
            "3i",
            channel.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i")),
        )
        if peer_pid != request.supervisor.pid or peer_uid != request.run_uid:
            channel.close()
            reject()
        channel.set_inheritable(False)
        return channel
    except BoundaryError:
        raise
    except (OSError, ValueError, struct.error):
        reject()
    finally:
        if control_fd >= 0:
            os.close(control_fd)


def build_command_plan(scenario: str, game: str) -> tuple[PlanStep, ...]:
    if scenario not in SCENARIOS or not game.startswith("/"):
        reject()
    boundary = f"{game}/.smapi-hard-state-e2"
    if scenario in NO_FAULT_SCENARIOS:
        return ()
    if scenario == "E2-permission":
        return (
            PlanStep("arm-chown", ("0:0", f"{game}/smapi-internal")),
            PlanStep("arm-chmod", ("000", f"{game}/smapi-internal")),
        )
    if scenario == "E2-read-only":
        return (
            PlanStep(
                "prepare-exec",
                (MOUNT, "-t", "tmpfs", "-o", "size=32m,nosuid,nodev,noexec", "tmpfs", "--", game),
                game,
            ),
            PlanStep("arm-exec", (MOUNT, "-o", "remount,ro,nosuid,nodev,noexec", "--", game), game),
        )
    if scenario == "E2-disk-full":
        image = f"{boundary}/disk-full.img"
        return (
            PlanStep("prepare-mkdir", (boundary,)),
            PlanStep("prepare-allocate", (str(LOOP_IMAGE_BYTES), image)),
            *disk_full_dynamic_steps(image, game),
            PlanStep("arm-fill", (game,)),
        )
    if scenario == "E2-cross-device":
        target = f"{game}/smapi-internal"
        return (
            PlanStep(
                "seeded-exec",
                (MOUNT, "-t", "tmpfs", "-o", "size=8m,nosuid,nodev,noexec", "tmpfs", "--", target),
                target,
            ),
            PlanStep("arm-verify-device", (target,)),
        )
    reject()


def disk_full_dynamic_steps(image: str, target: str) -> tuple[PlanStep, ...]:
    return (
        PlanStep("prepare-exec", (MKFS_EXT4, "-q", "-F", "-m", "0", "--", image)),
        PlanStep("prepare-exec", (MOUNT, "-o", "loop,nosuid,nodev,noexec", "--", image, target), target),
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


def process_start_time(pid: int) -> int:
    try:
        value = Path(f"/proc/{pid}/stat").read_text(encoding="ascii")
        fields = value[value.rfind(")") + 2:].split()
        return int(fields[19], 10)
    except (OSError, UnicodeError, ValueError, IndexError):
        reject()


def validate_nonroot_process_security(pid: int, run_uid: int) -> None:
    try:
        fields: dict[str, str] = {}
        for line in Path(f"/proc/{pid}/status").read_text(encoding="ascii").splitlines():
            if ":" in line:
                key, value = line.split(":", 1)
                fields[key] = value.strip()
        uids = tuple(int(value, 10) for value in fields["Uid"].split())
        if uids != (run_uid, run_uid, run_uid, run_uid) or int(fields["CapEff"], 16) != 0:
            reject()
        if fields.get("NoNewPrivs") != "1":
            reject()
    except BoundaryError:
        raise
    except (OSError, UnicodeError, ValueError, KeyError):
        reject()


def validate_supervisor_namespace(supervisor: SupervisorIdentity, run_uid: int) -> None:
    try:
        current = os.stat("/proc/self/ns/mnt")
        initial = os.stat("/proc/1/ns/mnt")
        supervisor_process = os.stat(f"/proc/{supervisor.pid}")
        supervisor_namespace = os.stat(f"/proc/{supervisor.pid}/ns/mnt")
        expected_namespace = (supervisor.mount_namespace_device, supervisor.mount_namespace_inode)
        if (
            supervisor_process.st_uid != run_uid
            or process_start_time(supervisor.pid) != supervisor.start_time
            or (current.st_dev, current.st_ino) != expected_namespace
            or (supervisor_namespace.st_dev, supervisor_namespace.st_ino) != expected_namespace
            or (current.st_dev, current.st_ino) == (initial.st_dev, initial.st_ino)
        ):
            reject()
        validate_nonroot_process_security(supervisor.pid, run_uid)
    except BoundaryError:
        raise
    except OSError:
        reject()


class Controller:
    def __init__(
        self,
        prefix_fd: int,
        prefix: Path,
        request: Request,
        request_inode: int,
        output_fd: int,
        game_fd: int,
    ):
        self.prefix_fd = prefix_fd
        self.prefix = prefix
        self.request = request
        self.request_inode = request_inode
        self.output_fd = output_fd
        self.game_fd = game_fd
        self.current_game_fd = -1
        self.game = prefix / request.game.relative_path
        self.boundary = self.game / ".smapi-hard-state-e2"
        self.boundary_fd = -1
        self.state_name = f".smapi-hard-state-controller-{request_inode}.json"
        self.log_name = f".smapi-hard-state-controller-{request_inode}.log"
        self.mounts: list[MountRecord] = []
        self.loop_device: str | None = None
        self.loop_rdev: int | None = None
        self.loop_backing_file: str | None = None
        self.internal_fd = -1
        self.internal_identity: tuple[int, int] | None = None
        self.internal_original: tuple[int, int, int] | None = None
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

    def reset_phase_deadline(self) -> None:
        self.deadline = time.monotonic() + self.request.cleanup_timeout_seconds

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

    def run(self, arguments: tuple[str, ...]) -> None:
        if not arguments or arguments[0] not in (MOUNT, UMOUNT, MKFS_EXT4):
            reject()
        log_fd = self.private_log_fd()
        try:
            result = subprocess.run(
                arguments,
                check=False,
                stdin=subprocess.DEVNULL,
                stdout=log_fd,
                stderr=log_fd,
                env=FIXED_ENVIRONMENT,
                timeout=self.remaining(),
                pass_fds=tuple(
                    descriptor
                    for descriptor in (self.output_fd, self.game_fd, self.current_game_fd, self.boundary_fd)
                    if descriptor >= 0
                ),
            )
        finally:
            os.close(log_fd)
        if result.returncode != 0:
            reject()

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

    def proc_game(self) -> str:
        return f"/proc/self/fd/{self.output_fd}/game"

    def proc_internal(self) -> str:
        if self.current_game_fd < 0:
            reject()
        return f"/proc/self/fd/{self.current_game_fd}/smapi-internal"

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

    def open_internal(self) -> None:
        if self.internal_fd >= 0:
            return
        if self.current_game_fd < 0:
            reject()
        self.internal_fd = os.open(
            "smapi-internal",
            os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
            dir_fd=self.current_game_fd,
        )
        metadata = os.fstat(self.internal_fd)
        game = os.fstat(self.current_game_fd)
        if (
            not stat.S_ISDIR(metadata.st_mode)
            or metadata.st_uid != self.request.run_uid
            or stat.S_IMODE(metadata.st_mode) != 0o700
            or metadata.st_dev != game.st_dev
        ):
            reject()
        self.internal_identity = (metadata.st_dev, metadata.st_ino)
        self.internal_original = (metadata.st_uid, metadata.st_gid, stat.S_IMODE(metadata.st_mode))

    def record_mount(self, target: str) -> None:
        try:
            mountinfo = Path("/proc/self/mountinfo").read_text(encoding="utf-8")
        except (OSError, UnicodeError):
            reject()
        matches = parse_mountinfo(mountinfo, {target})
        if len(matches) != 1:
            reject()
        self.mounts = [record for record in self.mounts if record.mount_id != matches[0].mount_id]
        self.mounts.append(matches[0])
        if len(self.mounts) > MAX_MOUNTS:
            reject()

    def known_mount_targets(self) -> set[str]:
        if self.request.scenario == "E2-read-only":
            return {str(self.game)}
        if self.request.scenario == "E2-disk-full":
            return {str(self.game)}
        if self.request.scenario == "E2-cross-device":
            return {str(self.game / "smapi-internal")}
        return set()

    def mount_operand(self, target: str) -> str:
        if target == str(self.game):
            return self.proc_game()
        if target == str(self.game / "smapi-internal"):
            return self.proc_internal()
        reject()

    def prepare(self) -> None:
        if self.request.scenario in NO_FAULT_SCENARIOS:
            self.write_state(False)
            return
        if self.request.scenario == "E2-read-only":
            self.run((
                MOUNT, "-t", "tmpfs", "-o", "size=32m,nosuid,nodev,noexec",
                "tmpfs", "--", self.proc_game(),
            ))
            self.record_mount(str(self.game))
        elif self.request.scenario == "E2-disk-full":
            self.prepare_disk_full()
        elif self.request.scenario not in ("E2-permission", "E2-cross-device"):
            reject()
        if self.request.scenario in ("E2-read-only", "E2-disk-full"):
            self.chown_current_game()
        self.write_state(False)

    def seeded(self) -> None:
        self.current_game_fd = os.open(
            "game",
            os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
            dir_fd=self.output_fd,
        )
        metadata = os.fstat(self.current_game_fd)
        if (
            not stat.S_ISDIR(metadata.st_mode)
            or metadata.st_uid != self.request.run_uid
            or stat.S_IMODE(metadata.st_mode) != 0o700
        ):
            reject()
        if self.request.scenario not in ("E2-read-only", "E2-disk-full") and (
            metadata.st_dev,
            metadata.st_ino,
        ) != (self.request.game.device, self.request.game.inode):
            reject()
        self.open_internal()
        if self.request.scenario == "E2-cross-device":
            self.run((
                MOUNT, "-t", "tmpfs", "-o", "size=8m,nosuid,nodev,noexec",
                "tmpfs", "--", self.proc_internal(),
            ))
            self.record_mount(str(self.game / "smapi-internal"))
            mounted = os.open(
                "smapi-internal",
                os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
                dir_fd=self.current_game_fd,
            )
            try:
                os.fchown(mounted, self.request.run_uid, 0)
                os.fchmod(mounted, 0o700)
            finally:
                os.close(mounted)
        self.write_state(False)

    def arm(self) -> None:
        if self.request.scenario in NO_FAULT_SCENARIOS:
            return
        if self.request.scenario == "E2-permission":
            if self.internal_fd < 0:
                reject()
            os.fchown(self.internal_fd, 0, 0)
            os.fchmod(self.internal_fd, 0)
        elif self.request.scenario == "E2-read-only":
            self.run((MOUNT, "-o", "remount,ro,nosuid,nodev,noexec", "--", self.proc_game()))
        elif self.request.scenario == "E2-disk-full":
            if self.boundary_fd < 0 or self.loop_device is None or self.current_game_fd < 0:
                reject()
            self.fill_filesystem(self.current_game_fd)
        elif self.request.scenario == "E2-cross-device":
            if self.internal_fd < 0 or self.current_game_fd < 0:
                reject()
            internal = os.stat("smapi-internal", dir_fd=self.current_game_fd, follow_symlinks=False)
            if internal.st_dev == os.fstat(self.current_game_fd).st_dev:
                reject()
            self.record_mount(str(self.game / "smapi-internal"))
        else:
            reject()
        self.write_state(False)

    def prepare_disk_full(self) -> None:
        self.create_boundary()
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
        self.run((MKFS_EXT4, "-q", "-F", "-m", "0", "--", image))
        # util-linux mount allocates the loop device with LO_FLAGS_AUTOCLEAR.
        # Ubuntu 24.04's losetup has no setup-time --autoclear option.
        self.run((MOUNT, "-o", "loop,nosuid,nodev,noexec", "--", image, self.proc_game()))
        self.record_mount(str(self.game))
        self.capture_loop_identity()
        self.remove_empty_lost_found()

    def loop_backing_path(self) -> Path:
        if self.loop_rdev is None:
            reject()
        return Path("/sys/dev/block") / f"{os.major(self.loop_rdev)}:{os.minor(self.loop_rdev)}" / "loop/backing_file"

    def read_loop_backing_file(self) -> str | None:
        try:
            with self.loop_backing_path().open("rb") as stream:
                raw = stream.read(4097)
            if len(raw) > 4096:
                reject()
            return raw.decode("utf-8").rstrip("\n")
        except FileNotFoundError:
            return None
        except (OSError, UnicodeError):
            reject()

    def read_loop_autoclear(self) -> bool | None:
        if self.loop_rdev is None:
            reject()
        path = Path("/sys/dev/block") / f"{os.major(self.loop_rdev)}:{os.minor(self.loop_rdev)}" / "loop/autoclear"
        try:
            raw = path.read_bytes()
        except FileNotFoundError:
            return None
        except OSError:
            reject()
        if raw not in (b"0\n", b"1\n"):
            reject()
        return raw == b"1\n"

    def capture_loop_identity(self) -> None:
        matches = [record for record in self.mounts if record.target == str(self.game)]
        if len(matches) != 1 or re.fullmatch(r"[0-9]+:[0-9]+", matches[0].device) is None:
            reject()
        major_text, minor_text = matches[0].device.split(":", 1)
        self.loop_rdev = os.makedev(int(major_text, 10), int(minor_text, 10))
        sysfs_device = Path("/sys/dev/block") / matches[0].device
        try:
            loop_name = sysfs_device.resolve(strict=True).name
        except OSError:
            reject()
        self.loop_device = f"/dev/{loop_name}"
        if LOOP_DEVICE_RE.fullmatch(self.loop_device) is None:
            reject()
        try:
            device = os.lstat(self.loop_device)
        except OSError:
            reject()
        if not stat.S_ISBLK(device.st_mode) or device.st_rdev != self.loop_rdev:
            reject()
        self.loop_backing_file = self.read_loop_backing_file()
        expected = self.actual_leaf("disk-full.img")
        if self.loop_backing_file != expected or self.read_loop_autoclear() is not True:
            reject()

    def wait_for_loop_autoclear(self) -> None:
        if self.loop_device is None or self.loop_rdev is None or self.loop_backing_file is None:
            reject()
        while self.remaining() > 0:
            current = self.read_loop_backing_file()
            if current is None:
                self.loop_device = None
                self.loop_rdev = None
                self.loop_backing_file = None
                return
            if current != self.loop_backing_file:
                # The loop number was reassigned; never detach an unrelated association.
                reject()
            if self.read_loop_autoclear() is not True:
                reject()
            try:
                device = os.lstat(self.loop_device)
            except OSError:
                reject()
            if not stat.S_ISBLK(device.st_mode) or device.st_rdev != self.loop_rdev:
                reject()
            time.sleep(0.02)
        reject()

    def remove_empty_lost_found(self) -> None:
        game = os.open(
            "game",
            os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
            dir_fd=self.output_fd,
        )
        lost_found = -1
        try:
            lost_found = os.open(
                "lost+found",
                os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
                dir_fd=game,
            )
            metadata = os.fstat(lost_found)
            if metadata.st_uid != 0 or os.listdir(lost_found):
                reject()
            os.close(lost_found)
            lost_found = -1
            os.rmdir("lost+found", dir_fd=game)
            os.fsync(game)
        finally:
            if lost_found >= 0:
                os.close(lost_found)
            os.close(game)

    def chown_current_game(self) -> None:
        descriptor = os.open(
            "game",
            os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
            dir_fd=self.output_fd,
        )
        try:
            os.fchown(descriptor, self.request.run_uid, 0)
            os.fchmod(descriptor, 0o700)
        finally:
            os.close(descriptor)

    def fill_filesystem(self, target_fd: int) -> None:
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
            "supervisor": {
                "pid": self.request.supervisor.pid,
                "start_time": self.request.supervisor.start_time,
                "mount_namespace_device": self.request.supervisor.mount_namespace_device,
                "mount_namespace_inode": self.request.supervisor.mount_namespace_inode,
            },
            "supervisor_socket": {
                "device": self.request.supervisor_socket.device,
                "inode": self.request.supervisor_socket.inode,
            },
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
        if self.internal_fd >= 0:
            if self.request.scenario == "E2-permission" and self.internal_original is not None:
                original_uid, original_gid, original_mode = self.internal_original
                os.fchown(self.internal_fd, original_uid, original_gid)
                os.fchmod(self.internal_fd, original_mode)
                current = os.stat("smapi-internal", dir_fd=self.current_game_fd, follow_symlinks=False)
                if (current.st_dev, current.st_ino) != self.internal_identity:
                    reject()
            os.close(self.internal_fd)
            self.internal_fd = -1
        if self.current_game_fd >= 0 and self.request.scenario != "E2-cross-device":
            os.close(self.current_game_fd)
            self.current_game_fd = -1
        discovered = parse_mountinfo(
            Path("/proc/self/mountinfo").read_text(encoding="utf-8"),
            self.known_mount_targets(),
        )
        tracked_ids = {record.mount_id for record in self.mounts}
        discovered_ids = {record.mount_id for record in discovered}
        if self.mounts and discovered_ids != tracked_ids:
            reject()
        self.mounts = list(discovered)
        self.write_state(False)
        for step in cleanup_mount_plan(self.mounts):
            current = parse_mountinfo(Path("/proc/self/mountinfo").read_text(encoding="utf-8"), {step.mount_target or ""})
            expected = next(record for record in self.mounts if record.target == step.mount_target)
            if len(current) != 1 or current[0].mount_id != expected.mount_id:
                reject()
            self.run((UMOUNT, "--", self.mount_operand(expected.target)))
            after = parse_mountinfo(Path("/proc/self/mountinfo").read_text(encoding="utf-8"), {expected.target})
            if after:
                reject()
        if self.current_game_fd >= 0:
            if self.request.scenario == "E2-cross-device" and self.internal_identity is not None:
                current = os.stat("smapi-internal", dir_fd=self.current_game_fd, follow_symlinks=False)
                if (current.st_dev, current.st_ino) != self.internal_identity:
                    reject()
            os.close(self.current_game_fd)
            self.current_game_fd = -1
        if self.loop_device is not None:
            self.wait_for_loop_autoclear()
        if self.boundary_fd >= 0:
            try:
                if "disk-full.img" in self.leaf_identities:
                    self.validate_leaf_identity("disk-full.img")
                    os.unlink("disk-full.img", dir_fd=self.boundary_fd)
            except OSError as error:
                if error.errno != errno.ENOENT:
                    raise
        if self.boundary_fd >= 0:
            boundary = os.stat(".smapi-hard-state-e2", dir_fd=self.game_fd, follow_symlinks=False)
            if (boundary.st_dev, boundary.st_ino) != self.boundary_identity:
                reject()
            os.close(self.boundary_fd)
            self.boundary_fd = -1
            os.rmdir(".smapi-hard-state-e2", dir_fd=self.game_fd)
        self.write_state(True)


def receive_command(channel: socket.socket, expected: bytes, timeout: float) -> bool:
    channel.settimeout(max(0.01, timeout))
    data = bytearray()
    try:
        while len(data) <= 16 and not data.endswith(b"\n"):
            block = channel.recv(17 - len(data))
            if not block:
                return False
            data.extend(block)
    except (OSError, socket.timeout):
        return False
    return bytes(data) == expected


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
    request_fd = -1
    controller: Controller | None = None
    if os.geteuid() != 0:
        return 77
    try:
        prefix, request_fd, request_source_inode, socket_path = parse_cli(arguments)
        request = read_sealed_request(request_fd)
        os.close(request_fd)
        request_fd = -1
        prefix_fd = validate_prefix(prefix)
        root_name = request.root.relative_path
        root_fd = open_request_root(prefix_fd, root_name)
        if request.root.relative_path != root_name:
            reject()
        validate_directory_identity(os.fstat(root_fd), request.root, request.run_uid)
        output_fd, game_fd = open_bound_directories(root_fd, request)
        validate_supervisor_namespace(request.supervisor, request.run_uid)
        channel = connect_supervisor_socket(output_fd, request, socket_path)
        validate_supervisor_namespace(request.supervisor, request.run_uid)
        controller = Controller(prefix_fd, prefix, request, request_source_inode, output_fd, game_fd)
        previous_handlers = {
            signal.SIGINT: signal.getsignal(signal.SIGINT),
            signal.SIGTERM: signal.getsignal(signal.SIGTERM),
        }
        def stop(_signum: int, _frame: Any) -> None:
            raise ShutdownRequested()
        signal.signal(signal.SIGINT, stop)
        signal.signal(signal.SIGTERM, stop)
        try:
            controller.reset_phase_deadline()
            controller.prepare()
            send_ack(channel, ACK_PREPARED)
            hold_deadline = time.monotonic() + request.hold_timeout_seconds
            if not receive_command(channel, b"seeded\n", hold_deadline - time.monotonic()):
                reject()
            controller.reset_phase_deadline()
            controller.seeded()
            send_ack(channel, ACK_READY)
            if not receive_command(channel, b"arm\n", hold_deadline - time.monotonic()):
                reject()
            controller.reset_phase_deadline()
            controller.arm()
            send_ack(channel, ACK_ARMED)
            requested = receive_command(channel, b"cleanup\n", hold_deadline - time.monotonic())
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
        if request_fd >= 0:
            os.close(request_fd)
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
