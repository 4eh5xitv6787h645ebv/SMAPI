#!/usr/bin/env python3
"""Synthetic tests for Linux GUI hard-state contract preparation."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
HELPER = REPOSITORY_ROOT / "build/scripts/prepare-linux-gui-hard-state-case.py"
VALIDATOR = REPOSITORY_ROOT / "build/scripts/validate-linux-gui-hard-state-inputs.py"
VERSION = "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.3"
COMMIT = "1" * 40
TREE = "2" * 40
RUNTIME_PARENT = Path(f"/run/user/{os.geteuid()}")
if not RUNTIME_PARENT.is_dir() or not os.access(RUNTIME_PARENT, os.W_OK | os.X_OK):
    RUNTIME_PARENT = Path("/dev/shm")


def load(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def executable_entry(name: str, content: bytes) -> zipfile.ZipInfo:
    entry = zipfile.ZipInfo(name)
    entry.create_system = 3
    entry.external_attr = (stat.S_IFREG | 0o755) << 16
    entry.compress_type = zipfile.ZIP_STORED
    return entry


class Fixture:
    def __init__(self, base: Path, module, scenario: str = "C3"):
        self.base = base
        self.module = module
        self.prefix = base / "prefix"
        self.prefix.mkdir(mode=0o711)
        os.chmod(self.prefix, 0o711)
        self.case_root = self.prefix / ("smapi-hard-state-" + "a" * 32)
        self.case_root.mkdir(mode=0o700)
        self.runtime = base / "runtime"
        self.runtime.mkdir(mode=0o700)
        self.package = base / f"SMAPI-{VERSION}-linux-x64-installer.zip"
        self.write_package()
        self.scenario = scenario

    def write_package(self, extra: list[tuple[str, bytes, bool]] | None = None, omit_backend: bool = False) -> None:
        root = f"SMAPI {VERSION} Linux installer"
        with zipfile.ZipFile(self.package, "w") as archive:
            archive.writestr(executable_entry(f"{root}/internal/linux/SMAPI.Installer.Gui", b"gui-apphost\n"), b"gui-apphost\n")
            if not omit_backend:
                archive.writestr(executable_entry(f"{root}/internal/linux/SMAPI.Installer", b"backend-apphost\n"), b"backend-apphost\n")
            archive.writestr(f"{root}/README.txt", b"public synthetic package\n")
            for name, content, executable in extra or []:
                if executable:
                    archive.writestr(executable_entry(name, content), content)
                else:
                    archive.writestr(name, content)
        os.chmod(self.package, 0o600)

    def prepare(self):
        return self.module.prepare(
            case_root=self.case_root,
            package=self.package,
            version=VERSION,
            expected_package_sha256=hashlib.sha256(self.package.read_bytes()).hexdigest(),
            commit=COMMIT,
            tree=TREE,
            scenario=self.scenario,
            repository_root=REPOSITORY_ROOT,
            runtime_root=self.runtime,
            required_prefix_uid=os.geteuid(),
        )


class PreparationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.module = load(HELPER, "hard_state_case_preparer")
        cls.validator = load(VALIDATOR, "hard_state_case_validator_for_preparer")

    def temporary(self):
        return tempfile.TemporaryDirectory(prefix="smapi-hard-state-prep-test-", dir=RUNTIME_PARENT)

    def test_prepares_exact_contract_marker_and_random_output_for_every_scenario(self):
        for scenario in sorted(self.module.SCENARIOS):
            with self.subTest(scenario=scenario), self.temporary() as name:
                fixture = Fixture(Path(name), self.module, scenario)
                contract_path, output = fixture.prepare()
                self.assertEqual(contract_path.parent.parent, fixture.runtime)
                self.assertRegex(contract_path.parent.name, r"^smapi-hard-state-contract-[0-9a-f]{32}$")
                self.assertRegex(output.name, r"^qualification-[0-9a-f]{32}$")
                self.assertFalse(output.exists())
                self.assertEqual(stat.S_IMODE(contract_path.parent.stat().st_mode), 0o700)
                self.assertEqual(stat.S_IMODE(contract_path.stat().st_mode), 0o600)
                game_marker = contract_path.parent / "Stardew Valley.dll"
                self.assertEqual(stat.S_IMODE(game_marker.stat().st_mode), 0o600)
                self.assertEqual(game_marker.stat().st_size, self.module.SYNTHETIC_MARKER_DECODED_BYTES)
                self.assertEqual(
                    hashlib.sha256(game_marker.read_bytes()).hexdigest(),
                    self.module.SYNTHETIC_MARKER_DECODED_SHA256,
                )
                marker_path = fixture.case_root / self.module.MARKER_NAME
                self.assertEqual(stat.S_IMODE(marker_path.stat().st_mode), 0o600)
                contract = json.loads(contract_path.read_text(encoding="ascii"))
                self.assertEqual(set(contract), {
                    "schema_version", "scenario", "release", "package", "game_marker",
                    "binaries", "isolation", "timeouts_seconds",
                })
                self.assertEqual(contract["scenario"], scenario)
                self.assertIs(contract["isolation"]["allow_privileged_fault_setup"], scenario.startswith("E2-"))
                self.assertEqual(contract["package"]["sha256"], hashlib.sha256(fixture.package.read_bytes()).hexdigest())
                self.assertEqual(contract["binaries"]["apphost_sha256"], hashlib.sha256(b"gui-apphost\n").hexdigest())
                self.assertEqual(contract["binaries"]["backend_sha256"], hashlib.sha256(b"backend-apphost\n").hexdigest())
                self.assertEqual(self.validator.validate_contract(contract, output), scenario)

    def test_requires_nonroot_and_exact_root_prefix_contract(self):
        with self.temporary() as name:
            fixture = Fixture(Path(name), self.module)
            with mock.patch.object(self.module.os, "geteuid", return_value=0):
                with self.assertRaisesRegex(self.module.PreparationError, "must-be-nonroot"):
                    fixture.prepare()
            os.chmod(fixture.prefix, 0o700)
            with self.assertRaisesRegex(self.module.PreparationError, "unsafe-prefix"):
                fixture.prepare()
            os.chmod(fixture.prefix, 0o711)
            os.chmod(fixture.case_root, 0o755)
            with self.assertRaisesRegex(self.module.PreparationError, "unsafe-root"):
                fixture.prepare()

    def test_rejects_nonrandom_case_name_and_nonempty_root_without_overwriting(self):
        with self.temporary() as name:
            fixture = Fixture(Path(name), self.module)
            fixture.case_root.rename(fixture.prefix / "manual-root")
            fixture.case_root = fixture.prefix / "manual-root"
            with self.assertRaisesRegex(self.module.PreparationError, "unsafe-root"):
                fixture.prepare()
        with self.temporary() as name:
            fixture = Fixture(Path(name), self.module)
            existing = fixture.case_root / "keep.txt"
            existing.write_text("keep", encoding="ascii")
            with self.assertRaisesRegex(self.module.PreparationError, "unsafe-root"):
                fixture.prepare()
            self.assertEqual(existing.read_text(encoding="ascii"), "keep")

    def test_rejects_symlinked_root_and_package(self):
        with self.temporary() as name:
            fixture = Fixture(Path(name), self.module)
            real_root = fixture.case_root
            linked_root = fixture.prefix / ("smapi-hard-state-" + "b" * 32)
            linked_root.symlink_to(real_root, target_is_directory=True)
            fixture.case_root = linked_root
            with self.assertRaisesRegex(self.module.PreparationError, "unsafe-root"):
                fixture.prepare()
        with self.temporary() as name:
            fixture = Fixture(Path(name), self.module)
            original = fixture.package
            linked = original.with_name("linked-" + original.name)
            linked.symlink_to(original)
            fixture.package = linked
            with self.assertRaisesRegex(self.module.PreparationError, "package"):
                fixture.prepare()

    def test_rejects_package_checksum_mismatch_without_side_effects(self):
        with self.temporary() as name:
            fixture = Fixture(Path(name), self.module)
            with self.assertRaisesRegex(self.module.PreparationError, "package-mismatch"):
                self.module.prepare(
                    case_root=fixture.case_root, package=fixture.package,
                    version=VERSION, expected_package_sha256="0" * 64, commit=COMMIT, tree=TREE,
                    scenario="C3", repository_root=REPOSITORY_ROOT, runtime_root=fixture.runtime,
                    required_prefix_uid=os.geteuid(),
                )
            self.assertEqual(list(fixture.case_root.iterdir()), [])
            self.assertEqual(list(fixture.runtime.iterdir()), [])

    def test_rejects_traversal_duplicate_and_missing_or_nonexecutable_targets(self):
        root = f"SMAPI {VERSION} Linux installer"
        cases = (
            ([(f"{root}/../escape", b"x", False)], False),
            ([(f"{root}/readme.TXT", b"duplicate", False)], False),
            ([], True),
        )
        for extra, omit_backend in cases:
            with self.subTest(extra=extra, omit_backend=omit_backend), self.temporary() as name:
                fixture = Fixture(Path(name), self.module)
                fixture.write_package(extra=extra, omit_backend=omit_backend)
                with self.assertRaisesRegex(self.module.PreparationError, "package"):
                    fixture.prepare()
        with self.temporary() as name:
            fixture = Fixture(Path(name), self.module)
            with zipfile.ZipFile(fixture.package, "w") as archive:
                archive.writestr(f"{root}/internal/linux/SMAPI.Installer.Gui", b"not executable")
                archive.writestr(executable_entry(f"{root}/internal/linux/SMAPI.Installer", b"backend"), b"backend")
            os.chmod(fixture.package, 0o600)
            with self.assertRaisesRegex(self.module.PreparationError, "package"):
                fixture.prepare()

    def test_rejects_altered_or_linked_checked_in_synthetic_fixture(self):
        with self.temporary() as name:
            fixture = Fixture(Path(name), self.module)
            altered = fixture.base / "altered-marker.base64"
            shutil.copyfile(self.module.SYNTHETIC_MARKER_FIXTURE, altered)
            os.chmod(altered, 0o600)
            data = bytearray(altered.read_bytes())
            data[0] = ord("A") if data[0] != ord("A") else ord("B")
            altered.write_bytes(data)
            with self.assertRaisesRegex(self.module.PreparationError, "fixture"):
                self.module.prepare(
                    case_root=fixture.case_root, package=fixture.package,
                    version=VERSION,
                    expected_package_sha256=hashlib.sha256(fixture.package.read_bytes()).hexdigest(),
                    commit=COMMIT, tree=TREE, scenario="C3", repository_root=REPOSITORY_ROOT,
                    runtime_root=fixture.runtime, required_prefix_uid=os.geteuid(),
                    synthetic_marker_fixture=altered,
                )
        with self.temporary() as name:
            fixture = Fixture(Path(name), self.module)
            linked = fixture.base / "linked-marker.base64"
            linked.symlink_to(self.module.SYNTHETIC_MARKER_FIXTURE)
            with self.assertRaisesRegex(self.module.PreparationError, "fixture"):
                self.module.prepare(
                    case_root=fixture.case_root, package=fixture.package,
                    version=VERSION,
                    expected_package_sha256=hashlib.sha256(fixture.package.read_bytes()).hexdigest(),
                    commit=COMMIT, tree=TREE, scenario="C3", repository_root=REPOSITORY_ROOT,
                    runtime_root=fixture.runtime, required_prefix_uid=os.geteuid(),
                    synthetic_marker_fixture=linked,
                )

    def test_rejects_release_values(self):
        for field, value in (("version", "4.5.3"), ("commit", "A" * 40), ("tree", "2" * 39), ("scenario", "E1")):
            with self.subTest(field=field), self.temporary() as name:
                fixture = Fixture(Path(name), self.module)
                values = {
                    "case_root": fixture.case_root, "package": fixture.package,
                    "version": VERSION, "expected_package_sha256": hashlib.sha256(fixture.package.read_bytes()).hexdigest(),
                    "commit": COMMIT, "tree": TREE, "scenario": "C3", "repository_root": REPOSITORY_ROOT,
                    "runtime_root": fixture.runtime, "required_prefix_uid": os.geteuid(),
                }
                values[field] = value
                with self.assertRaises(self.module.PreparationError):
                    self.module.prepare(**values)

    def test_random_collision_never_overwrites(self):
        with self.temporary() as name:
            fixture = Fixture(Path(name), self.module)
            collision = fixture.runtime / ("smapi-hard-state-contract-" + "00" * 16)
            collision.mkdir(mode=0o700)
            keep = collision / "keep"
            keep.write_text("preserve", encoding="ascii")
            with mock.patch.object(self.module.os, "urandom", return_value=b"\0" * 16):
                with self.assertRaisesRegex(self.module.PreparationError, "write"):
                    fixture.prepare()
            self.assertEqual(keep.read_text(encoding="ascii"), "preserve")
            self.assertEqual(list(fixture.case_root.iterdir()), [])

    def test_interrupted_private_write_removes_partial_case_material(self):
        with self.temporary() as name:
            fixture = Fixture(Path(name), self.module)
            with mock.patch.object(self.module.os, "fsync", side_effect=OSError("synthetic interruption")):
                with self.assertRaisesRegex(self.module.PreparationError, "write"):
                    fixture.prepare()
            self.assertEqual(list(fixture.case_root.iterdir()), [])
            self.assertEqual(list(fixture.runtime.iterdir()), [])

    def test_rejects_named_contract_directory_replacement_after_writes(self):
        with self.temporary() as name:
            fixture = Fixture(Path(name), self.module)
            original_writer = self.module.private_json_at
            calls = 0

            def replace_after_marker(directory_fd, filename, value):
                nonlocal calls
                result = original_writer(directory_fd, filename, value)
                calls += 1
                if calls == 2:
                    contract_directory = next(fixture.runtime.iterdir())
                    displaced = fixture.runtime / "displaced-private-contract"
                    contract_directory.rename(displaced)
                    contract_directory.mkdir(mode=0o700)
                    (contract_directory / "contract.json").write_text("{}\n", encoding="ascii")
                    os.chmod(contract_directory / "contract.json", 0o600)
                return result

            with mock.patch.object(self.module, "private_json_at", side_effect=replace_after_marker):
                with self.assertRaisesRegex(self.module.PreparationError, "identity"):
                    fixture.prepare()

    def test_success_machine_record_has_closed_shape_and_only_result_paths(self):
        contract = Path("/run/user/1000/private-contract/contract.json")
        output = Path("/srv/smapi-hard-state/smapi-hard-state-" + "a" * 32) / ("qualification-" + "b" * 32)
        with (
            mock.patch.object(self.module, "parse_arguments", return_value={}),
            mock.patch.object(self.module, "prepare", return_value=(contract, output)),
            mock.patch.object(self.module, "emit") as emit,
        ):
            self.assertEqual(self.module.main([]), 0)
        emit.assert_called_once_with({
            "contractPath": os.fspath(contract), "ok": True, "outputPath": os.fspath(output),
            "schemaVersion": 1, "status": "prepared",
        })

    def test_cli_rejects_unknown_arguments_with_one_sanitized_json_line(self):
        for arguments in (
            ["--unknown", "/private/fixture/token"],
            ["--game-marker", "/private/copied/Stardew Valley.dll"],
            ["--case-r", "/private/fixture/token"],
            ["--case-root", "/x"] * 8,
        ):
            with self.subTest(arguments=arguments):
                result = subprocess.run(
                    [sys.executable, os.fspath(HELPER), *arguments],
                    capture_output=True, text=True, timeout=5, check=False,
                )
                self.assertEqual(result.returncode, 2)
                self.assertEqual(result.stderr, "")
                lines = result.stdout.splitlines()
                self.assertEqual(len(lines), 1)
                self.assertEqual(json.loads(lines[0]), {
                    "code": "usage", "ok": False, "schemaVersion": 1, "status": "rejected",
                })
                self.assertNotIn("private", result.stdout)
                self.assertNotIn("token", result.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
