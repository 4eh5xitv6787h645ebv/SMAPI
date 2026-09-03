#!/usr/bin/env python3
"""Validate and admit one fixture-free Linux GUI hard-state qualification run."""

from __future__ import annotations

import array
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import sys
from types import ModuleType
from typing import Any, Iterable


SCHEMA_VERSION = 2
MARKER_NAME = ".smapi-linux-gui-hard-state-disposable-v1.json"
MARKER_PURPOSE = "smapi-linux-gui-hard-state-disposable-root"
SCENARIOS = frozenset({
    "E2-permission",
    "E2-read-only",
    "E2-disk-full",
    "E2-cross-device",
    "C2",
    "C3",
    "E5",
    "E6",
})
E2_SCENARIOS = frozenset(value for value in SCENARIOS if value.startswith("E2-"))
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
GIT_OBJECT_RE = re.compile(r"^[0-9a-f]{40}$")
VERSION_RE = re.compile(
    r"^(?P<base>[0-9]+\.[0-9]+\.[0-9]+)-unofficial\.4eh5xitv6787h645ebv\.linux\.alpha\.(?P<alpha>[1-9][0-9]*)$"
)
SAFE_OUTPUT_NAME_RE = re.compile(r"^[a-z0-9][a-z0-9._-]{7,127}$")
FORBIDDEN_KEYS = frozenset({
    "access_token", "api_key", "baseline", "bearer", "credential", "denylist",
    "fixture", "github_token", "modpack", "password", "private", "private_key", "save",
    "secret", "token",
})
FORBIDDEN_VALUE_PATTERNS = (
    re.compile(r"\b(?:gh[opsu]_[A-Za-z0-9_]{12,}|github_pat_[A-Za-z0-9_]{12,})\b"),
    re.compile(r"\bBearer\s+[A-Za-z0-9._~+/-]{8,}", re.IGNORECASE),
    re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
)
LIVE_COMPONENTS = frozenset({
    ".steam", "steam", "steamapps", "stardew valley", "stardewvalley", "mods", "saves",
})
MAX_CONTRACT_BYTES = 64 * 1024
MAX_PACKAGE_BYTES = 4 * 1024 * 1024 * 1024
MAX_GAME_MARKER_BYTES = 16 * 1024 * 1024
MAX_MOUNTINFO_BYTES = 4 * 1024 * 1024
MAX_SYSFS_BYTES = 4096
CAPTURE_POLICY = "exact-window-v1"
OUTPUT_BYTES_LIMIT = 1024 * 1024 * 1024
OUTPUT_QUOTA_ENV = "SMAPI_HARD_STATE_OUTPUT_QUOTA_TOKEN"
OUTPUT_QUOTA_MARKER = ".smapi-hard-state-output-quota-v1.json"
OUTPUT_QUOTA_PURPOSE = "smapi-hard-state-output-quota"
FS_IOC_GETFLAGS = 0x80086601
FS_IMMUTABLE_FL = 0x00000010
TOKEN_RE = re.compile(r"^[0-9a-f]{64}$")
STABLE_FILE_FIELDS = (
    "st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size",
    "st_mtime_ns", "st_ctime_ns",
)
CAPTURE_MODEL_PATH = Path(__file__).with_name("linux_gui_hard_state_capture_contract.py")


def load_capture_model() -> ModuleType:
    module_name = "smapi_linux_gui_hard_state_capture_contract"
    existing = sys.modules.get(module_name)
    if existing is not None:
        return existing
    specification = importlib.util.spec_from_file_location(module_name, CAPTURE_MODEL_PATH)
    if specification is None or specification.loader is None:
        raise RuntimeError("capture contract model unavailable")
    module = importlib.util.module_from_spec(specification)
    sys.modules[module_name] = module
    specification.loader.exec_module(module)
    return module


CAPTURE_MODEL = load_capture_model()
ENVIRONMENT_PROFILES = frozenset(profile.profile_id.value for profile in CAPTURE_MODEL.ENVIRONMENT_PROFILES)


class InputError(Exception):
    """A rejected caller-controlled input, represented publicly only by a stable code."""

    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


def reject(code: str) -> None:
    raise InputError(code)


