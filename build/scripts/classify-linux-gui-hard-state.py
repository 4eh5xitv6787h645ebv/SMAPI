#!/usr/bin/env python3
"""Strictly classify durable installer state for Linux GUI evidence capture.

This intentionally returns only bounded aggregate counts. Transaction IDs, game
paths, and journal contents never cross the classifier boundary.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import stat
import sys
import uuid
from dataclasses import dataclass
from typing import Any, Final

import linux_gui_hard_state_capture_contract as contract


MAX_TRANSACTIONS: Final = 16
MAX_OPERATIONS: Final = 20_000
MAX_FILE_BYTES: Final = 64 * 1024 * 1024
MAX_AGGREGATE_BYTES: Final = 1024 * 1024 * 1024
TRANSACTION_NAME = re.compile(r"^[0-9a-f]{32}$")
DIGEST = re.compile(r"^[0-9a-f]{64}$")
PLAN_KEYS: Final = (
    "schemaVersion", "transactionId", "createdUtcTicks", "canonicalGameRoot",
    "gameRootInode", "gameRootDeviceMajor", "gameRootDeviceMinor",
    "hasCoreAuthorizedReceiptMutation", "coreGenerationId",
    "coreRecoveryOperationCount", "coreRecoveryContentCount",
    "hasCoreAuthorizedManifestMutation",
    "hasCoreAuthorizedRecoveryPointerMutation", "entries",
)
ENTRY_KEYS: Final = (
    "index", "kind", "relativePath", "hadOriginal",
    "expectedExistingSha256", "expectedResultSha256", "resultUnixMode",
    "backupRelativePath", "stagedRelativePath", "createdDirectories",
)
EVENT_KEYS: Final = (
    "schemaVersion", "sequence", "kind", "operationIndex", "planSha256",
    "previousEventSha256", "eventSha256",
)
EVENT_KINDS: Final = frozenset({
    "Created", "Prepared", "Applying", "Intent", "Applied",
    "RecoveryObservedApplied", "RollingBack", "RollbackIntent",
    "RollbackApplied", "Committed", "RolledBack",
})
CORE_RECEIPT: Final = ".smapi-installer/ownership/receipt.json"
CORE_MANIFEST: Final = ".smapi-installer/ownership/manifest.json"
CORE_POINTER: Final = ".smapi-installer/recovery/current.json"
DOTNET_UNIX_EPOCH_TICKS: Final = 621_355_968_000_000_000


class ClassificationError(ValueError):
    """The physical state isn't a single strict, trustworthy classification."""


@dataclass(frozen=True, slots=True)
class TransactionStoreSummary:
    """Closed aggregate view of a transaction store (never IDs or paths)."""

    absent: bool
    incomplete_applied: int
    incomplete_unapplied: int
    rolled_back: int
    committed: int
    applied_operations: int
    rolled_back_operations: int

    def __post_init__(self) -> None:
        values = (
            self.incomplete_applied, self.incomplete_unapplied,
            self.rolled_back, self.committed,
            self.applied_operations, self.rolled_back_operations,
        )
        if any(type(value) is not int or value < 0 for value in values):
            raise ValueError("summary counts must be nonnegative integers")
        transactions = sum(values[:4])
        if self.absent is not (transactions == 0):
            raise ValueError("summary absence is inconsistent with its counts")
        if transactions > MAX_TRANSACTIONS:
            raise ValueError("summary exceeds the transaction bound")


@dataclass(slots=True)
class _OpenedTransaction:
    directory_fd: int
    directory_identity: tuple[int, ...]
    plan_fd: int
    plan_identity: tuple[int, ...]
    events_fd: int
    events_identity: tuple[int, ...]
    transaction_name: str

    def close(self) -> None:
        for descriptor in (self.events_fd, self.plan_fd, self.directory_fd):
            try:
                os.close(descriptor)
            except OSError:
                pass


@dataclass(frozen=True, slots=True)
class _Replay:
    status: str
    applied: frozenset[int]
    rolled_back: frozenset[int]


def _reject(message: str) -> ClassificationError:
    # Messages deliberately identify only the violated invariant.
    return ClassificationError(message)


def _strict_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise _reject("JSON contains a duplicate key")
        result[key] = value
    return result


