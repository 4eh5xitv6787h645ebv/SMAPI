#!/usr/bin/env python3
"""Qualify one workflow-built Linux candidate with the prepared private workload.

This adapter deliberately has no fixture discovery or download behavior. Every private input is an
explicit caller argument, every runtime write stays below one new private output root, and stdout is
limited to the allowlisted aggregate assembled at the end of a successful run.
"""

from __future__ import annotations

import argparse
from contextlib import contextmanager, redirect_stderr, redirect_stdout
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import signal
import stat
import subprocess
import sys
import time
from typing import Any, NoReturn
import zipfile

import prepare
import run_ab


EXPECTED_OFFICIAL_COMMIT = "79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0"
MAX_CANDIDATE_BYTES = 512 * 1024 * 1024
MAX_ARCHIVE_ENTRIES = 20_000
MAX_ARCHIVE_ENTRY_BYTES = 512 * 1024 * 1024
MAX_ARCHIVE_EXPANDED_BYTES = 2 * 1024 * 1024 * 1024
MAX_ARCHIVE_COMPRESSION_RATIO = 100
SUCCESS_KEYS = (
    "candidateSha256",
    "diagnosticTrackingEnabled",
    "displaySession",
    "fixtureArchivesVerified",
    "gameVersion",
    "immutableSourceTreesVerified",
    "installedSmapiAssembliesMatched",
    "invalidWorldStateTicks",
    "loadedCodeMods",
    "loadedContentPacks",
    "locationChangedTicks",
    "positionChangedTicks",
    "probeBufferOverflow",
    "processExitCode",
    "releaseCommit",
    "releaseVersion",
    "result",
    "schema",
    "skippedItems",
    "steadyDraws",
    "steadySeconds",
    "steadyUpdates",
    "transitionDraws",
    "transitionUpdates",
    "transitionsCompleted",
    "workloadIdentityMatched",
)
FAILURE_KEYS = ("code", "result", "schema")


class QualificationFailure(Exception):
    """A failure represented to the caller by a fixed path-free code."""

    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


def fail(code: str) -> NoReturn:
    if re.fullmatch(r"[a-z0-9]+(?:\.[a-z0-9]+)*", code) is None:
        raise RuntimeError("qualification failure codes must be fixed lowercase identifiers")
    raise QualificationFailure(code)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def paths_overlap(left: Path, right: Path) -> bool:
    return left == right or left in right.parents or right in left.parents


def assert_private_source(path: Path, repo: Path, kind: str, *, directory: bool) -> Path:
    try:
        resolved = path.expanduser().resolve(strict=True)
        metadata = resolved.lstat()
        prepare.reject_protected_source(repo, resolved, kind)
    except (OSError, ValueError):
        fail(f"input.{kind}")
    if stat.S_ISLNK(metadata.st_mode) or metadata.st_uid != os.geteuid():
        fail(f"input.{kind}")
    if directory:
        if not stat.S_ISDIR(metadata.st_mode) or stat.S_IMODE(metadata.st_mode) != 0o700:
            fail(f"input.{kind}")
    elif (
        not stat.S_ISREG(metadata.st_mode)
        or metadata.st_nlink != 1
        or stat.S_IMODE(metadata.st_mode) & 0o077
    ):
        fail(f"input.{kind}")
    return resolved


def prepare_output_root(path: Path, repo: Path, private_inputs: tuple[Path, ...]) -> Path:
    try:
        candidate = prepare.private_target(repo, os.fspath(path))
        parent = candidate.parent.resolve(strict=True)
        parent_metadata = parent.lstat()
    except (OSError, ValueError):
        fail("output.boundary")
    if (
        stat.S_ISLNK(parent_metadata.st_mode)
        or not stat.S_ISDIR(parent_metadata.st_mode)
        or parent_metadata.st_uid != os.geteuid()
        or stat.S_IMODE(parent_metadata.st_mode) != 0o700
    ):
        fail("output.boundary")
    if any(paths_overlap(candidate, source) for source in private_inputs):
        fail("output.overlap")
    try:
        candidate.mkdir(mode=0o700, parents=False)
    except OSError:
        fail("output.create")
    return candidate