def exact_object(value: Any, required: Iterable[str], code: str) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != set(required):
        reject(code)
    return value


def integer(value: Any, minimum: int, maximum: int, code: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or not minimum <= value <= maximum:
        reject(code)
    return value


def fixed_bool(value: Any, expected: bool, code: str) -> None:
    if value is not expected:
        reject(code)


def fixed_text(value: Any, pattern: re.Pattern[str], code: str) -> str:
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        reject(code)
    return value


def no_duplicate_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            reject("contract-json")
        result[key] = value
    return result


def iter_items(value: Any) -> Iterable[tuple[str | None, Any]]:
    if isinstance(value, dict):
        for key, nested in value.items():
            yield key, nested
            yield from iter_items(nested)
    elif isinstance(value, list):
        for nested in value:
            yield None, nested
            yield from iter_items(nested)


def reject_private_inputs(value: Any) -> None:
    for key, nested in iter_items(value):
        if key is not None:
            folded = key.casefold().replace("-", "_")
            if any(part == folded or part in folded.split("_") for part in FORBIDDEN_KEYS):
                reject("forbidden-input")
        if isinstance(nested, str):
            if any(ord(character) < 0x20 for character in nested):
                reject("forbidden-input")
            if any(pattern.search(nested) for pattern in FORBIDDEN_VALUE_PATTERNS):
                reject("forbidden-input")


def normalized_absolute_path(value: Any, code: str) -> Path:
    if not isinstance(value, str) or not value or "\x00" in value:
        reject(code)
    candidate = Path(value)
    if not candidate.is_absolute() or str(candidate) != value or ".." in candidate.parts:
        reject(code)
    return candidate


def parse_contract_bytes(raw: bytes) -> dict[str, Any]:
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=no_duplicate_object)
    except InputError:
        raise
    except (UnicodeError, json.JSONDecodeError):
        reject("contract-json")
    if not isinstance(value, dict):
        reject("contract-schema")
    reject_private_inputs(value)
    return value


def read_contract(path: Path) -> dict[str, Any]:
    descriptor = -1
    try:
        metadata = path.lstat()
        resolved = path.resolve(strict=True)
        if (
            resolved != path
            or not stat.S_ISREG(metadata.st_mode)
            or metadata.st_nlink != 1
            or metadata.st_uid != os.geteuid()
            or stat.S_IMODE(metadata.st_mode) != 0o600
            or metadata.st_size > MAX_CONTRACT_BYTES
        ):
            reject("contract-file")
        flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(path, flags)
        opened = os.fstat(descriptor)
        raw = bytearray()
        while len(raw) <= MAX_CONTRACT_BYTES:
            block = os.read(descriptor, min(4096, MAX_CONTRACT_BYTES + 1 - len(raw)))
            if not block:
                break
            raw.extend(block)
        after = os.fstat(descriptor)
        fields = ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
        if (
            len(raw) > MAX_CONTRACT_BYTES or len(raw) != metadata.st_size
            or any(getattr(metadata, field) != getattr(opened, field) for field in fields)
            or any(getattr(opened, field) != getattr(after, field) for field in fields)
        ):
            reject("contract-file")
        value = parse_contract_bytes(bytes(raw))
    except InputError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError):
        reject("contract-json")
    finally:
        if descriptor >= 0:
            os.close(descriptor)
    return value


def validate_release(value: Any) -> tuple[str, str]:
    release = exact_object(
        value,
        ("version", "tag", "url", "expected_commit", "expected_tree"),
        "release",
    )
    version = fixed_text(release["version"], VERSION_RE, "release")
    match = VERSION_RE.fullmatch(version)
    assert match is not None
    expected_tag = (
        f"fork-4eh5xitv6787h645ebv-linux-v{match.group('base')}-alpha.{match.group('alpha')}"
    )
    tag = release["tag"]
    url = release["url"]
    if tag != expected_tag or url != f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/{expected_tag}":
        reject("release")
    fixed_text(release["expected_commit"], GIT_OBJECT_RE, "release")
    fixed_text(release["expected_tree"], GIT_OBJECT_RE, "release")
    return version, expected_tag


