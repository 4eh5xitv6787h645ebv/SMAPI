#!/usr/bin/env python3
"""Prepare one closed, fixture-free Linux GUI hard-state qualification contract."""

from __future__ import annotations

import base64
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
from typing import Any, NoReturn
import zipfile


SCHEMA_VERSION = 1
REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
SYNTHETIC_MARKER_FIXTURE = REPOSITORY_ROOT / "build/fixtures/linux-gui-hard-state/Stardew Valley.dll.base64"
SYNTHETIC_MARKER_ENCODED_SHA256 = "0d73da2d4c7e7c7553033e359a6e1808c0e0be5ccd5f0f6fa781c7efc23fd0cf"
SYNTHETIC_MARKER_DECODED_SHA256 = "8617cb5b0132c275d2db285d7a6475ea326a2387f1fe98cb0c4d4218c6a15744"
SYNTHETIC_MARKER_DECODED_BYTES = 3584
MARKER_NAME = ".smapi-linux-gui-hard-state-disposable-v1.json"
MARKER_PURPOSE = "smapi-linux-gui-hard-state-disposable-root"
SCENARIOS = frozenset({
    "E2-permission", "E2-read-only", "E2-disk-full", "E2-cross-device",
    "C2", "C3", "E5", "E6",
})
E2_SCENARIOS = frozenset(value for value in SCENARIOS if value.startswith("E2-"))
VERSION_RE = re.compile(
    r"^(?P<base>[0-9]+\.[0-9]+\.[0-9]+)-unofficial\.4eh5xitv6787h645ebv\.linux\.alpha\.(?P<alpha>[1-9][0-9]*)$"
)
GIT_OBJECT_RE = re.compile(r"^[0-9a-f]{40}$")
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
CASE_NAME_RE = re.compile(r"^smapi-hard-state-[0-9a-f]{32}$")
SAFE_ARCHIVE_ROOT_RE = re.compile(r"^SMAPI [0-9A-Za-z._-]+ Linux installer$")
LIVE_COMPONENTS = frozenset({
    ".steam", "steam", "steamapps", "stardew valley", "stardewvalley", "mods", "saves",
})
MAX_PACKAGE_BYTES = 4 * 1024 * 1024 * 1024
MAX_ARCHIVE_ENTRIES = 20_000
MAX_ARCHIVE_ENTRY_BYTES = 512 * 1024 * 1024
MAX_ARCHIVE_EXPANDED_BYTES = 2 * 1024 * 1024 * 1024
MAX_COMPRESSION_RATIO = 100
MAX_ARCHIVE_NAME_BYTES = 1024
MAX_COMPONENT_BYTES = 240
TARGETS = {
    "apphost_sha256": "internal/linux/SMAPI.Installer.Gui",
    "backend_sha256": "internal/linux/SMAPI.Installer",
}
TIMEOUTS = {"startup": 120, "operation": 900, "settlement": 300, "cleanup": 120, "total": 1440}


class PreparationError(Exception):
    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


def reject(code: str) -> NoReturn:
    raise PreparationError(code)


def normalized_path(raw: str, code: str) -> Path:
    if not isinstance(raw, str) or not raw or any(ord(character) < 0x20 for character in raw):
        reject(code)
    path = Path(raw)
    if not path.is_absolute() or os.fspath(path) != raw or ".." in path.parts:
        reject(code)
    return path


def is_sensitive(path: Path, repository_root: Path, *, include_home: bool) -> bool:
    protected = [Path("/"), Path("/tmp"), Path("/var/tmp"), Path("/home"), repository_root]
    if include_home:
        protected.append(Path.home())
    if path in protected or repository_root in path.parents:
        return True
    if include_home and any(item in path.parents for item in (Path("/tmp"), Path("/var/tmp"), Path("/home"), Path.home())):
        return True
    return any(part.casefold() in LIVE_COMPONENTS for part in path.parts)


