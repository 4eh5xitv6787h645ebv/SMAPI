#!/usr/bin/env python3
"""Synthetic tests for validate-linux-gui-hard-state-inputs.py."""

from __future__ import annotations

import hashlib
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
from typing import Any, Callable


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
VALIDATOR = REPOSITORY_ROOT / "build/scripts/validate-linux-gui-hard-state-inputs.py"
MARKER_NAME = ".smapi-linux-gui-hard-state-disposable-v1.json"
MARKER_PURPOSE = "smapi-linux-gui-hard-state-disposable-root"
VERSION = "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.3"
TAG = "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3"
SCENARIOS = (
    "E2-permission", "E2-read-only", "E2-disk-full", "E2-cross-device", "C2", "C3", "E5", "E6",
)
PRIVATE_SENTINEL = "fixture-private-sentinel-should-never-appear"
ALLOWED_TEMP_PARENT = Path(f"/run/user/{os.geteuid()}")
if not ALLOWED_TEMP_PARENT.is_dir() or not os.access(ALLOWED_TEMP_PARENT, os.W_OK | os.X_OK):
    ALLOWED_TEMP_PARENT = Path("/dev/shm")


def load_validator():
    spec = importlib.util.spec_from_file_location("hard_state_input_validator", VALIDATOR)
    if spec is None or spec.loader is None:
        raise AssertionError("validator could not be loaded")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


VALIDATOR_MODULE = load_validator()


class Fixture:
    def __init__(self, base: Path, scenario: str = "C3"):
        self.base = base
        self.root = base / "isolated-qualification-root"
        self.root.mkdir(mode=0o700)
        os.chmod(self.root, 0o700)
        root_stat = self.root.stat()
        marker = {
            "schema_version": 1,
            "purpose": MARKER_PURPOSE,
            "root_device": root_stat.st_dev,
            "root_inode": root_stat.st_ino,
        }
        self.marker = self.root / MARKER_NAME
        self.marker.write_text(json.dumps(marker, sort_keys=True) + "\n", encoding="utf-8")
        os.chmod(self.marker, 0o600)
        self.package = base / f"SMAPI-{VERSION}-linux-x64-installer.zip"
        self.package.write_bytes(b"PK\x05\x06" + b"\x00" * 18)
        os.chmod(self.package, 0o600)
        package_data = self.package.read_bytes()
        self.game_marker = base / "Stardew Valley.dll"
        self.game_marker.write_bytes(b"MZ" + b"synthetic-redistribution-safe-game-marker")
        os.chmod(self.game_marker, 0o600)
        marker_data = self.game_marker.read_bytes()
        self.contract: dict[str, Any] = {
            "schema_version": 1,
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
                "size_bytes": len(package_data),
                "sha256": hashlib.sha256(package_data).hexdigest(),
            },
            "game_marker": {
                "path": str(self.game_marker),
                "size_bytes": len(marker_data),
                "sha256": hashlib.sha256(marker_data).hexdigest(),
            },
            "binaries": {"apphost_sha256": "3" * 64, "backend_sha256": "4" * 64},
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
            "timeouts_seconds": {
                "startup": 30,
                "operation": 300,
                "settlement": 120,
                "cleanup": 60,
                "total": 600,
            },
        }
        self.contract_path = base / "contract.json"
        self.output = self.root / "hard-state-output-00000001"
        self.write()

    def write(self) -> None:
        self.contract_path.write_text(json.dumps(self.contract, sort_keys=True) + "\n", encoding="utf-8")
        os.chmod(self.contract_path, 0o600)

    def refresh_root_identity(self) -> None:
        root_stat = self.root.stat()
        self.contract["isolation"]["disposable_root"] = str(self.root)
        self.contract["isolation"]["root_device"] = root_stat.st_dev
        self.contract["isolation"]["root_inode"] = root_stat.st_ino
        marker = {
            "schema_version": 1,
            "purpose": MARKER_PURPOSE,
            "root_device": root_stat.st_dev,
            "root_inode": root_stat.st_ino,
        }
        self.marker = self.root / MARKER_NAME
        self.marker.write_text(json.dumps(marker, sort_keys=True) + "\n", encoding="utf-8")
        os.chmod(self.marker, 0o600)
        self.output = self.root / "hard-state-output-00000001"
        self.write()