def validate_regular_package(value: Any, version: str) -> None:
    package = exact_object(value, ("path", "size_bytes", "sha256"), "package-file")
    path = normalized_absolute_path(package["path"], "package-file")
    expected_name = f"SMAPI-{version}-linux-x64-installer.zip"
    if path.name != expected_name:
        reject("package-file")
    expected_size = integer(package["size_bytes"], 4, MAX_PACKAGE_BYTES, "package-file")
    expected_digest = fixed_text(package["sha256"], SHA256_RE, "digest")
    try:
        metadata = path.lstat()
        resolved = path.resolve(strict=True)
        if (
            resolved != path
            or not stat.S_ISREG(metadata.st_mode)
            or metadata.st_nlink != 1
            or metadata.st_uid != os.geteuid()
            or stat.S_IMODE(metadata.st_mode) & 0o022
            or metadata.st_size != expected_size
            or metadata.st_size > MAX_PACKAGE_BYTES
        ):
            reject("package-file")
        flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(path, flags)
        digest = hashlib.sha256()
        prefix = b""
        try:
            opened = os.fstat(descriptor)
            if any(getattr(metadata, field) != getattr(opened, field) for field in STABLE_FILE_FIELDS):
                reject("package-file")
            while True:
                block = os.read(descriptor, 1024 * 1024)
                if not block:
                    break
                if len(prefix) < 4:
                    prefix += block[:4 - len(prefix)]
                digest.update(block)
            final = os.fstat(descriptor)
        finally:
            os.close(descriptor)
        if (
            any(getattr(opened, field) != getattr(final, field) for field in STABLE_FILE_FIELDS)
            or prefix not in (b"PK\x03\x04", b"PK\x05\x06", b"PK\x07\x08")
        ):
            reject("package-file")
        if digest.hexdigest() != expected_digest:
            reject("package-mismatch")
    except InputError:
        raise
    except OSError:
        reject("package-file")


def validate_game_marker(value: Any) -> None:
    marker = exact_object(value, ("path", "size_bytes", "sha256"), "game-marker")
    path = normalized_absolute_path(marker["path"], "game-marker")
    if path.name != "Stardew Valley.dll":
        reject("game-marker")
    expected_size = integer(marker["size_bytes"], 2, MAX_GAME_MARKER_BYTES, "game-marker")
    expected_digest = fixed_text(marker["sha256"], SHA256_RE, "game-marker")
    try:
        metadata = path.lstat()
        resolved = path.resolve(strict=True)
        if (
            resolved != path
            or not stat.S_ISREG(metadata.st_mode)
            or metadata.st_nlink != 1
            or metadata.st_uid != os.geteuid()
            or stat.S_IMODE(metadata.st_mode) & 0o022
            or metadata.st_size != expected_size
            or metadata.st_size > MAX_GAME_MARKER_BYTES
        ):
            reject("game-marker")
        flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(path, flags)
        digest = hashlib.sha256()
        prefix = b""
        try:
            opened = os.fstat(descriptor)
            if any(getattr(metadata, field) != getattr(opened, field) for field in STABLE_FILE_FIELDS):
                reject("game-marker")
            while True:
                block = os.read(descriptor, 1024 * 1024)
                if not block:
                    break
                if len(prefix) < 2:
                    prefix += block[:2 - len(prefix)]
                digest.update(block)
            final = os.fstat(descriptor)
        finally:
            os.close(descriptor)
        if (
            any(getattr(opened, field) != getattr(final, field) for field in STABLE_FILE_FIELDS)
            or prefix != b"MZ"
        ):
            reject("game-marker")
        if digest.hexdigest() != expected_digest:
            reject("game-marker-mismatch")
    except InputError:
        raise
    except OSError:
        reject("game-marker")


def validate_binaries(value: Any) -> None:
    binaries = exact_object(value, ("apphost_sha256", "backend_sha256"), "digest")
    fixed_text(binaries["apphost_sha256"], SHA256_RE, "digest")
    fixed_text(binaries["backend_sha256"], SHA256_RE, "digest")


