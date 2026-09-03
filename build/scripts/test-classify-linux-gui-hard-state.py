#!/usr/bin/env python3
"""Synthetic tests for strict Linux GUI durable-state classification."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
import uuid


SCRIPTS = Path(__file__).resolve().parent
sys.path.insert(0, str(SCRIPTS))
SPEC = importlib.util.spec_from_file_location(
    "classify_linux_gui_hard_state",
    SCRIPTS / "classify-linux-gui-hard-state.py",
)
assert SPEC is not None and SPEC.loader is not None
classifier = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = classifier
SPEC.loader.exec_module(classifier)
import linux_gui_hard_state_capture_contract as contract  # noqa: E402


ZERO = "0" * 64
ONE = "1" * 64


def event_bytes(plan_digest: str, events: list[tuple[str, int | None]]) -> bytes:
    previous = None
    lines: list[bytes] = []
    for sequence, (kind, operation) in enumerate(events):
        value = {
            "schemaVersion": 1,
            "sequence": sequence,
            "kind": kind,
            "operationIndex": operation,
            "planSha256": plan_digest,
            "previousEventSha256": previous,
        }
        unsigned = json.dumps(value, separators=(",", ":")).encode()
        digest = hashlib.sha256(unsigned).hexdigest()
        value["eventSha256"] = digest
        lines.append(json.dumps(value, separators=(",", ":")).encode())
        previous = digest
    return b"\n".join(lines) + b"\n"


CHAINS = {
    "unapplied": [
        ("Created", None), ("Prepared", None), ("Applying", None),
    ],
    "applied": [
        ("Created", None), ("Prepared", None), ("Applying", None),
        ("Intent", 0), ("Applied", 0),
    ],
    "committed": [
        ("Created", None), ("Prepared", None), ("Applying", None),
        ("Intent", 0), ("Applied", 0), ("Committed", None),
    ],
    "rolled-back": [
        ("Created", None), ("Prepared", None), ("Applying", None),
        ("Intent", 0), ("Applied", 0), ("RollingBack", None),
        ("RollbackIntent", 0), ("RollbackApplied", 0), ("RolledBack", None),
    ],
    "recovery-rolled-back": [
        ("Created", None), ("Prepared", None), ("Applying", None),
        ("Intent", 0), ("RecoveryObservedApplied", 0), ("RollingBack", None),
        ("RollbackApplied", 0), ("RolledBack", None),
    ],
}


class ClassifierTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.mkdtemp(prefix="smapi-classifier-test-")
        self.game = Path(self.temporary) / "game"
        self.game.mkdir(mode=0o700)

    def tearDown(self) -> None:
        shutil.rmtree(self.temporary)

    def _store(self) -> Path:
        installer = self.game / ".smapi-installer"
        installer.mkdir(mode=0o700, exist_ok=True)
        store = installer / "transactions"
        store.mkdir(mode=0o700, exist_ok=True)
        os.chmod(store, 0o700)
        return store

    def _plan(self, transaction_id: uuid.UUID, *, index: int = 0) -> bytes:
        root = self.game.stat()
        value = {
            "schemaVersion": 3,
            "transactionId": str(transaction_id),
            "createdUtcTicks": 638_800_000_000_000_000,
            "canonicalGameRoot": str(self.game),
            "gameRootInode": root.st_ino,
            "gameRootDeviceMajor": os.major(root.st_dev),
            "gameRootDeviceMinor": os.minor(root.st_dev),
            "hasCoreAuthorizedReceiptMutation": False,
            "coreGenerationId": None,
            "coreRecoveryOperationCount": 0,
            "coreRecoveryContentCount": 0,
            "hasCoreAuthorizedManifestMutation": False,
            "hasCoreAuthorizedRecoveryPointerMutation": False,
            "entries": [{
                "index": index,
                "kind": "WriteFile",
                "relativePath": "smapi-internal/example",
                "hadOriginal": False,
                "expectedExistingSha256": None,
                "expectedResultSha256": ONE,
                "resultUnixMode": 0o755,
                "backupRelativePath": "backups/00000000",
                "stagedRelativePath": "staged/00000000",
                "createdDirectories": ["smapi-internal"],
            }],
        }
        return json.dumps(value, indent=2).encode()

    def _transaction(self, chain: str, *, identifier: uuid.UUID | None = None) -> Path:
        identifier = identifier or uuid.uuid4()
        directory = self._store() / identifier.hex
        directory.mkdir(mode=0o700)
        os.chmod(directory, 0o700)
        plan = self._plan(identifier)
        (directory / "journal.json").write_bytes(plan)
        (directory / "events.jsonl").write_bytes(event_bytes(hashlib.sha256(plan).hexdigest(), CHAINS[chain]))
        os.chmod(directory / "journal.json", 0o600)
        os.chmod(directory / "events.jsonl", 0o600)
        return directory

    def _summary(self, kind: str) -> object:
        if kind != "absent":
            self._transaction(kind)
        return classifier.inspect_transaction_store(self.game)

    def assert_rejected(self, function, *args, **kwargs) -> None:
        with self.assertRaises(classifier.ClassificationError):
            function(*args, **kwargs)

    def test_absent_and_every_valid_physical_class_are_closed_counts(self) -> None:
        absent = self._summary("absent")
        self.assertEqual(
            (True, 0, 0, 0, 0, 0, 0),
            tuple(getattr(absent, field) for field in (
                "absent", "incomplete_applied", "incomplete_unapplied",
                "rolled_back", "committed", "applied_operations", "rolled_back_operations",
            )),
        )
        cases = {
            "unapplied": (0, 1, 0, 0, 0, 0),
            "applied": (1, 0, 0, 0, 1, 0),
            "committed": (0, 0, 0, 1, 1, 0),
            "rolled-back": (0, 0, 1, 0, 1, 1),
            "recovery-rolled-back": (0, 0, 1, 0, 1, 1),
        }
        for chain, expected in cases.items():
            with self.subTest(chain=chain):
                shutil.rmtree(self.game / ".smapi-installer", ignore_errors=True)
                actual = self._summary(chain)
                self.assertFalse(actual.absent)
                self.assertEqual(expected, (
                    actual.incomplete_applied, actual.incomplete_unapplied,
                    actual.rolled_back, actual.committed,
                    actual.applied_operations, actual.rolled_back_operations,
                ))
                with self.assertRaises((AttributeError, TypeError)):
                    actual.committed = 9

    def test_all_eight_scenario_composites_at_capture_and_after(self) -> None:
        absent = classifier.TransactionStoreSummary(True, 0, 0, 0, 0, 0, 0)
        applied = classifier.TransactionStoreSummary(False, 1, 0, 0, 0, 1, 0)
        rolled = classifier.TransactionStoreSummary(False, 0, 0, 1, 0, 1, 1)
        inputs = {
            contract.Scenario.E2_PERMISSION: (absent, False, False, False),
            contract.Scenario.E2_READ_ONLY: (absent, False, False, False),
            contract.Scenario.E2_DISK_FULL: (absent, False, False, False),
            contract.Scenario.E2_CROSS_DEVICE: (rolled, False, False, False),
            contract.Scenario.C2: (applied, True, False, False),
            contract.Scenario.C3: (rolled, True, False, False),
            contract.Scenario.E5: (applied, True, True, False),
            contract.Scenario.E6: (rolled, True, True, True),
        }
        for scenario, (capture_summary, barrier, backend, fresh) in inputs.items():
            spec = contract.capture_spec(scenario)
            with self.subTest(scenario=scenario.value, phase="capture"):
                self.assertIs(
                    spec.durable_at_capture,
                    classifier.classify_scenario(
                        scenario, phase="capture", before_digest=ZERO,
                        current_digest=ZERO, barrier_observed=barrier,
                        backend_loss_observed=backend, fresh_session_observed=fresh,
                        summary=capture_summary,
                    ),
                )
            after_summary = rolled if scenario in (contract.Scenario.C2, contract.Scenario.C3, contract.Scenario.E6) else capture_summary
            with self.subTest(scenario=scenario.value, phase="after"):
                self.assertIs(
                    spec.durable_after,
                    classifier.classify_scenario(
                        scenario, phase="after", before_digest=ZERO,
                        current_digest=ZERO, barrier_observed=barrier,
                        backend_loss_observed=backend, fresh_session_observed=fresh,
                        summary=after_summary,
                    ),
                )

    def test_event_canonical_bytes_duplicate_keys_and_chain_are_strict(self) -> None:
        mutations = ("canonical", "duplicate", "hash", "previous", "sequence", "transition")
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                shutil.rmtree(self.game / ".smapi-installer", ignore_errors=True)
                directory = self._transaction("applied")
                path = directory / "events.jsonl"
                lines = path.read_bytes().splitlines()
                values = [json.loads(line) for line in lines]
                if mutation == "canonical":
                    lines[0] = json.dumps(values[0]).encode()
                elif mutation == "duplicate":
                    lines[0] = lines[0].replace(b'{"schemaVersion":1', b'{"schemaVersion":1,"schemaVersion":1', 1)
                elif mutation == "hash":
                    values[-1]["eventSha256"] = ZERO
                    lines[-1] = json.dumps(values[-1], separators=(",", ":")).encode()
                elif mutation == "previous":
                    values[-1]["previousEventSha256"] = ZERO
                    lines[-1] = json.dumps(values[-1], separators=(",", ":")).encode()
                elif mutation == "sequence":
                    values[-1]["sequence"] = 99
                    lines[-1] = json.dumps(values[-1], separators=(",", ":")).encode()
                else:
                    plan_digest = values[0]["planSha256"]
                    lines = event_bytes(plan_digest, [("Created", None), ("Committed", None)]).splitlines()
                path.write_bytes(b"\n".join(lines) + b"\n")
                self.assert_rejected(classifier.inspect_transaction_store, self.game)

    def test_plan_schema_index_paths_digests_modes_and_links_are_strict(self) -> None:
        mutations = ("schema", "index", "path", "digest", "mode", "backup", "staged", "hardlink", "file-mode")
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                shutil.rmtree(self.game / ".smapi-installer", ignore_errors=True)
                directory = self._transaction("applied")
                path = directory / "journal.json"
                value = json.loads(path.read_bytes())
                entry = value["entries"][0]
                if mutation == "schema":
                    value["schemaVersion"] = 2
                elif mutation == "index":
                    entry["index"] = 1
                elif mutation == "path":
                    entry["relativePath"] = "../escape"
                elif mutation == "digest":
                    entry["expectedResultSha256"] = "A" * 64
                elif mutation == "mode":
                    entry["resultUnixMode"] = 0o1000
                elif mutation == "backup":
                    entry["backupRelativePath"] = "backups/1"
                elif mutation == "staged":
                    entry["stagedRelativePath"] = "staged/1"
                elif mutation == "hardlink":
                    os.link(path, self.game / "journal-hardlink")
                elif mutation == "file-mode":
                    os.chmod(path, 0o644)
                if mutation not in ("hardlink", "file-mode"):
                    path.write_bytes(json.dumps(value, indent=2).encode())
                    os.chmod(path, 0o600)
                self.assert_rejected(classifier.inspect_transaction_store, self.game)

    def test_nofollow_directory_modes_unknown_names_and_oversize_are_rejected(self) -> None:
        cases = ("symlink", "directory-mode", "unknown", "oversize")
        for case in cases:
            with self.subTest(case=case):
                shutil.rmtree(self.game / ".smapi-installer", ignore_errors=True)
                directory = self._transaction("applied")
                if case == "symlink":
                    real = self.game / "real-events"
                    (directory / "events.jsonl").rename(real)
                    (directory / "events.jsonl").symlink_to(real)
                elif case == "directory-mode":
                    os.chmod(directory, 0o755)
                elif case == "unknown":
                    (directory / "unknown").write_text("x")
                else:
                    with (directory / "events.jsonl").open("r+b") as stream:
                        stream.truncate(classifier.MAX_FILE_BYTES + 1)
                self.assert_rejected(classifier.inspect_transaction_store, self.game)

    def test_aggregate_size_bound_is_checked_before_file_contents(self) -> None:
        store = self._store()
        for _index in range(9):
            identifier = uuid.uuid4()
            directory = store / identifier.hex
            directory.mkdir(mode=0o700)
            for name in ("journal.json", "events.jsonl"):
                path = directory / name
                path.touch(mode=0o600)
                path.chmod(0o600)
                with path.open("r+b") as stream:
                    stream.truncate(classifier.MAX_FILE_BYTES)
        self.assert_rejected(classifier.inspect_transaction_store, self.game)

    def test_oracle_rejects_zero_or_two_incomplete_and_late_commit(self) -> None:
        absent = classifier.TransactionStoreSummary(True, 0, 0, 0, 0, 0, 0)
        two = classifier.TransactionStoreSummary(False, 2, 0, 0, 0, 2, 0)
        committed = classifier.TransactionStoreSummary(False, 0, 0, 0, 1, 1, 0)
        common = dict(
            phase="capture", before_digest=ZERO, current_digest=ZERO,
            barrier_observed=True, backend_loss_observed=True,
            fresh_session_observed=False,
        )
        self.assert_rejected(classifier.classify_scenario, "E5", summary=absent, **common)
        self.assert_rejected(classifier.classify_scenario, "E5", summary=two, **common)
        self.assert_rejected(classifier.classify_scenario, "E5", summary=committed, **common)

    def test_oracle_rejects_failed_or_stale_recovery_claims(self) -> None:
        incomplete = classifier.TransactionStoreSummary(False, 1, 0, 0, 0, 1, 0)
        rolled = classifier.TransactionStoreSummary(False, 0, 0, 1, 0, 1, 1)
        base = dict(
            scenario="E6", phase="after", before_digest=ZERO,
            current_digest=ZERO, barrier_observed=True,
            backend_loss_observed=True, fresh_session_observed=True,
        )
        self.assert_rejected(classifier.classify_scenario, summary=incomplete, **base)
        self.assert_rejected(classifier.classify_scenario, summary=rolled, **{**base, "fresh_session_observed": False})
        self.assert_rejected(classifier.classify_scenario, summary=rolled, **{**base, "current_digest": ONE})


if __name__ == "__main__":
    unittest.main(verbosity=2)