def copy_candidate(source: Path, destination: Path) -> str:
    flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(source, flags)
    except OSError:
        fail("candidate.open")
    digest = hashlib.sha256()
    try:
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode)
            or before.st_uid != os.geteuid()
            or before.st_nlink != 1
            or before.st_size <= 0
            or before.st_size > MAX_CANDIDATE_BYTES
        ):
            fail("candidate.identity")
        with os.fdopen(descriptor, "rb", closefd=False) as input_stream, destination.open("xb") as output_stream:
            os.chmod(destination, 0o600, follow_symlinks=False)
            for chunk in iter(lambda: input_stream.read(1024 * 1024), b""):
                digest.update(chunk)
                output_stream.write(chunk)
            output_stream.flush()
            os.fsync(output_stream.fileno())
        after = os.fstat(descriptor)
        identity = ("st_dev", "st_ino", "st_mode", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
        if any(getattr(before, key) != getattr(after, key) for key in identity):
            fail("candidate.changed")
    except OSError:
        fail("candidate.copy")
    finally:
        os.close(descriptor)
    if destination.stat().st_size != before.st_size or sha256(destination) != digest.hexdigest():
        fail("candidate.copy")
    return digest.hexdigest()


def terminate_process_group(process: subprocess.Popen[Any]) -> None:
    """Terminate and reap one child process group without emitting child diagnostics."""

    if process.poll() is not None:
        return
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=15)
    except subprocess.TimeoutExpired:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait()


def run_private(
    command: list[str],
    log_path: Path,
    code: str,
    *,
    environment: dict[str, str] | None = None,
    timeout: int | None = None,
) -> None:
    process: subprocess.Popen[bytes] | None = None
    with log_path.open("xb") as log:
        os.chmod(log_path, 0o600, follow_symlinks=False)
        try:
            try:
                process = subprocess.Popen(
                    command,
                    stdin=subprocess.DEVNULL,
                    stdout=log,
                    stderr=subprocess.STDOUT,
                    env=environment,
                    start_new_session=True,
                    close_fds=True,
                )
            except OSError:
                fail(f"{code}.start")
            try:
                status = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                terminate_process_group(process)
                fail(f"{code}.timeout")
        except BaseException:
            if process is not None:
                terminate_process_group(process)
            raise
    if status != 0:
        fail(code)


@contextmanager
def private_process_output(log_path: Path):
    """Route Python and inherited child-process output to one private file."""

    with log_path.open("xb", buffering=0) as binary_log:
        os.chmod(log_path, 0o600, follow_symlinks=False)
        saved_stdout = os.dup(1)
        saved_stderr = os.dup(2)
        text_descriptor = os.dup(binary_log.fileno())
        text_log = io.TextIOWrapper(os.fdopen(text_descriptor, "wb"), encoding="utf-8", write_through=True)
        try:
            sys.stdout.flush()
            sys.stderr.flush()
            os.dup2(binary_log.fileno(), 1)
            os.dup2(binary_log.fileno(), 2)
            with redirect_stdout(text_log), redirect_stderr(text_log):
                yield
        finally:
            text_log.flush()
            os.dup2(saved_stdout, 1)
            os.dup2(saved_stderr, 2)
            os.close(saved_stdout)
            os.close(saved_stderr)
            text_log.close()


def clone_private(source: Path, destination: Path, log_path: Path) -> None:
    run_private(
        ["cp", "--archive", "--reflink=auto", "--", os.fspath(source), os.fspath(destination)],
        log_path,
        "prepared.clone",
        timeout=300,
    )


def audit_fixture(
    repo: Path,
    kind: str,
    archive: Path,
    log: Path,
    environment: dict[str, str],
) -> None:
    run_private(
        [
            sys.executable,
            os.fspath(repo / "docs/technical/tools/fixture_archive_audit.py"),
            "audit",
            kind,
            os.fspath(archive),
        ],
        log,
        f"fixture.{kind}",
        environment=environment,
        timeout=300,
    )


