#!/usr/bin/env python3
"""Securely collect one validated Linux GUI hard-state broker result."""

from __future__ import annotations

import argparse
import fcntl
import importlib.util
import json
import os
from pathlib import Path
import stat
import sys
from types import ModuleType
from typing import Any, NoReturn


SCHEMA_VERSION = 1
AUTHORITY_KIND = "linux-gui-hard-state-expected-authority"
RESULT_KIND = "linux-gui-hard-state-result-collection"
MAX_AUTHORITY_BYTES = 16 * 1024
MAX_RESULT_BYTES = 64 * 1024
AGGREGATOR_PATH = Path(__file__).with_name("aggregate-linux-gui-hard-state-results.py")
REQUIRED_MEMFD_SEALS = (
    fcntl.F_SEAL_WRITE | fcntl.F_SEAL_GROW | fcntl.F_SEAL_SHRINK | fcntl.F_SEAL_SEAL
)
AUTHORITY_KEYS = frozenset({
    "kind", "schemaVersion", "scenario", "releaseTag", "sourceCommit", "sourceTree",
    "publicReleaseUrl", "packageSha256", "guiSha256", "backendSha256",
    "environmentProfile",
})
IDENTITY_KEYS = (
    "releaseTag", "sourceCommit", "sourceTree", "publicReleaseUrl", "packageSha256",
    "guiSha256", "backendSha256", "environmentProfile",
)
STABLE_FILE_FIELDS = (
    "st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size",
    "st_mtime_ns", "st_ctime_ns",
)


class CollectionError(Exception):
    """A detail-free rejection of caller-controlled state."""


class SilentParser(argparse.ArgumentParser):
    def error(self, _message: str) -> NoReturn:
        raise CollectionError()


def reject() -> NoReturn:
    raise CollectionError()


def canonical_bytes(value: dict[str, Any]) -> bytes:
    return (json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")


def load_aggregator() -> ModuleType:
    name = "smapi_linux_gui_hard_state_result_aggregator_for_collector"
    existing = sys.modules.get(name)
    if existing is not None:
        return existing
    specification = importlib.util.spec_from_file_location(name, AGGREGATOR_PATH)
    if specification is None or specification.loader is None:
        reject()
    module = importlib.util.module_from_spec(specification)
    sys.modules[name] = module
    try:
        specification.loader.exec_module(module)
    except BaseException:
        sys.modules.pop(name, None)
        reject()
    return module


def no_duplicate_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            reject()
        result[key] = value
    return result


def parse_object(raw: bytes) -> dict[str, Any]:
    try:
        value = json.loads(raw.decode("ascii"), object_pairs_hook=no_duplicate_object)
    except CollectionError:
        raise
    except (UnicodeError, json.JSONDecodeError):
        reject()
    if not isinstance(value, dict):
        reject()
    return value


def stable_metadata(value: os.stat_result) -> tuple[int, ...]:
    return tuple(getattr(value, field) for field in STABLE_FILE_FIELDS)


def read_bounded_descriptor(descriptor: int, maximum: int) -> bytes:
    raw = bytearray()
    try:
        os.lseek(descriptor, 0, os.SEEK_SET)
        while len(raw) <= maximum:
            block = os.read(descriptor, min(4096, maximum + 1 - len(raw)))
            if not block:
                break
            raw.extend(block)
    except OSError:
        reject()
    if not raw or len(raw) > maximum:
        reject()
    return bytes(raw)


def read_sealed_authority(descriptor: int) -> dict[str, Any]:
    if isinstance(descriptor, bool) or not isinstance(descriptor, int) or not 3 <= descriptor <= 1024:
        reject()
    try:
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode) or before.st_nlink != 0
            or not 0 < before.st_size <= MAX_AUTHORITY_BYTES
            or fcntl.fcntl(descriptor, fcntl.F_GET_SEALS) != REQUIRED_MEMFD_SEALS
        ):
            reject()
        raw = read_bounded_descriptor(descriptor, MAX_AUTHORITY_BYTES)
        after = os.fstat(descriptor)
        if len(raw) != before.st_size or stable_metadata(before) != stable_metadata(after):
            reject()
    except CollectionError:
        raise
    except OSError:
        reject()
    value = parse_object(raw)
    if raw != canonical_bytes(value):
        reject()
    if set(value) != AUTHORITY_KEYS:
        reject()
    return value


def normalized_absolute(value: str) -> Path:
    if (
        not value or "\x00" in value or not os.path.isabs(value)
        or os.path.normpath(value) != value or value == "/"
    ):
        reject()
    return Path(value)


