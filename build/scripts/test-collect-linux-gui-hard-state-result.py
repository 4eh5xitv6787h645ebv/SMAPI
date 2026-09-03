#!/usr/bin/env python3
"""Focused tests for secure Linux GUI hard-state result collection."""

from __future__ import annotations

import fcntl
import importlib.util
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


SCRIPT = Path(__file__).with_name("collect-linux-gui-hard-state-result.py")


def load(path: Path, name: str):
    specification = importlib.util.spec_from_file_location(name, path)
    assert specification is not None and specification.loader is not None
    module = importlib.util.module_from_spec(specification)
    sys.modules[name] = module
    specification.loader.exec_module(module)
    return module


collector = load(SCRIPT, "linux_gui_hard_state_result_collector")
aggregator = collector.load_aggregator()
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
FAILURE = (
    b'{"code":"invalid-input","kind":"linux-gui-hard-state-result-collection",'
    b'"ok":false,"schemaVersion":1,"status":"rejected"}\n'
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


def authority(spec, **changes) -> dict[str, object]:
    value: dict[str, object] = {
        "kind": collector.AUTHORITY_KIND,
        "schemaVersion": 1,
        "scenario": spec.scenario.value,
        **COMMON,
    }
    value.update(changes)
    return value


def sealed(value: dict[str, object] | None = None, *, raw: bytes | None = None, seal: bool = True) -> int:
    descriptor = os.memfd_create("hard-state-authority-test", os.MFD_CLOEXEC | os.MFD_ALLOW_SEALING)
    content = raw if raw is not None else collector.canonical_bytes(value or {})
    os.write(descriptor, content)
    if seal:
        fcntl.fcntl(descriptor, fcntl.F_ADD_SEALS, collector.REQUIRED_MEMFD_SEALS)
    return descriptor


def write_private(path: Path, raw: bytes) -> None:
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        os.write(descriptor, raw)
    finally:
        os.close(descriptor)


class CollectorFixture:
    def __init__(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="smapi-hard-state-collector-")
        self.root = Path(self.temporary.name)
        self.results = self.root / "results"
        self.results.mkdir(mode=0o700)
        self.broker = self.root / "broker-result.json"

    def set_result(self, spec, value: dict[str, object] | None = None, raw: bytes | None = None) -> None:
        self.broker.unlink(missing_ok=True)
        write_private(
            self.broker,
            raw if raw is not None else aggregator.canonical_bytes(value or case_record(spec)),
        )

    def close(self) -> None:
        self.temporary.cleanup()


class CollectResultTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = CollectorFixture()

    def tearDown(self) -> None:
        self.fixture.close()

    def collect(self, spec, expected_authority: dict[str, object] | None = None) -> str:
        descriptor = sealed(expected_authority or authority(spec))
        try:
            return collector.collect(str(self.fixture.broker), str(self.fixture.results), descriptor)
        finally:
            os.close(descriptor)

    def assert_rejected(self, spec, expected_authority: dict[str, object] | None = None) -> None:
        descriptor = sealed(expected_authority or authority(spec))
        try:
            with self.assertRaises(collector.CollectionError):
                collector.collect(str(self.fixture.broker), str(self.fixture.results), descriptor)
        finally:
            os.close(descriptor)

    def test_collects_all_eight_to_model_derived_names_then_aggregates_end_to_end(self) -> None:
        for spec in model.CAPTURE_SPECS:
            with self.subTest(scenario=spec.scenario.value):
                self.fixture.set_result(spec)
                name = self.collect(spec)
                self.assertEqual(f"{spec.output_basename}.result.json", name)
                output = self.fixture.results / name
                self.assertEqual(aggregator.canonical_bytes(case_record(spec)), output.read_bytes())
                metadata = output.stat()
                self.assertEqual(0o600, stat.S_IMODE(metadata.st_mode))
                self.assertEqual(os.getuid(), metadata.st_uid)
                self.assertEqual(1, metadata.st_nlink)
        aggregate = json.loads(aggregator.aggregate(str(self.fixture.results)))
        self.assertEqual(8, aggregate["scenarioCount"])
        self.assertEqual(8, aggregate["captureCount"])
        self.assertEqual(COMMON, {key: aggregate[key] for key in COMMON})

    def test_never_overwrites_an_existing_case_result(self) -> None:
        spec = model.CAPTURE_SPECS[0]
        self.fixture.set_result(spec)
        name = self.collect(spec)
        path = self.fixture.results / name
        before = path.read_bytes()
        before_stat = path.stat()
        with self.assertRaises(collector.CollectionError):
            self.collect(spec)
        after_stat = path.stat()
        self.assertEqual(before, path.read_bytes())
        self.assertEqual((before_stat.st_dev, before_stat.st_ino), (after_stat.st_dev, after_stat.st_ino))

    def test_rejects_noncanonical_symlink_hardlink_fifo_device_mode_and_empty_sources(self) -> None:
        spec = model.CAPTURE_SPECS[0]
        self.fixture.set_result(spec)
        with self.assertRaises(collector.CollectionError):
            descriptor = sealed(authority(spec))
            try:
                collector.collect(str(self.fixture.root) + "/./" + self.fixture.broker.name, str(self.fixture.results), descriptor)
            finally:
                os.close(descriptor)

        original = self.fixture.broker.read_bytes()
        outside = self.fixture.root / "outside.json"
        self.fixture.broker.rename(outside)
        self.fixture.broker.symlink_to(outside)
        self.assert_rejected(spec)
        self.fixture.broker.unlink()
        os.link(outside, self.fixture.broker)
        self.assert_rejected(spec)
        self.fixture.broker.unlink()
        outside.unlink()
        os.mkfifo(self.fixture.broker, 0o600)
        self.assert_rejected(spec)
        self.fixture.broker.unlink()
        write_private(self.fixture.broker, original)
        os.chmod(self.fixture.broker, 0o640)
        self.assert_rejected(spec)
        self.fixture.broker.unlink()
        write_private(self.fixture.broker, b"")
        self.assert_rejected(spec)

        descriptor = sealed(authority(spec))
        try:
            with self.assertRaises(collector.CollectionError):
                collector.collect("/dev/null", str(self.fixture.results), descriptor)
        finally:
            os.close(descriptor)

    def test_rejects_duplicate_json_keys_noninteger_schema_and_private_extra_fields(self) -> None:
        spec = model.CAPTURE_SPECS[1]
        base = aggregator.canonical_bytes(case_record(spec))
        for raw in (
            base.replace(b'"ok":true', b'"ok":true,"ok":true'),
            base.replace(b'"schemaVersion":2', b'"schemaVersion":2.0'),
            base.replace(b'"schemaVersion":2', b'"schemaVersion":true'),
            base.replace(b'"status":', b'"privatePath":"/private/Blossom","status":'),
            (json.dumps(case_record(spec), indent=2) + "\n").encode("ascii"),
        ):
            with self.subTest(raw=raw[:60]):
                self.fixture.set_result(spec, raw=raw)
                self.assert_rejected(spec)

    def test_rejects_scenario_profile_and_every_candidate_identity_mismatch(self) -> None:
        spec = model.CAPTURE_SPECS[2]
        self.fixture.set_result(spec)
        alternate = model.CAPTURE_SPECS[3]
        self.assert_rejected(spec, authority(alternate))
        self.assert_rejected(spec, authority(spec, environmentProfile="ubuntu-24.04-kde-x11"))
        replacements = {
            "releaseTag": "fork-4eh5xitv6787h645ebv-linux-v4.5.2-alpha.4",
            "sourceCommit": "6" * 40,
            "sourceTree": "7" * 40,
            "packageSha256": "8" * 64,
            "guiSha256": "9" * 64,
            "backendSha256": "a" * 64,
        }
        for key, value in replacements.items():
            with self.subTest(key=key):
                changed = dict(authority(spec))
                changed[key] = value
                if key == "releaseTag":
                    changed["publicReleaseUrl"] = (
                        "https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/" + value
                    )
                self.assert_rejected(spec, changed)
        changed_url = dict(authority(spec))
        changed_url["publicReleaseUrl"] += "-moved"
        self.assert_rejected(spec, changed_url)

    def test_authority_must_be_exact_duplicate_free_fully_sealed_memfd(self) -> None:
        spec = model.CAPTURE_SPECS[3]
        self.fixture.set_result(spec)
        descriptor = sealed(authority(spec), seal=False)
        try:
            with self.assertRaises(collector.CollectionError):
                collector.collect(str(self.fixture.broker), str(self.fixture.results), descriptor)
        finally:
            os.close(descriptor)
        raw = collector.canonical_bytes(authority(spec)).replace(
            b'"schemaVersion":1', b'"schemaVersion":1,"schemaVersion":1',
        )
        descriptor = sealed(raw=raw)
        try:
            with self.assertRaises(collector.CollectionError):
                collector.collect(str(self.fixture.broker), str(self.fixture.results), descriptor)
        finally:
            os.close(descriptor)
        descriptor = sealed(raw=(json.dumps(authority(spec), indent=2) + "\n").encode("ascii"))
        try:
            with self.assertRaises(collector.CollectionError):
                collector.collect(str(self.fixture.broker), str(self.fixture.results), descriptor)
        finally:
            os.close(descriptor)
        for invalid in (1.0, True, 2):
            with self.subTest(schema=invalid):
                changed = dict(authority(spec))
                changed["schemaVersion"] = invalid
                self.assert_rejected(spec, changed)

    def test_result_directory_must_be_existing_canonical_private_clean_and_unaliased(self) -> None:
        spec = model.CAPTURE_SPECS[4]
        self.fixture.set_result(spec)
        os.chmod(self.fixture.results, 0o750)
        self.assert_rejected(spec)
        os.chmod(self.fixture.results, 0o700)
        (self.fixture.results / "unexpected-private-file").write_text("secret", encoding="ascii")
        self.assert_rejected(spec)
        (self.fixture.results / "unexpected-private-file").unlink()

        alias = self.fixture.root / "result-alias"
        alias.symlink_to(self.fixture.results, target_is_directory=True)
        descriptor = sealed(authority(spec))
        try:
            with self.assertRaises(collector.CollectionError):
                collector.collect(str(self.fixture.broker), str(alias), descriptor)
        finally:
            os.close(descriptor)
        missing = self.fixture.root / "missing-results"
        descriptor = sealed(authority(spec))
        try:
            with self.assertRaises(collector.CollectionError):
                collector.collect(str(self.fixture.broker), str(missing), descriptor)
        finally:
            os.close(descriptor)

    def test_source_and_result_directory_must_belong_to_the_current_uid(self) -> None:
        spec = model.CAPTURE_SPECS[4]
        self.fixture.set_result(spec)
        descriptor = sealed(authority(spec))
        try:
            with mock.patch.object(collector.os, "getuid", return_value=os.getuid() + 1):
                with self.assertRaises(collector.CollectionError):
                    collector.collect(str(self.fixture.broker), str(self.fixture.results), descriptor)
        finally:
            os.close(descriptor)

        raw = self.fixture.broker.read_bytes()
        descriptor = sealed(authority(spec))
        try:
            with (
                mock.patch.object(collector, "read_result_path", return_value=raw),
                mock.patch.object(collector.os, "getuid", return_value=os.getuid() + 1),
            ):
                with self.assertRaises(collector.CollectionError):
                    collector.collect(str(self.fixture.broker), str(self.fixture.results), descriptor)
        finally:
            os.close(descriptor)

    def test_cli_emits_only_fixed_public_result_and_failure_records(self) -> None:
        spec = model.CAPTURE_SPECS[5]
        self.fixture.set_result(spec)
        descriptor = sealed(authority(spec))
        try:
            result = subprocess.run(
                [
                    sys.executable, str(SCRIPT),
                    "--broker-result", str(self.fixture.broker),
                    "--result-directory", str(self.fixture.results),
                    "--expected-authority-fd", str(descriptor),
                ],
                pass_fds=(descriptor,), capture_output=True, timeout=10, check=False,
            )
        finally:
            os.close(descriptor)
        self.assertEqual(0, result.returncode)
        self.assertEqual(b"", result.stderr)
        value = json.loads(result.stdout)
        self.assertEqual("c3-cancelled-rolled-back.result.json", value["collected"])
        self.assertNotIn(str(self.fixture.root).encode(), result.stdout)
        self.assertNotIn(b"pid", result.stdout.lower())
        failed = subprocess.run(
            [sys.executable, str(SCRIPT), "--broker-result", "/private/Blossom"],
            capture_output=True, timeout=10, check=False,
        )
        self.assertEqual(1, failed.returncode)
        self.assertEqual(FAILURE, failed.stdout)
        self.assertEqual(b"", failed.stderr)
        self.assertNotIn(b"Blossom", failed.stdout)

    def test_fsync_failure_is_rejected_and_existing_directory_entries_are_not_removed(self) -> None:
        spec = model.CAPTURE_SPECS[6]
        self.fixture.set_result(spec)
        descriptor = sealed(authority(spec))
        real_fsync = collector.os.fsync
        calls = 0

        def fail_directory_sync(fd):
            nonlocal calls
            calls += 1
            if calls == 2:
                raise OSError("synthetic")
            return real_fsync(fd)

        try:
            with mock.patch.object(collector.os, "fsync", side_effect=fail_directory_sync):
                with self.assertRaises(collector.CollectionError):
                    collector.collect(str(self.fixture.broker), str(self.fixture.results), descriptor)
        finally:
            os.close(descriptor)
        expected = self.fixture.results / f"{spec.output_basename}.result.json"
        self.assertTrue(expected.is_file())


if __name__ == "__main__":
    unittest.main(verbosity=2)