def validate_capture(value: Any) -> None:
    capture = exact_object(value, ("policy", "environment_profile"), "capture")
    if capture["policy"] != CAPTURE_POLICY:
        reject("capture")
    try:
        profile = CAPTURE_MODEL.environment_profile(capture["environment_profile"])
    except (KeyError, TypeError, ValueError):
        reject("capture")
    if capture["environment_profile"] != profile.profile_id.value:
        reject("capture")


def validate_resource_limits(value: Any) -> None:
    limits = exact_object(value, ("output_bytes",), "resource-limits")
    integer(limits["output_bytes"], OUTPUT_BYTES_LIMIT, OUTPUT_BYTES_LIMIT, "resource-limits")


def sensitive_root(path: Path, repository_root: Path) -> bool:
    if path == Path("/"):
        return True
    for protected in (Path("/tmp"), Path("/var/tmp"), Path("/home"), Path.home(), repository_root):
        if path == protected or protected in path.parents:
            return True
    return any(part.casefold() in LIVE_COMPONENTS for part in path.parts)


def validate_marker(root_fd: int, root_stat: os.stat_result) -> None:
    flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(MARKER_NAME, flags, dir_fd=root_fd)
        try:
            metadata = os.fstat(descriptor)
            raw = os.read(descriptor, 4097)
        finally:
            os.close(descriptor)
        if (
            not stat.S_ISREG(metadata.st_mode)
            or metadata.st_nlink != 1
            or metadata.st_uid != os.geteuid()
            or stat.S_IMODE(metadata.st_mode) != 0o600
            or len(raw) > 4096
        ):
            reject("marker")
        marker = json.loads(raw.decode("utf-8"), object_pairs_hook=no_duplicate_object)
    except InputError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError):
        reject("marker")
    marker = exact_object(marker, ("schema_version", "purpose", "root_device", "root_inode"), "marker")
    if (
        marker["schema_version"] != SCHEMA_VERSION
        or marker["purpose"] != MARKER_PURPOSE
        or marker["root_device"] != root_stat.st_dev
        or marker["root_inode"] != root_stat.st_ino
    ):
        reject("marker")


def validate_isolation(value: Any, output: Path, repository_root: Path) -> tuple[int, Path]:
    isolation = exact_object(
        value,
        ("disposable_root", "root_device", "root_inode", "disposable_vm", "live_roots_mounted",
         "installer_runs_as_root", "privileged_setup_confined_to_vm", "allow_privileged_fault_setup"),
        "isolation",
    )
    root = normalized_absolute_path(isolation["disposable_root"], "unsafe-root")
    if sensitive_root(root, repository_root):
        reject("unsafe-root")
    if output.parent != root or not SAFE_OUTPUT_NAME_RE.fullmatch(output.name):
        reject("unsafe-output")
    expected_device = integer(isolation["root_device"], 0, 2**64 - 1, "isolation")
    expected_inode = integer(isolation["root_inode"], 1, 2**64 - 1, "isolation")
    fixed_bool(isolation["disposable_vm"], True, "boundary")
    fixed_bool(isolation["live_roots_mounted"], False, "boundary")
    fixed_bool(isolation["installer_runs_as_root"], False, "boundary")
    fixed_bool(isolation["privileged_setup_confined_to_vm"], True, "boundary")
    root_fd = -1
    try:
        metadata = root.lstat()
        resolved = root.resolve(strict=True)
        if (
            resolved != root
            or not stat.S_ISDIR(metadata.st_mode)
            or metadata.st_uid != os.geteuid()
            or stat.S_IMODE(metadata.st_mode) != 0o700
            or (metadata.st_dev, metadata.st_ino) != (expected_device, expected_inode)
        ):
            reject("unsafe-root")
        flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        root_fd = os.open(root, flags)
        opened = os.fstat(root_fd)
        if (opened.st_dev, opened.st_ino) != (expected_device, expected_inode):
            reject("unsafe-root")
        entries = set(os.listdir(root_fd))
        if entries not in ({MARKER_NAME}, {MARKER_NAME, output.name}):
            reject("unsafe-root")
        validate_marker(root_fd, opened)
        return root_fd, root
    except InputError:
        if root_fd >= 0:
            os.close(root_fd)
        raise
    except OSError:
        if root_fd >= 0:
            os.close(root_fd)
        reject("unsafe-root")