def validate_prepared_inputs(
    repo: Path,
    prepared_root: Path,
    baseline: Path,
) -> tuple[dict[str, Any], dict[str, dict[str, Any]], str]:
    try:
        metadata = json.loads((prepared_root / "metadata.json").read_text(encoding="utf-8"))
        if metadata.get("schema") != 1 or metadata.get("officialCommit") != EXPECTED_OFFICIAL_COMMIT:
            fail("prepared.metadata")
        expected_scripts = {
            repo / "benchmarks/linux-real-world/prepare.py": metadata.get("prepareScriptSha256"),
            repo / "benchmarks/linux-real-world/run_ab.py": metadata.get("runnerScriptSha256"),
            repo / "benchmarks/linux-real-world/harness_common.py": metadata.get("commonScriptSha256"),
        }
        if any(
            not isinstance(expected, str) or sha256(path) != expected
            for path, expected in expected_scripts.items()
        ):
            fail("prepared.harness")
        expected_trees = {
            "game-a": metadata["products"]["a"]["gameTree"],
            "game-b": metadata["products"]["b"]["gameTree"],
            "mods": metadata["modsTree"],
            "saves": metadata["savesTree"],
        }
        actual_trees = {name: run_ab.tree_manifest(prepared_root / "gold" / name) for name in expected_trees}
        if actual_trees != expected_trees:
            fail("prepared.trees")
        run_ab.validate_runtime_probe_files(prepared_root / "gold/mods/SMAPI.BenchmarkProbe", metadata)
        workload_identity = run_ab.load_workload_baseline(baseline)
        prepared_baseline = prepared_root / "preflight-workload-identity.json"
        if prepared_baseline.is_file() and run_ab.load_workload_baseline(prepared_baseline) != workload_identity:
            fail("prepared.baseline")
    except QualificationFailure:
        raise
    except (OSError, KeyError, TypeError, ValueError, json.JSONDecodeError):
        fail("prepared.invalid")
    return metadata, expected_trees, workload_identity


def validate_candidate_archive(candidate: Path, expected_root: str) -> None:
    """Bound and validate the outer package before invoking any shell-based checker."""

    try:
        size = candidate.stat().st_size
        if size <= 0 or size > MAX_CANDIDATE_BYTES:
            fail("candidate.archive")
        with zipfile.ZipFile(candidate) as archive:
            infos = archive.infolist()
            if not infos or len(infos) > MAX_ARCHIVE_ENTRIES:
                fail("candidate.archive")
            names: set[str] = set()
            regular_names: set[str] = set()
            expanded = 0
            compressed = 0
            for info in infos:
                name = info.filename.rstrip("/")
                parts = PurePosixPath(name).parts
                mode = (info.external_attr >> 16) & 0xFFFF
                kind = stat.S_IFMT(mode)
                is_directory = info.is_dir() or info.filename.endswith("/")
                expected_kinds = (0, stat.S_IFDIR) if is_directory else (0, stat.S_IFREG)
                if (
                    not name
                    or len(info.filename.encode("utf-8")) > 4096
                    or info.filename.startswith("/")
                    or "\\" in info.filename
                    or any(part in ("", ".", "..") for part in parts)
                    or parts[0] != expected_root
                    or name in names
                    or kind not in expected_kinds
                    or info.flag_bits & 1
                    or info.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED)
                ):
                    fail("candidate.archive")
                names.add(name)
                if not is_directory:
                    regular_names.add(name)
                expanded += info.file_size
                compressed += info.compress_size
                if (
                    info.file_size < 0
                    or info.compress_size < 0
                    or info.file_size > MAX_ARCHIVE_ENTRY_BYTES
                    or expanded > MAX_ARCHIVE_EXPANDED_BYTES
                ):
                    fail("candidate.archive")
            if expanded > max(compressed, 1) * MAX_ARCHIVE_COMPRESSION_RATIO:
                fail("candidate.archive")
            required = {
                f"{expected_root}/README.txt",
                f"{expected_root}/install on Linux.sh",
                f"{expected_root}/install on Linux (graphical).sh",
                f"{expected_root}/internal/linux/SMAPI.Installer",
                f"{expected_root}/internal/linux/SMAPI.Installer.Gui",
                f"{expected_root}/internal/linux/install.dat",
            }
            if not required.issubset(regular_names):
                fail("candidate.profile")
    except QualificationFailure:
        raise
    except (OSError, UnicodeError, ValueError, zipfile.BadZipFile):
        fail("candidate.archive")


