#!/usr/bin/env python3
"""Fixture-free safety tests for the privileged Linux GUI hard-state broker."""

from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
BROKER = ROOT / "build/scripts/run-linux-gui-hard-state-case.py"
CAPTURE_MODEL = ROOT / "build/scripts/linux_gui_hard_state_capture_contract.py"


def load_broker():
    spec = importlib.util.spec_from_file_location("hard_state_broker", BROKER)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def canonical_json(value: object) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")


class BrokerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.module = load_broker()

    def test_cli_requires_exact_absolute_contract_output_and_execute(self):
        accepted = self.module.parse_arguments(["--contract", "/x/contract.json", "--output", "/x/output-name", "--execute"])
        self.assertEqual(accepted, (Path("/x/contract.json"), Path("/x/output-name")))
        for arguments in (
            [],
            ["--contract", "/x/c", "--output", "/x/o"],
            ["--contract", "relative", "--output", "/x/output-name", "--execute"],
            ["--contract", "/x/c", "--output", "/x/../output-name", "--execute"],
            ["--contract", "/x/c", "--output", "/x/output-name", "--execute", "--unknown"],
        ):
            with self.subTest(arguments=arguments), self.assertRaises(self.module.BrokerError):
                self.module.parse_arguments(arguments)

    def test_nonroot_execution_fails_with_one_fixed_private_json_line(self):
        if os.geteuid() == 0:
            self.skipTest("nonroot refusal requires a nonroot runner")
        result = subprocess.run(
            [sys.executable, str(BROKER), "--contract", "/private/example", "--output", "/private/output-name", "--execute"],
            capture_output=True, text=True, timeout=5, check=False,
        )
        self.assertEqual(result.returncode, 2)
        self.assertEqual(result.stderr, "")
        self.assertEqual(json.loads(result.stdout), {
            "code": "broker", "kind": "linux-gui-hard-state-qualification",
            "ok": False, "schemaVersion": 2, "status": "failed",
        })
        self.assertNotIn("/private", result.stdout)

    def test_root_helper_hash_rejects_user_owned_writable_and_symlink_inputs(self):
        with tempfile.TemporaryDirectory(prefix="hard-state-broker-test-", dir="/dev/shm") as name:
            base = Path(name)
            source = base / "helper.py"
            source.write_text("pass\n", encoding="ascii")
            os.chmod(source, 0o644)
            with self.assertRaises(self.module.BrokerError):
                self.module.fixed_file_hash(source, 1024)
            alias = base / "alias.py"
            alias.symlink_to(source)
            with self.assertRaises(self.module.BrokerError):
                self.module.fixed_file_hash(alias, 1024)

    def test_root_helper_hash_rejects_metadata_change_during_read(self):
        trusted = Path("/usr/bin/env")
        actual_fstat = os.fstat
        calls = 0

        def changed_after_read(descriptor):
            nonlocal calls
            calls += 1
            value = actual_fstat(descriptor)
            if calls == 2:
                fields = {
                    name: getattr(value, name)
                    for name in ("st_dev", "st_ino", "st_mode", "st_uid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
                }
                fields["st_ctime_ns"] += 1
                return SimpleNamespace(**fields)
            return value

        with mock.patch.object(self.module.os, "fstat", changed_after_read), self.assertRaises(self.module.BrokerError):
            self.module.fixed_file_hash(trusted, 4 * 1024 * 1024)

    def test_request_is_stably_read_atomically_consumed_and_sealed(self):
        with tempfile.TemporaryDirectory(prefix="hs-broker-", dir="/dev/shm") as name:
            base = Path(name)
            root = base / "run-component"
            output = root / "output-name"
            root.mkdir(mode=0o700)
            output.mkdir(mode=0o700)
            request = root / self.module.REQUEST_NAME
            value = {"supervisor_socket": {"relative_path": "run-component/output-name/control/boundary-a.sock"}}
            request.write_bytes(canonical_json(value))
            os.chmod(request, 0o600)
            raw, relative, inode = self.module.read_and_consume_request(request, root, output, os.geteuid())
            self.assertEqual(json.loads(raw), value)
            self.assertEqual(relative, "run-component/output-name/control/boundary-a.sock")
            self.assertGreater(inode, 0)
            self.assertFalse(request.exists())
            descriptor = self.module.sealed_memfd("request-test", raw, 32 * 1024)
            try:
                self.assertEqual(
                    self.module.fcntl.fcntl(descriptor, self.module.fcntl.F_GET_SEALS),
                    self.module.REQUIRED_MEMFD_SEALS,
                )
                with self.assertRaises(OSError):
                    os.write(descriptor, b"x")
            finally:
                os.close(descriptor)
            request.write_bytes(canonical_json(value))
            os.chmod(request, 0o600)
            value["supervisor_socket"]["relative_path"] = "run-component/output-name/control/../escape.sock"
            request.write_bytes(canonical_json(value))
            with self.assertRaises(self.module.BrokerError):
                self.module.read_and_consume_request(request, root, output, os.geteuid())
            self.assertFalse(request.exists())

    def test_request_name_replacement_fails_without_deleting_the_replacement(self):
        with tempfile.TemporaryDirectory(prefix="hs-broker-", dir="/dev/shm") as name:
            base = Path(name)
            root = base / "run-component"
            output = root / "output-name"
            root.mkdir(mode=0o700)
            output.mkdir(mode=0o700)
            request = root / self.module.REQUEST_NAME
            request.write_bytes(canonical_json({
                "supervisor_socket": {"relative_path": "run-component/output-name/control/boundary-a.sock"},
            }))
            os.chmod(request, 0o600)
            actual_stat = os.stat

            def replaced_stat(path, *args, **kwargs):
                value = actual_stat(path, *args, **kwargs)
                if path == self.module.REQUEST_NAME and kwargs.get("dir_fd") is not None:
                    fields = {
                        field: getattr(value, field)
                        for field in self.module.STABLE_NAMED_FIELDS
                    }
                    fields["st_ino"] += 1
                    return SimpleNamespace(**fields)
                return value

            with mock.patch.object(self.module.os, "stat", side_effect=replaced_stat):
                with self.assertRaises(self.module.BrokerError):
                    self.module.read_and_consume_request(request, root, output, os.geteuid())
            self.assertTrue(request.exists())

    def test_controller_ledgers_are_exactly_removed_and_unsafe_objects_fail_closed(self):
        with tempfile.TemporaryDirectory(prefix="hs-ledger-", dir="/dev/shm") as name:
            prefix = Path(name)
            os.chmod(prefix, 0o711)
            inode = 424242
            for suffix in ("json", "log"):
                path = prefix / f".smapi-hard-state-controller-{inode}.{suffix}"
                path.write_text("private\n", encoding="ascii")
                os.chmod(path, 0o600)
            self.module.cleanup_controller_ledgers(
                prefix, inode, _prefix_uid=os.geteuid(), _ledger_uid=os.geteuid(),
            )
            self.assertEqual(list(prefix.iterdir()), [])
            unsafe = prefix / f".smapi-hard-state-controller-{inode}.log"
            unsafe.write_text("unsafe\n", encoding="ascii")
            os.chmod(unsafe, 0o644)
            with self.assertRaises(self.module.BrokerError):
                self.module.cleanup_controller_ledgers(
                    prefix, inode, _prefix_uid=os.geteuid(), _ledger_uid=os.geteuid(),
                )
            self.assertTrue(unsafe.exists())
            unsafe.unlink()
            for kind in ("symlink", "hardlink", "oversize", "owner"):
                with self.subTest(kind=kind):
                    target = prefix / f".smapi-hard-state-controller-{inode}.log"
                    peer = prefix / "peer"
                    if kind == "symlink":
                        peer.write_text("peer", encoding="ascii")
                        target.symlink_to(peer.name)
                    else:
                        target.write_text("private", encoding="ascii")
                        os.chmod(target, 0o600)
                        if kind == "hardlink":
                            os.link(target, peer)
                        elif kind == "oversize":
                            os.truncate(target, self.module.MAX_LEDGER_BYTES + 1)
                    ledger_uid = os.geteuid() + 1 if kind == "owner" else os.geteuid()
                    with self.assertRaises(self.module.BrokerError):
                        self.module.cleanup_controller_ledgers(
                            prefix, inode, _prefix_uid=os.geteuid(), _ledger_uid=ledger_uid,
                        )
                    self.assertTrue(target.exists() or target.is_symlink())
                    target.unlink()
                    if peer.exists():
                        peer.unlink()

    def test_residual_request_cleanup_deletes_only_an_exact_private_regular_file(self):
        with tempfile.TemporaryDirectory(prefix="hs-request-cleanup-", dir="/dev/shm") as name:
            root = Path(name)
            os.chmod(root, 0o700)
            request = root / self.module.REQUEST_NAME
            request.write_text("{}\n", encoding="ascii")
            os.chmod(request, 0o600)
            self.module.cleanup_residual_request(root, os.geteuid())
            self.assertFalse(request.exists())
            for kind in ("mode", "symlink", "hardlink", "oversize", "owner"):
                with self.subTest(kind=kind):
                    peer = root / "peer"
                    if kind == "symlink":
                        peer.write_text("peer", encoding="ascii")
                        request.symlink_to(peer.name)
                    else:
                        request.write_text("{}\n", encoding="ascii")
                        os.chmod(request, 0o644 if kind == "mode" else 0o600)
                        if kind == "hardlink":
                            os.link(request, peer)
                        elif kind == "oversize":
                            os.truncate(request, self.module.MAX_REQUEST_BYTES + 1)
                    run_uid = os.geteuid() + 1 if kind == "owner" else os.geteuid()
                    with self.assertRaises(self.module.BrokerError):
                        self.module.cleanup_residual_request(root, run_uid)
                    self.assertTrue(request.exists() or request.is_symlink())
                    request.unlink()
                    if peer.exists():
                        peer.unlink()

    def test_bootstrap_rejects_non_root_owned_prefix_before_namespace_changes(self):
        with tempfile.TemporaryDirectory(prefix="hs-broker-", dir="/dev/shm") as name:
            prefix = Path(name)
            root = prefix / "run-component"
            root.mkdir(mode=0o700)
            output = root / "output-name"
            contract = prefix / "contract.json"
            contract.write_text(json.dumps({
                "isolation": {"disposable_root": str(root)},
                "timeouts_seconds": {"total": 25},
            }), encoding="utf-8")
            os.chmod(contract, 0o600)
            with self.assertRaises(self.module.BrokerError):
                self.module.read_bootstrap(contract, output)

    def test_case_root_lock_allows_exactly_one_broker_and_rechecks_identity(self):
        with tempfile.TemporaryDirectory(prefix="hs-root-lock-", dir="/dev/shm") as name:
            root = Path(name)
            os.chmod(root, 0o700)
            uid = os.geteuid()
            gid = os.getegid()
            first, identity = self.module.acquire_root_lock(root, uid, gid)
            try:
                self.assertEqual(identity, (root.stat().st_dev, root.stat().st_ino))
                with self.assertRaises(self.module.BrokerError):
                    self.module.acquire_root_lock(root, uid, gid)
            finally:
                os.close(first)
            second, repeated = self.module.acquire_root_lock(root, uid, gid)
            try:
                self.assertEqual(repeated, identity)
            finally:
                os.close(second)
            with self.assertRaises(self.module.BrokerError):
                self.module.acquire_root_lock(root, uid + 1, gid)

    def test_admitted_identity_requires_nonzero_system_primary_gid_everywhere(self):
        uid = os.geteuid()
        primary_gid = self.module.pwd.getpwuid(uid).pw_gid
        self.assertGreater(uid, 0)
        self.assertGreater(primary_gid, 0)
        self.assertEqual(self.module.admitted_primary_gid(uid, primary_gid, primary_gid), primary_gid)
        for contract_gid, root_gid in ((0, primary_gid), (primary_gid, 0), (primary_gid + 1, primary_gid)):
            with self.subTest(contract_gid=contract_gid, root_gid=root_gid), self.assertRaises(self.module.BrokerError):
                self.module.admitted_primary_gid(uid, contract_gid, root_gid)

    def test_exact_pidfd_signal_stops_only_the_bound_direct_child(self):
        sleeper = subprocess.Popen(["/bin/sleep", "30"])
        self.assertGreater(self.module.process_start_time(sleeper.pid), 0)
        self.module.signal_exact(sleeper, self.module.signal.SIGTERM)
        sleeper.wait(timeout=2)
        self.assertIsNotNone(sleeper.poll())

    def test_cgroup_scope_rejects_a_user_owned_delegated_parent(self):
        with tempfile.TemporaryDirectory(prefix="hard-state-cgroup-test-", dir="/dev/shm") as name:
            with self.assertRaises(self.module.BrokerError):
                self.module.CgroupScope(os.geteuid(), Path(name), validate_mount=False)

    def test_output_quota_facts_fail_closed_for_wrong_kernel_or_identity_evidence(self):
        block = stat.S_IFBLK | 0o600
        directory = stat.S_IFDIR | 0o700
        source = SimpleNamespace(st_mode=block, st_uid=0, st_rdev=os.makedev(7, 4))
        output = SimpleNamespace(st_mode=directory, st_uid=1000, st_gid=1001)
        values = SimpleNamespace(f_frsize=4096, f_blocks=250000)
        arguments = [
            "ext4", frozenset({"rw", "nosuid", "nodev"}), (7, 4), source,
            self.module.OUTPUT_BYTES_LIMIT, True, Path("/image"), Path("/image"),
            output, values, 1000, 1001, self.module.OUTPUT_BYTES_LIMIT,
        ]
        self.module.validate_output_mount_facts(*arguments)
        mutations = (
            (0, "tmpfs"),
            (1, frozenset({"rw", "nosuid"})),
            (1, frozenset({"rw", "nosuid", "nodev", "noexec"})),
            (2, (7, 5)),
            (3, SimpleNamespace(st_mode=stat.S_IFREG | 0o600, st_uid=0, st_rdev=os.makedev(7, 4))),
            (4, self.module.OUTPUT_BYTES_LIMIT - 1),
            (5, False),
            (6, Path("/other-image")),
            (8, SimpleNamespace(st_mode=directory, st_uid=1002, st_gid=1001)),
            (9, SimpleNamespace(f_frsize=4096, f_blocks=300000)),
        )
        for index, value in mutations:
            with self.subTest(index=index, value=value):
                changed = list(arguments)
                changed[index] = value
                with self.assertRaises(self.module.BrokerError):
                    self.module.validate_output_mount_facts(*changed)

    def test_output_quota_cleanup_unmounts_exact_target_and_unlinks_only_bound_objects(self):
        with tempfile.TemporaryDirectory(prefix="hs-quota-cleanup-", dir="/dev/shm") as name:
            root = Path(name)
            output = root / "output-name"
            output.mkdir(mode=0o700)
            retained = root / ".output-name.retained-test"
            retained.mkdir(mode=0o700)
            (retained / "result.json").write_text("{}\n", encoding="ascii")
            retained_status = retained.stat()
            image = root / ".quota.ext4"
            image.write_bytes(b"image")
            quota = self.module.OutputQuota.__new__(self.module.OutputQuota)
            quota.root = root
            quota.output = output
            quota.run_uid = os.geteuid()
            quota.run_gid = os.getegid()
            quota.limit = self.module.OUTPUT_BYTES_LIMIT
            quota.root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY)
            root_status = root.stat()
            quota.root_identity = (root_status.st_dev, root_status.st_ino)
            quota.root_restricted = False
            quota.image_fd = os.open(image, os.O_RDONLY)
            image_status = image.stat()
            quota.image_path = image
            quota.image_identity = (image_status.st_dev, image_status.st_ino, image_status.st_size)
            output_status = output.stat()
            quota.output_identity = (output_status.st_dev, output_status.st_ino)
            quota.mount_device = (7, 4)
            quota.mounted = True
            quota.marker_identity = None
            commands = []
            with (
                mock.patch.object(quota, "validate", return_value=None),
                mock.patch.object(quota, "_validate_image", return_value=None),
                mock.patch.object(quota, "_restrict_root", return_value=None),
                mock.patch.object(quota, "_restore_root", return_value=None),
                mock.patch.object(
                    quota, "_preserve",
                    return_value=(retained, (retained_status.st_dev, retained_status.st_ino)),
                ),
                mock.patch.object(self.module, "_run_root_command", side_effect=lambda command: commands.append(command)),
                mock.patch.object(self.module, "mounted_output_record", side_effect=self.module.BrokerError),
                mock.patch.object(self.module, "_loop_still_backs", return_value=False),
            ):
                quota.close(preserve=True)
            self.assertEqual(commands, [["/usr/bin/umount", str(output)]])
            self.assertTrue((output / "result.json").is_file())
            self.assertFalse(retained.exists())
            self.assertFalse(image.exists())

    def test_cgroup_cleanup_uses_exact_bound_scope_kill_and_requires_empty(self):
        with tempfile.TemporaryDirectory(prefix="hard-state-cgroup-test-", dir="/dev/shm") as name:
            base = Path(name)
            scope_path = base / "smapi-hard-state-test"
            scope_path.mkdir(mode=0o700)
            scope = self.module.CgroupScope.__new__(self.module.CgroupScope)
            scope.base_fd = os.open(base, os.O_RDONLY | os.O_DIRECTORY)
            scope.fd = os.open(scope_path, os.O_RDONLY | os.O_DIRECTORY)
            scope.name = scope_path.name
            metadata = os.fstat(scope.fd)
            scope.identity = (metadata.st_dev, metadata.st_ino)
            scope.relative = "/smapi-hard-state-test"
            writes = []
            populated = iter((True, False, False))
            with (
                mock.patch.object(scope, "_write", side_effect=lambda key, value: writes.append((key, value))),
                mock.patch.object(scope, "_populated", side_effect=lambda: next(populated)),
                mock.patch.object(scope, "validate", return_value=None),
            ):
                scope.kill_and_remove(self.module.time.monotonic() + 1)
            self.assertEqual(writes, [("cgroup.kill", b"1\n")])
            self.assertFalse(scope_path.exists())

    def test_child_environment_is_closed_and_does_not_copy_secrets(self):
        with mock.patch.dict(os.environ, {
            "SMAPI_TEST_PRIVATE_TOKEN": "private-value",
            "XDG_SESSION_TYPE": "wayland",
            "XDG_CURRENT_DESKTOP": "ubuntu:GNOME",
        }, clear=True):
            result = self.module.child_environment(os.geteuid())
        self.assertNotIn("SMAPI_TEST_PRIVATE_TOKEN", result)
        self.assertEqual(result["PATH"], "/usr/bin:/bin")
        self.assertEqual(result["XDG_SESSION_TYPE"], "wayland")
        self.assertEqual(result["XDG_CURRENT_DESKTOP"], "ubuntu:GNOME")
        for key, value in (("XDG_SESSION_TYPE", "tty"), ("XDG_CURRENT_DESKTOP", "GNOME;TOKEN=secret")):
            with self.subTest(key=key), mock.patch.dict(os.environ, {key: value}, clear=True):
                with self.assertRaises(self.module.BrokerError):
                    self.module.child_environment(os.geteuid())

    def test_result_schema_is_closed_and_failed_child_never_becomes_success(self):
        failure = {
            "code": "boundary", "kind": "linux-gui-hard-state-qualification",
            "ok": False, "schemaVersion": 2, "status": "failed",
        }
        self.assertFalse(self.module.validate_result(canonical_json(failure)))
        failure["details"] = "/private/leak"
        with self.assertRaises(self.module.BrokerError):
            self.module.validate_result(canonical_json(failure))

        success = {key: False for key in self.module.CASE_KEYS}
        success.update({
            "kind": "linux-gui-hard-state-qualification", "ok": True,
            "scenario": "C3", "schemaVersion": 2,
            "status": "captured_pending_privacy_and_public_authority",
            "releaseTag": "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3", "sourceCommit": "1" * 40, "sourceTree": "2" * 40,
            "publicReleaseUrl": "https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3", "packageSha256": "3" * 64,
            "guiSha256": "4" * 64, "backendSha256": "5" * 64,
            "evidenceId": "C3", "fault": None,
            "environmentProfile": "ubuntu-24.04-gnome-xwayland",
            "visibleState": "cancelled-and-rolled-back",
            "durableAtCapture": "rolled-back", "durableAfter": "rolled-back",
            "exactWindowCaptured": True, "atspiEvidenceRecorded": True,
            "durableClassificationVerified": True, "cleanupComplete": True,
            "packageIdentityReverified": True,
        })
        self.assertTrue(self.module.validate_result(canonical_json(success)))
        for key, invalid in (
            ("evidenceId", "C2"),
            ("durableAtCapture", "applied"),
            ("environmentProfile", "ubuntu-current"),
            ("exactWindowCaptured", False),
        ):
            with self.subTest(key=key):
                changed = dict(success)
                changed[key] = invalid
                with self.assertRaises(self.module.BrokerError):
                    self.module.validate_result(canonical_json(changed))
        success["unexpected"] = True
        with self.assertRaises(self.module.BrokerError):
            self.module.validate_result(canonical_json(success))

        for malformed in (
            canonical_json({**success, "schemaVersion": 2.0}),
            b'{"code":"boundary","code":"capture","kind":"linux-gui-hard-state-qualification","ok":false,"schemaVersion":2,"status":"failed"}\n',
            (json.dumps(failure, indent=2) + "\n").encode("ascii"),
        ):
            with self.assertRaises(self.module.BrokerError):
                self.module.validate_result(malformed)

        success.pop("unexpected")
        contract = {
            "scenario": "C3",
            "capture": {"environment_profile": "ubuntu-24.04-gnome-xwayland"},
            "release": {
                "tag": success["releaseTag"], "url": success["publicReleaseUrl"],
                "expected_commit": success["sourceCommit"], "expected_tree": success["sourceTree"],
            },
            "package": {"sha256": success["packageSha256"]},
            "binaries": {
                "apphost_sha256": success["guiSha256"],
                "backend_sha256": success["backendSha256"],
            },
        }
        self.assertTrue(self.module.validate_result(canonical_json(success), contract))
        for path, replacement in (
            (("scenario",), "C2"),
            (("capture", "environment_profile"), "ubuntu-24.04-kde-x11"),
            (("release", "expected_commit"), "9" * 40),
            (("package", "sha256"), "9" * 64),
        ):
            changed = json.loads(json.dumps(contract))
            target = changed
            for component in path[:-1]:
                target = target[component]
            target[path[-1]] = replacement
            with self.subTest(contract_path=path), self.assertRaises(self.module.BrokerError):
                self.module.validate_result(canonical_json(success), changed)

    def test_root_broker_closed_case_mapping_matches_the_nonroot_shared_capture_model(self):
        spec = importlib.util.spec_from_file_location("broker_test_capture_model", CAPTURE_MODEL)
        self.assertIsNotNone(spec)
        self.assertIsNotNone(spec.loader)
        model = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = model
        spec.loader.exec_module(model)
        expected = {
            item.scenario.value: (
                item.evidence_id.value,
                None if item.fault is None else item.fault.value,
                item.visible_state.value,
                item.durable_at_capture.value,
                item.durable_after.value,
            )
            for item in model.CAPTURE_SPECS
        }
        profiles = {item.profile_id.value for item in model.ENVIRONMENT_PROFILES}
        self.assertEqual(self.module.CASE_EXPECTED, expected)
        self.assertEqual(self.module.ENVIRONMENT_PROFILES, profiles)

    def test_main_propagates_valid_child_failure_without_leaking_or_returning_success(self):
        failure = (
            b'{"code":"boundary","kind":"linux-gui-hard-state-qualification",'
            b'"ok":false,"schemaVersion":2,"status":"failed"}\n'
        )
        writes = []

        def record_write(_descriptor, content):
            writes.append(bytes(content))
            return len(content)

        with (
            mock.patch.object(self.module, "parse_arguments", return_value=(Path("/contract"), Path("/output-name"))),
            mock.patch.object(self.module, "run_case", return_value=(failure, False)),
            mock.patch.object(self.module.os, "write", side_effect=record_write),
        ):
            status = self.module.main([])
        self.assertEqual(status, 2)
        self.assertEqual(writes, [failure])
        self.assertNotIn(b"/contract", writes[0])

    def test_run_case_reaches_socketpair_and_puts_both_children_in_one_scope_contract(self):
        with tempfile.TemporaryDirectory(prefix="hard-state-broker-run-", dir="/dev/shm") as name:
            base = Path(name)
            root = base / "run-component"
            output = root / "output-name"
            control = output / "control"
            control.mkdir(parents=True, mode=0o700)
            os.chmod(root, 0o700)
            request = root / self.module.REQUEST_NAME
            request.write_bytes(canonical_json({
                "supervisor_socket": {"relative_path": "run-component/output-name/control/boundary.sock"},
            }))
            os.chmod(request, 0o600)
            failure = (
                b'{"code":"boundary","kind":"linux-gui-hard-state-qualification",'
                b'"ok":false,"schemaVersion":2,"status":"failed"}\n'
            )
            launched = []

            class FakeScope:
                def __init__(self, _uid):
                    self.join_calls = 0
                    self.cleaned = False

                def join_current(self):
                    self.join_calls += 1

                def kill_and_remove(self, _deadline):
                    self.cleaned = True

            scope = FakeScope(1000)
            sent = []
            run_uid = os.geteuid()
            run_gid = os.getegid()

            class FakeSocket:
                def fileno(self):
                    return 88

                def close(self):
                    pass

                def sendall(self, value):
                    sent.append(bytes(value))

                def shutdown(self, _how):
                    pass

            class FakeProcess:
                def __init__(self, command, **kwargs):
                    self.command = command
                    self.kwargs = kwargs
                    self.pid = 5000 + len(launched)
                    self.returncode = 2
                    launched.append(self)

                def poll(self):
                    return self.returncode

                def communicate(self, **_kwargs):
                    return failure, b""

                def wait(self, **_kwargs):
                    return self.returncode

            with (
                mock.patch.object(self.module.os, "geteuid", return_value=0),
                mock.patch.object(self.module.os, "getuid", return_value=0),
                mock.patch.object(self.module, "read_bootstrap", return_value=({"resource_limits": {"output_bytes": self.module.OUTPUT_BYTES_LIMIT}}, b"{}", root, run_uid, run_gid, 25)),
                mock.patch.object(self.module, "fixed_file_hash", return_value="a" * 64),
                mock.patch.object(self.module, "make_namespace_private", return_value=None),
                mock.patch.object(self.module, "OutputQuota") as quota_type,
                mock.patch.object(self.module, "CgroupScope", return_value=scope),
                mock.patch.object(self.module, "child_environment", return_value={"PATH": "/usr/bin:/bin"}),
                mock.patch.object(self.module.socket, "socketpair", return_value=(FakeSocket(), FakeSocket())),
                mock.patch.object(self.module.subprocess, "Popen", side_effect=FakeProcess),
                mock.patch.object(self.module, "process_start_time", return_value=123),
                mock.patch.object(self.module, "drop_to", return_value=None),
                mock.patch.object(self.module.resource, "setrlimit", return_value=None) as set_limit,
                mock.patch.object(self.module, "cleanup_controller_ledgers", return_value=None) as ledger_cleanup,
                mock.patch.object(self.module, "cleanup_residual_request", return_value=None) as request_cleanup,
            ):
                result, succeeded = self.module.run_case(base / "contract.json", output)
                self.assertEqual((result, succeeded), (failure, False))
                self.assertEqual(len(launched), 2)
                self.assertIn("--broker-fd", launched[0].command)
                self.assertIn("--contract-fd", launched[0].command)
                self.assertNotIn("--contract", launched[0].command)
                self.assertIn("--supervisor-socket", launched[1].command)
                self.assertIn("--request-fd", launched[1].command)
                self.assertNotIn("--request", launched[1].command)
                launched[0].kwargs["preexec_fn"]()
                launched[1].kwargs["preexec_fn"]()
                self.assertEqual(scope.join_calls, 2)
                set_limit.assert_called_once_with(
                    self.module.resource.RLIMIT_FSIZE,
                    (self.module.MAX_LEDGER_BYTES, self.module.MAX_LEDGER_BYTES),
                )
                self.assertEqual(json.loads(sent[0]), {
                    "controller_pid": launched[1].pid,
                    "controller_request_fd": mock.ANY,
                    "controller_script_sha256": "a" * 64,
                    "controller_start_time": 123,
                    "request_source_inode": mock.ANY,
                })
                ledger_cleanup.assert_called_once_with(root.parent, mock.ANY)
                request_cleanup.assert_called_once_with(root, run_uid)
                self.assertTrue(scope.cleaned)
                quota_type.return_value.close.assert_called_once_with(preserve=True)

    def test_sealed_contract_failure_still_removes_the_new_cgroup_scope(self):
        with tempfile.TemporaryDirectory(prefix="hs-sealed-failure-", dir="/dev/shm") as name:
            root = Path(name)
            os.chmod(root, 0o700)
            run_uid = os.geteuid()
            run_gid = os.getegid()

            class FakeScope:
                cleaned = False

                def kill_and_remove(self, _deadline):
                    self.cleaned = True

            scope = FakeScope()
            with (
                mock.patch.object(self.module.os, "geteuid", return_value=0),
                mock.patch.object(self.module.os, "getuid", return_value=0),
                mock.patch.object(self.module, "read_bootstrap", return_value=({"resource_limits": {"output_bytes": self.module.OUTPUT_BYTES_LIMIT}}, b"{}", root, run_uid, run_gid, 25)),
                mock.patch.object(self.module, "fixed_file_hash", return_value="a" * 64),
                mock.patch.object(self.module, "make_namespace_private", return_value=None),
                mock.patch.object(self.module, "OutputQuota"),
                mock.patch.object(self.module, "CgroupScope", return_value=scope),
                mock.patch.object(self.module, "sealed_memfd", side_effect=self.module.BrokerError),
                mock.patch.object(self.module, "cleanup_residual_request", return_value=None),
                mock.patch.object(self.module, "cleanup_controller_ledgers", return_value=None),
            ):
                with self.assertRaises(self.module.BrokerError):
                    self.module.run_case(Path("/contract"), root / "output-name")
            self.assertTrue(scope.cleaned)


if __name__ == "__main__":
    unittest.main(verbosity=2)