def stable_fields(left: os.stat_result, right: os.stat_result) -> bool:
    fields = ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
    return all(getattr(left, field) == getattr(right, field) for field in fields)


def open_owned_regular(path: Path, maximum: int, code: str) -> tuple[int, os.stat_result]:
    descriptor = -1
    try:
        if not path.is_absolute() or ".." in path.parts or path.resolve(strict=True) != path:
            reject(code)
        descriptor = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
        metadata = os.fstat(descriptor)
        if (
            not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1
            or metadata.st_uid != os.geteuid() or metadata.st_size <= 0 or metadata.st_size > maximum
            or metadata.st_mode & 0o7022
        ):
            reject(code)
        return descriptor, metadata
    except PreparationError:
        if descriptor >= 0:
            os.close(descriptor)
        raise
    except OSError:
        if descriptor >= 0:
            os.close(descriptor)
        reject(code)


def digest_descriptor(descriptor: int, expected: os.stat_result, code: str) -> str:
    try:
        os.lseek(descriptor, 0, os.SEEK_SET)
        digest = hashlib.sha256()
        remaining = expected.st_size
        while remaining:
            block = os.read(descriptor, min(1024 * 1024, remaining))
            if not block:
                reject(code)
            digest.update(block)
            remaining -= len(block)
        if os.read(descriptor, 1):
            reject(code)
        if not stable_fields(expected, os.fstat(descriptor)):
            reject(code)
        return digest.hexdigest()
    except PreparationError:
        raise
    except OSError:
        reject(code)


def archive_path(name: str, expected_root: str) -> PurePosixPath:
    try:
        encoded = name.encode("utf-8")
    except UnicodeError:
        reject("package")
    if (
        not name or len(encoded) > MAX_ARCHIVE_NAME_BYTES or "\\" in name or "\x00" in name
        or name.startswith("/")
    ):
        reject("package")
    path = PurePosixPath(name.rstrip("/"))
    if (
        not path.parts or path.parts[0] != expected_root
        or any(part in ("", ".", "..") or len(part.encode("utf-8")) > MAX_COMPONENT_BYTES for part in path.parts)
    ):
        reject("package")
    return path


def inspect_package(
    path: Path,
    version: str,
    expected_sha256: str,
) -> tuple[int, str, dict[str, str]]:
    descriptor, opened = open_owned_regular(path, MAX_PACKAGE_BYTES, "package")
    try:
        if path.name != f"SMAPI-{version}-linux-x64-installer.zip":
            reject("package")
        package_digest = digest_descriptor(descriptor, opened, "package")
        if package_digest != expected_sha256:
            reject("package-mismatch")
        expected_root = f"SMAPI {version} Linux installer"
        if SAFE_ARCHIVE_ROOT_RE.fullmatch(expected_root) is None:
            reject("release")
        os.lseek(descriptor, 0, os.SEEK_SET)
        with os.fdopen(os.dup(descriptor), "rb", closefd=True) as stream, zipfile.ZipFile(stream, "r") as archive:
            entries = archive.infolist()
            if not entries or len(entries) > MAX_ARCHIVE_ENTRIES:
                reject("package")
            seen: set[str] = set()
            insensitive: set[str] = set()
            target_entries: dict[str, zipfile.ZipInfo] = {}
            expanded = 0
            reverse_targets = {f"{expected_root}/{relative}": key for key, relative in TARGETS.items()}
            for entry in entries:
                relative = archive_path(entry.filename, expected_root)
                canonical = relative.as_posix()
                folded = canonical.casefold()
                if canonical in seen or folded in insensitive or entry.flag_bits & 1:
                    reject("package")
                seen.add(canonical)
                insensitive.add(folded)
                is_directory = entry.is_dir() or entry.filename.endswith("/")
                unix_type = (entry.external_attr >> 16) & 0o170000
                if unix_type not in (0, stat.S_IFDIR if is_directory else stat.S_IFREG):
                    reject("package")
                if entry.file_size < 0 or entry.file_size > MAX_ARCHIVE_ENTRY_BYTES or entry.compress_size < 0:
                    reject("package")
                expanded += entry.file_size
                if expanded > MAX_ARCHIVE_EXPANDED_BYTES:
                    reject("package")
                if entry.file_size > 0 and entry.compress_size == 0:
                    reject("package")
                if entry.compress_size > 0 and entry.file_size > entry.compress_size * MAX_COMPRESSION_RATIO:
                    reject("package")
                if canonical in reverse_targets:
                    if is_directory or entry.file_size <= 0 or (entry.external_attr >> 16) & 0o111 == 0:
                        reject("package")
                    target_entries[reverse_targets[canonical]] = entry
            if set(target_entries) != set(TARGETS):
                reject("package")
            binary_hashes: dict[str, str] = {}
            for key in TARGETS:
                entry = target_entries[key]
                digest = hashlib.sha256()
                total = 0
                with archive.open(entry, "r") as source:
                    while True:
                        block = source.read(min(1024 * 1024, entry.file_size - total + 1))
                        if not block:
                            break
                        total += len(block)
                        if total > entry.file_size:
                            reject("package")
                        digest.update(block)
                if total != entry.file_size:
                    reject("package")
                binary_hashes[key] = digest.hexdigest()
        final = os.fstat(descriptor)
        named = os.stat(path, follow_symlinks=False)
        if not stable_fields(opened, final) or not stable_fields(opened, named):
            reject("package")
        return opened.st_size, package_digest, binary_hashes
    except PreparationError:
        raise
    except (OSError, ValueError, RuntimeError, zipfile.BadZipFile):
        reject("package")
    finally:
        os.close(descriptor)