def run(fixture: Fixture, arguments: list[str] | None = None) -> subprocess.CompletedProcess[str]:
    command = [
        sys.executable,
        str(VALIDATOR),
        "--contract",
        str(fixture.contract_path),
        "--output",
        str(fixture.output),
    ] if arguments is None else [sys.executable, str(VALIDATOR), *arguments]
    return subprocess.run(command, check=False, capture_output=True, text=True, timeout=10)


def payload(result: subprocess.CompletedProcess[str]) -> dict[str, Any]:
    if result.stderr != "":
        raise AssertionError(f"validator wrote stderr: {result.stderr!r}")
    lines = result.stdout.splitlines()
    if len(lines) != 1:
        raise AssertionError(f"validator did not emit exactly one JSON line: {result.stdout!r}")
    return json.loads(lines[0])


class HardStateInputValidatorTests(unittest.TestCase):
    def fixture(self, scenario: str = "C3") -> tuple[tempfile.TemporaryDirectory[str], Fixture]:
        temporary = tempfile.TemporaryDirectory(prefix="smapi-hard-state-test-", dir=ALLOWED_TEMP_PARENT)
        return temporary, Fixture(Path(temporary.name), scenario)

    def assert_rejected(self, mutation: Callable[[Fixture], None], code: str, scenario: str = "C3") -> None:
        temporary, fixture = self.fixture(scenario)
        with temporary:
            mutation(fixture)
            fixture.write()
            result = run(fixture)
            self.assertEqual(result.returncode, 2, result.stdout)
            self.assertEqual(payload(result), {"code": code, "ok": False, "schemaVersion": 1, "status": "rejected"})
            self.assertNotIn(PRIVATE_SENTINEL, result.stdout + result.stderr)
            self.assertFalse(fixture.output.exists())

    def test_accepts_every_exact_scenario_and_creates_private_new_output(self) -> None:
        for scenario in SCENARIOS:
            with self.subTest(scenario=scenario):
                temporary, fixture = self.fixture(scenario)
                with temporary:
                    result = run(fixture)
                    self.assertEqual(result.returncode, 0, result.stdout)
                    self.assertEqual(payload(result), {
                        "ok": True,
                        "scenario": scenario,
                        "schemaVersion": 1,
                        "status": "validated",
                    })
                    self.assertTrue(fixture.output.is_dir())
                    self.assertEqual(stat.S_IMODE(fixture.output.stat().st_mode), 0o700)
                    self.assertEqual(list(fixture.output.iterdir()), [])
                    self.assertNotIn(str(fixture.base), result.stdout)
                    self.assertNotIn(fixture.contract["package"]["sha256"], result.stdout)

    def test_rejects_reused_directory_file_and_symlink_outputs(self) -> None:
        for kind in ("directory", "file", "symlink"):
            with self.subTest(kind=kind):
                temporary, fixture = self.fixture()
                with temporary:
                    if kind == "directory":
                        fixture.output.mkdir()
                    elif kind == "file":
                        fixture.output.write_text("used", encoding="utf-8")
                    else:
                        fixture.output.symlink_to(fixture.package)
                    result = run(fixture)
                    self.assertEqual(result.returncode, 2)
                    self.assertEqual(payload(result)["code"], "output-exists")

    def test_rejects_root_symlink_and_identity_or_marker_mismatch(self) -> None:
        def symlink_root(fixture: Fixture) -> None:
            real = fixture.root
            alias = fixture.base / "isolated-root-alias"
            alias.symlink_to(real, target_is_directory=True)
            fixture.contract["isolation"]["disposable_root"] = str(alias)
            fixture.output = alias / fixture.output.name

        self.assert_rejected(symlink_root, "unsafe-root")
        self.assert_rejected(
            lambda fixture: fixture.contract["isolation"].__setitem__("root_inode", fixture.root.stat().st_ino + 1),
            "unsafe-root",
        )

        def marker_mismatch(fixture: Fixture) -> None:
            marker = json.loads(fixture.marker.read_text(encoding="utf-8"))
            marker["root_inode"] += 1
            fixture.marker.write_text(json.dumps(marker) + "\n", encoding="utf-8")
            os.chmod(fixture.marker, 0o600)

        self.assert_rejected(marker_mismatch, "marker")
        self.assert_rejected(lambda fixture: os.chmod(fixture.marker, 0o644), "marker")

    def test_rejects_nonempty_or_nonprivate_disposable_root(self) -> None:
        self.assert_rejected(
            lambda fixture: (fixture.root / "prior-run.txt").write_text("used", encoding="utf-8"),
            "unsafe-root",
        )
        self.assert_rejected(lambda fixture: os.chmod(fixture.root, 0o755), "unsafe-root")

    def test_rejects_repository_home_steam_and_live_game_roots(self) -> None:
        def set_root(fixture: Fixture, root: Path) -> None:
            fixture.contract["isolation"]["disposable_root"] = str(root)
            fixture.output = root / "hard-state-output-00000001"

        self.assert_rejected(lambda fixture: set_root(fixture, REPOSITORY_ROOT), "unsafe-root")
        self.assert_rejected(lambda fixture: set_root(fixture, Path.home()), "unsafe-root")
        self.assert_rejected(lambda fixture: set_root(fixture, Path("/tmp/smapi-hard-state-live-risk")), "unsafe-root")

        def steam_root(fixture: Fixture) -> None:
            fixture.root = fixture.base / "Steam" / "steamapps" / "fresh-root"
            fixture.root.mkdir(parents=True, mode=0o700)
            os.chmod(fixture.root, 0o700)
            fixture.refresh_root_identity()

        self.assert_rejected(steam_root, "unsafe-root")

        def game_root(fixture: Fixture) -> None:
            fixture.root = fixture.base / "Stardew Valley" / "fresh-root"
            fixture.root.mkdir(parents=True, mode=0o700)
            os.chmod(fixture.root, 0o700)
            fixture.refresh_root_identity()

        self.assert_rejected(game_root, "unsafe-root")

    def test_rejects_package_symlink_wrong_name_size_digest_and_writable_mode(self) -> None:
        def symlink_package(fixture: Fixture) -> None:
            target = fixture.package
            alias = fixture.base / ("copy-" + fixture.package.name)
            alias.symlink_to(target)
            fixture.contract["package"]["path"] = str(alias)

        self.assert_rejected(symlink_package, "package-file")
        self.assert_rejected(lambda fixture: fixture.contract["package"].__setitem__("size_bytes", 99), "package-file")
        self.assert_rejected(lambda fixture: fixture.contract["package"].__setitem__("sha256", "f" * 64), "package-mismatch")
        self.assert_rejected(lambda fixture: os.chmod(fixture.package, 0o622), "package-file")

        def wrong_name(fixture: Fixture) -> None:
            renamed = fixture.base / "candidate.zip"
            fixture.package.rename(renamed)
            fixture.contract["package"]["path"] = str(renamed)

        self.assert_rejected(wrong_name, "package-file")

    def test_rejects_game_marker_symlink_wrong_name_size_and_digest(self) -> None:
        def symlink_marker(fixture: Fixture) -> None:
            source = fixture.base / "game-marker-source.dll"
            fixture.game_marker.rename(source)
            fixture.game_marker.symlink_to(source)

        self.assert_rejected(symlink_marker, "game-marker")

        def wrong_name(fixture: Fixture) -> None:
            renamed = fixture.base / "GameMarker.dll"
            fixture.game_marker.rename(renamed)
            fixture.contract["game_marker"]["path"] = str(renamed)

        self.assert_rejected(wrong_name, "game-marker")
        self.assert_rejected(
            lambda fixture: fixture.contract["game_marker"].__setitem__("size_bytes", 16 * 1024 * 1024 + 1),
            "game-marker",
        )
        self.assert_rejected(
            lambda fixture: fixture.contract["game_marker"].__setitem__("sha256", "f" * 64),
            "game-marker-mismatch",
        )

    def test_rejects_file_metadata_change_between_name_and_descriptor_binding(self) -> None:
        with tempfile.TemporaryDirectory(prefix="hs-validator-race-", dir=ALLOWED_TEMP_PARENT) as name:
            fixture = Fixture(Path(name))
            cases = (
                (fixture.package, VALIDATOR_MODULE.validate_regular_package, fixture.contract["package"], VERSION),
                (fixture.game_marker, VALIDATOR_MODULE.validate_game_marker, fixture.contract["game_marker"], None),
            )
            for path, validator, value, version in cases:
                with self.subTest(path=path.name):
                    os.chmod(path, 0o600)
                    real_open = VALIDATOR_MODULE.os.open
                    changed = False

                    def raced_open(candidate, *arguments, **keywords):
                        nonlocal changed
                        if not changed and Path(candidate) == path:
                            changed = True
                            os.chmod(path, 0o400)
                        return real_open(candidate, *arguments, **keywords)

                    with mock.patch.object(VALIDATOR_MODULE.os, "open", side_effect=raced_open):
                        with self.assertRaises(VALIDATOR_MODULE.InputError):
                            if version is None:
                                validator(value)
                            else:
                                validator(value, version)
                    self.assertTrue(changed)

    def test_rejects_release_commit_digest_and_binary_digest_mismatches(self) -> None:
        self.assert_rejected(lambda fixture: fixture.contract["release"].__setitem__("tag", TAG + "-moved"), "release")
        self.assert_rejected(lambda fixture: fixture.contract["release"].__setitem__("url", "https://example.invalid/release"), "release")
        self.assert_rejected(lambda fixture: fixture.contract["release"].__setitem__("expected_commit", "A" * 40), "release")
        self.assert_rejected(lambda fixture: fixture.contract["binaries"].__setitem__("apphost_sha256", "0" * 63), "digest")

    def test_rejects_unknown_duplicate_secret_and_private_fixture_inputs_without_leakage(self) -> None:
        self.assert_rejected(lambda fixture: fixture.contract.__setitem__("modpack_path", PRIVATE_SENTINEL), "forbidden-input")
        self.assert_rejected(lambda fixture: fixture.contract.__setitem__("github_token", "ghp_" + "x" * 24), "forbidden-input")

        temporary, fixture = self.fixture()
        with temporary:
            raw = fixture.contract_path.read_text(encoding="utf-8").rstrip()
            raw = raw[:-1] + ',"scenario":"E5"}\n'
            fixture.contract_path.write_text(raw, encoding="utf-8")
            os.chmod(fixture.contract_path, 0o600)
            result = run(fixture)
            self.assertEqual(payload(result)["code"], "contract-json")
            self.assertFalse(fixture.output.exists())

    def test_rejects_boundary_flag_and_scenario_privilege_disagreement(self) -> None:
        self.assert_rejected(lambda fixture: fixture.contract["isolation"].__setitem__("disposable_vm", False), "boundary")
        self.assert_rejected(lambda fixture: fixture.contract["isolation"].__setitem__("live_roots_mounted", True), "boundary")
        self.assert_rejected(lambda fixture: fixture.contract["isolation"].__setitem__("installer_runs_as_root", True), "boundary")
        self.assert_rejected(lambda fixture: fixture.contract["isolation"].__setitem__("allow_privileged_fault_setup", True), "boundary")
        self.assert_rejected(
            lambda fixture: fixture.contract["isolation"].__setitem__("allow_privileged_fault_setup", False),
            "boundary",
            scenario="E2-disk-full",
        )

    def test_rejects_unbounded_or_incoherent_timeouts(self) -> None:
        self.assert_rejected(lambda fixture: fixture.contract["timeouts_seconds"].__setitem__("startup", 121), "timeout")
        self.assert_rejected(lambda fixture: fixture.contract["timeouts_seconds"].__setitem__("operation", 9), "timeout")
        self.assert_rejected(lambda fixture: fixture.contract["timeouts_seconds"].__setitem__("total", 100), "timeout")
        self.assert_rejected(lambda fixture: fixture.contract["timeouts_seconds"].__setitem__("total", 1801), "timeout")

    def test_rejects_contract_file_alias_mode_and_oversize(self) -> None:
        temporary, fixture = self.fixture()
        with temporary:
            os.chmod(fixture.contract_path, 0o644)
            result = run(fixture)
            self.assertEqual(payload(result)["code"], "contract-file")

        temporary, fixture = self.fixture()
        with temporary:
            alias = fixture.base / "contract-alias.json"
            alias.symlink_to(fixture.contract_path)
            result = run(fixture, ["--contract", str(alias), "--output", str(fixture.output)])
            self.assertEqual(payload(result)["code"], "contract-file")

        temporary, fixture = self.fixture()
        with temporary:
            fixture.contract_path.write_bytes(b"{" + b" " * (64 * 1024) + b"}")
            os.chmod(fixture.contract_path, 0o600)
            result = run(fixture)
            self.assertEqual(payload(result)["code"], "contract-file")

    def test_rejects_unsafe_output_name_and_cli_without_echoing_values(self) -> None:
        temporary, fixture = self.fixture()
        with temporary:
            fixture.output = fixture.root / "short"
            result = run(fixture)
            self.assertEqual(payload(result)["code"], "unsafe-output")

            result = run(fixture, ["--unknown", PRIVATE_SENTINEL, "--output", str(fixture.output)])
            self.assertEqual(result.returncode, 2)
            self.assertEqual(payload(result)["code"], "usage")
            self.assertNotIn(PRIVATE_SENTINEL, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