def safe_extract_outer(candidate: Path, destination: Path, expected_root: str) -> Path:
    try:
        with zipfile.ZipFile(candidate) as archive:
            infos = archive.infolist()
            if not infos or len(infos) > 20_000:
                fail("candidate.archive")
            names: set[str] = set()
            expanded = 0
            for info in infos:
                name = info.filename.rstrip("/")
                parts = PurePosixPath(name).parts
                mode = (info.external_attr >> 16) & 0xFFFF
                kind = stat.S_IFMT(mode)
                if (
                    not name
                    or info.filename.startswith("/")
                    or "\\" in info.filename
                    or any(part in ("", ".", "..") for part in parts)
                    or parts[0] != expected_root
                    or name in names
                    or kind not in (0, stat.S_IFREG, stat.S_IFDIR)
                ):
                    fail("candidate.archive")
                names.add(name)
                expanded += info.file_size
                if info.file_size > 512 * 1024 * 1024 or expanded > 2 * 1024 * 1024 * 1024:
                    fail("candidate.archive")
            for info in infos:
                relative = PurePosixPath(info.filename.rstrip("/"))
                target = destination.joinpath(*relative.parts)
                if info.is_dir() or info.filename.endswith("/"):
                    target.mkdir(mode=0o700, parents=True, exist_ok=True)
                    continue
                target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                with archive.open(info) as source, target.open("xb") as output:
                    shutil.copyfileobj(source, output, length=1024 * 1024)
                source_mode = (info.external_attr >> 16) & 0o777
                os.chmod(target, 0o700 if source_mode & 0o111 else 0o600, follow_symlinks=False)
    except QualificationFailure:
        raise
    except (OSError, ValueError, zipfile.BadZipFile):
        fail("candidate.archive")
    return destination / expected_root


def nested_payload_hashes(package_root: Path) -> dict[str, str]:
    try:
        with zipfile.ZipFile(package_root / "internal/linux/install.dat") as payload:
            required = ("StardewModdingAPI.dll", "StardewModdingAPI-net6.dll")
            if any(payload.namelist().count(name) != 1 for name in required):
                fail("candidate.payload")
            return {name: hashlib.sha256(payload.read(name)).hexdigest() for name in required}
    except QualificationFailure:
        raise
    except (OSError, KeyError, zipfile.BadZipFile):
        fail("candidate.payload")


def isolated_environment(root: Path) -> dict[str, str]:
    home = root / "home"
    config = root / "xdg-config"
    data = root / "xdg-data"
    cache = root / "xdg-cache"
    runtime = root / "xdg-runtime"
    temp = root / "tmp"
    for path in (home, config, data, cache, runtime, temp):
        path.mkdir(mode=0o700, parents=True, exist_ok=False)
    return {
        "PATH": "/usr/bin:/bin",
        "LANG": "C.UTF-8",
        "LC_ALL": "C.UTF-8",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1",
        "HOME": os.fspath(home),
        "XDG_CONFIG_HOME": os.fspath(config),
        "XDG_DATA_HOME": os.fspath(data),
        "XDG_CACHE_HOME": os.fspath(cache),
        "XDG_RUNTIME_DIR": os.fspath(runtime),
        "TMPDIR": os.fspath(temp),
    }