def read_synthetic_marker_fixture(path: Path) -> bytes:
    descriptor = -1
    try:
        if path.resolve(strict=True) != path:
            reject("fixture")
        descriptor = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode) or before.st_nlink != 1
            or before.st_uid not in (0, os.geteuid()) or before.st_mode & 0o7022
            or before.st_size <= 0 or before.st_size > 128 * 1024
        ):
            reject("fixture")
        raw = bytearray()
        while len(raw) <= 128 * 1024:
            block = os.read(descriptor, min(4096, 128 * 1024 + 1 - len(raw)))
            if not block:
                break
            raw.extend(block)
        after = os.fstat(descriptor)
        named = os.stat(path, follow_symlinks=False)
        if (
            len(raw) != before.st_size or len(raw) > 128 * 1024
            or not stable_fields(before, after) or not stable_fields(before, named)
            or hashlib.sha256(raw).hexdigest() != SYNTHETIC_MARKER_ENCODED_SHA256
        ):
            reject("fixture")
        try:
            decoded = base64.b64decode(b"".join(bytes(raw).splitlines()), validate=True)
        except (ValueError, base64.binascii.Error):
            reject("fixture")
        if (
            len(decoded) != SYNTHETIC_MARKER_DECODED_BYTES or decoded[:2] != b"MZ"
            or hashlib.sha256(decoded).hexdigest() != SYNTHETIC_MARKER_DECODED_SHA256
        ):
            reject("fixture")
        return decoded
    except PreparationError:
        raise
    except OSError:
        reject("fixture")
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def open_case_root(
    case_root: Path,
    repository_root: Path,
    required_prefix_uid: int,
) -> tuple[int, int, os.stat_result, os.stat_result]:
    prefix = case_root.parent
    prefix_fd = root_fd = -1
    try:
        if (
            case_root.name == "" or CASE_NAME_RE.fullmatch(case_root.name) is None
            or is_sensitive(case_root, repository_root, include_home=True)
            or prefix.resolve(strict=True) != prefix or case_root.resolve(strict=True) != case_root
        ):
            reject("unsafe-root")
        prefix_fd = os.open(prefix, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
        prefix_status = os.fstat(prefix_fd)
        if (
            not stat.S_ISDIR(prefix_status.st_mode) or prefix_status.st_uid != required_prefix_uid
            or stat.S_IMODE(prefix_status.st_mode) != 0o711
        ):
            reject("unsafe-prefix")
        root_fd = os.open(case_root.name, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=prefix_fd)
        root_status = os.fstat(root_fd)
        if (
            not stat.S_ISDIR(root_status.st_mode) or root_status.st_uid != os.geteuid()
            or stat.S_IMODE(root_status.st_mode) != 0o700 or os.listdir(root_fd)
        ):
            reject("unsafe-root")
        return prefix_fd, root_fd, prefix_status, root_status
    except PreparationError:
        if root_fd >= 0:
            os.close(root_fd)
        if prefix_fd >= 0:
            os.close(prefix_fd)
        raise
    except OSError:
        if root_fd >= 0:
            os.close(root_fd)
        if prefix_fd >= 0:
            os.close(prefix_fd)
        reject("unsafe-root")


def private_bytes_at(directory_fd: int, name: str, data: bytes) -> os.stat_result:
    descriptor = -1
    created = succeeded = False
    try:
        descriptor = os.open(
            name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW,
            0o600, dir_fd=directory_fd,
        )
        created = True
        view = memoryview(data)
        while view:
            written = os.write(descriptor, view)
            if written <= 0:
                reject("write")
            view = view[written:]
        os.fchmod(descriptor, 0o600)
        os.fsync(descriptor)
        metadata = os.fstat(descriptor)
        if (
            not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1
            or metadata.st_uid != os.geteuid() or stat.S_IMODE(metadata.st_mode) != 0o600
        ):
            reject("write")
        succeeded = True
        return metadata
    except PreparationError:
        raise
    except OSError:
        reject("write")
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        if created and not succeeded:
            try:
                os.unlink(name, dir_fd=directory_fd)
            except OSError:
                pass


def private_json_at(directory_fd: int, name: str, value: dict[str, Any]) -> os.stat_result:
    data = (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")
    return private_bytes_at(directory_fd, name, data)


def random_name(prefix: str) -> str:
    return f"{prefix}{os.urandom(16).hex()}"


def prepare(
    *,
    case_root: Path,
    package: Path,
    version: str,
    expected_package_sha256: str,
    commit: str,
    tree: str,
    scenario: str,
    repository_root: Path | None = None,
    runtime_root: Path | None = None,
    required_prefix_uid: int = 0,
    synthetic_marker_fixture: Path | None = None,
) -> tuple[Path, Path]:
    if os.geteuid() == 0:
        reject("must-be-nonroot")
    repository_root = repository_root or REPOSITORY_ROOT
    runtime_root = runtime_root or Path(f"/run/user/{os.geteuid()}")
    synthetic_marker_fixture = synthetic_marker_fixture or SYNTHETIC_MARKER_FIXTURE
    if required_prefix_uid == 0 and synthetic_marker_fixture != SYNTHETIC_MARKER_FIXTURE:
        reject("fixture")
    if VERSION_RE.fullmatch(version) is None or GIT_OBJECT_RE.fullmatch(commit) is None or GIT_OBJECT_RE.fullmatch(tree) is None:
        reject("release")
    if SHA256_RE.fullmatch(expected_package_sha256) is None:
        reject("package-mismatch")
    if scenario not in SCENARIOS:
        reject("scenario")
    if is_sensitive(package, repository_root, include_home=False):
        reject("package")
    package_size, package_digest, binaries = inspect_package(package, version, expected_package_sha256)
    synthetic_marker = read_synthetic_marker_fixture(synthetic_marker_fixture)
    marker_digest = hashlib.sha256(synthetic_marker).hexdigest()
    prefix_fd, root_fd, prefix_status, root_status = open_case_root(case_root, repository_root, required_prefix_uid)
    runtime_fd = contract_fd = -1
    contract_directory = ""
    game_marker_created = root_marker_created = contract_created = False
    try:
        if runtime_root != Path(f"/run/user/{os.geteuid()}") and runtime_root.parent != Path("/run/user"):
            # Unit tests may inject a private runtime root, but production stays in /run/user/UID.
            if required_prefix_uid == 0:
                reject("runtime")
        if runtime_root.resolve(strict=True) != runtime_root:
            reject("runtime")
        runtime_fd = os.open(runtime_root, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
        runtime_status = os.fstat(runtime_fd)
        if (
            not stat.S_ISDIR(runtime_status.st_mode) or runtime_status.st_uid != os.geteuid()
            or stat.S_IMODE(runtime_status.st_mode) != 0o700
        ):
            reject("runtime")
        contract_directory = random_name("smapi-hard-state-contract-")
        os.mkdir(contract_directory, 0o700, dir_fd=runtime_fd)
        contract_fd = os.open(
            contract_directory, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
            dir_fd=runtime_fd,
        )
        contract_directory_status = os.fstat(contract_fd)
        if (
            not stat.S_ISDIR(contract_directory_status.st_mode)
            or contract_directory_status.st_uid != os.geteuid()
            or stat.S_IMODE(contract_directory_status.st_mode) != 0o700
        ):
            reject("runtime")
        output_name = random_name("qualification-")
        output = case_root / output_name
        game_marker = runtime_root / contract_directory / "Stardew Valley.dll"
        game_marker_status = private_bytes_at(contract_fd, "Stardew Valley.dll", synthetic_marker)
        game_marker_created = True
        match = VERSION_RE.fullmatch(version)
        assert match is not None
        tag = f"fork-4eh5xitv6787h645ebv-linux-v{match.group('base')}-alpha.{match.group('alpha')}"
        contract = {
            "schema_version": SCHEMA_VERSION,
            "scenario": scenario,
            "release": {
                "version": version,
                "tag": tag,
                "url": f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/{tag}",
                "expected_commit": commit,
                "expected_tree": tree,
            },
            "package": {"path": os.fspath(package), "size_bytes": package_size, "sha256": package_digest},
            "game_marker": {
                "path": os.fspath(game_marker),
                "size_bytes": len(synthetic_marker),
                "sha256": marker_digest,
            },
            "binaries": binaries,
            "isolation": {
                "disposable_root": os.fspath(case_root),
                "root_device": root_status.st_dev,
                "root_inode": root_status.st_ino,
                "disposable_vm": True,
                "live_roots_mounted": False,
                "installer_runs_as_root": False,
                "privileged_setup_confined_to_vm": True,
                "allow_privileged_fault_setup": scenario in E2_SCENARIOS,
            },
            "timeouts_seconds": TIMEOUTS,
        }
        contract_status = private_json_at(contract_fd, "contract.json", contract)
        contract_created = True
        marker = {
            "schema_version": SCHEMA_VERSION,
            "purpose": MARKER_PURPOSE,
            "root_device": root_status.st_dev,
            "root_inode": root_status.st_ino,
        }
        marker_status = private_json_at(root_fd, MARKER_NAME, marker)
        root_marker_created = True
        os.fsync(contract_fd)
        os.fsync(runtime_fd)
        os.fsync(root_fd)
        root_final = os.fstat(root_fd)
        root_named = os.stat(case_root.name, dir_fd=prefix_fd, follow_symlinks=False)
        contract_directory_final = os.fstat(contract_fd)
        contract_directory_named = os.stat(contract_directory, dir_fd=runtime_fd, follow_symlinks=False)
        contract_named = os.stat("contract.json", dir_fd=contract_fd, follow_symlinks=False)
        game_marker_named = os.stat("Stardew Valley.dll", dir_fd=contract_fd, follow_symlinks=False)
        marker_named = os.stat(MARKER_NAME, dir_fd=root_fd, follow_symlinks=False)
        if (
            not stable_fields(prefix_status, os.fstat(prefix_fd))
            or (root_final.st_dev, root_final.st_ino, root_final.st_uid, stat.S_IMODE(root_final.st_mode))
            != (root_status.st_dev, root_status.st_ino, os.geteuid(), 0o700)
            or (root_named.st_dev, root_named.st_ino, root_named.st_uid, stat.S_IMODE(root_named.st_mode))
            != (root_final.st_dev, root_final.st_ino, os.geteuid(), 0o700)
            or not stable_fields(contract_directory_final, contract_directory_named)
            or not stable_fields(contract_status, contract_named)
            or not stable_fields(game_marker_status, game_marker_named)
            or not stable_fields(marker_status, marker_named)
            or case_root.resolve(strict=True) != case_root
            or set(os.listdir(root_fd)) != {MARKER_NAME}
        ):
            reject("identity")
        return runtime_root / contract_directory / "contract.json", output
    except PreparationError:
        if root_marker_created:
            try:
                os.unlink(MARKER_NAME, dir_fd=root_fd)
            except OSError:
                pass
        if contract_created and contract_fd >= 0:
            try:
                os.unlink("contract.json", dir_fd=contract_fd)
            except OSError:
                pass
        if game_marker_created and contract_fd >= 0:
            try:
                os.unlink("Stardew Valley.dll", dir_fd=contract_fd)
            except OSError:
                pass
        if contract_fd >= 0:
            os.close(contract_fd)
            contract_fd = -1
        if contract_directory and runtime_fd >= 0:
            try:
                os.rmdir(contract_directory, dir_fd=runtime_fd)
            except OSError:
                pass
        raise
    except OSError:
        if root_marker_created:
            try:
                os.unlink(MARKER_NAME, dir_fd=root_fd)
            except OSError:
                pass
        if contract_created and contract_fd >= 0:
            try:
                os.unlink("contract.json", dir_fd=contract_fd)
            except OSError:
                pass
        if game_marker_created and contract_fd >= 0:
            try:
                os.unlink("Stardew Valley.dll", dir_fd=contract_fd)
            except OSError:
                pass
        if contract_fd >= 0:
            os.close(contract_fd)
            contract_fd = -1
        if contract_directory and runtime_fd >= 0:
            try:
                os.rmdir(contract_directory, dir_fd=runtime_fd)
            except OSError:
                pass
        reject("write")
    finally:
        if contract_fd >= 0:
            os.close(contract_fd)
        if runtime_fd >= 0:
            os.close(runtime_fd)
        os.close(root_fd)
        os.close(prefix_fd)


def parse_arguments(arguments: list[str]) -> dict[str, Any]:
    flags = (
        "--case-root", "--package", "--expected-package-sha256", "--version",
        "--commit", "--tree", "--scenario",
    )
    if len(arguments) != len(flags) * 2:
        reject("usage")
    parsed: dict[str, str] = {}
    for index in range(0, len(arguments), 2):
        flag = arguments[index]
        if flag not in flags or flag in parsed:
            reject("usage")
        parsed[flag] = arguments[index + 1]
    if set(parsed) != set(flags):
        reject("usage")
    return {
        "case_root": normalized_path(parsed["--case-root"], "unsafe-root"),
        "package": normalized_path(parsed["--package"], "package"),
        "expected_package_sha256": parsed["--expected-package-sha256"],
        "version": parsed["--version"],
        "commit": parsed["--commit"],
        "tree": parsed["--tree"],
        "scenario": parsed["--scenario"],
    }


def emit(value: dict[str, Any]) -> None:
    data = (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")
    os.write(sys.stdout.fileno(), data)


def main(arguments: list[str]) -> int:
    try:
        values = parse_arguments(arguments)
        contract, output = prepare(**values)
        emit({
            "contractPath": os.fspath(contract), "ok": True, "outputPath": os.fspath(output),
            "schemaVersion": SCHEMA_VERSION, "status": "prepared",
        })
        return 0
    except PreparationError as error:
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
