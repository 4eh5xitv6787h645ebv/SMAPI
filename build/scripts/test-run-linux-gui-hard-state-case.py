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


def load_broker():
    spec = importlib.util.spec_from_file_location("hard_state_broker", BROKER)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


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
            "ok": False, "schemaVersion": 1, "status": "failed",
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
            request.write_text(json.dumps(value), encoding="utf-8")
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
            request.write_text(json.dumps(value), encoding="utf-8")
            os.chmod(request, 0o600)
            value["supervisor_socket"]["relative_path"] = "run-component/output-name/control/../escape.sock"
            request.write_text(json.dumps(value), encoding="utf-8")
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
            request.write_text(json.dumps({
                "supervisor_socket": {"relative_path": "run-component/output-name/control/boundary-a.sock"},
            }), encoding="utf-8")
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
        old = os.environ.get("SMAPI_TEST_PRIVATE_TOKEN")
        os.environ["SMAPI_TEST_PRIVATE_TOKEN"] = "private-value"
        try:
            result = self.module.child_environment(os.geteuid())
        finally:
            if old is None:
                os.environ.pop("SMAPI_TEST_PRIVATE_TOKEN", None)
            else:
                os.environ["SMAPI_TEST_PRIVATE_TOKEN"] = old
        self.assertNotIn("SMAPI_TEST_PRIVATE_TOKEN", result)
        self.assertEqual(result["PATH"], "/usr/bin:/bin")

    def test_result_schema_is_closed_and_failed_child_never_becomes_success(self):
        failure = {
            "code": "boundary", "kind": "linux-gui-hard-state-qualification",
            "ok": False, "schemaVersion": 1, "status": "failed",
        }
        self.assertFalse(self.module.validate_result((json.dumps(failure, separators=(",", ":")) + "\n").encode("ascii")))
        failure["details"] = "/private/leak"
        with self.assertRaises(self.module.BrokerError):
            self.module.validate_result((json.dumps(failure) + "\n").encode("ascii"))

        success = {key: False for key in self.module.PREFLIGHT_KEYS}
        success.update({
            "kind": "linux-gui-hard-state-qualification", "ok": True,
            "scenario": "C3", "schemaVersion": 1, "status": "preflighted",
            "releaseTag": "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3", "sourceCommit": "1" * 40, "sourceTree": "2" * 40,
            "publicReleaseUrl": "https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3", "packageSha256": "3" * 64,
            "guiSha256": "4" * 64, "backendSha256": "5" * 64,
            "capturePending": True, "durableClassificationPending": True,
            "publicAuthorityVerificationPending": True, "exactWindowCaptured": False,
        })
        self.assertTrue(self.module.validate_result((json.dumps(success) + "\n").encode("ascii")))
        success["unexpected"] = True
        with self.assertRaises(self.module.BrokerError):
            self.module.validate_result((json.dumps(success) + "\n").encode("ascii"))

    def test_main_propagates_valid_child_failure_without_leaking_or_returning_success(self):
        failure = (
            b'{"code":"boundary","kind":"linux-gui-hard-state-qualification",'
            b'"ok":false,"schemaVersion":1,"status":"failed"}\n'
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
            request.write_text(json.dumps({
                "supervisor_socket": {"relative_path": "run-component/output-name/control/boundary.sock"},
            }), encoding="utf-8")
            os.chmod(request, 0o600)
            failure = (
                b'{"code":"boundary","kind":"linux-gui-hard-state-qualification",'
                b'"ok":false,"schemaVersion":1,"status":"failed"}\n'
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
                mock.patch.object(self.module, "read_bootstrap", return_value=({}, b"{}", root, run_uid, run_gid, 25)),
                mock.patch.object(self.module, "fixed_file_hash", return_value="a" * 64),
                mock.patch.object(self.module, "make_namespace_private", return_value=None),
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

    def test_sealed_contract_failure_still_removes_the_new_cgroup_scope(self):
        root = Path("/safe-prefix/run-component")

        class FakeScope:
            cleaned = False

            def kill_and_remove(self, _deadline):
                self.cleaned = True

        scope = FakeScope()
        with (
            mock.patch.object(self.module.os, "geteuid", return_value=0),
            mock.patch.object(self.module.os, "getuid", return_value=0),
            mock.patch.object(self.module, "read_bootstrap", return_value=({}, b"{}", root, 1000, 1000, 25)),
            mock.patch.object(self.module, "fixed_file_hash", return_value="a" * 64),
            mock.patch.object(self.module, "make_namespace_private", return_value=None),
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