def adapt_metadata(metadata: dict[str, Any], game: Path, release_commit: str) -> dict[str, Any]:
    adapted = json.loads(json.dumps(metadata))
    adapted["forkCommit"] = release_commit
    adapted["products"]["b"]["commit"] = release_commit
    adapted["products"]["b"]["gameTree"] = run_ab.tree_manifest(game)
    adapted["products"]["b"]["smapiAssemblySha256"] = sha256(game / "StardewModdingAPI.dll")
    adapted["commonLauncherSha256"] = sha256(game / "StardewModdingAPI")
    adapted["commonDepsSha256"] = sha256(game / "StardewModdingAPI.deps.json")
    return adapted


def start_xvfb(display: str, log_path: Path) -> subprocess.Popen[bytes]:
    if re.fullmatch(r":[1-9][0-9]{0,3}", display) is None:
        fail("display.invalid")
    socket = Path(f"/tmp/.X11-unix/X{display[1:]}")
    if socket.exists():
        fail("display.inuse")
    log = log_path.open("xb")
    os.chmod(log_path, 0o600, follow_symlinks=False)
    try:
        process = subprocess.Popen(
            ["Xvfb", display, "-screen", "0", f"{run_ab.EXPECTED_RESOLUTION}x24", "-nolisten", "tcp"],
            stdin=subprocess.DEVNULL,
            stdout=log,
            stderr=subprocess.STDOUT,
            start_new_session=True,
        )
    except OSError:
        log.close()
        fail("display.start")
    log.close()
    try:
        for _ in range(100):
            if socket.exists():
                return process
            if process.poll() is not None:
                fail("display.start")
            time.sleep(0.05)
        fail("display.start")
    except BaseException:
        terminate_process_group(process)
        raise


def stop_xvfb(process: subprocess.Popen[bytes]) -> None:
    terminate_process_group(process)


def execute_candidate_sample(
    candidate_prepared: Path,
    output_root: Path,
    args: argparse.Namespace,
    adapted: dict[str, Any],
) -> None:
    display = args.display
    xvfb = start_xvfb(display, output_root / "xvfb.log")
    try:
        environment_path = candidate_prepared / "environment.json"
        environment_path.write_text(
            json.dumps(run_ab.environment_metadata(args.cpu_list, display), indent=2) + "\n",
            encoding="utf-8",
        )
        os.chmod(environment_path, 0o600)
        with private_process_output(output_root / "driver.log"):
            run_ab.run_sample(
                candidate_prepared,
                1,
                "b",
                1,
                False,
                display,
                args.cpu_list,
                args.timeout,
                args.max_busy_percent,
                adapted["expectedLoadedCodeMods"],
                adapted["expectedLoadedContentPacks"],
                "candidate-runs",
                "candidate",
            )
    finally:
        stop_xvfb(xvfb)


def success_aggregate(
    *,
    candidate_sha256: str,
    release_commit: str,
    release_version: str,
    sample: dict[str, Any],
) -> dict[str, Any]:
    probe = sample["probe"]
    log = sample["log"]
    header = probe["header"]
    result = {
        "schema": 1,
        "result": "passed",
        "releaseCommit": release_commit,
        "releaseVersion": release_version,
        "candidateSha256": candidate_sha256,
        "installedSmapiAssembliesMatched": True,
        "workloadIdentityMatched": True,
        "gameVersion": log["gameVersion"],
        "loadedCodeMods": log["loadedCodeMods"],
        "loadedContentPacks": log["loadedContentPacks"],
        "skippedItems": log["skippedModCount"],
        "steadySeconds": probe["steadySeconds"],
        "steadyUpdates": probe["steadyUpdates"],
        "steadyDraws": probe["steadyDraws"],
        "transitionUpdates": probe["transitionUpdates"],
        "transitionDraws": probe["transitionDraws"],
        "transitionsCompleted": True,
        "invalidWorldStateTicks": header["invalidWorldStateTicks"],
        "locationChangedTicks": header["locationChangedTicks"],
        "positionChangedTicks": header["positionChangedTicks"],
        "probeBufferOverflow": header["bufferOverflow"],
        "processExitCode": 0,
        "immutableSourceTreesVerified": 4,
        "fixtureArchivesVerified": 2,
        "diagnosticTrackingEnabled": False,
        "displaySession": "x11-xvfb",
    }
    if tuple(sorted(result)) != tuple(sorted(SUCCESS_KEYS)):
        raise RuntimeError("sanitized success aggregate schema drift")
    return result