def validate_boundaries_for_scenario(value: Any, scenario: str) -> None:
    expected = scenario in E2_SCENARIOS
    if value is not expected:
        reject("boundary")


def validate_timeouts(value: Any) -> None:
    timeouts = exact_object(value, ("startup", "operation", "settlement", "cleanup", "total"), "timeout")
    startup = integer(timeouts["startup"], 5, 120, "timeout")
    operation = integer(timeouts["operation"], 10, 900, "timeout")
    settlement = integer(timeouts["settlement"], 5, 300, "timeout")
    cleanup = integer(timeouts["cleanup"], 5, 120, "timeout")
    total = integer(timeouts["total"], 25, 1800, "timeout")
    if total < startup + operation + settlement + cleanup:
        reject("timeout")


def create_output(root_fd: int, output: Path, expected_root: tuple[int, int]) -> None:
    created = False
    try:
        if os.stat(output.name, dir_fd=root_fd, follow_symlinks=False):
            reject("output-exists")
    except FileNotFoundError:
        pass
    except InputError:
        raise
    except OSError:
        reject("unsafe-output")
    try:
        os.mkdir(output.name, 0o700, dir_fd=root_fd)
        created = True
        flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        output_fd = os.open(output.name, flags, dir_fd=root_fd)
        try:
            os.fchmod(output_fd, 0o700)
            metadata = os.fstat(output_fd)
            if (
                not stat.S_ISDIR(metadata.st_mode)
                or metadata.st_uid != os.geteuid()
                or stat.S_IMODE(metadata.st_mode) != 0o700
                or (os.fstat(root_fd).st_dev, os.fstat(root_fd).st_ino) != expected_root
            ):
                reject("output-create")
        finally:
            os.close(output_fd)
    except FileExistsError:
        reject("output-exists")
    except InputError:
        if created:
            try:
                os.rmdir(output.name, dir_fd=root_fd)
            except OSError:
                pass
        raise
    except OSError:
        if created:
            try:
                os.rmdir(output.name, dir_fd=root_fd)
            except OSError:
                pass
        reject("output-create")


def mountinfo_unescape(value: str) -> str:
    def replace(match: re.Match[str]) -> str:
        return chr(int(match.group(1), 8))

    return re.sub(r"\\([0-7]{3})", replace, value)


def read_bounded_pseudo(path: Path, maximum: int, encoding: str) -> str:
    descriptor = -1
    try:
        descriptor = os.open(
            path,
            os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0) | os.O_NONBLOCK,
        )
        raw = bytearray()
        while len(raw) <= maximum:
            block = os.read(descriptor, min(4096, maximum + 1 - len(raw)))
            if not block:
                break
            raw.extend(block)
        if not raw or len(raw) > maximum:
            reject("output-quota")
        return bytes(raw).decode(encoding)
    except InputError:
        raise
    except (OSError, UnicodeError):
        reject("output-quota")
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def prepared_mount_record(output: Path) -> tuple[str, str, frozenset[str], tuple[int, int]]:
    try:
        records: list[tuple[str, str, frozenset[str], tuple[int, int]]] = []
        for line in read_bounded_pseudo(
            Path("/proc/self/mountinfo"), MAX_MOUNTINFO_BYTES, "utf-8",
        ).splitlines():
            before, separator, after = line.partition(" - ")
            fields = before.split()
            filesystem = after.split()
            if not separator or len(fields) < 6 or len(filesystem) < 3:
                continue
            if mountinfo_unescape(fields[4]) != os.fspath(output):
                continue
            major_text, colon, minor_text = fields[2].partition(":")
            if not colon:
                reject("output-quota")
            options = frozenset(fields[5].split(",")) | frozenset(filesystem[2].split(","))
            records.append((filesystem[0], mountinfo_unescape(filesystem[1]), options, (int(major_text), int(minor_text))))
        if len(records) != 1:
            reject("output-exists")
        return records[0]
    except InputError:
        raise
    except (OSError, UnicodeError, ValueError):
        reject("output-quota")


