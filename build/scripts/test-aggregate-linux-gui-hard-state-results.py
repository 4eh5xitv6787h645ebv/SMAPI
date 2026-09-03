#!/usr/bin/env python3
"""Fixture-free tests for the Linux GUI hard-state result aggregator."""

from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import random
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


SCRIPT = Path(__file__).with_name("aggregate-linux-gui-hard-state-results.py")
MODEL_PATH = Path(__file__).with_name("linux_gui_hard_state_capture_contract.py")


def load(path: Path, name: str):
    specification = importlib.util.spec_from_file_location(name, path)
    assert specification is not None and specification.loader is not None
    module = importlib.util.module_from_spec(specification)
    sys.modules[name] = module
    specification.loader.exec_module(module)
    return module


aggregator = load(SCRIPT, "linux_gui_hard_state_result_aggregator")
model = aggregator.load_capture_model()


RELEASE_TAG = "fork-4eh5xitv6787h645ebv-linux-v4.5.2-alpha.3"
COMMON = {
    "releaseTag": RELEASE_TAG,
    "sourceCommit": "1" * 40,
    "sourceTree": "2" * 40,
    "publicReleaseUrl": f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/{RELEASE_TAG}",
    "packageSha256": "3" * 64,
    "guiSha256": "4" * 64,
    "backendSha256": "5" * 64,
    "environmentProfile": "ubuntu-24.04-gnome-x11",
}
FAILURE_BYTES = (
    b'{"code":"invalid-input","kind":"linux-gui-hard-state-aggregate","ok":false,'
    b'"schemaVersion":2,"status":"rejected"}\n'
)


def case_record(spec) -> dict[str, object]:
    return {
        "kind": "linux-gui-hard-state-qualification",
        "schemaVersion": 2,
        "status": "captured_pending_privacy_and_public_authority",
        "ok": True,
        "scenario": spec.scenario.value,
        "evidenceId": spec.evidence_id.value,
        "fault": None if spec.fault is None else spec.fault.value,
        **COMMON,
        "visibleState": spec.visible_state.value,
        "durableAtCapture": spec.durable_at_capture.value,
        "durableAfter": spec.durable_after.value,
        "exactWindowCaptured": True,
        "atspiEvidenceRecorded": True,
        "durableClassificationVerified": True,
        "cleanupComplete": True,
        "packageIdentityReverified": True,
    }


def write_record(root: Path, spec, value: dict[str, object] | None = None, raw: bytes | None = None) -> Path:
    path = root / f"{spec.output_basename}.result.json"
    content = raw if raw is not None else aggregator.canonical_bytes(value or case_record(spec))
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        os.write(descriptor, content)
    finally:
        os.close(descriptor)
    return path


class Fixture:
    def __init__(self, order=None):
        self.temporary = tempfile.TemporaryDirectory(prefix="smapi-gui-result-aggregate-")
        self.root = Path(self.temporary.name)
        os.chmod(self.root, 0o700)
        specs = list(model.CAPTURE_SPECS if order is None else order)
        for spec in specs:
            write_record(self.root, spec)

    def close(self) -> None:
        self.temporary.cleanup()


class StatProxy:
    def __init__(self, source: os.stat_result, *, uid: int):
        self._source = source
        self.st_uid = uid

    def __getattr__(self, name: str):
        return getattr(self._source, name)


class AggregateResultsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = Fixture()

    def tearDown(self) -> None:
        self.fixture.close()

    def assert_rejected(self, root: Path | None = None) -> None:
        status, output = aggregator.run(["--input-directory", str(root or self.fixture.root)])
        self.assertEqual(1, status)
        self.assertEqual(FAILURE_BYTES, output)

    def rewrite(self, spec, mutate) -> Path:
        path = self.fixture.root / f"{spec.output_basename}.result.json"
        value = case_record(spec)
        mutate(value)
        path.write_bytes(aggregator.canonical_bytes(value))
        os.chmod(path, 0o600)
        return path

    def test_valid_aggregate_has_only_fixed_public_fields(self) -> None:
        status, output = aggregator.run(["--input-directory", str(self.fixture.root)])
        self.assertEqual(0, status)
        value = json.loads(output)
        self.assertEqual("linux-gui-hard-state-aggregate", value["kind"])
        self.assertEqual("captured_pending_privacy_and_public_authority", value["status"])
        self.assertIs(value["ok"], True)
        self.assertEqual(2, value["schemaVersion"])
        self.assertEqual(8, value["scenarioCount"])
        self.assertEqual(8, value["captureCount"])
        self.assertEqual(4, value["e2FaultSourceCount"])
        self.assertEqual(COMMON, {key: value[key] for key in COMMON})
        self.assertEqual(
            [spec.scenario.value for spec in model.CAPTURE_SPECS],
            [item["scenario"] for item in value["scenarios"]],
        )
        self.assertEqual(
            {
                "durableAfter", "durableAtCapture", "evidenceId", "fault", "scenario",
                "visibleState",
            },
            set(value["scenarios"][0]),
        )
        forbidden = (str(self.fixture.root), "pid", "inode", "timestamp", "imageSha256")
        decoded = output.decode("ascii")
        self.assertTrue(output.endswith(b"\n"))
        self.assertTrue(all(text not in decoded for text in forbidden))

    def test_creation_order_does_not_change_bytes(self) -> None:
        expected = aggregator.run(["--input-directory", str(self.fixture.root)])[1]
        for seed in range(5):
            order = list(model.CAPTURE_SPECS)
            random.Random(seed).shuffle(order)
            alternate = Fixture(order)
            try:
                status, output = aggregator.run(["--input-directory", str(alternate.root)])
                self.assertEqual(0, status)
                self.assertEqual(expected, output)
            finally:
                alternate.close()

    def test_missing_and_extra_objects_are_rejected(self) -> None:
        first = model.CAPTURE_SPECS[0]
        (self.fixture.root / f"{first.output_basename}.result.json").unlink()
        self.assert_rejected()
        write_record(self.fixture.root, first)
        (self.fixture.root / "unexpected").mkdir()
        self.assert_rejected()

    def test_duplicate_json_keys_are_rejected(self) -> None:
        spec = model.CAPTURE_SPECS[0]
        raw = aggregator.canonical_bytes(case_record(spec)).replace(b'"ok":true', b'"ok":true,"ok":true')
        path = self.fixture.root / f"{spec.output_basename}.result.json"
        path.write_bytes(raw)
        os.chmod(path, 0o600)
        self.assert_rejected()

    def test_wrong_model_mapping_is_rejected(self) -> None:
        spec = model.CAPTURE_SPECS[0]
        self.rewrite(spec, lambda value: value.__setitem__("visibleState", "install-failed-rolled-back"))
        self.assert_rejected()

    def test_production_identity_mismatch_is_rejected(self) -> None:
        spec = model.CAPTURE_SPECS[-1]

        def mutate(value):
            tag = "fork-4eh5xitv6787h645ebv-linux-v4.5.2-alpha.4"
            value["releaseTag"] = tag
            value["publicReleaseUrl"] = f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/{tag}"

        self.rewrite(spec, mutate)
        self.assert_rejected()

    def test_environment_profile_mismatch_and_unknown_profile_are_rejected(self) -> None:
        spec = model.CAPTURE_SPECS[1]
        self.rewrite(spec, lambda value: value.__setitem__("environmentProfile", "ubuntu-24.04-kde-x11"))
        self.assert_rejected()
        self.rewrite(spec, lambda value: value.__setitem__("environmentProfile", "private-desktop"))
        self.assert_rejected()

    def test_each_required_boolean_must_be_exact_true(self) -> None:
        spec = model.CAPTURE_SPECS[2]
        for key in aggregator.TRUE_KEYS:
            with self.subTest(key=key):
                self.rewrite(spec, lambda value, key=key: value.__setitem__(key, False))
                self.assert_rejected()

    def test_status_and_schema_shape_are_exact(self) -> None:
        spec = model.CAPTURE_SPECS[3]
        self.rewrite(spec, lambda value: value.__setitem__("status", "captured"))
        self.assert_rejected()
        self.rewrite(spec, lambda value: value.__setitem__("unexpected", True))
        self.assert_rejected()
        self.rewrite(spec, lambda value: value.pop("fault"))
        self.assert_rejected()

    def test_private_sentinel_never_appears_in_failure(self) -> None:
        sentinel = "wife-private-modpack-Blossom-secret"
        spec = model.CAPTURE_SPECS[4]
        self.rewrite(spec, lambda value: value.__setitem__("releaseTag", sentinel))
        status, output = aggregator.run(["--input-directory", str(self.fixture.root)])
        self.assertEqual(1, status)
        self.assertEqual(FAILURE_BYTES, output)
        self.assertNotIn(sentinel.encode("ascii"), output)
        self.assertNotIn(str(self.fixture.root).encode("ascii"), output)

    def test_symlink_and_hard_link_are_rejected(self) -> None:
        spec = model.CAPTURE_SPECS[5]
        path = self.fixture.root / f"{spec.output_basename}.result.json"
        outside = self.fixture.root.parent / f"{self.fixture.root.name}-outside"
        outside.write_bytes(path.read_bytes())
        os.chmod(outside, 0o600)
        try:
            path.unlink()
            path.symlink_to(outside)
            self.assert_rejected()
            path.unlink()
            os.link(outside, path)
            self.assert_rejected()
        finally:
            outside.unlink(missing_ok=True)

    def test_mode_and_owner_are_rejected(self) -> None:
        spec = model.CAPTURE_SPECS[6]
        path = self.fixture.root / f"{spec.output_basename}.result.json"
        os.chmod(path, 0o640)
        self.assert_rejected()
        os.chmod(path, 0o600)
        real_stat = os.stat

        def mismatched_owner(target, *args, **kwargs):
            value = real_stat(target, *args, **kwargs)
            if target == f"{spec.output_basename}.result.json":
                return StatProxy(value, uid=os.getuid() + 1)
            return value

        with mock.patch.object(aggregator.os, "stat", side_effect=mismatched_owner):
            self.assert_rejected()

    def test_per_file_size_limit_is_rejected(self) -> None:
        spec = model.CAPTURE_SPECS[7]
        path = self.fixture.root / f"{spec.output_basename}.result.json"
        path.write_bytes(b"x" * (aggregator.MAX_RESULT_BYTES + 1))
        os.chmod(path, 0o600)
        self.assert_rejected()

    def test_metadata_race_is_rejected(self) -> None:
        original = aggregator.stable_metadata
        calls = 0

        def raced(value):
            nonlocal calls
            calls += 1
            result = original(value)
            if calls == 3:
                return result[:-1] + (result[-1] + 1,)
            return result

        with mock.patch.object(aggregator, "stable_metadata", side_effect=raced):
            self.assert_rejected()

    def test_cli_is_exact_and_failure_is_silent_and_fixed(self) -> None:
        invalid_arguments = (
            [],
            ["--input-directory"],
            ["--input-directory", "relative"],
            ["--input-directory", str(self.fixture.root), "extra"],
        )
        for arguments in invalid_arguments:
            result = subprocess.run([sys.executable, SCRIPT, *arguments], check=False, capture_output=True)
            self.assertEqual(1, result.returncode)
            self.assertEqual(FAILURE_BYTES, result.stdout)
            self.assertEqual(b"", result.stderr)


if __name__ == "__main__":
    unittest.main()