def read_result_path(path: Path) -> bytes:
    descriptor = -1
    try:
        before = path.lstat()
        if (
            path.resolve(strict=True) != path or not stat.S_ISREG(before.st_mode)
            or before.st_uid != os.getuid() or stat.S_IMODE(before.st_mode) != 0o600
            or before.st_nlink != 1 or not 0 < before.st_size <= MAX_RESULT_BYTES
        ):
            reject()
        descriptor = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NONBLOCK | os.O_NOFOLLOW)
        opened = os.fstat(descriptor)
        if stable_metadata(before) != stable_metadata(opened):
            reject()
        raw = read_bounded_descriptor(descriptor, MAX_RESULT_BYTES)
        after = os.fstat(descriptor)
        if len(raw) != before.st_size or stable_metadata(opened) != stable_metadata(after):
            reject()
    except CollectionError:
        raise
    except OSError:
        reject()
    finally:
        if descriptor >= 0:
            os.close(descriptor)
    try:
        named = path.lstat()
    except OSError:
        reject()
    if stable_metadata(before) != stable_metadata(named):
        reject()
    return raw


def read_named_result(directory_fd: int, name: str) -> bytes:
    descriptor = -1
    try:
        before = os.stat(name, dir_fd=directory_fd, follow_symlinks=False)
        if (
            not stat.S_ISREG(before.st_mode) or before.st_uid != os.getuid()
            or stat.S_IMODE(before.st_mode) != 0o600 or before.st_nlink != 1
            or not 0 < before.st_size <= MAX_RESULT_BYTES
        ):
            reject()
        descriptor = os.open(name, os.O_RDONLY | os.O_CLOEXEC | os.O_NONBLOCK | os.O_NOFOLLOW, dir_fd=directory_fd)
        opened = os.fstat(descriptor)
        if stable_metadata(before) != stable_metadata(opened):
            reject()
        raw = read_bounded_descriptor(descriptor, MAX_RESULT_BYTES)
        after = os.fstat(descriptor)
        if len(raw) != before.st_size or stable_metadata(opened) != stable_metadata(after):
            reject()
    except CollectionError:
        raise
    except OSError:
        reject()
    finally:
        if descriptor >= 0:
            os.close(descriptor)
    try:
        named = os.stat(name, dir_fd=directory_fd, follow_symlinks=False)
    except OSError:
        reject()
    if stable_metadata(before) != stable_metadata(named):
        reject()
    return raw


def validate_authority(authority: dict[str, Any], aggregator: ModuleType, model: ModuleType) -> Any:
    if (
        authority["kind"] != AUTHORITY_KIND
        or type(authority["schemaVersion"]) is not int
        or authority["schemaVersion"] != SCHEMA_VERSION
    ):
        reject()
    try:
        expected = model.capture_spec(authority["scenario"])
        profile = model.environment_profile(authority["environmentProfile"])
        aggregator.exact_text(authority["releaseTag"], aggregator.TAG_RE)
        aggregator.exact_text(authority["sourceCommit"], aggregator.GIT_OBJECT_RE)
        aggregator.exact_text(authority["sourceTree"], aggregator.GIT_OBJECT_RE)
        for key in ("packageSha256", "guiSha256", "backendSha256"):
            aggregator.exact_text(authority[key], aggregator.SHA256_RE)
    except BaseException:
        reject()
    if (
        authority["scenario"] != expected.scenario.value
        or authority["environmentProfile"] != profile.profile_id.value
        or authority["publicReleaseUrl"] != (
            "https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/"
            + authority["releaseTag"]
        )
    ):
        reject()
    return expected


def validate_case(
    value: dict[str, Any],
    expected: Any,
    authority: dict[str, Any],
    aggregator: ModuleType,
    model: ModuleType,
) -> dict[str, Any]:
    if set(value) != aggregator.CASE_KEYS or type(value.get("schemaVersion")) is not int:
        reject()
    try:
        validated = aggregator.validate_case(value, expected, model)
    except BaseException:
        reject()
    if any(validated[key] != authority[key] for key in IDENTITY_KEYS):
        reject()
    return validated


def validate_existing(directory_fd: int, names: list[str], authority: dict[str, Any], aggregator: ModuleType, model: ModuleType) -> None:
    by_name = {f"{spec.output_basename}.result.json": spec for spec in model.CAPTURE_SPECS}
    if len(names) != len(set(names)) or any(name not in by_name for name in names):
        reject()
    for name in names:
        value = parse_object(read_named_result(directory_fd, name))
        validate_case(value, by_name[name], authority, aggregator, model)