def _parse_json(raw: bytes, label: str) -> Any:
    try:
        text = raw.decode("utf-8", errors="strict")
        return json.loads(
            text,
            object_pairs_hook=_strict_object,
            parse_constant=lambda _value: (_ for _ in ()).throw(_reject("JSON contains a non-finite number")),
        )
    except ClassificationError:
        raise
    except (UnicodeError, json.JSONDecodeError, RecursionError) as exc:
        raise _reject(f"{label} isn't strict UTF-8 JSON") from exc


def _exact_keys(value: Any, keys: tuple[str, ...], label: str) -> dict[str, Any]:
    if type(value) is not dict or tuple(value.keys()) != keys:
        raise _reject(f"{label} doesn't have its exact ordered schema")
    return value


def _integer(value: Any, label: str, minimum: int = 0, maximum: int | None = None) -> int:
    if type(value) is not int or value < minimum or (maximum is not None and value > maximum):
        raise _reject(f"{label} isn't a bounded integer")
    return value


def _boolean(value: Any, label: str) -> bool:
    if type(value) is not bool:
        raise _reject(f"{label} isn't a boolean")
    return value


def _nullable_digest(value: Any, label: str, allow_null: bool) -> str | None:
    if value is None and allow_null:
        return None
    if type(value) is not str or DIGEST.fullmatch(value) is None:
        raise _reject(f"{label} isn't a canonical SHA-256 digest")
    return value


def _relative_path(value: Any, label: str) -> str:
    if type(value) is not str or not value or len(value.encode("utf-8")) > 4096:
        raise _reject(f"{label} isn't a bounded relative path")
    if value.startswith("/") or "\\" in value or "\0" in value:
        raise _reject(f"{label} isn't a canonical relative path")
    parts = value.split("/")
    if any(not part or part in (".", "..") or len(part.encode("utf-8")) > 255 for part in parts):
        raise _reject(f"{label} isn't a canonical relative path")
    if any(any(ord(character) < 32 or 0xD800 <= ord(character) <= 0xDFFF for character in part) for part in parts):
        raise _reject(f"{label} contains an unsafe character")
    return value


def _identity(metadata: os.stat_result) -> tuple[int, ...]:
    return (
        metadata.st_dev, metadata.st_ino, metadata.st_mode, metadata.st_nlink,
        metadata.st_uid, metadata.st_gid, metadata.st_size,
        metadata.st_mtime_ns, metadata.st_ctime_ns,
    )


def _require_directory(metadata: os.stat_result, exact_mode: int | None, label: str) -> None:
    if not stat.S_ISDIR(metadata.st_mode) or metadata.st_uid != os.geteuid():
        raise _reject(f"{label} isn't a current-user directory")
    if exact_mode is not None and stat.S_IMODE(metadata.st_mode) != exact_mode:
        raise _reject(f"{label} doesn't have its exact private mode")


def _require_file(metadata: os.stat_result, label: str) -> None:
    if (
        not stat.S_ISREG(metadata.st_mode)
        or metadata.st_uid != os.geteuid()
        or stat.S_IMODE(metadata.st_mode) != 0o600
        or metadata.st_nlink != 1
        or metadata.st_size < 0
        or metadata.st_size > MAX_FILE_BYTES
    ):
        raise _reject(f"{label} isn't an exact bounded private regular file")


