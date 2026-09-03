#!/usr/bin/env python3
"""Fixture-free tests for the external Linux GUI hard-state supervisor."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
from pathlib import Path
from contextlib import ExitStack
import socket
import stat
import signal
import subprocess
import sys
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest
from unittest import mock
import zipfile


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
SUPERVISOR = REPOSITORY_ROOT / "build/scripts/qualify-linux-gui-hard-states.py"
VALIDATOR_MARKER = ".smapi-linux-gui-hard-state-disposable-v1.json"
VERSION = "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.3"
TAG = "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3"
ALLOWED_TEMP_PARENT = Path(f"/run/user/{os.geteuid()}")
if not ALLOWED_TEMP_PARENT.is_dir() or not os.access(ALLOWED_TEMP_PARENT, os.W_OK | os.X_OK):
    ALLOWED_TEMP_PARENT = Path("/dev/shm")


def load_supervisor():
    spec = importlib.util.spec_from_file_location("hard_state_supervisor", SUPERVISOR)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


class Fixture:
    def __init__(self, base: Path, scenario: str = "C3"):
        self.base = base
        self.root = base / "isolated-qualification-root"
        self.root.mkdir(mode=0o700)
        root_stat = self.root.stat()
        marker = {
            "purpose": "smapi-linux-gui-hard-state-disposable-root",
            "root_device": root_stat.st_dev,
            "root_inode": root_stat.st_ino,
            "schema_version": 2,
        }
        marker_path = self.root / VALIDATOR_MARKER
        marker_path.write_text(json.dumps(marker, sort_keys=True) + "\n", encoding="utf-8")
        os.chmod(marker_path, 0o600)
        self.package = base / f"SMAPI-{VERSION}-linux-x64-installer.zip"
        with zipfile.ZipFile(self.package, "w") as archive:
            archive.writestr(f"SMAPI {VERSION} Linux installer/README.txt", "synthetic")
        os.chmod(self.package, 0o600)
        self.game_marker = base / "Stardew Valley.dll"
        self.game_marker.write_bytes(b"MZsynthetic redistribution-safe marker")
        os.chmod(self.game_marker, 0o600)
        self.output = self.root / "hard-state-output-00000001"
        self.contract = {
            "schema_version": 2,
            "scenario": scenario,
            "release": {
                "version": VERSION,
                "tag": TAG,
                "url": f"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/{TAG}",
                "expected_commit": "1" * 40,
                "expected_tree": "2" * 40,
            },
            "package": {
                "path": str(self.package),
                "size_bytes": self.package.stat().st_size,
                "sha256": hashlib.sha256(self.package.read_bytes()).hexdigest(),
            },
            "game_marker": {
                "path": str(self.game_marker),
                "size_bytes": self.game_marker.stat().st_size,
                "sha256": hashlib.sha256(self.game_marker.read_bytes()).hexdigest(),
            },
            "binaries": {"apphost_sha256": "3" * 64, "backend_sha256": "4" * 64},
            "capture": {
                "policy": "exact-window-v1",
                "environment_profile": "ubuntu-24.04-gnome-xwayland",
            },
            "isolation": {
                "disposable_root": str(self.root),
                "root_device": root_stat.st_dev,
                "root_inode": root_stat.st_ino,
                "disposable_vm": True,
                "live_roots_mounted": False,
                "installer_runs_as_root": False,
                "privileged_setup_confined_to_vm": True,
                "allow_privileged_fault_setup": scenario.startswith("E2-"),
            },
            "timeouts_seconds": {"startup": 5, "operation": 10, "settlement": 5, "cleanup": 5, "total": 25},
            "resource_limits": {"output_bytes": 1024 * 1024 * 1024},
        }
        self.contract_path = base / "contract.json"
        self.write()

    def write(self):
        self.contract_path.write_text(json.dumps(self.contract, sort_keys=True) + "\n", encoding="utf-8")
        os.chmod(self.contract_path, 0o600)


def invoke(fixture: Fixture, mode: str = "--admission-only", extra: list[str] | None = None) -> subprocess.CompletedProcess[str]:
    command = [sys.executable, str(SUPERVISOR), "--contract", str(fixture.contract_path), "--output", str(fixture.output), mode]
    if extra:
        command.extend(extra)
    return subprocess.run(command, capture_output=True, text=True, timeout=10, check=False)


def parsed(result: subprocess.CompletedProcess[str]) -> dict:
    if result.stderr:
        raise AssertionError("supervisor emitted stderr")
    lines = result.stdout.splitlines()
    if len(lines) != 1:
        raise AssertionError("supervisor did not emit one JSON line")
    return json.loads(lines[0])


class SupervisorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.module = load_supervisor()

    def temporary(self):
        return tempfile.TemporaryDirectory(prefix="smapi-supervisor-test-", dir=ALLOWED_TEMP_PARENT)

    def test_requires_one_explicit_mode_and_closed_arguments(self):
        with self.temporary() as name:
            fixture = Fixture(Path(name))
            for arguments in (
                ["--contract", str(fixture.contract_path), "--output", str(fixture.output)],
                ["--contract", str(fixture.contract_path), "--output", str(fixture.output), "--execute", "--admission-only"],
                ["--contract", str(fixture.contract_path), "--output", str(fixture.output), "--admission-only", "--unknown"],
            ):
                result = subprocess.run([sys.executable, str(SUPERVISOR), *arguments], capture_output=True, text=True, timeout=5)
                self.assertEqual(result.returncode, 2)
                self.assertEqual(parsed(result)["code"], "usage")
                self.assertFalse(fixture.output.exists())

    def test_admission_composes_validator_for_every_scenario_without_leaking_paths_or_hashes(self):
        for scenario in ("E2-permission", "E2-read-only", "E2-disk-full", "E2-cross-device", "C2", "C3", "E5", "E6"):
            with self.subTest(scenario=scenario), self.temporary() as name:
                fixture = Fixture(Path(name), scenario)
                result = invoke(fixture)
                self.assertEqual(result.returncode, 0, result.stdout)
                self.assertEqual(parsed(result), {
                    "kind": "linux-gui-hard-state-qualification",
                    "ok": True,
                    "scenario": scenario,
                    "schemaVersion": 2,
                    "status": "admitted",
                })
                self.assertNotIn(str(fixture.base), result.stdout)
                self.assertNotIn(fixture.contract["package"]["sha256"], result.stdout)
                self.assertEqual(stat.S_IMODE(fixture.output.stat().st_mode), 0o700)

    def test_all_scenarios_have_exact_terminal_routes_and_barrier_scope(self):
        expected = {
            "E2-permission": ("e2-permission", None),
            "E2-read-only": ("e2-read-only", None),
            "E2-disk-full": ("e2-disk-full", None),
            "E2-cross-device": ("e2-cross-device", None),
            "C2": ("c3-terminal", None),
            "C3": ("c3-terminal", None),
            "E5": ("e5-backend-loss", None),
            "E6": ("e5-backend-loss", "e6-automatic-recovery"),
        }
        for scenario, routes in expected.items():
            with self.subTest(scenario=scenario):
                self.assertEqual(self.module.qualification_routes(scenario), routes)
                initial = self.module.AT_SPI_ROUTES[routes[0]]
                self.assertEqual(initial[:7], self.module.BASE_LOCAL_ROUTE)
                if scenario in ("C2", "C3"):
                    self.assertEqual(initial[-3:], ("execution.cancel", "state.c2", "terminal.c3"))
                if scenario in ("E5", "E6"):
                    self.assertEqual(initial[-1], "state.e5")
                if routes[1] is not None:
                    self.assertEqual(
                        self.module.AT_SPI_ROUTES[routes[1]],
                        self.module.BASE_LOCAL_ROUTE + ("terminal.e6",),
                    )
                self.assertEqual(scenario in self.module.BARRIER_SCENARIOS, scenario in ("C2", "C3", "E5", "E6"))

    def test_execute_case_drives_exact_routes_terminal_settlement_and_restart_for_all_scenarios(self):
        expected_routes = {
            "E2-permission": ("e2-permission",),
            "E2-read-only": ("e2-read-only",),
            "E2-disk-full": ("e2-disk-full",),
            "E2-cross-device": ("e2-cross-device",),
            "C2": ("c3-terminal",),
            "C3": ("c3-terminal",),
            "E5": ("e5-backend-loss",),
            "E6": ("e5-backend-loss", "e6-automatic-recovery"),
        }

        for scenario, routes in expected_routes.items():
            with self.subTest(scenario=scenario), self.temporary() as name:
                base = Path(name)
                output = base / "hard-state-output"
                output.mkdir(mode=0o700)
                package = base / "candidate.zip"
                package.write_bytes(b"synthetic candidate")
                os.chmod(package, 0o600)
                contract = {
                    "scenario": scenario,
                    "package": {"path": str(package), "sha256": "1" * 64},
                    "release": {"version": VERSION},
                    "binaries": {"apphost_sha256": "2" * 64, "backend_sha256": "3" * 64},
                    "capture": {
                        "policy": "exact-window-v1",
                        "environment_profile": "ubuntu-24.04-gnome-xwayland",
                    },
                    "game_marker": {"path": str(base / "marker"), "size_bytes": 32, "sha256": "4" * 64},
                    "isolation": {"disposable_root": str(base)},
                    "timeouts_seconds": {"startup": 5, "operation": 10, "settlement": 5, "cleanup": 5, "total": 25},
                    "resource_limits": {"output_bytes": 1024 * 1024 * 1024},
                }
                sessions = []
                boundaries = []
                barriers = []
                next_pid = iter(range(4101, 4120))

                class FakeLauncher:
                    def __init__(self):
                        self.pid = next(next_pid)

                class FakeBoundary:
                    def __init__(self, *_args, **_kwargs):
                        self.events = []
                        boundaries.append(self)

                    def seeded(self, _deadline):
                        self.events.append("seeded")

                    def arm(self, _deadline):
                        self.events.append("arm")

                    def cleanup(self, _deadline):
                        self.events.append("cleanup")

                    def close(self):
                        self.events.append("close")

                class FakeBarrier:
                    def __init__(self, *_args):
                        self.events = []
                        barriers.append(self)

                    def wait(self, _backend, _deadline):
                        self.events.append("wait")
                        return 7

                    def release(self):
                        self.events.append("release")

                    def close(self):
                        self.events.append("close")

                class FakeAtspi:
                    def __init__(self, route, _gui, _hash, _control, _output, _environment, suffix, _deadline):
                        self.route = route
                        self.suffix = suffix
                        self.milestones = []
                        self.completed = False
                        sessions.append(self)

                    def advance(self, milestone, *_args):
                        self.milestones.append(milestone)

                    def complete(self, _deadline):
                        self.completed = True

                    def close(self, *_args, **_kwargs):
                        pass

                def fake_hash(path, *_args, **_kwargs):
                    if path == package:
                        digest = "1" * 64
                    elif path.name == "SMAPI.Installer.Gui":
                        digest = "2" * 64
                    elif path.name == "SMAPI.Installer":
                        digest = "3" * 64
                    else:
                        digest = "4" * 64
                    return digest, SimpleNamespace(st_dev=1, st_ino=1)

                def fake_seed(_marker, _size, _digest, target_output):
                    return target_output / "game"

                def fake_identity(pid, group=None):
                    return SimpleNamespace(pid=pid, process_group=pid if group is None else group)

                def fake_descendant(_root_pid, expected_hash, process_group, _deadline):
                    pid = next(next_pid)
                    return SimpleNamespace(pid=pid, process_group=process_group, executable_sha256=expected_hash)

                inventory_value = [{"path": "unrelated-fixture-sentinel.bin", "type": "file", "sha256": "5" * 64}]
                inventory_calls = 0

                def fake_inventory(*_args, **_kwargs):
                    nonlocal inventory_calls
                    inventory_calls += 1
                    if inventory_calls == 1 or (
                        inventory_calls == 4 and scenario in ("E2-read-only", "E2-disk-full")
                    ):
                        return [], "0" * 64
                    return inventory_value, "6" * 64

                with ExitStack() as stack:
                    replacements = {
                        "hash_regular": fake_hash,
                        "proc_all_capabilities": lambda _pid: (0, 0, 0, 0),
                        "secure_extract": lambda _package, destination, version, *_args: destination / f"SMAPI {version} Linux installer",
                        "BoundarySession": FakeBoundary,
                        "seed_game": fake_seed,
                        "inventory": fake_inventory,
                        "write_private_json": lambda *_args, **_kwargs: None,
                        "compile_barrier": lambda target: target / "barrier.so",
                        "BarrierServer": FakeBarrier,
                        "minimal_environment": lambda *_args: {"PATH": "/usr/bin:/bin"},
                        "launch_gui": lambda *_args: FakeLauncher(),
                        "bind_any_process": lambda pid, group: fake_identity(pid, group),
                        "find_bound_descendant": fake_descendant,
                        "bind_exact_app_tree": lambda *_args: [],
                        "AtspiSession": FakeAtspi,
                        "private_file": lambda *_args: None,
                        "pidfd_signal": lambda *_args: None,
                        "identity_matches": lambda *_args: False,
                        "cleanup_processes": lambda *_args: True,
                        "enforce_output_bound": lambda *_args: None,
                    }
                    for attribute, replacement in replacements.items():
                        stack.enter_context(mock.patch.object(self.module, attribute, replacement))
                    result = self.module.execute_case(contract, output)

                self.assertEqual(tuple(session.route for session in sessions), routes)
                for session in sessions:
                    self.assertEqual(tuple(session.milestones), self.module.AT_SPI_ROUTES[session.route])
                    self.assertTrue(session.completed)
                self.assertEqual(boundaries[0].events, ["seeded", "arm", "cleanup"])
                self.assertEqual(bool(barriers), scenario in ("C2", "C3", "E5", "E6"))
                if scenario in ("C2", "C3"):
                    self.assertEqual(
                        tuple(sessions[0].milestones[-3:]),
                        ("execution.cancel", "state.c2", "terminal.c3"),
                    )
                    self.assertEqual(barriers[0].events[:2], ["wait", "release"])
                if scenario == "E6":
                    self.assertEqual(
                        tuple(sessions[1].milestones),
                        self.module.BASE_LOCAL_ROUTE + ("terminal.e6",),
                    )
                self.assertEqual(result["inventoryVerified"], scenario != "E5")

    def test_e2_inventory_projections_bind_each_real_boundary_view(self):
        base = [
            {"path": "Stardew Valley.dll", "type": "file", "device": 1, "inode": 10, "sha256": "1" * 64},
            {"path": "smapi-internal", "type": "directory", "device": 1, "inode": 11, "mode": 0o700},
            {"path": "unrelated-fixture-sentinel.bin", "type": "file", "device": 1, "inode": 12, "sha256": "2" * 64},
        ]
        permission_armed = [dict(value) for value in base]
        permission_armed[1].update({"mode": 0, "uid": 0})
        self.assertEqual(
            self.module.e2_terminal_digest("E2-permission", base),
            self.module.e2_terminal_digest("E2-permission", permission_armed),
        )

        disk_armed = [*base, {
            "path": "capacity.bin", "type": "file", "device": 2, "inode": 99,
            "size": 31 * 1024 * 1024, "sha256": "3" * 64,
        }]
        self.assertEqual(
            self.module.e2_terminal_digest("E2-disk-full", base),
            self.module.e2_terminal_digest("E2-disk-full", disk_armed),
        )
        self.assertEqual(
            self.module.e2_terminal_digest("E2-read-only", base),
            self.module.e2_terminal_digest("E2-read-only", [dict(value) for value in base]),
        )

        cross_mounted = [dict(value, device=20, inode=value["inode"] + 100) for value in base]
        self.assertEqual(
            self.module.e2_restored_digest("E2-cross-device", base),
            self.module.e2_restored_digest("E2-cross-device", cross_mounted),
        )
        changed = [dict(value) for value in cross_mounted]
        changed[-1]["sha256"] = "4" * 64
        self.assertNotEqual(
            self.module.e2_restored_digest("E2-cross-device", base),
            self.module.e2_restored_digest("E2-cross-device", changed),
        )

    def test_validator_rejection_is_reduced_to_fixed_admission_code(self):
        with self.temporary() as name:
            fixture = Fixture(Path(name))
            fixture.contract["package"]["sha256"] = "f" * 64
            fixture.write()
            result = invoke(fixture)
            self.assertEqual(result.returncode, 2)
            self.assertEqual(parsed(result)["code"], "admission")
            self.assertNotIn(str(fixture.base), result.stdout)
            self.assertFalse(fixture.output.exists())

    def test_secure_extraction_accepts_exact_root_and_rejects_traversal_symlink_duplicate_and_ratio(self):
        with self.temporary() as name:
            base = Path(name)
            valid = base / "valid.zip"
            root_name = f"SMAPI {VERSION} Linux installer"
            with zipfile.ZipFile(valid, "w") as archive:
                info = zipfile.ZipInfo(f"{root_name}/internal/linux/tool")
                info.external_attr = (stat.S_IFREG | 0o755) << 16
                archive.writestr(info, b"tool")
            extracted = self.module.secure_extract(valid, base / "valid-output", VERSION)
            self.assertTrue((extracted / "internal/linux/tool").is_file())
            self.assertTrue(os.access(extracted / "internal/linux/tool", os.X_OK))

            cases = {
                "traversal": [(f"{root_name}/../escape", b"x", stat.S_IFREG | 0o644)],
                "symlink": [(f"{root_name}/link", b"target", stat.S_IFLNK | 0o777)],
                "duplicate": [(f"{root_name}/same", b"a", stat.S_IFREG | 0o644), (f"{root_name}/SAME", b"b", stat.S_IFREG | 0o644)],
                "ratio": [(f"{root_name}/large", b"0" * 20000, stat.S_IFREG | 0o644)],
            }
            for index, (case, entries) in enumerate(cases.items()):
                with self.subTest(case=case):
                    archive_path = base / f"{case}.zip"
                    compression = zipfile.ZIP_DEFLATED if case == "ratio" else zipfile.ZIP_STORED
                    with zipfile.ZipFile(archive_path, "w", compression=compression) as archive:
                        for path, content, mode in entries:
                            info = zipfile.ZipInfo(path)
                            info.external_attr = mode << 16
                            info.compress_type = compression
                            archive.writestr(info, content)
                    with self.assertRaises(self.module.QualificationError):
                        self.module.secure_extract(archive_path, base / f"bad-output-{index}", VERSION)

    def test_seed_game_copies_only_the_exact_contract_bound_marker_object(self):
        with tempfile.TemporaryDirectory(prefix="hard-state-seed-", dir="/dev/shm") as name:
            base = Path(name)
            marker = base / "Stardew Valley.dll"
            content = b"MZsynthetic public managed-marker placeholder"
            marker.write_bytes(content)
            os.chmod(marker, 0o600)
            digest = hashlib.sha256(content).hexdigest()

            success = base / "success"
            wrong_digest = base / "wrong-digest"
            replaced_source = base / "replaced-source"
            for output in (success, wrong_digest, replaced_source):
                output.mkdir(mode=0o700)

            game = self.module.seed_game(marker, len(content), digest, success)
            self.assertEqual((game / "Stardew Valley.dll").read_bytes(), content)
            with self.assertRaises(self.module.QualificationError):
                self.module.seed_game(marker, len(content), "0" * 64, wrong_digest)

            real_hash = self.module.hash_regular

            def replace_after_hash(path, maximum, require_executable=False):
                result = real_hash(path, maximum, require_executable)
                os.rename(marker, base / "displaced-marker")
                marker.write_bytes(content)
                os.chmod(marker, 0o600)
                return result

            with mock.patch.object(self.module, "hash_regular", side_effect=replace_after_hash):
                with self.assertRaises(self.module.QualificationError):
                    self.module.seed_game(marker, len(content), digest, replaced_source)

    def test_inventory_is_bounded_and_rejects_links_and_unexpected_mount_identity(self):
        with self.temporary() as name:
            root = Path(name) / "inventory"
            root.mkdir(mode=0o700)
            (root / "file").write_bytes(b"safe")
            os.chmod(root / "file", 0o600)
            values, digest = self.module.inventory(root)
            self.assertEqual(len(values), 1)
            self.assertRegex(digest, r"^[0-9a-f]{64}$")
            (root / "link").symlink_to(root / "file")
            with self.assertRaises(self.module.QualificationError):
                self.module.inventory(root)

    def test_process_identity_descendant_bound_and_pidfd_cleanup_use_exact_process(self):
        sleeper = subprocess.Popen(["/bin/sleep", "30"], start_new_session=True)
        try:
            digest, _ = self.module.hash_proc_executable(sleeper.pid)
            identity = self.module.bind_process(sleeper.pid, digest, sleeper.pid)
            self.assertTrue(self.module.identity_matches(identity))
            self.module.pidfd_signal(identity, signal.SIGTERM)
            sleeper.wait(timeout=2)
            self.assertFalse(self.module.identity_matches(identity))
        finally:
            if sleeper.poll() is None:
                sleeper.kill()
                sleeper.wait()

    def test_cleanup_contains_but_rejects_an_unexpected_exact_descendant(self):
        parent = subprocess.Popen(
            [sys.executable, "-c", (
                "import subprocess,sys,time; p=subprocess.Popen(['/bin/sleep','30']); "
                "print(p.pid,flush=True); time.sleep(30)"
            )],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            start_new_session=True,
        )
        child_pid = int(parent.stdout.readline().strip()) if parent.stdout is not None else -1
        try:
            root = self.module.bind_any_process(parent.pid, parent.pid)
            self.assertFalse(self.module.cleanup_processes(parent, [root], time.monotonic() + 4))
            self.assertIsNotNone(parent.poll())
            deadline = time.monotonic() + 2
            while Path(f"/proc/{child_pid}").exists() and time.monotonic() < deadline:
                time.sleep(0.02)
            self.assertFalse(Path(f"/proc/{child_pid}").exists())
        finally:
            if parent.poll() is None:
                parent.kill()
                parent.wait()
            if parent.stdout is not None:
                parent.stdout.close()

    def test_private_barrier_server_requires_exact_peer_pid_and_fixed_message_then_releases(self):
        with self.temporary() as name:
            base = Path(name)
            control = base / "control"
            control.mkdir(mode=0o700)
            server = self.module.BarrierServer(control)
            client = subprocess.Popen(
                [sys.executable, "-c", (
                    "import os,socket,sys; s=socket.socket(socket.AF_UNIX); s.connect(sys.argv[1]); "
                    "s.sendall(f'SMAPI_HARD_STATE_BARRIER_V1 pid={os.getpid()} op=7\\n'.encode()); "
                    "sys.stdout.buffer.write(s.recv(8))"
                ), str(server.path)],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            )
            try:
                digest, metadata = self.module.hash_proc_executable(client.pid)
                group, started = self.module.proc_stat(client.pid)
                identity = self.module.ProcessIdentity(
                    client.pid, started, group,
                    metadata.st_dev, metadata.st_ino, metadata.st_size, digest,
                )
                self.assertEqual(server.wait(identity, time.monotonic() + 2), 7)
                server.release()
                stdout, stderr = client.communicate(timeout=2)
                self.assertEqual((client.returncode, stdout, stderr), (0, b"release\n", b""))
            finally:
                server.close()
                if client.poll() is None:
                    client.kill()
                    client.wait()

    def test_atspi_controller_uses_authenticated_fixed_protocol_and_operation_enum(self):
        helper_source = r'''#!/usr/bin/env python3
import argparse,hashlib,hmac,json,socket
p=argparse.ArgumentParser(); p.add_argument("--supervisor-socket"); p.add_argument("--token-file"); p.add_argument("--session-id"); p.add_argument("--trace-file"); a=p.parse_args()
token=bytes.fromhex(open(a.token_file,encoding="ascii").read().strip())
def canonical(v): return json.dumps(v,sort_keys=True,separators=(",",":"),ensure_ascii=True).encode("ascii")
def signed(v):
 r=dict(v); r["proof"]=hmac.new(token,canonical(v),hashlib.sha256).hexdigest(); return r
def send(s,v): s.sendall(canonical(signed(v))+b"\n")
def receive(s):
 data=b""
 while not data.endswith(b"\n"): data += s.recv(4096)
 value=json.loads(data); proof=value.pop("proof"); assert hmac.compare_digest(proof,hmac.new(token,canonical(value),hashlib.sha256).hexdigest()); return value
s=socket.socket(socket.AF_UNIX); s.connect(a.supervisor_socket); nonce="ab"*16
send(s,{"type":"hello","version":1,"session":a.session_id,"nonce":nonce})
admit=receive(s); assert admit["nonce"]==nonce and admit["route"]=="e2-permission"
milestones=("release.local-folder","release.continue","game.choose-folder","game.continue-valid","plan.inspect","plan.confirm","execution.run","state.e2-permission")
for sequence,milestone in enumerate(milestones):
 command=receive(s); assert command["sequence"]==sequence and command["milestone"]==milestone
 if milestone=="plan.inspect": assert command["operation"]=="install"
 if milestone.startswith("state."):
  send(s,{"type":"capture-ready","version":1,"session":a.session_id,"sequence":sequence,"milestone":milestone})
  continued=receive(s); assert continued["type"]=="continue" and continued["milestone"]==milestone
 send(s,{"type":"reached","version":1,"session":a.session_id,"sequence":sequence,"milestone":milestone})
done=receive(s); send(s,{"type":"completed","version":1,"session":a.session_id,"sequence":done["sequence"]})
f=open(a.trace_file,"x",encoding="ascii"); f.write('{"event":"synthetic-complete"}\n'); f.close(); __import__('os').chmod(a.trace_file,0o600)
'''
        with self.temporary() as name:
            base = Path(name)
            control = base / "control"
            output = base / "output"
            game = base / "game"
            package = base / "package"
            for path in (control, output, game, package):
                path.mkdir(mode=0o700)
            helper = base / "synthetic-atspi.py"
            helper.write_text(helper_source, encoding="utf-8")
            os.chmod(helper, 0o600)
            sleeper = subprocess.Popen(["/bin/sleep", "30"], start_new_session=True)
            previous = self.module.OPERATOR_HELPER
            try:
                self.module.OPERATOR_HELPER = helper
                digest, _ = self.module.hash_proc_executable(sleeper.pid)
                gui = self.module.bind_process(sleeper.pid, digest, sleeper.pid)
                session = self.module.AtspiSession(
                    "e2-permission", gui, digest, control, output,
                    {"PATH": "/usr/bin:/bin"}, "synthetic", time.monotonic() + 5,
                )
                for milestone in self.module.AT_SPI_ROUTES["e2-permission"]:
                    session.advance(milestone, time.monotonic() + 5, package, game)
                session.complete(time.monotonic() + 5)
                self.assertEqual(session.observation_count, 1)
                self.assertTrue((output / "atspi-synthetic.trace.jsonl").is_file())
                self.assertEqual(stat.S_IMODE((control / "atspi-synthetic.token").stat().st_mode), 0o600)
            finally:
                self.module.OPERATOR_HELPER = previous
                if sleeper.poll() is None:
                    sleeper.kill()
                sleeper.wait()

    def test_boundary_session_binds_same_namespace_peer_and_fixed_stages(self):
        with tempfile.TemporaryDirectory(prefix="hs-", dir="/dev/shm") as name:
            prefix = Path(name)
            root = prefix / "qualification-root"
            output = root / "hard-state-output"
            game = output / "game"
            control = output / "control"
            for path in (root, output, game, control):
                path.mkdir(mode=0o700)
            contract = {
                "scenario": "E2-permission",
                "isolation": {"disposable_root": str(root)},
                "timeouts_seconds": {"total": 25, "cleanup": 5},
            }
            observed: list[dict] = []

            def controller():
                request = root / self.module.BOUNDARY_REQUEST_NAME
                deadline = time.monotonic() + 3
                while not request.exists() and time.monotonic() < deadline:
                    time.sleep(0.01)
                value = json.loads(request.read_text(encoding="utf-8"))
                observed.append(value)
                relative = value["supervisor_socket"]["relative_path"]
                client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                client.connect(str(prefix / relative))
                try:
                    for expected, response in (
                        (None, b'{"ok":true,"status":"prepared"}\n'),
                        (b"seeded\n", b'{"ok":true,"status":"ready"}\n'),
                        (b"arm\n", b'{"ok":true,"status":"armed"}\n'),
                        (b"cleanup\n", b'{"ok":true,"status":"cleaned"}\n'),
                    ):
                        if expected is not None:
                            self.assertEqual(client.recv(64), expected)
                        client.sendall(response)
                finally:
                    client.close()

            thread = threading.Thread(target=controller)
            thread.start()
            session = self.module.BoundarySession(
                contract, output, game, control, time.monotonic() + 3,
                _test_controller_uid=os.geteuid(),
            )
            session.seeded(time.monotonic() + 3)
            session.arm(time.monotonic() + 3)
            session.cleanup(time.monotonic() + 3)
            thread.join(timeout=3)
            self.assertFalse(thread.is_alive())
            self.assertEqual(observed[0]["supervisor"]["pid"], os.getpid())
            namespace = Path(f"/proc/{os.getpid()}/ns/mnt").stat()
            self.assertEqual(
                (observed[0]["supervisor"]["mount_namespace_device"], observed[0]["supervisor"]["mount_namespace_inode"]),
                (namespace.st_dev, namespace.st_ino),
            )
            self.assertFalse(session.path.exists())

    def test_broker_channel_accepts_only_exact_bound_controller_identity(self):
        sleeper = subprocess.Popen(["/bin/sleep", "30"])
        try:
            supervisor_module = self.module
            _group, started = self.module.proc_stat(sleeper.pid)
            helper_hash = "7" * 64
            prefix = Path("/vm/prefix")
            socket_relative = "run-00000001/output-name/control/boundary.sock"
            request_inode = 4242
            request_fd = 55
            arguments = [
                sys.executable, str(self.module.CONTROLLER_HELPER),
                "--allowed-vm-prefix", str(prefix),
                "--request-fd", str(request_fd),
                "--request-source-inode", str(request_inode),
                "--supervisor-socket", socket_relative,
            ]
            expected_command_line = b"\0".join(os.fsencode(value) for value in arguments) + b"\0"

            class FakeSocket:
                family = socket.AF_UNIX
                type = socket.SOCK_STREAM

                def __init__(self, start_time):
                    self.payload = (json.dumps({
                        "controller_pid": sleeper.pid,
                        "controller_request_fd": request_fd,
                        "controller_script_sha256": helper_hash,
                        "controller_start_time": start_time,
                        "request_source_inode": request_inode,
                    }, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")

                def getsockopt(self, *_args):
                    return supervisor_module.struct.pack("3i", 9001, 0, 0)

                def settimeout(self, _timeout):
                    pass

                def recv(self, _count):
                    value, self.payload = self.payload, b""
                    return value

                def close(self):
                    pass

            metadata = SimpleNamespace(st_mode=stat.S_IFSOCK)
            executable = SimpleNamespace(st_dev=1, st_ino=2, st_size=4096)

            def make_channel(start_time):
                fake = FakeSocket(start_time)
                patches = (
                    mock.patch.object(self.module.os, "fstat", return_value=metadata),
                    mock.patch.object(self.module.os, "getppid", return_value=9001),
                    mock.patch.object(self.module.socket, "socket", return_value=fake),
                    mock.patch.object(self.module, "hash_trusted_helper", return_value=(helper_hash, executable)),
                    mock.patch.object(self.module, "hash_proc_executable", return_value=("8" * 64, executable)),
                    mock.patch.object(self.module.Path, "read_bytes", return_value=expected_command_line),
                )
                return fake, patches

            _fake, patches = make_channel(started)
            with ExitStack() as stack:
                for patch in patches:
                    stack.enter_context(patch)
                channel = self.module.BrokerChannel(77)
                identity = channel.receive(prefix, socket_relative, request_inode, time.monotonic() + 2)
                self.assertEqual((identity.pid, identity.start_time), (sleeper.pid, started))
                channel.close()

            _fake, patches = make_channel(started + 1)
            with ExitStack() as stack:
                for patch in patches:
                    stack.enter_context(patch)
                channel = self.module.BrokerChannel(77)
                with self.assertRaises(self.module.QualificationError):
                    channel.receive(prefix, socket_relative, request_inode, time.monotonic() + 2)
                channel.close()
        finally:
            sleeper.terminate()
            sleeper.wait(timeout=2)

    def test_execute_contract_requires_exact_fully_sealed_memfd(self):
        raw = b'{"schema_version":1}'
        descriptor = os.memfd_create("sealed-contract-test", os.MFD_CLOEXEC | os.MFD_ALLOW_SEALING)
        try:
            os.write(descriptor, raw)
            with self.assertRaises(self.module.QualificationError):
                self.module.read_sealed_bytes(descriptor, 1024)
            self.module.fcntl.fcntl(
                descriptor,
                self.module.fcntl.F_ADD_SEALS,
                self.module.REQUIRED_MEMFD_SEALS,
            )
            self.assertEqual(self.module.read_sealed_bytes(descriptor, 1024), raw)
            with self.assertRaises(OSError):
                os.write(descriptor, b"x")
        finally:
            os.close(descriptor)
        with self.assertRaises(self.module.QualificationError):
            self.module.read_sealed_bytes(1024, 1024)

    def test_execute_failure_is_fixed_and_does_not_leak_private_input(self):
        with self.temporary() as name:
            fixture = Fixture(Path(name))
            result = invoke(fixture, "--execute")
            self.assertEqual(result.returncode, 2)
            self.assertIn(parsed(result)["code"], self.module.FAILURE_CODES)
            self.assertNotIn(str(fixture.base), result.stdout)
            self.assertNotIn(fixture.contract["package"]["sha256"], result.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