def loop_device_facts(source: str, device: tuple[int, int]) -> tuple[os.stat_result, int, bool, os.stat_result]:
    try:
        source_path = Path(source)
        if re.fullmatch(r"/dev/loop[0-9]+", source) is None or source_path.resolve(strict=True) != source_path:
            reject("output-quota")
        source_status = source_path.lstat()
        if (
            not stat.S_ISBLK(source_status.st_mode)
            or (os.major(source_status.st_rdev), os.minor(source_status.st_rdev)) != device
        ):
            reject("output-quota")
        sysfs = Path(f"/sys/dev/block/{device[0]}:{device[1]}")
        size_raw = read_bounded_pseudo(sysfs / "size", MAX_SYSFS_BYTES, "ascii")
        autoclear_raw = read_bounded_pseudo(sysfs / "loop/autoclear", MAX_SYSFS_BYTES, "ascii")
        backing_raw = read_bounded_pseudo(
            sysfs / "loop/backing_file", MAX_SYSFS_BYTES, "utf-8",
        ).rstrip("\n")
        if re.fullmatch(r"[0-9]+\n", size_raw) is None or autoclear_raw != "1\n" or not backing_raw:
            reject("output-quota")
        backing_text = mountinfo_unescape(backing_raw)
        backing = Path(backing_text if backing_text.startswith("/") else "/" + backing_text)
        if not backing.is_absolute() or ".." in backing.parts:
            reject("output-quota")
        return source_status, int(size_raw) * 512, True, backing.lstat()
    except InputError:
        raise
    except (OSError, UnicodeError, ValueError):
        reject("output-quota")


def prepared_observation(output_fd: int, output: Path) -> tuple[Any, ...]:
    output_status = os.fstat(output_fd)
    filesystem, source, options, device = prepared_mount_record(output)
    source_status, source_bytes, autoclear, backing_status = loop_device_facts(source, device)
    values = os.statvfs(output_fd)
    return (
        output_status, filesystem, source, options, device, values,
        source_status, source_bytes, autoclear, backing_status,
    )


def observation_signature(observation: tuple[Any, ...]) -> tuple[Any, ...]:
    (
        output_status, filesystem, source, options, device, values,
        source_status, source_bytes, autoclear, backing_status,
    ) = observation
    return (
        tuple(getattr(output_status, field) for field in STABLE_FILE_FIELDS),
        filesystem, source, options, device,
        tuple(getattr(values, field) for field in (
            "f_bsize", "f_frsize", "f_blocks", "f_bfree", "f_bavail", "f_files",
            "f_ffree", "f_favail", "f_flag", "f_namemax",
        )),
        tuple(getattr(source_status, field) for field in (
            "st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_rdev",
        )),
        source_bytes, autoclear,
        tuple(getattr(backing_status, field) for field in (
            "st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size",
        )),
    )


