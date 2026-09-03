#!/usr/bin/env python3
"""Validate and aggregate the eight private Linux GUI hard-state case results."""

from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import sys
from types import ModuleType
from typing import Any, NoReturn


SCHEMA_VERSION = 2
CASE_KIND = "linux-gui-hard-state-qualification"
AGGREGATE_KIND = "linux-gui-hard-state-aggregate"
CAPTURED_STATUS = "captured_pending_privacy_and_public_authority"
MAX_RESULT_BYTES = 64 * 1024
MAX_TOTAL_BYTES = 1024 * 1024
CAPTURE_MODEL_PATH = Path(__file__).with_name("linux_gui_hard_state_capture_contract.py")
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
GIT_OBJECT_RE = re.compile(r"^[0-9a-f]{40}$")
TAG_RE = re.compile(
    r"^fork-4eh5xitv6787h645ebv-linux-v[0-9]+\.[0-9]+\.[0-9]+-alpha\.[1-9][0-9]*$"
)
CASE_KEYS = frozenset({
    "kind", "schemaVersion", "status", "ok", "scenario", "evidenceId", "fault",
    "releaseTag", "sourceCommit", "sourceTree", "publicReleaseUrl", "packageSha256",
    "guiSha256", "backendSha256", "environmentProfile", "visibleState",
    "durableAtCapture", "durableAfter", "exactWindowCaptured", "atspiEvidenceRecorded",
    "durableClassificationVerified", "cleanupComplete", "packageIdentityReverified",
})
COMMON_KEYS = (
    "releaseTag", "sourceCommit", "sourceTree", "publicReleaseUrl", "packageSha256",
    "guiSha256", "backendSha256", "environmentProfile",
)
TRUE_KEYS = (
    "exactWindowCaptured", "atspiEvidenceRecorded", "durableClassificationVerified",
    "cleanupComplete", "packageIdentityReverified",
)
STABLE_METADATA_FIELDS = (
    "st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size",
    "st_mtime_ns", "st_ctime_ns",
)
FAILURE = {
    "code": "invalid-input",
    "kind": AGGREGATE_KIND,
    "ok": False,
    "schemaVersion": SCHEMA_VERSION,
    "status": "rejected",
}


class AggregateError(Exception):
    """An intentionally detail-free rejection of caller-controlled input."""


def reject() -> NoReturn:
    raise AggregateError()


def load_capture_model() -> ModuleType:
    module_name = "smapi_linux_gui_hard_state_capture_contract"
    existing = sys.modules.get(module_name)
    if existing is not None:
        return existing
    specification = importlib.util.spec_from_file_location(module_name, CAPTURE_MODEL_PATH)
    if specification is None or specification.loader is None:
        reject()
    module = importlib.util.module_from_spec(specification)
    sys.modules[module_name] = module
    try:
        specification.loader.exec_module(module)
    except BaseException:
        sys.modules.pop(module_name, None)
        reject()
    return module


def stable_metadata(value: os.stat_result) -> tuple[int, ...]:
    return tuple(getattr(value, field) for field in STABLE_METADATA_FIELDS)


def no_duplicate_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            reject()
        result[key] = value
    return result


def parse_case(raw: bytes) -> dict[str, Any]:
    try:
        value = json.loads(raw.decode("ascii"), object_pairs_hook=no_duplicate_object)
    except (UnicodeError, json.JSONDecodeError):
        reject()
    if not isinstance(value, dict) or set(value) != CASE_KEYS:
        reject()
    return value


def exact_text(value: Any, pattern: re.Pattern[str]) -> str:
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        reject()
    return value


def validate_case(value: dict[str, Any], expected: Any, model: ModuleType) -> dict[str, Any]:
    expected_fault = None if expected.fault is None else expected.fault.value
    if (
        value["kind"] != CASE_KIND
        or value["schemaVersion"] != SCHEMA_VERSION
        or value["status"] != CAPTURED_STATUS
        or value["ok"] is not True
        or value["scenario"] != expected.scenario.value
        or value["evidenceId"] != expected.evidence_id.value
        or value["fault"] != expected_fault
        or value["visibleState"] != expected.visible_state.value
        or value["durableAtCapture"] != expected.durable_at_capture.value
        or value["durableAfter"] != expected.durable_after.value
        or any(value[key] is not True for key in TRUE_KEYS)
    ):
        reject()

    release_tag = exact_text(value["releaseTag"], TAG_RE)
    exact_text(value["sourceCommit"], GIT_OBJECT_RE)
    exact_text(value["sourceTree"], GIT_OBJECT_RE)
    for key in ("packageSha256", "guiSha256", "backendSha256"):
        exact_text(value[key], SHA256_RE)
    if value["publicReleaseUrl"] != (
        f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/{release_tag}"
    ):
        reject()
    try:
        profile = model.environment_profile(value["environmentProfile"])
    except (KeyError, TypeError, ValueError):
        reject()
    if value["environmentProfile"] != profile.profile_id.value:
        reject()
    return value


