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


SCRIPT = Path(__file__).with_name("arm-linux-gui-hard-state-boundary.py")
SPEC = importlib.util.spec_from_file_location("hard_state_boundary", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


def identity(path: str, device: int, inode: int) -> dict[str, object]:
    return {"relative_path": path, "device": device, "inode": inode}


def request(scenario: str = "C3") -> dict[str, object]:
    return {
        "schema_version": 1,
        "scenario": scenario,
        "run_uid": 1000,
        "root": identity("run-00000001", 10, 20),
        "output": identity("run-00000001/output", 10, 21),
        "game": identity("run-00000001/output/game", 10, 22),
        "timeouts_seconds": {"hold": 300, "cleanup": 60},
    }


class BoundaryControllerTests(unittest.TestCase):
    def test_cli_parser_accepts_only_fixed_arguments(self) -> None:
        parsed = MODULE.parse_cli([
            "--allowed-vm-prefix", "/var/lib/smapi-hard-state-vm",
            "--request", "request-00000001.json",
            "--ack-fd", "3",
        ])
        self.assertEqual(parsed, (Path("/var/lib/smapi-hard-state-vm"), "request-00000001.json", 3))
        for arguments in (
            [],
            ["--allowed-vm-prefix", "/var/lib/x", "--request", "../request.json", "--ack-fd", "3"],
            ["--allowed-vm-prefix", "/var/lib/x", "--request", "request.json", "--ack-fd", "03"],
            ["--allowed-vm-prefix", "/var/lib/x", "--command", "mount /", "--ack-fd", "3"],
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
                self.assertEqual(value.game.relative_path, "run-00000001/output/game")

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
        for raw in cases:
            with self.subTest(raw=raw[:32]):
                with self.assertRaises(MODULE.BoundaryError):
                    MODULE.parse_request(raw)

    def test_relative_layout_rejects_absolute_traversal_and_wrong_topology(self) -> None:
        invalid = (
            ("/run", "/run/output", "/run/output/game"),
            ("run/../live", "run/../live/output", "run/../live/output/game"),
            ("run", "run/other", "run/other/game"),
            ("run", "run/output", "run/output/other"),
            ("run;mount", "run;mount/output", "run;mount/output/game"),
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

    def test_request_file_identity_requires_private_single_link_nonroot_regular_file(self) -> None:
        valid = types.SimpleNamespace(
            st_mode=stat.S_IFREG | 0o600,
            st_uid=1000,
            st_nlink=1,
            st_size=128,
        )
        MODULE.validate_request_file_identity(valid)
        for change in (
            {"st_mode": stat.S_IFLNK | 0o600},
            {"st_mode": stat.S_IFREG | 0o640},
            {"st_uid": 0},
            {"st_nlink": 2},
            {"st_size": MODULE.MAX_REQUEST_BYTES + 1},
        ):
            with self.subTest(change=change):
                metadata = types.SimpleNamespace(**{**vars(valid), **change})
                with self.assertRaises(MODULE.BoundaryError):
                    MODULE.validate_request_file_identity(metadata)

    def test_no_fault_scenarios_have_empty_plans(self) -> None:
        for scenario in sorted(MODULE.NO_FAULT_SCENARIOS):
            self.assertEqual(MODULE.build_command_plan(scenario, "/vm/run/output/game"), ())

    def test_no_fault_production_arm_does_not_create_or_execute_a_fault(self) -> None:
        for scenario in sorted(MODULE.NO_FAULT_SCENARIOS):
            with self.subTest(scenario=scenario):
                parsed = MODULE.parse_request(json.dumps(request(scenario)).encode("ascii"))
                controller = MODULE.Controller(-1, Path("/vm"), parsed, 42, -1)
                states: list[bool] = []
                controller.write_state = states.append
                controller.create_boundary = lambda: self.fail("no-fault scenario created a boundary")
                controller.run = lambda *_args, **_kwargs: self.fail("no-fault scenario executed a command")
                controller.arm()
                self.assertEqual(states, [False])
                self.assertEqual(controller.boundary_fd, -1)

    def test_e2_plans_are_closed_bounded_and_below_game(self) -> None:
        game = "/vm/run/output/game"
        for scenario in sorted(MODULE.SCENARIOS - MODULE.NO_FAULT_SCENARIOS):
            with self.subTest(scenario=scenario):
                plan = MODULE.build_command_plan(scenario, game)
                if scenario == "E2-disk-full":
                    allocate = next(step for step in plan if step.action == "allocate")
                    plan += MODULE.disk_full_dynamic_steps(allocate.arguments[1], f"{game}/.smapi-hard-state-e2/disk-full-target")
                self.assertLessEqual(sum(step.mount_target is not None for step in plan), MODULE.MAX_MOUNTS)
                for step in plan:
                    self.assertIn(step.action, {"mkdir", "chmod", "allocate", "exec", "exec-capture-loop", "fill"})
                    self.assertNotIn("sh", step.arguments[:1])
                    for argument in step.arguments:
                        if argument.startswith(game):
                            self.assertTrue(argument.startswith(game + "/.smapi-hard-state-e2/"))
                for step in plan:
                    if step.action.startswith("exec"):
                        self.assertIn(step.arguments[0], {MODULE.MOUNT, MODULE.UMOUNT, MODULE.LOSETUP, MODULE.MKFS_EXT4})
                        self.assertNotIn("-l", step.arguments)
                        self.assertNotIn("--lazy", step.arguments)
                        self.assertNotIn("-f", step.arguments if step.arguments[0] == MODULE.UMOUNT else ())
                        self.assertNotIn("--force", step.arguments)
        permission = MODULE.build_command_plan("E2-permission", game)
        self.assertIn(MODULE.PlanStep("chmod", ("000", f"{game}/.smapi-hard-state-e2/permission")), permission)
        read_only = MODULE.build_command_plan("E2-read-only", game)
        self.assertTrue(any("remount,bind,ro" in step.arguments for step in read_only))
        cross_device = MODULE.build_command_plan("E2-cross-device", game)
        self.assertTrue(any("tmpfs" in step.arguments for step in cross_device))
        disk_full = MODULE.build_command_plan("E2-disk-full", game)
        self.assertTrue(any(
            step.arguments[0] == str(32 * 1024 * 1024)
            for step in disk_full
            if step.action == "allocate"
        ))
        for scenario, candidate_game in (("E2-shell", game), ("E2-permission", "relative/game")):
            with self.subTest(scenario=scenario, game=candidate_game):
                with self.assertRaises(MODULE.BoundaryError):
                    MODULE.build_command_plan(scenario, candidate_game)
        with self.assertRaises(MODULE.BoundaryError):
            MODULE.disk_full_dynamic_steps("/vm/image", "/vm/target", "/dev/loop0;id")

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

    def test_private_socket_validation_binds_peer_uid_and_fixed_protocol(self) -> None:
        first, second = socket.socketpair(socket.AF_UNIX, socket.SOCK_STREAM)
        try:
            validated = MODULE.validate_ack_socket(first.fileno(), os.geteuid())
            try:
                MODULE.send_ack(validated, MODULE.ACK_ARMED)
                self.assertEqual(second.recv(128), b'{"ok":true,"status":"armed"}\n')
            finally:
                validated.close()
            with self.assertRaises(MODULE.BoundaryError):
                MODULE.validate_ack_socket(first.fileno(), os.geteuid() + 1)
        finally:
            first.close()
            second.close()

    def test_ack_protocol_is_fixed_sanitized_json(self) -> None:
        self.assertEqual(
            {MODULE.ACK_ARMED, MODULE.ACK_CLEANED, MODULE.ACK_REJECTED},
            {
                b'{"ok":true,"status":"armed"}\n',
                b'{"ok":true,"status":"cleaned"}\n',
                b'{"ok":false,"status":"rejected"}\n',
            },
        )

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