def validate_prepared_output_facts(
    output_status: os.stat_result,
    filesystem: str,
    source: str,
    options: frozenset[str],
    device: tuple[int, int],
    values: os.statvfs_result,
    marker_status: os.stat_result,
    marker_flags: int,
    marker: dict[str, Any],
    token: str,
    source_status: os.stat_result,
    source_bytes: int,
    autoclear: bool,
    backing_status: os.stat_result,
) -> None:
    if (
        not stat.S_ISDIR(output_status.st_mode)
        or output_status.st_uid != os.geteuid() or output_status.st_gid != os.getegid()
        or stat.S_IMODE(output_status.st_mode) != 0o700
        or filesystem != "ext4" or re.fullmatch(r"/dev/loop[0-9]+", source) is None
        or not {"rw", "nosuid", "nodev"}.issubset(options) or "noexec" in options
        or not stat.S_ISBLK(source_status.st_mode)
        or (os.major(source_status.st_rdev), os.minor(source_status.st_rdev)) != device
        or source_bytes != OUTPUT_BYTES_LIMIT or not autoclear
        or values.f_frsize <= 0 or values.f_blocks <= 0
        or values.f_frsize * values.f_blocks > OUTPUT_BYTES_LIMIT
        or not stat.S_ISREG(marker_status.st_mode) or marker_status.st_uid != 0
        or marker_status.st_nlink != 1 or stat.S_IMODE(marker_status.st_mode) != 0o444
        or marker_flags & FS_IMMUTABLE_FL == 0
        or not stat.S_ISREG(backing_status.st_mode) or backing_status.st_uid != 0
        or backing_status.st_nlink != 1 or stat.S_IMODE(backing_status.st_mode) != 0o600
    ):
        reject("output-quota")
    marker = exact_object(marker, (
        "filesystem", "imageDevice", "imageInode", "imageSize", "limitBytes",
        "mountDeviceMajor", "mountDeviceMinor", "outputDevice", "outputInode",
        "purpose", "schemaVersion", "token",
    ), "output-quota")
    for key in (
        "imageDevice", "imageInode", "imageSize", "limitBytes", "mountDeviceMajor",
        "mountDeviceMinor", "outputDevice", "outputInode", "schemaVersion",
    ):
        if isinstance(marker[key], bool) or not isinstance(marker[key], int):
            reject("output-quota")
    if (
        marker["filesystem"] != "ext4" or marker["purpose"] != OUTPUT_QUOTA_PURPOSE
        or marker["schemaVersion"] != 1 or marker["token"] != token
        or marker["imageDevice"] < 0 or marker["imageInode"] <= 0
        or marker["imageSize"] != OUTPUT_BYTES_LIMIT or marker["limitBytes"] != OUTPUT_BYTES_LIMIT
        or (marker["imageDevice"], marker["imageInode"], marker["imageSize"])
            != (backing_status.st_dev, backing_status.st_ino, backing_status.st_size)
        or (marker["mountDeviceMajor"], marker["mountDeviceMinor"]) != device
        or (marker["outputDevice"], marker["outputInode"]) != (output_status.st_dev, output_status.st_ino)
    ):
        reject("output-quota")


def validate_prepared_output(root_fd: int, output: Path, expected_root: tuple[int, int]) -> None:
    token = os.environ.get(OUTPUT_QUOTA_ENV)
    if token is None or TOKEN_RE.fullmatch(token) is None:
        reject("output-exists")
    output_fd = marker_fd = -1
    try:
        output_fd = os.open(
            output.name,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0),
            dir_fd=root_fd,
        )
        first = prepared_observation(output_fd, output)
        (
            output_status, filesystem, source, options, device, values,
            source_status, source_bytes, autoclear, backing_status,
        ) = first
        named = os.stat(output.name, dir_fd=root_fd, follow_symlinks=False)
        if (
            (output_status.st_dev, output_status.st_ino) != (named.st_dev, named.st_ino)
            or (os.fstat(root_fd).st_dev, os.fstat(root_fd).st_ino) != expected_root
        ):
            reject("output-quota")
        marker_fd = os.open(
            OUTPUT_QUOTA_MARKER,
            os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0),
            dir_fd=output_fd,
        )
        marker_status = os.fstat(marker_fd)
        raw = os.read(marker_fd, 4097)
        repeated = os.fstat(marker_fd)
        if (
            not stat.S_ISREG(marker_status.st_mode) or marker_status.st_uid != 0
            or marker_status.st_nlink != 1 or stat.S_IMODE(marker_status.st_mode) != 0o444
            or not 0 < marker_status.st_size <= 4096 or len(raw) != marker_status.st_size
            or any(getattr(marker_status, field) != getattr(repeated, field) for field in STABLE_FILE_FIELDS)
        ):
            reject("output-quota")
        flags = array.array("I", [0])
        fcntl.ioctl(marker_fd, FS_IOC_GETFLAGS, flags, True)
        marker = json.loads(raw.decode("ascii"), object_pairs_hook=no_duplicate_object)
        validate_prepared_output_facts(
            output_status, filesystem, source, options, device, values,
            marker_status, flags[0], marker, token,
            source_status, source_bytes, autoclear, backing_status,
        )
        second = prepared_observation(output_fd, output)
        (
            repeated_output, repeated_filesystem, repeated_source, repeated_options,
            repeated_device, repeated_values, repeated_source_status, repeated_source_bytes,
            repeated_autoclear, repeated_backing_status,
        ) = second
        repeated_named = os.stat(output.name, dir_fd=root_fd, follow_symlinks=False)
        repeated_root = os.fstat(root_fd)
        if (
            observation_signature(first) != observation_signature(second)
            or (repeated_output.st_dev, repeated_output.st_ino)
                != (repeated_named.st_dev, repeated_named.st_ino)
            or (repeated_root.st_dev, repeated_root.st_ino) != expected_root
        ):
            reject("output-quota")
        validate_prepared_output_facts(
            repeated_output, repeated_filesystem, repeated_source, repeated_options,
            repeated_device, repeated_values, marker_status, flags[0], marker, token,
            repeated_source_status, repeated_source_bytes, repeated_autoclear,
            repeated_backing_status,
        )
        if os.environ.pop(OUTPUT_QUOTA_ENV, None) != token:
            reject("output-quota")
    except InputError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError):
        reject("output-quota")
    finally:
        if marker_fd >= 0:
            os.close(marker_fd)
        if output_fd >= 0:
            os.close(output_fd)