def _open_root_nofollow(path: str) -> tuple[int, str]:
    canonical = os.path.abspath(path)
    if not os.path.isabs(canonical):
        raise _reject("game root must be absolute")
    components = [part for part in canonical.split(os.sep) if part]
    descriptor = os.open(os.sep, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
    try:
        for component in components:
            if component in (".", ".."):
                raise _reject("game root isn't canonical")
            try:
                next_descriptor = os.open(
                    component,
                    os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                    dir_fd=descriptor,
                )
            except OSError as exc:
                raise _reject("game root can't be opened without following links") from exc
            os.close(descriptor)
            descriptor = next_descriptor
        _require_directory(os.fstat(descriptor), None, "game root")
        return descriptor, canonical.rstrip(os.sep) or os.sep
    except Exception:
        os.close(descriptor)
        raise


def _open_optional_directory(parent_fd: int, name: str, exact_mode: int | None, label: str) -> int | None:
    try:
        descriptor = os.open(
            name,
            os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
            dir_fd=parent_fd,
        )
    except FileNotFoundError:
        return None
    except OSError as exc:
        raise _reject(f"{label} can't be opened safely") from exc
    try:
        _require_directory(os.fstat(descriptor), exact_mode, label)
        return descriptor
    except Exception:
        os.close(descriptor)
        raise


def _open_file(parent_fd: int, name: str, label: str) -> tuple[int, tuple[int, ...]]:
    try:
        descriptor = os.open(
            name,
            os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW | os.O_CLOEXEC,
            dir_fd=parent_fd,
        )
    except OSError as exc:
        raise _reject(f"{label} can't be opened safely") from exc
    try:
        metadata = os.fstat(descriptor)
        _require_file(metadata, label)
        return descriptor, _identity(metadata)
    except Exception:
        os.close(descriptor)
        raise


def _stable_names(directory_fd: int, maximum: int, label: str) -> tuple[str, ...]:
    before = _identity(os.fstat(directory_fd))
    try:
        first = tuple(sorted(os.listdir(directory_fd)))
        second = tuple(sorted(os.listdir(directory_fd)))
    except OSError as exc:
        raise _reject(f"{label} can't be enumerated safely") from exc
    after = _identity(os.fstat(directory_fd))
    if before != after or first != second or len(first) > maximum:
        raise _reject(f"{label} isn't a stable bounded directory")
    if any(type(name) is not str or name in ("", ".", "..") for name in first):
        raise _reject(f"{label} has an unsafe entry name")
    return first


def _read_stable(descriptor: int, expected: tuple[int, ...], label: str) -> bytes:
    if _identity(os.fstat(descriptor)) != expected:
        raise _reject(f"{label} changed before inspection")
    size = expected[6]
    data = bytearray()
    while len(data) < size:
        chunk = os.read(descriptor, min(1024 * 1024, size - len(data)))
        if not chunk:
            break
        data.extend(chunk)
    if len(data) != size or os.read(descriptor, 1) or _identity(os.fstat(descriptor)) != expected:
        raise _reject(f"{label} changed during inspection")
    return bytes(data)


def _validate_plan(raw: bytes, transaction_name: str, canonical_root: str, root: os.stat_result) -> tuple[str, int]:
    value = _exact_keys(_parse_json(raw, "transaction plan"), PLAN_KEYS, "transaction plan")
    if value["schemaVersion"] != 3:
        raise _reject("transaction plan isn't schema 3")
    transaction_id = value["transactionId"]
    try:
        parsed_id = uuid.UUID(transaction_id) if type(transaction_id) is str else None
    except (ValueError, AttributeError) as exc:
        raise _reject("transaction ID isn't canonical") from exc
    if parsed_id is None or parsed_id.int == 0 or str(parsed_id) != transaction_id or parsed_id.hex != transaction_name:
        raise _reject("transaction identity doesn't match its directory")
    _integer(value["createdUtcTicks"], "created time", DOTNET_UNIX_EPOCH_TICKS)
    if value["canonicalGameRoot"] != canonical_root:
        raise _reject("transaction game root doesn't match the inspected root")
    if (
        _integer(value["gameRootInode"], "game-root inode", 1) != root.st_ino
        or _integer(value["gameRootDeviceMajor"], "game-root device major") != os.major(root.st_dev)
        or _integer(value["gameRootDeviceMinor"], "game-root device minor") != os.minor(root.st_dev)
    ):
        raise _reject("transaction game-root identity doesn't match")

    has_receipt = _boolean(value["hasCoreAuthorizedReceiptMutation"], "receipt authorization")
    has_manifest = _boolean(value["hasCoreAuthorizedManifestMutation"], "manifest authorization")
    has_pointer = _boolean(value["hasCoreAuthorizedRecoveryPointerMutation"], "pointer authorization")
    generation_id = value["coreGenerationId"]
    if generation_id is not None:
        try:
            generation = uuid.UUID(generation_id) if type(generation_id) is str else None
        except (ValueError, AttributeError) as exc:
            raise _reject("core generation ID isn't canonical") from exc
        if generation is None or str(generation) != generation_id or generation != parsed_id:
            raise _reject("core generation identity is inconsistent")
    recovery_count = _integer(value["coreRecoveryOperationCount"], "core recovery operation count", 0, MAX_OPERATIONS)
    content_count = _integer(value["coreRecoveryContentCount"], "core recovery content count", 0, recovery_count)
    entries = value["entries"]
    if type(entries) is not list or not 1 <= len(entries) <= MAX_OPERATIONS:
        raise _reject("transaction entries aren't a bounded nonempty array")
    if recovery_count > len(entries):
        raise _reject("core recovery count exceeds the plan")

    exact_paths: set[str] = set()
    folded_paths: set[str] = set()
    created_paths: set[str] = set()
    kinds_by_path: dict[str, str] = {}
    for index, untyped_entry in enumerate(entries):
        entry = _exact_keys(untyped_entry, ENTRY_KEYS, "transaction entry")
        if _integer(entry["index"], "entry index", 0, MAX_OPERATIONS - 1) != index:
            raise _reject("transaction entry indices aren't contiguous")
        kind = entry["kind"]
        if kind not in ("WriteFile", "RemoveFile"):
            raise _reject("transaction entry kind is unknown")
        path = _relative_path(entry["relativePath"], "transaction path")
        if path in exact_paths or path.casefold() in folded_paths:
            raise _reject("transaction paths aren't unique")
        exact_paths.add(path)
        folded_paths.add(path.casefold())
        kinds_by_path[path] = kind
        had_original = _boolean(entry["hadOriginal"], "original-file marker")
        existing = _nullable_digest(entry["expectedExistingSha256"], "existing-file digest", True)
        if had_original is not (existing is not None):
            raise _reject("original-file marker and digest are inconsistent")
        result = _nullable_digest(entry["expectedResultSha256"], "result digest", kind == "RemoveFile")
        mode = entry["resultUnixMode"]
        if mode is not None:
            _integer(mode, "result mode", 0, 0o777)
        if kind == "RemoveFile" and (result is not None or mode is not None):
            raise _reject("remove entry contains write-only fields")
        if entry["backupRelativePath"] != f"backups/{index:08d}":
            raise _reject("transaction backup path isn't canonical")
        expected_staged = f"staged/{index:08d}" if kind == "WriteFile" else None
        if entry["stagedRelativePath"] != expected_staged:
            raise _reject("transaction staged path isn't canonical")
        directories = entry["createdDirectories"]
        if type(directories) is not list or len(directories) > 256:
            raise _reject("created-directory list isn't bounded")
        local: set[str] = set()
        for raw_directory in directories:
            directory = _relative_path(raw_directory, "created directory")
            if not path.startswith(directory + "/") or directory in local or directory in created_paths:
                raise _reject("created directory isn't a unique destination parent")
            local.add(directory)
            created_paths.add(directory)

    if has_receipt is not (CORE_RECEIPT in exact_paths):
        raise _reject("receipt authorization is inconsistent")
    if has_manifest is not (CORE_MANIFEST in exact_paths) or has_pointer is not (CORE_POINTER in exact_paths):
        raise _reject("core-state authorization is inconsistent")
    if has_manifest != has_receipt or (generation_id is None) is not (not has_pointer):
        raise _reject("core-state tuple authorization is inconsistent")
    if content_count > recovery_count:
        raise _reject("core recovery content count is inconsistent")
    if has_manifest and kinds_by_path[CORE_MANIFEST] != kinds_by_path[CORE_RECEIPT]:
        raise _reject("ownership tuple mixes mutation kinds")
    return hashlib.sha256(raw).hexdigest(), len(entries)


def _canonical_event(value: dict[str, Any], include_digest: bool) -> bytes:
    keys = EVENT_KEYS if include_digest else EVENT_KEYS[:-1]
    projected = {key: value[key] for key in keys}
    return json.dumps(projected, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def _validate_events(raw: bytes, plan_digest: str, operation_count: int) -> _Replay:
    if not raw or not raw.endswith(b"\n") or b"\r" in raw:
        raise _reject("event log isn't complete canonical JSONL")
    lines = raw[:-1].split(b"\n")
    if not lines or len(lines) > 4 * operation_count + 8 or any(not line for line in lines):
        raise _reject("event log record count isn't bounded")

    previous: str | None = None
    prepared = applying = rolling_back = recovery_observed = final = False
    next_intent = 0
    pending_intent: int | None = None
    next_rollback = -1
    pending_rollback: int | None = None
    intended: set[int] = set()
    applied: set[int] = set()
    rolled_back: set[int] = set()
    status = ""

    for sequence, raw_line in enumerate(lines):
        event = _exact_keys(_parse_json(raw_line, "event record"), EVENT_KEYS, "event record")
        if _canonical_event(event, True) != raw_line:
            raise _reject("event record isn't canonical")
        if event["schemaVersion"] != 1 or _integer(event["sequence"], "event sequence") != sequence:
            raise _reject("event schema or sequence is invalid")
        kind = event["kind"]
        if type(kind) is not str or kind not in EVENT_KINDS:
            raise _reject("event kind is unknown")
        operation = event["operationIndex"]
        if operation is not None:
            operation = _integer(operation, "event operation index", 0, operation_count - 1)
        if event["planSha256"] != plan_digest or event["previousEventSha256"] != previous:
            raise _reject("event plan or previous digest is invalid")
        digest = _nullable_digest(event["eventSha256"], "event digest", False)
        if digest != hashlib.sha256(_canonical_event(event, False)).hexdigest() or final:
            raise _reject("event digest chain or terminal state is invalid")

        valid = False
        if kind == "Created":
            valid = sequence == 0 and operation is None
        elif kind == "Prepared":
            valid = sequence > 0 and not prepared and not applying and not rolling_back and operation is None
        elif kind == "Applying":
            valid = prepared and not applying and not rolling_back and operation is None
        elif kind == "Intent":
            valid = applying and not rolling_back and not recovery_observed and pending_intent is None and operation == next_intent
        elif kind == "Applied":
            valid = applying and not rolling_back and operation is not None and operation == pending_intent
        elif kind == "RecoveryObservedApplied":
            valid = applying and not rolling_back and not recovery_observed and operation is not None and operation == pending_intent
        elif kind == "RollingBack":
            valid = sequence > 0 and not rolling_back and operation is None
        elif kind == "RollbackIntent":
            valid = rolling_back and pending_rollback is None and operation is not None and operation == next_rollback
        elif kind == "RollbackApplied":
            valid = (
                rolling_back and operation is not None and operation == next_rollback
                and (pending_rollback is None or operation == pending_rollback)
            )
        elif kind == "Committed":
            valid = (
                applying and not rolling_back and not recovery_observed
                and pending_intent is None and next_intent == operation_count
                and operation is None
            )
        elif kind == "RolledBack":
            valid = rolling_back and pending_rollback is None and next_rollback < 0 and operation is None
        if not valid:
            raise _reject("event transition is invalid")

        if kind == "Prepared":
            prepared = True
        elif kind == "Applying":
            applying = True
        elif kind == "Intent":
            pending_intent = operation
            intended.add(operation)  # type: ignore[arg-type]
            next_rollback = operation  # type: ignore[assignment]
        elif kind in ("Applied", "RecoveryObservedApplied"):
            applied.add(operation)  # type: ignore[arg-type]
            pending_intent = None
            next_intent += 1
            recovery_observed = kind == "RecoveryObservedApplied"
        elif kind == "RollingBack":
            rolling_back = True
            next_rollback = max(intended) if intended else -1
        elif kind == "RollbackIntent":
            pending_rollback = operation
        elif kind == "RollbackApplied":
            rolled_back.add(operation)  # type: ignore[arg-type]
            pending_rollback = None
            next_rollback -= 1
        elif kind in ("Committed", "RolledBack"):
            final = True
        previous = digest
        status = kind

    if status == "" or lines and _parse_json(lines[0], "event record")["kind"] != "Created":
        raise _reject("event log has no creation record")
    return _Replay(status, frozenset(applied), frozenset(rolled_back))


def inspect_transaction_store(game_root: str | os.PathLike[str]) -> TransactionStoreSummary:
    """Inspect the anchored transaction store and return privacy-safe counts."""

    root_fd, canonical_root = _open_root_nofollow(os.fspath(game_root))
    installer_fd: int | None = None
    store_fd: int | None = None
    opened: list[_OpenedTransaction] = []
    try:
        root_identity = os.fstat(root_fd)
        installer_fd = _open_optional_directory(root_fd, ".smapi-installer", 0o700, "installer state")
        if installer_fd is None:
            return TransactionStoreSummary(True, 0, 0, 0, 0, 0, 0)
        store_fd = _open_optional_directory(installer_fd, "transactions", 0o700, "transaction store")
        if store_fd is None:
            return TransactionStoreSummary(True, 0, 0, 0, 0, 0, 0)
        store_before = _identity(os.fstat(store_fd))
        names = _stable_names(store_fd, MAX_TRANSACTIONS, "transaction store")
        if not names:
            return TransactionStoreSummary(True, 0, 0, 0, 0, 0, 0)
        if any(TRANSACTION_NAME.fullmatch(name) is None for name in names):
            raise _reject("transaction store contains an unknown entry")

        aggregate = 0
        for name in names:
            try:
                directory_fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=store_fd)
            except OSError as exc:
                raise _reject("transaction directory can't be opened safely") from exc
            try:
                directory_metadata = os.fstat(directory_fd)
                _require_directory(directory_metadata, 0o700, "transaction directory")
                directory_identity = _identity(directory_metadata)
                children = _stable_names(directory_fd, 4, "transaction directory")
                if not {"journal.json", "events.jsonl"}.issubset(children) or any(
                    child not in {"journal.json", "events.jsonl", "staged", "backups"} for child in children
                ):
                    raise _reject("transaction directory doesn't contain its exact state files")
                for payload_name in ("staged", "backups"):
                    if payload_name in children:
                        payload_fd = _open_optional_directory(directory_fd, payload_name, 0o700, "transaction payload directory")
                        if payload_fd is None:
                            raise _reject("transaction payload directory disappeared")
                        os.close(payload_fd)
                plan_fd, plan_identity = _open_file(directory_fd, "journal.json", "transaction plan")
                try:
                    events_fd, events_identity = _open_file(directory_fd, "events.jsonl", "event log")
                except Exception:
                    os.close(plan_fd)
                    raise
                aggregate += plan_identity[6] + events_identity[6]
                if aggregate > MAX_AGGREGATE_BYTES:
                    os.close(events_fd)
                    os.close(plan_fd)
                    raise _reject("transaction state exceeds its aggregate size bound")
                opened.append(_OpenedTransaction(
                    directory_fd, directory_identity, plan_fd, plan_identity,
                    events_fd, events_identity, name,
                ))
            except Exception:
                if not opened or opened[-1].directory_fd != directory_fd:
                    os.close(directory_fd)
                raise

        incomplete_applied = incomplete_unapplied = rolled_back = committed = 0
        applied_operations = rolled_back_operations = 0
        for item in opened:
            plan_raw = _read_stable(item.plan_fd, item.plan_identity, "transaction plan")
            events_raw = _read_stable(item.events_fd, item.events_identity, "event log")
            plan_digest, operation_count = _validate_plan(
                plan_raw, item.transaction_name, canonical_root, root_identity,
            )
            replay = _validate_events(events_raw, plan_digest, operation_count)
            applied_operations += len(replay.applied)
            rolled_back_operations += len(replay.rolled_back)
            if replay.status == "Committed":
                committed += 1
            elif replay.status == "RolledBack":
                rolled_back += 1
            elif len(replay.applied) > len(replay.rolled_back):
                incomplete_applied += 1
            else:
                incomplete_unapplied += 1
            if _identity(os.fstat(item.directory_fd)) != item.directory_identity:
                raise _reject("transaction directory changed during inspection")
            _stable_names(item.directory_fd, 4, "transaction directory")
        if _identity(os.fstat(store_fd)) != store_before or _stable_names(store_fd, MAX_TRANSACTIONS, "transaction store") != names:
            raise _reject("transaction store changed during inspection")
        if _identity(os.fstat(root_fd)) != _identity(root_identity):
            raise _reject("game root changed during inspection")
        return TransactionStoreSummary(
            False, incomplete_applied, incomplete_unapplied, rolled_back, committed,
            applied_operations, rolled_back_operations,
        )
    finally:
        for item in opened:
            item.close()
        if store_fd is not None:
            os.close(store_fd)
        if installer_fd is not None:
            os.close(installer_fd)
        os.close(root_fd)


def classify_scenario(
    scenario: contract.Scenario | str,
    *,
    phase: str,
    before_digest: str,
    current_digest: str,
    barrier_observed: bool,
    backend_loss_observed: bool,
    fresh_session_observed: bool,
    summary: TransactionStoreSummary,
) -> contract.DurableState:
    """Apply the closed evidence oracle to one physical store summary."""

    spec = contract.capture_spec(scenario)
    if phase not in ("capture", "after"):
        raise _reject("qualification phase is unknown")
    if any(type(value) is not bool for value in (barrier_observed, backend_loss_observed, fresh_session_observed)):
        raise _reject("observation flags aren't booleans")
    if type(before_digest) is not str or DIGEST.fullmatch(before_digest) is None:
        raise _reject("before-state digest isn't canonical")
    if type(current_digest) is not str or DIGEST.fullmatch(current_digest) is None:
        raise _reject("current-state digest isn't canonical")
    if not isinstance(summary, TransactionStoreSummary):
        raise _reject("transaction summary has the wrong type")
    incomplete = summary.incomplete_applied + summary.incomplete_unapplied
    if incomplete > 1:
        raise _reject("qualification has multiple incomplete transactions")

    restored = before_digest == current_digest
    scenario_value = spec.scenario
    if scenario_value.value.startswith("E2-"):
        if incomplete != 0 or not restored:
            raise _reject("E2 state isn't unchanged or completely restored")
        if scenario_value is contract.Scenario.E2_CROSS_DEVICE and summary.rolled_back < 1:
            raise _reject("cross-device failure lacks a completed rollback")
        if barrier_observed or backend_loss_observed or fresh_session_observed:
            raise _reject("E2 contains an unrelated observation")
    elif scenario_value is contract.Scenario.C2:
        if not barrier_observed or backend_loss_observed or fresh_session_observed:
            raise _reject("C2 boundary observations are inconsistent")
        if phase == "capture":
            if summary.incomplete_applied != 1 or summary.incomplete_unapplied != 0:
                raise _reject("C2 capture isn't exactly one applied incomplete transaction")
        elif incomplete != 0 or summary.rolled_back < 1 or not restored:
            raise _reject("C2 terminal state isn't rolled back and restored")
    elif scenario_value is contract.Scenario.C3:
        if not barrier_observed or backend_loss_observed or fresh_session_observed:
            raise _reject("C3 boundary observations are inconsistent")
        if incomplete != 0 or summary.rolled_back < 1 or not restored:
            raise _reject("C3 state isn't rolled back and restored")
    elif scenario_value is contract.Scenario.E5:
        if not barrier_observed or not backend_loss_observed or fresh_session_observed:
            raise _reject("E5 boundary observations are inconsistent")
        if summary.incomplete_applied != 1 or summary.incomplete_unapplied != 0:
            raise _reject("E5 isn't exactly one applied incomplete transaction")
    elif scenario_value is contract.Scenario.E6:
        if not barrier_observed or not backend_loss_observed or not fresh_session_observed:
            raise _reject("E6 recovery observations are inconsistent")
        if incomplete != 0 or summary.rolled_back < 1 or not restored:
            raise _reject("E6 recovery isn't rolled back and restored")
    else:  # pragma: no cover - capture_spec already closes the enum.
        raise _reject("qualification scenario is unknown")

    return spec.durable_at_capture if phase == "capture" else spec.durable_after


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-root", required=True)
    parser.add_argument("--scenario", required=True, choices=[item.value for item in contract.Scenario])
    parser.add_argument("--phase", required=True, choices=("capture", "after"))
    parser.add_argument("--before-digest", required=True)
    parser.add_argument("--current-digest", required=True)
    parser.add_argument("--barrier-observed", action="store_true")
    parser.add_argument("--backend-loss-observed", action="store_true")
    parser.add_argument("--fresh-session-observed", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    try:
        arguments = _parser().parse_args(argv)
        summary = inspect_transaction_store(arguments.game_root)
        durable = classify_scenario(
            arguments.scenario,
            phase=arguments.phase,
            before_digest=arguments.before_digest,
            current_digest=arguments.current_digest,
            barrier_observed=arguments.barrier_observed,
            backend_loss_observed=arguments.backend_loss_observed,
            fresh_session_observed=arguments.fresh_session_observed,
            summary=summary,
        )
        print(json.dumps({
            "absent": summary.absent,
            "appliedOperations": summary.applied_operations,
            "committed": summary.committed,
            "durableState": durable.value,
            "incompleteApplied": summary.incomplete_applied,
            "incompleteUnapplied": summary.incomplete_unapplied,
            "ok": True,
            "rolledBack": summary.rolled_back,
            "rolledBackOperations": summary.rolled_back_operations,
            "schemaVersion": 1,
        }, separators=(",", ":"), sort_keys=True))
        return 0
    except (ClassificationError, OSError) as exc:
        print(json.dumps({
            "code": "unsafe-durable-state", "ok": False, "schemaVersion": 1,
            "status": "rejected",
        }, separators=(",", ":"), sort_keys=True))
        print(f"durable-state classification rejected: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
