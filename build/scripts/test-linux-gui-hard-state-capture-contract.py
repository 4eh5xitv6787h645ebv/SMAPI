#!/usr/bin/env python3
"""Standalone tests for the closed Linux GUI hard-state capture contract."""

from __future__ import annotations

from dataclasses import FrozenInstanceError
import importlib.util
from pathlib import Path
import sys
from types import MappingProxyType
import unittest


MODULE_PATH = Path(__file__).with_name("linux_gui_hard_state_capture_contract.py")
SPEC = importlib.util.spec_from_file_location("linux_gui_hard_state_capture_contract", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
contract = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = contract
SPEC.loader.exec_module(contract)


class CaptureContractTests(unittest.TestCase):
    def test_exact_capture_map(self) -> None:
        expected = {
            "E2-permission": ("E2", "permission", "e2-permission", "state.e2-permission", "state.e2-permission", "install", "filesystem-eacces", "install-failed-unchanged", "unchanged", "unchanged", "unchanged", "e2-permission", "linux-gui-screenshot-evidence.md#evidence-e2"),
            "E2-read-only": ("E2", "read-only", "e2-read-only", "state.e2-read-only", "state.e2-read-only", "install", "filesystem-erofs", "install-failed-unchanged", "unchanged", "unchanged", "unchanged", "e2-read-only", "linux-gui-screenshot-evidence.md#evidence-e2"),
            "E2-disk-full": ("E2", "disk-full", "e2-disk-full", "state.e2-disk-full", "state.e2-disk-full", "install", "filesystem-enospc", "install-failed-unchanged", "unchanged", "unchanged", "unchanged", "e2-disk-full", "linux-gui-screenshot-evidence.md#evidence-e2"),
            "E2-cross-device": ("E2", "cross-device", "e2-cross-device", "state.e2-cross-device", "state.e2-cross-device", "install", "filesystem-exdev", "install-failed-rolled-back", "unchanged", "rolled-back", "rolled-back", "e2-cross-device", "linux-gui-screenshot-evidence.md#evidence-e2"),
            "C2": ("C2", None, "c3-terminal", "state.c2", "terminal.c3", "install", "post-applied-cancel", "cancellation-finishing-safely", "unchanged", "applied", "rolled-back", "c2-finishing-safely", "linux-gui-screenshot-evidence.md#evidence-c2"),
            "C3": ("C3", None, "c3-terminal", "terminal.c3", "terminal.c3", "install", "post-applied-cancel", "cancelled-and-rolled-back", "unchanged", "rolled-back", "rolled-back", "c3-cancelled-rolled-back", "linux-gui-screenshot-evidence.md#evidence-c3"),
            "E5": ("E5", None, "e5-backend-loss", "state.e5", "state.e5", "install", "post-applied-backend-sigkill", "backend-state-unknown-recovery-required", "unchanged", "recovery-required", "recovery-required", "e5-recovery-required", "linux-gui-screenshot-evidence.md#evidence-e5"),
            "E6": ("E6", None, "e6-automatic-recovery", "terminal.e6", "terminal.e6", "automatic-recovery", "fresh-session-automatic-recovery", "automatic-recovery-completed-fresh-inspection-required", "recovery-required", "recovery-completed", "recovery-completed", "e6-automatic-recovery", "linux-gui-screenshot-evidence.md#evidence-e6"),
        }
        actual = {}
        for spec in contract.CAPTURE_SPECS:
            actual[spec.scenario.value] = (
                spec.evidence_id.value, None if spec.fault is None else spec.fault.value,
                spec.atspi_route.value, spec.capture_milestone.value,
                spec.required_terminal_milestone.value, spec.operation.value,
                spec.boundary_trigger.value, spec.visible_state.value,
                spec.durable_before.value, spec.durable_at_capture.value,
                spec.durable_after.value, spec.output_basename, spec.docs_anchor,
            )
            self.assertEqual(contract.EXECUTION_WINDOW_TITLE, spec.window_title)
        self.assertEqual(expected, actual)

    def test_capture_uniqueness_and_derivation(self) -> None:
        specs = contract.CAPTURE_SPECS
        self.assertEqual(8, len(specs))
        self.assertEqual(len(specs), len({item.scenario for item in specs}))
        self.assertEqual(len(specs), len({item.output_basename for item in specs}))
        self.assertEqual(4, len([item for item in specs if item.evidence_id.value == "E2"]))
        self.assertEqual(
            {"permission", "read-only", "disk-full", "cross-device"},
            {item.fault.value for item in specs if item.fault is not None},
        )
        contract.validate_contract()

    def test_c2_transient_and_c3_terminal_are_distinct(self) -> None:
        c2 = contract.capture_spec("C2")
        c3 = contract.capture_spec("C3")
        self.assertEqual(c2.atspi_route, c3.atspi_route)
        self.assertEqual(c2.required_terminal_milestone, c3.required_terminal_milestone)
        self.assertNotEqual(c2.capture_milestone, c3.capture_milestone)
        self.assertEqual("applied", c2.durable_at_capture.value)
        self.assertEqual("rolled-back", c3.durable_at_capture.value)

    def test_e6_targets_fresh_second_session(self) -> None:
        e5 = contract.capture_spec("E5")
        e6 = contract.capture_spec("E6")
        self.assertEqual("e5-backend-loss", e5.atspi_route.value)
        self.assertEqual("e6-automatic-recovery", e6.atspi_route.value)
        self.assertEqual("automatic-recovery", e6.operation.value)
        self.assertEqual("recovery-required", e6.durable_before.value)
        self.assertEqual("terminal.e6", e6.capture_milestone.value)

    def test_exact_environment_profiles_are_consistent(self) -> None:
        expected = {
            "ubuntu-24.04-gnome-x11": ("Ubuntu", "GNOME", "x11", "x11"),
            "ubuntu-24.04-gnome-xwayland": ("Ubuntu", "GNOME", "wayland", "xwayland"),
            "ubuntu-24.04-kde-x11": ("Ubuntu", "KDE", "x11", "x11"),
            "ubuntu-24.04-kde-xwayland": ("Ubuntu", "KDE", "wayland", "xwayland"),
        }
        actual = {}
        for profile in contract.ENVIRONMENT_PROFILES:
            actual[profile.profile_id.value] = (
                profile.distribution.value, profile.desktop.value,
                profile.session.value, profile.window_backend.value,
            )
            self.assertEqual("24.04.4 LTS", profile.distribution_version)
            self.assertEqual("amd64", profile.architecture.value)
            self.assertEqual(100, profile.scale_percent)
            self.assertEqual("light", profile.theme.value)
            self.assertEqual((1920, 1080), (profile.resolution_width, profile.resolution_height))
        self.assertEqual(expected, actual)
        self.assertEqual(len(expected), len(contract.ENVIRONMENT_PROFILE_BY_ID))

    def test_records_and_collections_are_immutable(self) -> None:
        with self.assertRaises(FrozenInstanceError):
            contract.CAPTURE_SPECS[0].output_basename = "changed"
        with self.assertRaises(TypeError):
            contract.CAPTURE_SPEC_BY_SCENARIO[contract.Scenario.C2] = contract.CAPTURE_SPECS[0]
        self.assertIsInstance(contract.CAPTURE_SPEC_BY_SCENARIO, MappingProxyType)
        self.assertIsInstance(contract.ENVIRONMENT_PROFILE_BY_ID, MappingProxyType)
        with self.assertRaises(TypeError):
            contract.CAPTURE_SPECS[0] = contract.CAPTURE_SPECS[1]

    def test_lookup_accepts_exact_enum_or_string_only(self) -> None:
        self.assertIs(contract.capture_spec("C2"), contract.capture_spec(contract.Scenario.C2))
        profile_id = contract.EnvironmentId.UBUNTU_GNOME_X11
        self.assertIs(contract.environment_profile(profile_id.value), contract.environment_profile(profile_id))
        for unknown in ("c2", "E2", "E7", "", 2, None):
            with self.subTest(scenario=unknown), self.assertRaises(ValueError):
                contract.capture_spec(unknown)
        for unknown in ("ubuntu", "gnome-x11", "", 1, None):
            with self.subTest(profile=unknown), self.assertRaises(ValueError):
                contract.environment_profile(unknown)


if __name__ == "__main__":
    unittest.main()