def validate_contract(
    contract: dict[str, Any],
    output: Path,
    *,
    require_prepared_output: bool = False,
) -> str:
    repository_root = Path(__file__).resolve().parents[2]
    contract = exact_object(
        contract,
        ("schema_version", "scenario", "release", "package", "game_marker", "binaries", "capture",
         "isolation", "timeouts_seconds", "resource_limits"),
        "contract-schema",
    )
    if contract["schema_version"] != SCHEMA_VERSION:
        reject("contract-schema")
    scenario = contract["scenario"]
    if not isinstance(scenario, str) or scenario not in SCENARIOS:
        reject("scenario")
    version, _tag = validate_release(contract["release"])
    validate_regular_package(contract["package"], version)
    validate_game_marker(contract["game_marker"])
    validate_binaries(contract["binaries"])
    validate_capture(contract["capture"])
    validate_resource_limits(contract["resource_limits"])
    output = normalized_absolute_path(str(output), "unsafe-output")
    root_fd, _root = validate_isolation(contract["isolation"], output, repository_root)
    try:
        validate_boundaries_for_scenario(contract["isolation"]["allow_privileged_fault_setup"], scenario)
        validate_timeouts(contract["timeouts_seconds"])
        root_stat = os.fstat(root_fd)
        try:
            os.stat(output.name, dir_fd=root_fd, follow_symlinks=False)
        except FileNotFoundError:
            if require_prepared_output:
                reject("output-quota")
            create_output(root_fd, output, (root_stat.st_dev, root_stat.st_ino))
        except OSError:
            reject("unsafe-output")
        else:
            validate_prepared_output(root_fd, output, (root_stat.st_dev, root_stat.st_ino))
    finally:
        os.close(root_fd)
    return scenario


def validate(contract_path: Path, output: Path) -> str:
    return validate_contract(read_contract(contract_path), output)


def parse_cli(arguments: list[str]) -> tuple[Path, Path]:
    if len(arguments) != 4:
        reject("usage")
    values: dict[str, str] = {}
    for index in (0, 2):
        flag = arguments[index]
        if flag not in ("--contract", "--output") or flag in values:
            reject("usage")
        values[flag] = arguments[index + 1]
    if set(values) != {"--contract", "--output"}:
        reject("usage")
    return normalized_absolute_path(values["--contract"], "contract-file"), normalized_absolute_path(values["--output"], "unsafe-output")


def emit(payload: dict[str, Any]) -> None:
    data = (json.dumps(payload, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")
    os.write(sys.stdout.fileno(), data)


def main(arguments: list[str]) -> int:
    try:
        contract_path, output = parse_cli(arguments)
        scenario = validate(contract_path, output)
        emit({"ok": True, "scenario": scenario, "schemaVersion": SCHEMA_VERSION, "status": "validated"})
        return 0
    except InputError as error:
        emit({"code": error.code, "ok": False, "schemaVersion": SCHEMA_VERSION, "status": "rejected"})
        return 2
    except KeyboardInterrupt:
        emit({"code": "interrupted", "ok": False, "schemaVersion": SCHEMA_VERSION, "status": "rejected"})
        return 130
    except BaseException:
        emit({"code": "internal-error", "ok": False, "schemaVersion": SCHEMA_VERSION, "status": "rejected"})
        return 70


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