def write_once(directory_fd: int, name: str, raw: bytes) -> None:
    descriptor = -1
    try:
        descriptor = os.open(
            name,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW,
            0o600,
            dir_fd=directory_fd,
        )
        view = memoryview(raw)
        while view:
            written = os.write(descriptor, view)
            if written <= 0:
                reject()
            view = view[written:]
        os.fchmod(descriptor, 0o600)
        os.fsync(descriptor)
        status = os.fstat(descriptor)
        if (
            not stat.S_ISREG(status.st_mode) or status.st_uid != os.getuid()
            or stat.S_IMODE(status.st_mode) != 0o600 or status.st_nlink != 1
            or status.st_size != len(raw)
        ):
            reject()
    except CollectionError:
        raise
    except OSError:
        reject()
    finally:
        if descriptor >= 0:
            os.close(descriptor)
    try:
        named = os.stat(name, dir_fd=directory_fd, follow_symlinks=False)
        if stable_metadata(status) != stable_metadata(named):
            reject()
        os.fsync(directory_fd)
    except OSError:
        reject()


def collect(broker_result: str, result_directory: str, authority_fd: int) -> str:
    source = normalized_absolute(broker_result)
    destination = normalized_absolute(result_directory)
    authority = read_sealed_authority(authority_fd)
    aggregator = load_aggregator()
    model = aggregator.load_capture_model()
    expected = validate_authority(authority, aggregator, model)
    source_raw = read_result_path(source)
    parsed_source = parse_object(source_raw)
    if source_raw != canonical_bytes(parsed_source):
        reject()
    value = validate_case(parsed_source, expected, authority, aggregator, model)
    output_name = f"{expected.output_basename}.result.json"

    directory_fd = -1
    try:
        directory_fd = os.open(destination, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
        opened = os.fstat(directory_fd)
        named = destination.lstat()
        if (
            destination.resolve(strict=True) != destination
            or not stat.S_ISDIR(opened.st_mode)
            or (opened.st_dev, opened.st_ino) != (named.st_dev, named.st_ino)
            or opened.st_uid != os.getuid() or stat.S_IMODE(opened.st_mode) != 0o700
        ):
            reject()
        initial_names = os.listdir(directory_fd)
        validate_existing(directory_fd, initial_names, authority, aggregator, model)
        repeated_names = os.listdir(directory_fd)
        if (
            output_name in initial_names or len(repeated_names) != len(initial_names)
            or set(repeated_names) != set(initial_names)
        ):
            reject()
        write_once(directory_fd, output_name, aggregator.canonical_bytes(value))
        final_names = os.listdir(directory_fd)
        validate_existing(directory_fd, final_names, authority, aggregator, model)
        repeated = os.fstat(directory_fd)
        if (
            len(final_names) != len(initial_names) + 1
            or set(final_names) != set(initial_names) | {output_name}
            or (repeated.st_dev, repeated.st_ino, repeated.st_uid, stat.S_IMODE(repeated.st_mode))
                != (opened.st_dev, opened.st_ino, opened.st_uid, 0o700)
        ):
            reject()
    except CollectionError:
        raise
    except OSError:
        reject()
    finally:
        if directory_fd >= 0:
            os.close(directory_fd)
    return output_name


def parse_cli(arguments: list[str]) -> argparse.Namespace:
    parser = SilentParser(add_help=False)
    parser.add_argument("--broker-result", required=True)
    parser.add_argument("--result-directory", required=True)
    parser.add_argument("--expected-authority-fd", required=True, type=int)
    parsed = parser.parse_args(arguments)
    normalized_absolute(parsed.broker_result)
    normalized_absolute(parsed.result_directory)
    if not 3 <= parsed.expected_authority_fd <= 1024:
        reject()
    return parsed


def result_payload(ok: bool, output_name: str | None = None) -> bytes:
    if not ok:
        return canonical_bytes({
            "code": "invalid-input", "kind": RESULT_KIND, "ok": False,
            "schemaVersion": SCHEMA_VERSION, "status": "rejected",
        })
    return canonical_bytes({
        "collected": output_name, "kind": RESULT_KIND, "ok": True,
        "schemaVersion": SCHEMA_VERSION, "status": "collected",
    })


def run(arguments: list[str]) -> tuple[int, bytes]:
    try:
        parsed = parse_cli(arguments)
        name = collect(parsed.broker_result, parsed.result_directory, parsed.expected_authority_fd)
        return 0, result_payload(True, name)
    except BaseException:
        return 1, result_payload(False)


def main() -> int:
    status, output = run(sys.argv[1:])
    try:
        os.write(sys.stdout.fileno(), output)
    except OSError:
        return 1
    return status


if __name__ == "__main__":
    raise SystemExit(main())