def failure_aggregate(code: str) -> dict[str, Any]:
    result = {"schema": 1, "result": "failed", "code": code}
    if tuple(sorted(result)) != tuple(sorted(FAILURE_KEYS)):
        raise RuntimeError("sanitized failure aggregate schema drift")
    return result


class PrivateArgumentParser(argparse.ArgumentParser):
    """Avoid echoing caller-supplied private argument values on parse errors."""

    def error(self, message: str) -> NoReturn:
        fail("arguments.invalid")


def qualify(args: argparse.Namespace) -> dict[str, Any]:
    if os.geteuid() == 0:
        fail("user.root")
    repo = Path(__file__).resolve().parents[2]
    if re.fullmatch(r"[0-9a-f]{40}", args.release_commit) is None:
        fail("release.commit")
    version_pattern = r"[0-9]+\.[0-9]+\.[0-9]+-unofficial\.4eh5xitv6787h645ebv\.linux\.alpha\.[1-9][0-9]*"
    if re.fullmatch(version_pattern, args.release_version) is None:
        fail("release.version")
    if args.timeout < 420 or not 0 < args.max_busy_percent <= 100:
        fail("runtime.arguments")

    prepared_root = assert_private_source(Path(args.prepared_root), repo, "prepared", directory=True)
    baseline = assert_private_source(Path(args.workload_baseline), repo, "baseline", directory=False)
    modpack_archive = assert_private_source(Path(args.modpack_archive), repo, "modpack", directory=False)
    save_archive = assert_private_source(Path(args.save_archive), repo, "save", directory=False)
    candidate_input = Path(args.candidate_zip).expanduser().resolve(strict=True)
    output_root = prepare_output_root(
        Path(args.output_root),
        repo,
        (prepared_root, baseline, modpack_archive, save_archive, candidate_input),
    )
    candidate = output_root / "candidate.zip"
    candidate_sha = copy_candidate(candidate_input, candidate)

    validation_environment = isolated_environment(output_root / "validation-state")
    audit_fixture(
        repo,
        "modpack",
        modpack_archive,
        output_root / "modpack-audit.log",
        validation_environment,
    )
    audit_fixture(
        repo,
        "save",
        save_archive,
        output_root / "save-audit.log",
        validation_environment,
    )
    metadata, original_trees, _ = validate_prepared_inputs(repo, prepared_root, baseline)
    immutable_file_hashes = {
        prepared_root / "metadata.json": sha256(prepared_root / "metadata.json"),
        baseline: sha256(baseline),
        modpack_archive: sha256(modpack_archive),
        save_archive: sha256(save_archive),
    }

    expected_package_root = f"SMAPI {args.release_version} Linux installer"
    validate_candidate_archive(candidate, expected_package_root)
    run_private(
        [os.fspath(repo / "build/scripts/test-linux-release-package.sh"), os.fspath(candidate), args.release_version],
        output_root / "package-check.log",
        "candidate.package",
        environment=validation_environment,
        timeout=300,
    )
    package_root = safe_extract_outer(
        candidate,
        output_root / "package",
        expected_package_root,
    )
    payload_hashes = nested_payload_hashes(package_root)

    candidate_prepared = output_root / "prepared"
    (candidate_prepared / "gold").mkdir(mode=0o700, parents=True)
    clone_private(
        prepared_root / "gold/game-a",
        candidate_prepared / "gold/game-b",
        output_root / "clone-game.log",
    )
    clone_private(
        prepared_root / "gold/mods",
        candidate_prepared / "gold/mods",
        output_root / "clone-mods.log",
    )
    clone_private(
        prepared_root / "gold/saves",
        candidate_prepared / "gold/saves",
        output_root / "clone-saves.log",
    )
    environment = isolated_environment(output_root / "installer-state")
    installer = package_root / "internal/linux/SMAPI.Installer"
    run_private(
        [
            os.fspath(installer),
            "--no-prompt",
            "--install",
            "--game-path",
            os.fspath(candidate_prepared / "gold/game-b"),
        ],
        output_root / "installer-console.log",
        "candidate.install",
        environment=environment,
        timeout=180,
    )
    game = candidate_prepared / "gold/game-b"
    if any(sha256(game / name) != expected for name, expected in payload_hashes.items()):
        fail("candidate.payloadmatch")
    adapted = adapt_metadata(metadata, game, args.release_commit)
    (candidate_prepared / "metadata.json").write_text(json.dumps(adapted, indent=2) + "\n", encoding="utf-8")
    os.chmod(candidate_prepared / "metadata.json", 0o600)
    shutil.copyfile(baseline, candidate_prepared / "preflight-workload-identity.json")
    os.chmod(candidate_prepared / "preflight-workload-identity.json", 0o600)

    try:
        execute_candidate_sample(candidate_prepared, output_root, args, adapted)
    except QualificationFailure:
        raise
    except (OSError, subprocess.CalledProcessError, ValueError):
        fail("workload.run")

    run_root = candidate_prepared / "candidate-runs/01-b1"
    try:
        run_ab.validate_saved_sample(run_root, adapted, "01-b1", 1, "b", 1, False, "candidate")
        sample = json.loads((run_root / "sample.json").read_text(encoding="utf-8"))
    except (OSError, KeyError, TypeError, ValueError, json.JSONDecodeError):
        fail("workload.acceptance")
    if sample["log"]["smapiVersion"] != args.release_version:
        fail("workload.version")
    final_trees = {
        name: run_ab.tree_manifest(prepared_root / "gold" / name)
        for name in original_trees
    }
    if final_trees != original_trees:
        fail("prepared.changed")
    if any(sha256(path) != expected for path, expected in immutable_file_hashes.items()):
        fail("input.changed")
    if (
        sha256(modpack_archive) != metadata["modpackArchiveSha256"]
        or sha256(save_archive) != metadata["saveArchiveSha256"]
    ):
        fail("fixture.changed")
    return success_aggregate(
        candidate_sha256=candidate_sha,
        release_commit=args.release_commit,
        release_version=args.release_version,
        sample=sample,
    )


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = PrivateArgumentParser(description="Qualify one workflow candidate with explicit private prepared inputs.")
    parser.add_argument("--candidate-zip", required=True)
    parser.add_argument("--release-version", required=True)
    parser.add_argument("--release-commit", required=True)
    parser.add_argument("--prepared-root", required=True)
    parser.add_argument("--workload-baseline", required=True)
    parser.add_argument("--modpack-archive", required=True)
    parser.add_argument("--save-archive", required=True)
    parser.add_argument(
        "--output-root",
        required=True,
        help="New child of an existing current-user mode-0700 directory.",
    )
    parser.add_argument("--cpu-list", required=True)
    parser.add_argument("--display", default=":97")
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--max-busy-percent", type=float, default=100.0)
    return parser.parse_args(argv)


def main() -> int:
    os.umask(0o077)
    try:
        result = qualify(parse_args())
    except QualificationFailure as error:
        print(json.dumps(failure_aggregate(error.code), separators=(",", ":")), file=sys.stderr)
        return 1
    except BaseException as error:
        if isinstance(error, SystemExit) and error.code == 0:
            raise
        code = "interrupted" if isinstance(error, KeyboardInterrupt) else "unexpected"
        print(json.dumps(failure_aggregate(code), separators=(",", ":")), file=sys.stderr)
        return 130 if code == "interrupted" else 1
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
