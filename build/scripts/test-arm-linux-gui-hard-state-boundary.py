#!/usr/bin/env python3
"""Safe unprivileged tests for the hard-state root boundary controller."""

from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import socket
import stat
import subprocess
import sys
import tempfile
import types
import unittest
from unittest import mock


SCRIPT = Path(__file__).with_name("arm-linux-gui-hard-state-boundary.py")
SPEC = importlib.util.spec_from_file_location("hard_state_boundary", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


def identity(path: str, device: int, inode: int) -> dict[str, object]:
    return {"relative_path": path, "device": device, "inode": inode}


def request(scenario: str = "C3", uid: int = 1000, socket_device: int = 10, socket_inode: int = 23) -> dict[str, object]:
    output = "run-00000001/hard-state-output-0001"
    namespace = os.stat("/proc/self/ns/mnt")
    return {
        "schema_version": 1,
        "scenario": scenario,
        "run_uid": uid,
        "root": identity("run-00000001", 10, 20),
        "output": identity(output, 10, 21),
        "game": identity(f"{output}/game", 10, 22),
        "supervisor": {
            "pid": os.getpid(),
            "start_time": MODULE.process_start_time(os.getpid()),
            "mount_namespace_device": namespace.st_dev,
            "mount_namespace_inode": namespace.st_ino,
        },
        "supervisor_socket": identity(f"{output}/control/boundary-0001.sock", socket_device, socket_inode),
        "timeouts_seconds": {"hold": 300, "cleanup": 60},
    }


class BoundaryControllerTests(unittest.TestCase):
    def test_cli_parser_accepts_only_fixed_arguments(self) -> None:
        parsed = MODULE.parse_cli([
            "--allowed-vm-prefix", "/var/lib/smapi-hard-state-vm",
            "--request-fd", "9",
            "--request-source-inode", "12345",
            "--supervisor-socket", "run-00000001/hard-state-output-0001/control/boundary-0001.sock",
        ])
        self.assertEqual(parsed, (
            Path("/var/lib/smapi-hard-state-vm"),
            9,
            12345,
            ("run-00000001", "hard-state-output-0001", "control", "boundary-0001.sock"),
        ))
        for arguments in (
            [],
            ["--allowed-vm-prefix", "/var/lib/x", "--request-fd", "2", "--request-source-inode", "1", "--supervisor-socket", "run/output000/control/a.sock"],
            ["--allowed-vm-prefix", "/var/lib/x", "--request-fd", "9", "--request-source-inode", "0", "--supervisor-socket", "run/output000/control/a.sock"],
            ["--allowed-vm-prefix", "/var/lib/x", "--command", "mount /", "--request-source-inode", "1", "--supervisor-socket", "run/output000/control/a.sock"],
        ):
            with self.subTest(arguments=arguments):
                with self.assertRaises(MODULE.BoundaryError):
                    MODULE.parse_cli(arguments)

    def test_request_parser_accepts_exact_scenarios_and_bounded_nonroot_identity(self) -> None:
        for scenario in sorted(MODULE.SCENARIOS):
            with self.subTest(scenario=scenario):
                value = MODULE.parse_request(json.dumps(request(scenario)).encode("ascii"))
                self.assertEqual(value.scenario, scenario)
                self.assertEqual(value.run_uid, 1000)
                self.assertEqual(value.game.relative_path, "run-00000001/hard-state-output-0001/game")

    def test_request_parser_rejects_unknown_duplicate_root_uid_and_bad_timeouts(self) -> None:
        cases: list[bytes] = []
        unknown = request()
        unknown["command"] = "mount /"
        cases.append(json.dumps(unknown).encode("ascii"))
        cases.append(b'{"schema_version":1,"schema_version":1}')
        root_uid = request()
        root_uid["run_uid"] = 0
        cases.append(json.dumps(root_uid).encode("ascii"))
        timeout = request()
        timeout["timeouts_seconds"] = {"hold": 1801, "cleanup": 60}
        cases.append(json.dumps(timeout).encode("ascii"))
        wrong_socket = request()
        wrong_socket["supervisor_socket"]["relative_path"] = "run-00000001/other-output/control/boundary.sock"
        cases.append(json.dumps(wrong_socket).encode("ascii"))
        for raw in cases:
            with self.subTest(raw=raw[:32]):
                with self.assertRaises(MODULE.BoundaryError):
                    MODULE.parse_request(raw)

    def test_relative_layout_rejects_absolute_traversal_and_wrong_topology(self) -> None:
        invalid = (
            ("/run", "/run/hard-state-output", "/run/hard-state-output/game"),
            ("run/../live", "run/../live/hard-state-output", "run/../live/hard-state-output/game"),
            ("run", "run/short", "run/short/game"),
            ("run", "run/hard-state-output", "run/hard-state-output/other"),
            ("run;mount", "run;mount/hard-state-output", "run;mount/hard-state-output/game"),
        )
        for root, output, game in invalid:
            with self.subTest(root=root, output=output, game=game):
                with self.assertRaises(MODULE.BoundaryError):
                    MODULE.validate_relative_layout(
                        MODULE.Identity(root, 1, 1), MODULE.Identity(output, 1, 2), MODULE.Identity(game, 1, 3)
                    )

    def test_directory_identity_requires_exact_nonroot_owner_mode_device_and_inode(self) -> None:
        expected = MODULE.Identity("run", 10, 20)
        valid = types.SimpleNamespace(st_mode=stat.S_IFDIR | 0o700, st_uid=1000, st_dev=10, st_ino=20)
        MODULE.validate_directory_identity(valid, expected, 1000)
        for change in (
            {"st_mode": stat.S_IFLNK | 0o700},
            {"st_mode": stat.S_IFDIR | 0o755},
            {"st_uid": 0},
            {"st_dev": 11},
            {"st_ino": 21},
        ):
            metadata = types.SimpleNamespace(**{**vars(valid), **change})
            with self.subTest(change=change):
                with self.assertRaises(MODULE.BoundaryError):
                    MODULE.validate_directory_identity(metadata, expected, 1000)

    def test_allowed_prefix_metadata_requires_root_owned_execute_only_traversal(self) -> None:
        valid = types.SimpleNamespace(st_mode=stat.S_IFDIR | 0o711, st_uid=0)
        MODULE.validate_prefix_metadata(valid)
        for change in (
            {"st_mode": stat.S_IFDIR | 0o700},
            {"st_mode": stat.S_IFDIR | 0o755},
            {"st_mode": stat.S_IFLNK | 0o711},
            {"st_uid": 1000},
        ):
            with self.subTest(change=change):
                with self.assertRaises(MODULE.BoundaryError):
                    MODULE.validate_prefix_metadata(types.SimpleNamespace(**{**vars(valid), **change}))

    def test_request_requires_exact_fully_sealed_memfd(self) -> None:
        raw = json.dumps(request()).encode("ascii")
        descriptor = os.memfd_create("sealed-request-test", os.MFD_CLOEXEC | os.MFD_ALLOW_SEALING)
        try:
            os.write(descriptor, raw)
            with self.assertRaises(MODULE.BoundaryError):
                MODULE.read_sealed_request(descriptor)
            MODULE.fcntl.fcntl(descriptor, MODULE.fcntl.F_ADD_SEALS, MODULE.REQUIRED_MEMFD_SEALS)
            parsed = MODULE.read_sealed_request(descriptor)
            self.assertEqual(parsed.scenario, "C3")
            with self.assertRaises(OSError):
                os.write(descriptor, b"x")
        finally:
            os.close(descriptor)

    def test_no_fault_scenarios_have_empty_plans(self) -> None:
        for scenario in sorted(MODULE.NO_FAULT_SCENARIOS):
            self.assertEqual(MODULE.build_command_plan(scenario, "/vm/run/output/game"), ())

    def test_no_fault_production_arm_does_not_create_or_execute_a_fault(self) -> None:
        for scenario in sorted(MODULE.NO_FAULT_SCENARIOS):
            with self.subTest(scenario=scenario):
                parsed = MODULE.parse_request(json.dumps(request(scenario)).encode("ascii"))
                controller = MODULE.Controller(-1, Path("/vm"), parsed, 42, -1, -1)
                states: list[bool] = []
                controller.write_state = states.append
                controller.create_boundary = lambda: self.fail("no-fault scenario created a boundary")
                controller.run = lambda *_args, **_kwargs: self.fail("no-fault scenario executed a command")
                controller.prepare()
                controller.arm()
                self.assertEqual(states, [False])
                self.assertEqual(controller.boundary_fd, -1)

    def test_e2_plans_are_closed_bounded_and_below_game(self) -> None:
        game = "/vm/run/hard-state-output/game"
        for scenario in sorted(MODULE.SCENARIOS - MODULE.NO_FAULT_SCENARIOS):
            with self.subTest(scenario=scenario):
                plan = MODULE.build_command_plan(scenario, game)
                self.assertLessEqual(len({step.mount_target for step in plan if step.mount_target}), MODULE.MAX_MOUNTS)
                for step in plan:
                    self.assertRegex(step.action, r"^(prepare|seeded|arm)-")
                    self.assertNotIn("sh", step.arguments[:1])
                    for argument in step.arguments:
                        if argument.startswith(game):
                            self.assertTrue(argument == game or argument.startswith(game + "/"))
                for step in plan:
                    if "exec" in step.action:
                        self.assertIn(step.arguments[0], {MODULE.MOUNT, MODULE.UMOUNT, MODULE.MKFS_EXT4})
                        self.assertNotIn("-l", step.arguments)
                        self.assertNotIn("--lazy", step.arguments)
                        self.assertNotIn("-f", step.arguments if step.arguments[0] == MODULE.UMOUNT else ())
                        self.assertNotIn("--force", step.arguments)
        permission = MODULE.build_command_plan("E2-permission", game)
        self.assertIn(MODULE.PlanStep("arm-chmod", ("000", f"{game}/smapi-internal")), permission)
        read_only = MODULE.build_command_plan("E2-read-only", game)
        self.assertEqual([step.action for step in read_only], ["prepare-exec", "arm-exec"])
        self.assertTrue(any("remount,ro,nosuid,nodev,noexec" in step.arguments for step in read_only))
        cross_device = MODULE.build_command_plan("E2-cross-device", game)
        self.assertEqual([step.action for step in cross_device], ["seeded-exec", "arm-verify-device"])
        self.assertTrue(any("tmpfs" in step.arguments for step in cross_device))
        disk_full = MODULE.build_command_plan("E2-disk-full", game)
        self.assertFalse(any("losetup" in argument for step in disk_full for argument in step.arguments))
        loop_mount = next(step for step in disk_full if step.arguments[0] == MODULE.MOUNT)
        self.assertEqual(loop_mount.arguments[1:3], ("-o", "loop,nosuid,nodev,noexec"))
        self.assertTrue(any(step.arguments[0] == MODULE.MKFS_EXT4 for step in disk_full if "exec" in step.action))
        self.assertTrue(any(
            step.arguments[0] == str(32 * 1024 * 1024)
            for step in disk_full
            if step.action == "prepare-allocate"
        ))
        self.assertEqual(disk_full[-1], MODULE.PlanStep("arm-fill", (game,)))
        for scenario, candidate_game in (("E2-shell", game), ("E2-permission", "relative/game")):
            with self.subTest(scenario=scenario, game=candidate_game):
                with self.assertRaises(MODULE.BoundaryError):
                    MODULE.build_command_plan(scenario, candidate_game)
    def test_production_read_only_disk_full_and_permission_phase_wiring(self) -> None:
        read_only = MODULE.Controller(
            -1, Path("/vm"),
            MODULE.parse_request(json.dumps(request("E2-read-only")).encode("ascii")),
            42, 10, 11,
        )
        read_commands: list[tuple[str, ...]] = []
        read_mounts: list[str] = []
        read_only.run = lambda arguments, **_kwargs: read_commands.append(arguments)
        read_only.record_mount = read_mounts.append
        read_only.chown_current_game = lambda: None
        read_only.write_state = lambda _cleaned: None
        read_only.prepare()
        read_only.arm()
        self.assertEqual(read_mounts, ["/vm/run-00000001/hard-state-output-0001/game"])
        self.assertIn("tmpfs", read_commands[0])
        self.assertIn("remount,ro,nosuid,nodev,noexec", read_commands[1])
        self.assertTrue(all("/.smapi-hard-state-e2/" not in value for command in read_commands for value in command))

        disk = MODULE.Controller(
            -1, Path("/vm"),
            MODULE.parse_request(json.dumps(request("E2-disk-full")).encode("ascii")),
            43, 10, 11,
        )
        disk_prepared: list[bool] = []
        disk_filled: list[int] = []
        disk.prepare_disk_full = lambda: disk_prepared.append(True)
        disk.chown_current_game = lambda: None
        disk.write_state = lambda _cleaned: None
        disk.prepare()
        disk.boundary_fd = 13
        disk.loop_device = "/dev/loop0"
        disk.current_game_fd = 12
        disk.fill_filesystem = disk_filled.append
        disk.arm()
        self.assertEqual(disk_prepared, [True])
        self.assertEqual(disk_filled, [12])

        permission = MODULE.Controller(
            -1, Path("/vm"),
            MODULE.parse_request(json.dumps(request("E2-permission")).encode("ascii")),
            44, 10, 11,
        )
        permission.internal_fd = 12
        permission.write_state = lambda _cleaned: None
        with mock.patch.object(MODULE.os, "fchown") as chown, mock.patch.object(MODULE.os, "fchmod") as chmod:
            permission.arm()
        chown.assert_called_once_with(12, 0, 0)
        chmod.assert_called_once_with(12, 0)

    def test_production_disk_full_loop_device_is_kernel_autocleared(self) -> None:
        with tempfile.TemporaryDirectory(dir="/dev/shm") as temporary:
            boundary = Path(temporary)
            controller = MODULE.Controller(
                -1, Path("/vm"),
                MODULE.parse_request(json.dumps(request("E2-disk-full")).encode("ascii")),
                45, 10, 11,
            )
            controller.boundary_fd = os.open(boundary, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
            controller.current_game_fd = 12
            controller.create_boundary = lambda: None
            commands: list[tuple[str, ...]] = []

            def run(arguments: tuple[str, ...], **_kwargs):
                commands.append(arguments)

            controller.run = run
            ordering: list[str] = []
            controller.capture_loop_identity = lambda: ordering.append("captured")
            controller.record_mount = lambda _target: ordering.append("mounted")
            controller.remove_empty_lost_found = lambda: None
            try:
                with mock.patch.object(MODULE.os, "posix_fallocate"), mock.patch.object(MODULE.os, "fsync"):
                    controller.prepare_disk_full()
            finally:
                os.close(controller.boundary_fd)
                controller.boundary_fd = -1

            self.assertFalse(any("losetup" in argument for command in commands for argument in command))
            self.assertEqual(commands[0][0], MODULE.MKFS_EXT4)
            loop_mount = next(command for command in commands if command[0] == MODULE.MOUNT)
            self.assertEqual(
                loop_mount[1:3],
                ("-o", "loop,nosuid,nodev,noexec"),
            )
            self.assertEqual(ordering, ["mounted", "captured"])

    def test_mounted_loop_identity_requires_exact_device_backing_and_autoclear(self) -> None:
        controller = MODULE.Controller.__new__(MODULE.Controller)
        controller.game = Path("/vm/run/output/game")
        controller.mounts = [MODULE.MountRecord(42, 1, "7:7", str(controller.game))]
        controller.loop_device = None
        controller.loop_rdev = None
        controller.loop_backing_file = None
        expected = "/vm/run/output/game/.smapi-hard-state-e2/disk-full.img"
        controller.actual_leaf = mock.Mock(return_value=expected)
        controller.read_loop_backing_file = mock.Mock(return_value=expected)
        controller.read_loop_autoclear = mock.Mock(return_value=True)
        block = types.SimpleNamespace(st_mode=stat.S_IFBLK, st_rdev=os.makedev(7, 7))
        with (
            mock.patch.object(MODULE.Path, "resolve", return_value=Path("/sys/devices/virtual/block/loop7")),
            mock.patch.object(MODULE.os, "lstat", return_value=block),
        ):
            controller.capture_loop_identity()
        self.assertEqual(controller.loop_device, "/dev/loop7")
        self.assertEqual(controller.loop_rdev, os.makedev(7, 7))
        controller.read_loop_autoclear.assert_called_once_with()

        controller.read_loop_autoclear = mock.Mock(return_value=False)
        with (
            mock.patch.object(MODULE.Path, "resolve", return_value=Path("/sys/devices/virtual/block/loop7")),
            mock.patch.object(MODULE.os, "lstat", return_value=block),
            self.assertRaises(MODULE.BoundaryError),
        ):
            controller.capture_loop_identity()

    def test_autoclear_cleanup_accepts_only_disappearance_of_the_exact_loop(self) -> None:
        controller = MODULE.Controller.__new__(MODULE.Controller)
        controller.loop_device = "/dev/loop7"
        controller.loop_rdev = os.makedev(7, 7)
        controller.loop_backing_file = "/vm/run/output/game/.smapi-hard-state-e2/disk-full.img"
        controller.remaining = lambda: 1
        controller.read_loop_backing_file = mock.Mock(side_effect=[controller.loop_backing_file, None])
        controller.read_loop_autoclear = mock.Mock(return_value=True)
        block = types.SimpleNamespace(st_mode=stat.S_IFBLK, st_rdev=os.makedev(7, 7))
        with mock.patch.object(MODULE.os, "lstat", return_value=block), mock.patch.object(MODULE.time, "sleep", return_value=None):
            controller.wait_for_loop_autoclear()
        self.assertIsNone(controller.loop_device)
        self.assertIsNone(controller.loop_rdev)
        self.assertIsNone(controller.loop_backing_file)

    def test_autoclear_cleanup_rejects_foreign_or_still_bound_loop_without_detach(self) -> None:
        for state in ("foreign", "still-bound"):
            with self.subTest(state=state):
                controller = MODULE.Controller.__new__(MODULE.Controller)
                controller.loop_device = "/dev/loop7"
                controller.loop_rdev = os.makedev(7, 7)
                controller.loop_backing_file = "/vm/run/output/game/.smapi-hard-state-e2/disk-full.img"
                if state == "foreign":
                    controller.remaining = lambda: 1
                    controller.read_loop_backing_file = mock.Mock(return_value="/unrelated.img")
                else:
                    remaining = iter((0.1, 0.1, 0.0))
                    controller.remaining = lambda: next(remaining)
                    controller.read_loop_backing_file = mock.Mock(return_value=controller.loop_backing_file)
                controller.read_loop_autoclear = mock.Mock(return_value=True)
                block = types.SimpleNamespace(st_mode=stat.S_IFBLK, st_rdev=os.makedev(7, 7))
                with (
                    mock.patch.object(MODULE.os, "lstat", return_value=block),
                    mock.patch.object(MODULE.time, "sleep", return_value=None),
                    self.assertRaises(MODULE.BoundaryError),
                ):
                    controller.wait_for_loop_autoclear()

    def test_cleanup_mount_plan_is_leaf_first_and_never_lazy_or_forced(self) -> None:
        records = (
            MODULE.MountRecord(10, 1, "0:1", "/vm/game/a"),
            MODULE.MountRecord(12, 10, "0:2", "/vm/game/a/leaf"),
            MODULE.MountRecord(11, 1, "0:3", "/vm/game/b"),
        )
        plan = MODULE.cleanup_mount_plan(records)
        self.assertEqual([step.mount_target for step in plan], ["/vm/game/a/leaf", "/vm/game/b", "/vm/game/a"])
        self.assertTrue(all(step.arguments[:2] == (MODULE.UMOUNT, "--") for step in plan))
        self.assertTrue(all("-l" not in step.arguments and "-f" not in step.arguments for step in plan))

    def test_mountinfo_parser_retains_exact_ids_for_only_admitted_targets(self) -> None:
        text = (
            "42 1 7:1 / /vm/game/a rw - tmpfs tmpfs rw\n"
            "43 42 7:2 / /vm/game/a/leaf rw - ext4 /dev/loop0 rw\n"
            "99 1 8:1 / /host rw - ext4 /dev/sda1 rw\n"
        )
        records = MODULE.parse_mountinfo(text, {"/vm/game/a", "/vm/game/a/leaf"})
        self.assertEqual([(item.mount_id, item.parent_id, item.device, item.target) for item in records], [
            (42, 1, "7:1", "/vm/game/a"), (43, 42, "7:2", "/vm/game/a/leaf")
        ])

    def test_private_listener_connection_binds_socket_inode_peer_pid_and_uid(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            control = output / "control"
            control.mkdir(mode=0o700)
            listener_path = control / "boundary-0001.sock"
            listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            listener.bind(os.fspath(listener_path))
            os.chmod(listener_path, 0o600)
            listener.listen(1)
            output_fd = os.open(output, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
            try:
                socket_stat = listener_path.lstat()
                parsed = MODULE.parse_request(json.dumps(request(
                    "E2-permission",
                    uid=os.geteuid(),
                    socket_device=socket_stat.st_dev,
                    socket_inode=socket_stat.st_ino,
                )).encode("ascii"))
                client = MODULE.connect_supervisor_socket(
                    output_fd,
                    parsed,
                    ("run-00000001", "hard-state-output-0001", "control", "boundary-0001.sock"),
                )
                server, _ = listener.accept()
                try:
                    MODULE.send_ack(client, MODULE.ACK_PREPARED)
                    self.assertEqual(server.recv(128), b'{"ok":true,"status":"prepared"}\n')
                finally:
                    client.close()
                    server.close()
                wrong = MODULE.parse_request(json.dumps(request(
                    "E2-permission",
                    uid=os.geteuid(),
                    socket_device=socket_stat.st_dev,
                    socket_inode=socket_stat.st_ino + 1,
                )).encode("ascii"))
                with self.assertRaises(MODULE.BoundaryError):
                    MODULE.connect_supervisor_socket(
                        output_fd,
                        wrong,
                        ("run-00000001", "hard-state-output-0001", "control", "boundary-0001.sock"),
                    )
                wrong_peer_value = request(
                    "E2-permission",
                    uid=os.geteuid(),
                    socket_device=socket_stat.st_dev,
                    socket_inode=socket_stat.st_ino,
                )
                wrong_peer_value["supervisor"]["pid"] = os.getpid() + 1
                wrong_peer = MODULE.parse_request(json.dumps(wrong_peer_value).encode("ascii"))
                with self.assertRaises(MODULE.BoundaryError):
                    MODULE.connect_supervisor_socket(
                        output_fd,
                        wrong_peer,
                        ("run-00000001", "hard-state-output-0001", "control", "boundary-0001.sock"),
                    )
            finally:
                os.close(output_fd)
                listener.close()

    def test_ack_protocol_is_fixed_sanitized_json(self) -> None:
        self.assertEqual(
            {MODULE.ACK_PREPARED, MODULE.ACK_READY, MODULE.ACK_ARMED, MODULE.ACK_CLEANED, MODULE.ACK_REJECTED},
            {
                b'{"ok":true,"status":"prepared"}\n',
                b'{"ok":true,"status":"ready"}\n',
                b'{"ok":true,"status":"armed"}\n',
                b'{"ok":true,"status":"cleaned"}\n',
                b'{"ok":false,"status":"rejected"}\n',
            },
        )

    def test_command_protocol_accepts_only_one_exact_bounded_line(self) -> None:
        for expected in (b"seeded\n", b"arm\n", b"cleanup\n"):
            first, second = socket.socketpair(socket.AF_UNIX, socket.SOCK_STREAM)
            try:
                second.sendall(expected)
                self.assertTrue(MODULE.receive_command(first, expected, 1))
            finally:
                first.close()
                second.close()
        first, second = socket.socketpair(socket.AF_UNIX, socket.SOCK_STREAM)
        try:
            second.sendall(b"arm-now\n")
            self.assertFalse(MODULE.receive_command(first, b"arm\n", 1))
        finally:
            first.close()
            second.close()

    @unittest.skipIf(os.geteuid() == 0, "the safe default test specifically checks nonroot refusal")
    def test_cli_rejects_nonroot_without_output_or_privileged_action(self) -> None:
        result = subprocess.run(
            [sys.executable, str(SCRIPT)], check=False, capture_output=True, timeout=5
        )
        self.assertEqual(result.returncode, 77)
        self.assertEqual(result.stdout, b"")
        self.assertEqual(result.stderr, b"")


if __name__ == "__main__":
    unittest.main(verbosity=2)
