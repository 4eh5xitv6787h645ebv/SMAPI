#!/usr/bin/env python3
"""Synthetic tests for the candidate-package trusted-workload adapter."""

from __future__ import annotations

from contextlib import redirect_stderr, redirect_stdout
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

import qualify_candidate as candidate


class CandidateQualificationTests(unittest.TestCase):
    RELEASE_VERSION = "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.3"

    @staticmethod
    def write_candidate_archive(path: Path, release_version: str, *, bomb_bytes: int = 0) -> None:
        root = f"SMAPI {release_version} Linux installer"
        entries = {
            f"{root}/README.txt": b"readme",
            f"{root}/install on Linux.sh": b"#!/bin/sh\n",
            f"{root}/install on Linux (graphical).sh": b"#!/bin/sh\n",
            f"{root}/internal/linux/SMAPI.Installer": b"installer",
            f"{root}/internal/linux/SMAPI.Installer.Gui": b"gui",
            f"{root}/internal/linux/install.dat": b"payload",
        }
        with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, data in entries.items():
                archive.writestr(name, data)
            if bomb_bytes:
                archive.writestr(f"{root}/internal/linux/bomb", b"0" * bomb_bytes)

    def test_failure_aggregate_is_fixed_and_path_free(self) -> None:
        value = candidate.failure_aggregate("candidate.package")
        self.assertEqual({"schema": 1, "result": "failed", "code": "candidate.package"}, value)
        self.assertEqual(tuple(sorted(candidate.FAILURE_KEYS)), tuple(sorted(value)))

    def test_success_aggregate_has_only_allowlisted_values(self) -> None:
        sample = {
            "probe": {
                "steadySeconds": 180.25,
                "steadyUpdates": 4000,
                "steadyDraws": 500,
                "transitionUpdates": 300,
                "transitionDraws": 25,
                "header": {
                    "invalidWorldStateTicks": 0,
                    "locationChangedTicks": 0,
                    "positionChangedTicks": 0,
                    "bufferOverflow": False,
                },
            },
            "log": {
                "gameVersion": "1.6.15 build 24356",
                "loadedCodeMods": 10,
                "loadedContentPacks": 20,
                "skippedModCount": 1,
            },
        }
        value = candidate.success_aggregate(
            candidate_sha256="a" * 64,
            release_commit="b" * 40,
            release_version=self.RELEASE_VERSION,
            sample=sample,
        )
        self.assertEqual(tuple(sorted(candidate.SUCCESS_KEYS)), tuple(sorted(value)))
        self.assertTrue(value["installedSmapiAssembliesMatched"])
        serialized = json.dumps(value)
        for forbidden in ("/home/", "Mods/", "Saves/", "workloadIdentitySha256"):
            self.assertNotIn(forbidden, serialized)

    def test_candidate_copy_rejects_links_and_preserves_bytes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source.zip"
            source.write_bytes(b"candidate bytes")
            os.chmod(source, 0o600)
            digest = candidate.copy_candidate(source, root / "copy.zip")
            self.assertEqual(candidate.sha256(source), digest)
            self.assertEqual(source.read_bytes(), (root / "copy.zip").read_bytes())

            linked = root / "linked.zip"
            os.link(source, linked)
            with self.assertRaises(candidate.QualificationFailure) as failure:
                candidate.copy_candidate(source, root / "rejected.zip")
            self.assertEqual("candidate.identity", failure.exception.code)

    def test_candidate_copy_rejects_oversized_input_before_reading(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "oversized.zip"
            with source.open("wb") as stream:
                stream.truncate(candidate.MAX_CANDIDATE_BYTES + 1)
            with self.assertRaises(candidate.QualificationFailure) as failure:
                candidate.copy_candidate(source, root / "copy.zip")
            self.assertEqual("candidate.identity", failure.exception.code)

    def test_private_file_source_rejects_group_or_world_access(self) -> None:
        repo = Path(__file__).resolve().parents[2]
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "baseline.json"
            source.write_text("{}", encoding="utf-8")
            os.chmod(source, 0o644)
            with self.assertRaises(candidate.QualificationFailure) as failure:
                candidate.assert_private_source(source, repo, "baseline", directory=False)
            self.assertEqual("input.baseline", failure.exception.code)
            os.chmod(source, 0o600)
            self.assertEqual(source, candidate.assert_private_source(source, repo, "baseline", directory=False))

    def test_argument_errors_do_not_echo_private_values(self) -> None:
        stdout = io.StringIO()
        stderr = io.StringIO()
        with (
            redirect_stdout(stdout),
            redirect_stderr(stderr),
            self.assertRaises(candidate.QualificationFailure) as failure,
        ):
            candidate.parse_args(["--unknown", "/private/fixture-name"])
        self.assertEqual("arguments.invalid", failure.exception.code)
        self.assertEqual("", stdout.getvalue())
        self.assertEqual("", stderr.getvalue())

    def test_nested_payload_requires_exact_two_assemblies(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            internal = root / "internal/linux"
            internal.mkdir(parents=True)
            with zipfile.ZipFile(internal / "install.dat", "w") as archive:
                archive.writestr("StardewModdingAPI.dll", b"main")
                archive.writestr("StardewModdingAPI-net6.dll", b"net6")
            hashes = candidate.nested_payload_hashes(root)
            self.assertEqual(candidate.hashlib.sha256(b"main").hexdigest(), hashes["StardewModdingAPI.dll"])
            self.assertEqual(candidate.hashlib.sha256(b"net6").hexdigest(), hashes["StardewModdingAPI-net6.dll"])

            with zipfile.ZipFile(internal / "install.dat", "w") as archive:
                archive.writestr("StardewModdingAPI.dll", b"main")
            with self.assertRaises(candidate.QualificationFailure) as failure:
                candidate.nested_payload_hashes(root)
            self.assertEqual("candidate.payload", failure.exception.code)

    def test_safe_outer_extraction_rejects_traversal_and_links(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive_path = root / "candidate.zip"
            with zipfile.ZipFile(archive_path, "w") as archive:
                archive.writestr("Expected/internal/linux/file", b"ok")
            package = candidate.safe_extract_outer(archive_path, root / "out", "Expected")
            self.assertEqual(b"ok", (package / "internal/linux/file").read_bytes())

            traversal = root / "traversal.zip"
            with zipfile.ZipFile(traversal, "w") as archive:
                archive.writestr("Expected/../escape", b"bad")
            with self.assertRaises(candidate.QualificationFailure):
                candidate.safe_extract_outer(traversal, root / "bad-out", "Expected")

            linked = root / "link.zip"
            info = zipfile.ZipInfo("Expected/link")
            info.external_attr = (stat.S_IFLNK | 0o777) << 16
            with zipfile.ZipFile(linked, "w") as archive:
                archive.writestr(info, "target")
            with self.assertRaises(candidate.QualificationFailure):
                candidate.safe_extract_outer(linked, root / "link-out", "Expected")

    def test_candidate_archive_preflight_enforces_profile_and_bounds(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            valid = root / "valid.zip"
            self.write_candidate_archive(valid, self.RELEASE_VERSION)
            expected_root = f"SMAPI {self.RELEASE_VERSION} Linux installer"
            candidate.validate_candidate_archive(valid, expected_root)

            incomplete = root / "incomplete.zip"
            with zipfile.ZipFile(incomplete, "w") as archive:
                archive.writestr(f"{expected_root}/README.txt", b"readme")
            with self.assertRaises(candidate.QualificationFailure) as failure:
                candidate.validate_candidate_archive(incomplete, expected_root)
            self.assertEqual("candidate.profile", failure.exception.code)

            bomb = root / "bomb.zip"
            self.write_candidate_archive(bomb, self.RELEASE_VERSION, bomb_bytes=2 * 1024 * 1024)
            with self.assertRaises(candidate.QualificationFailure) as failure:
                candidate.validate_candidate_archive(bomb, expected_root)
            self.assertEqual("candidate.archive", failure.exception.code)

            with mock.patch.object(candidate, "MAX_ARCHIVE_ENTRIES", 1):
                with self.assertRaises(candidate.QualificationFailure):
                    candidate.validate_candidate_archive(valid, expected_root)

    def test_private_child_output_and_clone_errors_never_reach_public_streams(self) -> None:
        secret = "/private/sentinel-fixture-name"
        stdout = io.StringIO()
        stderr = io.StringIO()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            process_log = root / "process.log"
            with redirect_stdout(stdout), redirect_stderr(stderr):
                with candidate.private_process_output(process_log):
                    subprocess.run(
                        [
                            sys.executable,
                            "-c",
                            "import sys; print(sys.argv[1]); print(sys.argv[1], file=sys.stderr)",
                            secret,
                        ],
                        check=True,
                    )
                    print(secret)
                with self.assertRaises(candidate.QualificationFailure):
                    candidate.clone_private(Path(secret), root / "unused", root / "clone.log")
            self.assertNotIn(secret, stdout.getvalue() + stderr.getvalue())
            self.assertIn(secret, process_log.read_text(encoding="utf-8"))
            self.assertIn(secret, (root / "clone.log").read_text(encoding="utf-8"))
            self.assertEqual(0o600, stat.S_IMODE(process_log.stat().st_mode))
            self.assertEqual(0o600, stat.S_IMODE((root / "clone.log").stat().st_mode))

    def test_run_private_reaps_process_group_when_interrupted(self) -> None:
        process = mock.Mock(pid=12345)
        process.poll.return_value = None
        process.wait.side_effect = [KeyboardInterrupt(), 0]
        with tempfile.TemporaryDirectory() as temporary:
            with (
                mock.patch.object(candidate.subprocess, "Popen", return_value=process),
                mock.patch.object(candidate.os, "killpg") as killpg,
                self.assertRaises(KeyboardInterrupt),
            ):
                candidate.run_private(["unused"], Path(temporary) / "child.log", "child")
        killpg.assert_called_once_with(12345, candidate.signal.SIGTERM)
        self.assertEqual(2, process.wait.call_count)

    def test_main_turns_keyboard_interrupt_into_fixed_json(self) -> None:
        stdout = io.StringIO()
        stderr = io.StringIO()
        previous_umask = os.umask(0o077)
        os.umask(previous_umask)
        try:
            with (
                mock.patch.object(candidate, "parse_args", return_value=mock.Mock()),
                mock.patch.object(candidate, "qualify", side_effect=KeyboardInterrupt),
                redirect_stdout(stdout),
                redirect_stderr(stderr),
            ):
                self.assertEqual(130, candidate.main())
        finally:
            os.umask(previous_umask)
        self.assertEqual("", stdout.getvalue())
        self.assertEqual(
            '{"schema":1,"result":"failed","code":"interrupted"}\n',
            stderr.getvalue(),
        )

    def test_candidate_sample_always_stops_xvfb_on_interrupt(self) -> None:
        process = mock.Mock()
        arguments = mock.Mock(
            display=":97",
            cpu_list="1",
            timeout=900,
            max_busy_percent=35.0,
        )
        adapted = {"expectedLoadedCodeMods": 2, "expectedLoadedContentPacks": 3}
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            prepared = root / "prepared"
            prepared.mkdir()
            with (
                mock.patch.object(candidate, "start_xvfb", return_value=process),
                mock.patch.object(candidate, "stop_xvfb") as stop,
                mock.patch.object(candidate.run_ab, "environment_metadata", return_value={}),
                mock.patch.object(candidate.run_ab, "run_sample", side_effect=KeyboardInterrupt),
                self.assertRaises(KeyboardInterrupt),
            ):
                candidate.execute_candidate_sample(prepared, root, arguments, adapted)
        stop.assert_called_once_with(process)

    def test_xvfb_startup_interrupt_terminates_process_group(self) -> None:
        process = mock.Mock()
        process.poll.return_value = None
        with tempfile.TemporaryDirectory() as temporary:
            with (
                mock.patch.object(candidate.subprocess, "Popen", return_value=process),
                mock.patch.object(candidate.Path, "exists", return_value=False),
                mock.patch.object(candidate.time, "sleep", side_effect=KeyboardInterrupt),
                mock.patch.object(candidate, "terminate_process_group") as terminate,
                self.assertRaises(KeyboardInterrupt),
            ):
                candidate.start_xvfb(":4093", Path(temporary) / "xvfb.log")
        terminate.assert_called_once_with(process)

    def test_qualify_orchestrates_candidate_and_rechecks_immutable_inputs(self) -> None:
        events: list[str] = []
        commands: list[list[str]] = []
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            os.chmod(root, 0o700)
            prepared = root / "prepared-input"
            prepared.mkdir(mode=0o700)
            (prepared / "metadata.json").write_text("{}", encoding="utf-8")
            baseline = root / "baseline.json"
            modpack = root / "modpack.archive"
            save = root / "save.archive"
            for path, data in ((baseline, b"baseline"), (modpack, b"modpack"), (save, b"save")):
                path.write_bytes(data)
                os.chmod(path, 0o600)
            candidate_zip = root / "candidate.zip"
            candidate_zip.write_bytes(b"candidate")
            package_root = root / "synthetic-package"
            (package_root / "internal/linux").mkdir(parents=True)
            (package_root / "internal/linux/SMAPI.Installer").write_bytes(b"installer")
            output = root / "qualification"
            release_commit = "b" * 40
            arguments = candidate.argparse.Namespace(
                candidate_zip=str(candidate_zip),
                release_version=self.RELEASE_VERSION,
                release_commit=release_commit,
                prepared_root=str(prepared),
                workload_baseline=str(baseline),
                modpack_archive=str(modpack),
                save_archive=str(save),
                output_root=str(output),
                cpu_list="1",
                display=":97",
                timeout=900,
                max_busy_percent=35.0,
            )
            original_trees = {
                "game-a": {"sha256": "1"},
                "game-b": {"sha256": "2"},
                "mods": {"sha256": "3"},
                "saves": {"sha256": "4"},
            }
            metadata = {
                "modpackArchiveSha256": candidate.sha256(modpack),
                "saveArchiveSha256": candidate.sha256(save),
                "expectedLoadedCodeMods": 2,
                "expectedLoadedContentPacks": 3,
                "expectedSkippedMods": 0,
            }
            adapted = dict(metadata)
            adapted["products"] = {"b": {"commit": release_commit}}
            sample = {
                "probe": {
                    "steadySeconds": 180.0,
                    "steadyUpdates": 4000,
                    "steadyDraws": 500,
                    "transitionUpdates": 300,
                    "transitionDraws": 25,
                    "header": {
                        "invalidWorldStateTicks": 0,
                        "locationChangedTicks": 0,
                        "positionChangedTicks": 0,
                        "bufferOverflow": False,
                    },
                },
                "log": {
                    "smapiVersion": self.RELEASE_VERSION,
                    "gameVersion": "1.6.15 build 24356",
                    "loadedCodeMods": 2,
                    "loadedContentPacks": 3,
                    "skippedModCount": 0,
                },
            }

            def clone(_source: Path, destination: Path, _log: Path) -> None:
                events.append(f"clone:{destination.name}")
                destination.mkdir(parents=True)
                if destination.name == "game-b":
                    (destination / "StardewModdingAPI.dll").write_bytes(b"main")
                    (destination / "StardewModdingAPI-net6.dll").write_bytes(b"net6")

            def run_private(
                command: list[str],
                _log: Path,
                _code: str,
                **_kwargs: object,
            ) -> None:
                commands.append(command)
                events.append("package" if command[0].endswith("test-linux-release-package.sh") else "installer")

            def run_sample(
                candidate_prepared: Path,
                *_args: object,
            ) -> None:
                events.append("sample")
                run_root = candidate_prepared / "candidate-runs/01-b1"
                run_root.mkdir(parents=True)
                (run_root / "sample.json").write_text(json.dumps(sample), encoding="utf-8")

            tree_calls: list[str] = []

            def tree_manifest(path: Path) -> dict[str, str]:
                tree_calls.append(path.name)
                return original_trees[path.name]

            hash_calls: list[Path] = []
            real_sha256 = candidate.sha256

            def tracked_sha256(path: Path) -> str:
                hash_calls.append(path)
                return real_sha256(path)

            with (
                mock.patch.object(candidate, "sha256", side_effect=tracked_sha256),
                mock.patch.object(candidate, "copy_candidate", return_value="a" * 64),
                mock.patch.object(candidate, "audit_fixture", side_effect=lambda *_args: events.append("audit")),
                mock.patch.object(
                    candidate,
                    "validate_prepared_inputs",
                    return_value=(metadata, original_trees, "identity"),
                ),
                mock.patch.object(
                    candidate,
                    "validate_candidate_archive",
                    side_effect=lambda *_args: events.append("preflight"),
                ),
                mock.patch.object(candidate, "run_private", side_effect=run_private),
                mock.patch.object(candidate, "safe_extract_outer", return_value=package_root),
                mock.patch.object(
                    candidate,
                    "nested_payload_hashes",
                    return_value={
                        "StardewModdingAPI.dll": candidate.hashlib.sha256(b"main").hexdigest(),
                        "StardewModdingAPI-net6.dll": candidate.hashlib.sha256(b"net6").hexdigest(),
                    },
                ),
                mock.patch.object(candidate, "clone_private", side_effect=clone),
                mock.patch.object(candidate, "adapt_metadata", return_value=adapted),
                mock.patch.object(candidate, "start_xvfb", return_value=mock.Mock()),
                mock.patch.object(candidate, "stop_xvfb") as stop_xvfb,
                mock.patch.object(candidate.run_ab, "environment_metadata", return_value={}),
                mock.patch.object(candidate.run_ab, "run_sample", side_effect=run_sample),
                mock.patch.object(candidate.run_ab, "validate_saved_sample") as validate_sample,
                mock.patch.object(candidate.run_ab, "tree_manifest", side_effect=tree_manifest),
            ):
                result = candidate.qualify(arguments)

            self.assertTrue(result["installedSmapiAssembliesMatched"])
            self.assertEqual(2, events.count("audit"))
            self.assertLess(events.index("preflight"), events.index("package"))
            self.assertEqual(3, sum(event.startswith("clone:") for event in events))
            self.assertIn("installer", events)
            self.assertIn("sample", events)
            package_command = next(
                command
                for command in commands
                if command[0].endswith("test-linux-release-package.sh")
            )
            self.assertEqual(self.RELEASE_VERSION, package_command[-1])
            installer_command = next(command for command in commands if command[0].endswith("SMAPI.Installer"))
            self.assertEqual(
                ["--no-prompt", "--install", "--game-path"],
                installer_command[1:4],
            )
            self.assertEqual(output / "prepared/gold/game-b", Path(installer_command[4]))
            self.assertCountEqual(tree_calls, original_trees)
            for immutable in (prepared / "metadata.json", baseline, modpack, save):
                self.assertGreaterEqual(hash_calls.count(immutable), 2)
            stop_xvfb.assert_called_once()
            validate_sample.assert_called_once()

    def test_output_root_must_be_new_private_and_disjoint(self) -> None:
        repo = Path(__file__).resolve().parents[2]
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary)
            os.chmod(parent, 0o700)
            private_input = parent / "input"
            private_input.mkdir(mode=0o700)
            output = candidate.prepare_output_root(parent / "output", repo, (private_input,))
            self.assertTrue(output.is_dir())
            self.assertEqual(0o700, stat.S_IMODE(output.stat().st_mode))
            with self.assertRaises(candidate.QualificationFailure):
                candidate.prepare_output_root(private_input / "nested", repo, (private_input,))


if __name__ == "__main__":
    unittest.main()