def read_regular_file(directory_fd: int, name: str, current_uid: int) -> bytes:
    try:
        before = os.stat(name, dir_fd=directory_fd, follow_symlinks=False)
    except OSError:
        reject()
    if (
        not stat.S_ISREG(before.st_mode)
        or stat.S_IMODE(before.st_mode) != 0o600
        or before.st_uid != current_uid
        or before.st_nlink != 1
        or before.st_size > MAX_RESULT_BYTES
    ):
        reject()
    flags = os.O_RDONLY | os.O_CLOEXEC | os.O_NONBLOCK
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    try:
        descriptor = os.open(name, flags, dir_fd=directory_fd)
    except OSError:
        reject()
    try:
        opened = os.fstat(descriptor)
        if stable_metadata(opened) != stable_metadata(before):
            reject()
        chunks: list[bytes] = []
        remaining = MAX_RESULT_BYTES + 1
        while remaining:
            try:
                chunk = os.read(descriptor, min(16 * 1024, remaining))
            except OSError:
                reject()
            if not chunk:
                break
            chunks.append(chunk)
            remaining -= len(chunk)
        raw = b"".join(chunks)
        if len(raw) > MAX_RESULT_BYTES:
            reject()
        after_read = os.fstat(descriptor)
        if stable_metadata(after_read) != stable_metadata(opened):
            reject()
    finally:
        os.close(descriptor)
    try:
        after_close = os.stat(name, dir_fd=directory_fd, follow_symlinks=False)
    except OSError:
        reject()
    if stable_metadata(after_close) != stable_metadata(before):
        reject()
    return raw


def canonical_bytes(value: dict[str, Any]) -> bytes:
    return (json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")


def aggregate(input_directory: str) -> bytes:
    if (
        not input_directory
        or "\x00" in input_directory
        or not os.path.isabs(input_directory)
        or os.path.normpath(input_directory) != input_directory
        or input_directory == "/"
    ):
        reject()
    model = load_capture_model()
    expected_names = tuple(f"{spec.output_basename}.result.json" for spec in model.CAPTURE_SPECS)
    expected_name_set = frozenset(expected_names)
    flags = os.O_RDONLY | os.O_CLOEXEC | getattr(os, "O_DIRECTORY", 0)
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    try:
        directory_fd = os.open(input_directory, flags)
    except OSError:
        reject()
    try:
        current_uid = os.getuid()
        before_directory = os.fstat(directory_fd)
        if (
            not stat.S_ISDIR(before_directory.st_mode)
            or before_directory.st_uid != current_uid
            or stat.S_IMODE(before_directory.st_mode) & 0o077
        ):
            reject()
        try:
            initial_names = os.listdir(directory_fd)
        except OSError:
            reject()
        if len(initial_names) != len(expected_names) or frozenset(initial_names) != expected_name_set:
            reject()

        records: list[dict[str, Any]] = []
        total_bytes = 0
        for spec in model.CAPTURE_SPECS:
            raw = read_regular_file(directory_fd, f"{spec.output_basename}.result.json", current_uid)
            total_bytes += len(raw)
            if total_bytes > MAX_TOTAL_BYTES:
                reject()
            records.append(validate_case(parse_case(raw), spec, model))

        try:
            final_names = os.listdir(directory_fd)
            after_directory = os.fstat(directory_fd)
        except OSError:
            reject()
        if (
            len(final_names) != len(initial_names)
            or frozenset(final_names) != frozenset(initial_names)
            or stable_metadata(after_directory) != stable_metadata(before_directory)
        ):
            reject()
    finally:
        os.close(directory_fd)

    common = {key: records[0][key] for key in COMMON_KEYS}
    if any(any(record[key] != common[key] for key in COMMON_KEYS) for record in records[1:]):
        reject()
    summaries = [
        {
            "scenario": record["scenario"],
            "evidenceId": record["evidenceId"],
            "fault": record["fault"],
            "visibleState": record["visibleState"],
            "durableAtCapture": record["durableAtCapture"],
            "durableAfter": record["durableAfter"],
        }
        for record in records
    ]
    result = {
        "kind": AGGREGATE_KIND,
        "schemaVersion": SCHEMA_VERSION,
        "status": CAPTURED_STATUS,
        "ok": True,
        **common,
        "scenarioCount": len(records),
        "captureCount": sum(record["exactWindowCaptured"] is True for record in records),
        "e2FaultSourceCount": sum(record["fault"] is not None for record in records),
        "scenarios": summaries,
    }
    return canonical_bytes(result)


def run(arguments: list[str]) -> tuple[int, bytes]:
    if len(arguments) != 2 or arguments[0] != "--input-directory":
        return 1, canonical_bytes(FAILURE)
    try:
        return 0, aggregate(arguments[1])
    except BaseException:
        return 1, canonical_bytes(FAILURE)


def main() -> int:
    status, output = run(sys.argv[1:])
    try:
        os.write(sys.stdout.fileno(), output)
    except OSError:
        return 1
    return status


if __name__ == "__main__":
    raise SystemExit(main())
