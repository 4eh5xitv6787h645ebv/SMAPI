#!/usr/bin/env python3
"""Standalone tests for fail-closed Linux GUI capture-environment verification."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest


MODULE_PATH = Path(__file__).with_name("verify-linux-gui-capture-environment.py")
SPEC = importlib.util.spec_from_file_location("verify_linux_gui_capture_environment", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
verifier = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = verifier
SPEC.loader.exec_module(verifier)


class FakeProbe:
    def __init__(self, *, xwayland: bool, desktop: str) -> None:
        self.xwayland = xwayland
        self.desktop = desktop
        self.calls: list[tuple[str, ...]] = []
        self.fail_command: str | None = None
        self.oversize_command: str | None = None
        self.malformed_command: str | None = None
        self.race_resolution = False
        self.xrandr_calls = 0

    def __call__(self, argv: object, environment: object) -> object:
        command = tuple(argv)  # type: ignore[arg-type]
        self.calls.append(command)
        name = command[0]
        if name == self.fail_command:
            return verifier._CommandResult(1, b"", b"")
        if name == self.oversize_command:
            return verifier._CommandResult(0, b"x" * (verifier.MAX_COMMAND_BYTES + 1), b"")
        if name == self.malformed_command:
            return verifier._CommandResult(0, b"not valid\n", b"")
        if name == verifier.XRANDR:
            self.xrandr_calls += 1
            width = 1280 if self.race_resolution and self.xrandr_calls == 2 else 1920
            output = f"Screen 0: minimum 8 x 8, current {width} x 1080, maximum 32767 x 32767\n"
        elif name == verifier.XDPYINFO:
            extension = "    XWAYLAND\n" if self.xwayland else ""
            output = f"name of display: :1\n{extension}  dimensions:    1920x1080 pixels (508x285 millimeters)\n"
        elif name == verifier.GDBUS:
            output = "(uint32 1, [], [(0, 0, 1.0, uint32 0, true, [('DP-1', 'vendor', 'model')])], {})\n"
        elif name == verifier.GSETTINGS:
            output = "'prefer-light'\n"
        elif name == verifier.KSCREEN_DOCTOR:
            output = "\x1b[01;32mOutput: \x1b[0;0m1 HDMI-1 enabled connected\n  Geometry: 0,0 1920x1080  \x1b[01;33mScale: \x1b[0;0m1 Rotation: 1\n"
        elif name == verifier.KREADCONFIG5:
            output = "KubuntuLight\n"
        else:
            raise AssertionError(f"unexpected command: {command}")
        return verifier._CommandResult(0, output.encode(), b"")


def os_release(*, identity: int = 1, pretty: str = "Ubuntu 24.04.4 LTS") -> object:
    return verifier._OsReleaseSnapshot(
        {"ID": "ubuntu", "NAME": "Ubuntu", "VERSION_ID": "24.04", "PRETTY_NAME": pretty},
        (1, identity, 0o100644, 0, 100, 1),
    )


def uname(machine: str = "x86_64") -> object:
    return SimpleNamespace(machine=machine)


class CaptureEnvironmentTests(unittest.TestCase):
    def verify(self, profile: str, *, probe: FakeProbe | None = None, environment: dict[str, str] | None = None, **kwargs: object) -> object:
        desktop = "GNOME" if "gnome" in profile else "KDE"
        session = "wayland" if profile.endswith("xwayland") else "x11"
        fake = probe or FakeProbe(xwayland=session == "wayland", desktop=desktop)
        values = environment or {
            "DISPLAY": ":1",
            "XDG_SESSION_TYPE": session,
            "XDG_CURRENT_DESKTOP": "ubuntu:GNOME" if desktop == "GNOME" else "KDE",
            "DBUS_SESSION_BUS_ADDRESS": "unix:path=/run/user/1000/bus",
            "HOME": "/home/capture",
            "XAUTHORITY": "/run/user/1000/xauth",
        }
        arguments = {
            "_os_release_reader": lambda: os_release(),
            "_uname_reader": uname,
            "_environment_reader": lambda: values,
            "_command_runner": fake,
        }
        arguments.update(kwargs)
        return verifier.verify_capture_environment(profile, **arguments)

    def test_all_four_closed_profiles(self) -> None:
        profiles = (
            "ubuntu-24.04-gnome-x11",
            "ubuntu-24.04-gnome-xwayland",
            "ubuntu-24.04-kde-x11",
            "ubuntu-24.04-kde-xwayland",
        )
        for profile in profiles:
            with self.subTest(profile=profile):
                facts = self.verify(profile)
                expected_desktop = "GNOME" if "gnome" in profile else "KDE"
                self.assertEqual(profile, facts.profile_id)
                self.assertEqual(expected_desktop, facts.desktop)
                self.assertEqual("xwayland" if profile.endswith("xwayland") else "x11", facts.window_backend)
                self.assertEqual((1920, 1080, 100, "light"), (facts.resolution_width, facts.resolution_height, facts.scale_percent, facts.theme))
                self.assertTrue(facts.display_present)

    def test_profile_mismatches_fail_closed(self) -> None:
        cases = (
            {"XDG_SESSION_TYPE": "wayland", "XDG_CURRENT_DESKTOP": "ubuntu:GNOME", "DISPLAY": ":1"},
            {"XDG_SESSION_TYPE": "x11", "XDG_CURRENT_DESKTOP": "KDE", "DISPLAY": ":1"},
            {"XDG_SESSION_TYPE": "x11", "XDG_CURRENT_DESKTOP": "ubuntu:GNOME", "DISPLAY": "remote:1"},
        )
        for environment in cases:
            with self.subTest(environment=environment), self.assertRaises(verifier.CaptureEnvironmentError):
                self.verify("ubuntu-24.04-gnome-x11", environment=environment)
        with self.assertRaises(verifier.CaptureEnvironmentError):
            self.verify("ubuntu-24.04-gnome-xwayland", probe=FakeProbe(xwayland=False, desktop="GNOME"))

    def test_distribution_and_architecture_mismatches_fail(self) -> None:
        with self.assertRaises(verifier.CaptureEnvironmentError):
            self.verify("ubuntu-24.04-gnome-x11", _os_release_reader=lambda: os_release(pretty="Ubuntu 24.04.3 LTS"))
        with self.assertRaises(verifier.CaptureEnvironmentError):
            self.verify("ubuntu-24.04-gnome-x11", _uname_reader=lambda: uname("aarch64"))
        with self.assertRaises(verifier.CaptureEnvironmentError):
            self.verify("ubuntu-24.04-unknown-x11")

    def test_operating_system_metadata_parser_is_strict_and_bounded(self) -> None:
        parsed = verifier._parse_os_release(
            b'NAME="Ubuntu"\nPRETTY_NAME="Ubuntu 24.04.4 LTS"\nID=ubuntu\nVERSION_ID="24.04"\n'
        )
        self.assertEqual("Ubuntu 24.04.4 LTS", parsed["PRETTY_NAME"])
        for malformed in (
            b"ID=ubuntu\nID=ubuntu\n",
            b"ID=ubuntu linux\n",
            b"not-an-assignment\n",
            b"ID=\xff\n",
            b'ID="unterminated\n',
        ):
            with self.subTest(malformed=malformed), self.assertRaises(verifier.CaptureEnvironmentError):
                verifier._parse_os_release(malformed)

    def test_malformed_resolution_scale_and_theme_fail(self) -> None:
        for command in (verifier.XRANDR, verifier.XDPYINFO, verifier.GDBUS, verifier.GSETTINGS):
            probe = FakeProbe(xwayland=False, desktop="GNOME")
            probe.malformed_command = command
            with self.subTest(command=command), self.assertRaises(verifier.CaptureEnvironmentError):
                self.verify("ubuntu-24.04-gnome-x11", probe=probe)
        for command in (verifier.KSCREEN_DOCTOR, verifier.KREADCONFIG5):
            probe = FakeProbe(xwayland=False, desktop="KDE")
            probe.malformed_command = command
            with self.subTest(command=command), self.assertRaises(verifier.CaptureEnvironmentError):
                self.verify("ubuntu-24.04-kde-x11", probe=probe)
        with self.assertRaises(verifier.CaptureEnvironmentError):
            verifier._kde_scale("Scale: 1 \x1b]private-terminal-sequence")

    def test_unbounded_environment_and_command_output_fail(self) -> None:
        environment = {"DISPLAY": ":1", "XDG_SESSION_TYPE": "x11", "XDG_CURRENT_DESKTOP": "X" * 5000}
        with self.assertRaises(verifier.CaptureEnvironmentError):
            self.verify("ubuntu-24.04-gnome-x11", environment=environment)
        probe = FakeProbe(xwayland=False, desktop="GNOME")
        probe.oversize_command = verifier.XRANDR
        with self.assertRaises(verifier.CaptureEnvironmentError):
            self.verify("ubuntu-24.04-gnome-x11", probe=probe)

    def test_command_failure_and_stderr_fail(self) -> None:
        probe = FakeProbe(xwayland=False, desktop="GNOME")
        probe.fail_command = verifier.GDBUS
        with self.assertRaises(verifier.CaptureEnvironmentError):
            self.verify("ubuntu-24.04-gnome-x11", probe=probe)

        def stderr_runner(argv: object, environment: object) -> object:
            return verifier._CommandResult(0, b"", b"warning")
        with self.assertRaises(verifier.CaptureEnvironmentError):
            self.verify("ubuntu-24.04-gnome-x11", _command_runner=stderr_runner)

    def test_races_are_rejected(self) -> None:
        probe = FakeProbe(xwayland=False, desktop="GNOME")
        probe.race_resolution = True
        with self.assertRaises(verifier.CaptureEnvironmentError):
            self.verify("ubuntu-24.04-gnome-x11", probe=probe)

        releases = iter((os_release(identity=1), os_release(identity=2)))
        with self.assertRaises(verifier.CaptureEnvironmentError):
            self.verify("ubuntu-24.04-gnome-x11", _os_release_reader=lambda: next(releases))

        environments = iter((
            {"DISPLAY": ":1", "XDG_SESSION_TYPE": "x11", "XDG_CURRENT_DESKTOP": "ubuntu:GNOME"},
            {"DISPLAY": ":2", "XDG_SESSION_TYPE": "x11", "XDG_CURRENT_DESKTOP": "ubuntu:GNOME"},
        ))
        with self.assertRaises(verifier.CaptureEnvironmentError):
            self.verify("ubuntu-24.04-gnome-x11", _environment_reader=lambda: next(environments))

    def test_scale_overrides_must_prove_exact_100_percent(self) -> None:
        for key, value in (("GDK_SCALE", "2"), ("GDK_DPI_SCALE", "0.75"), ("QT_SCALE_FACTOR", "1.25"), ("QT_SCREEN_SCALE_FACTORS", "HDMI-1=1")):
            environment = {
                "DISPLAY": ":1", "XDG_SESSION_TYPE": "x11",
                "XDG_CURRENT_DESKTOP": "ubuntu:GNOME", key: value,
            }
            with self.subTest(key=key), self.assertRaises(verifier.CaptureEnvironmentError):
                self.verify("ubuntu-24.04-gnome-x11", environment=environment)

    def test_public_facts_are_frozen_bounded_and_private_free(self) -> None:
        secret = "PRIVATE_TOKEN_4f017"
        environment = {
            "DISPLAY": ":1", "XDG_SESSION_TYPE": "x11",
            "XDG_CURRENT_DESKTOP": "ubuntu:GNOME",
            "HOME": f"/home/{secret}", "DBUS_SESSION_BUS_ADDRESS": f"unix:path=/{secret}",
            "UNRELATED_SECRET": secret,
        }
        facts = self.verify("ubuntu-24.04-gnome-x11", environment=environment)
        rendered = repr(facts.as_dict())
        self.assertNotIn(secret, rendered)
        self.assertLess(len(rendered), 1024)
        self.assertEqual(
            {"schemaVersion", "profileId", "distribution", "distributionVersion", "architecture", "desktop", "session", "windowBackend", "displayPresent", "resolutionWidth", "resolutionHeight", "scalePercent", "theme"},
            set(facts.as_dict()),
        )
        with self.assertRaises(AttributeError):
            facts.theme = "dark"


if __name__ == "__main__":
    unittest.main()
