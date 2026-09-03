#!/usr/bin/env python3
"""Synthetic tests for the Linux GUI screenshot capture staging tool."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat
import struct
import subprocess
import sys
import tempfile
from typing import Callable
from unittest import mock
import zlib


ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / "build/scripts/stage-linux-gui-screenshot.py"
VALIDATOR = ROOT / "build/scripts/validate-linux-gui-screenshot-evidence.py"
PRIVATE = "fixture-private-value"
IDENTITY = {
    "source_commit": "1" * 40,
    "source_tree": "2" * 40,
    "release_tag": "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3",
    "package_url": (
        "https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/"
        "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3/"
        "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.3-linux-x64-installer.zip"
    ),
    "package_sha256": "3" * 64,
    "public_release_url": (
        "https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/"
        "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3"
    ),
    "gui_binary_sha256": "4" * 64,
    "backend_binary_sha256": "5" * 64,
}


def png_chunk(kind: bytes, payload: bytes) -> bytes:
    checksum = zlib.crc32(payload, zlib.crc32(kind)) & 0xffffffff
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", checksum)


def make_png(
    *,
    color_type: int = 2,
    interlace: int = 0,
    critical: bytes | None = None,
    extra_decoded: bytes = b"",
) -> tuple[bytes, bytes]:
    width, height = 3, 2
    channels = 3 if color_type == 2 else 4
    pixels = bytes((index * 17 + 3) % 256 for index in range(width * height * channels))
    row_width = width * channels
    scanlines = b"".join(b"\0" + pixels[row * row_width:(row + 1) * row_width] for row in range(height))
    header = struct.pack(">IIBBBBB", width, height, 8, color_type, 0, 0, interlace)
    parts = [
        b"\x89PNG\r\n\x1a\n",
        png_chunk(b"IHDR", header),
        png_chunk(b"bKGD", b"\0\0\0\0\0\0"),
        png_chunk(b"tEXt", b"date:create\0metadata removed before staging"),
    ]
    if critical is not None:
        parts.append(png_chunk(critical, b"unsafe"))
    parts.extend((png_chunk(b"IDAT", zlib.compress(scanlines + extra_decoded)), png_chunk(b"IEND", b"")))
    return b"".join(parts), pixels


def load_validator():
    spec = importlib.util.spec_from_file_location("screenshot_validator", VALIDATOR)
    if spec is None or spec.loader is None:
        raise AssertionError("validator could not be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def prepare(root: Path, source_data: bytes | None = None) -> tuple[Path, Path, Path, Path]:
    stage = root / "stage"
    stage.mkdir(mode=0o700)
    source = root / "source.png"
    source.write_bytes(source_data if source_data is not None else make_png()[0])
    os.chmod(source, 0o600)
    identity = root / "identity.json"
    identity.write_text(json.dumps(IDENTITY), encoding="utf-8")
    private = root / "private.txt"
    private.write_text(PRIVATE + "\n", encoding="utf-8")
    os.chmod(private, 0o600)
    return stage, source, identity, private


def base_command(stage: Path, source: Path, identity: Path, private: Path) -> list[str]:
    return [
        sys.executable, str(TOOL), "--input", str(source),
        "--capture-tool", "ImageMagick import 7.1",
        "--capture-command", "import -window 42 png32:capture.png",
        "--stage-directory", str(stage), "--filename", "g2-error.png",
        "--evidence-id", "G2", "--evidence-class", "controlled_fixture",
        "--production-identity", str(identity), "--private-strings-file", str(private),
        "--fixture-or-injection", "Disposable typed filesystem refusal",
        "--operation", "Read-only inspection",
        "--durable-before", "Disposable game copy unchanged",
        "--durable-after", "Disposable game copy unchanged",
        "--qualification-reference", "docs/technical/linux-gui-screenshot-evidence.md#evidence-g2",
        "--distribution", "Example Linux 1", "--architecture", "x86_64",
        "--desktop-environment", "GNOME", "--session-type", "wayland",
        "--display-backend", "xwayland", "--display-scale-percent", "100",
        "--theme", "light", "--resolution", "1920x1080",
        "--avalonia", "12.1.1", "--dotnet-sdk", "10.0.108", "--dotnet-runtime", "10.0.11",
    ]


def replace(arguments: list[str], option: str, value: str) -> list[str]:
    result = list(arguments)
    result[result.index(option) + 1] = value
    return result


def run(arguments: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(arguments, check=False, capture_output=True, text=True)


def expect_failure(
    name: str,
    mutation: Callable[[Path, Path, Path, Path, list[str]], list[str]],
    expected: str,
) -> None:
    with tempfile.TemporaryDirectory(prefix="smapi-screenshot-stage-negative.") as temporary:
        stage, source, identity, private = prepare(Path(temporary))
        result = run(mutation(stage, source, identity, private, base_command(stage, source, identity, private)))
        if result.returncode == 0 or expected not in result.stdout + result.stderr:
            raise AssertionError(
                f"{name}: expected {expected!r}, got {result.returncode}:\n{result.stdout}{result.stderr}"
            )
        if list(stage.iterdir()):
            raise AssertionError(f"{name}: failed staging retained output")


def main() -> int:
    validator = load_validator()
    for color_type, filename, evidence_id in ((2, "g2-error.png", "G2"), (6, "g3-privacy.png", "G3")):
        with tempfile.TemporaryDirectory(prefix="smapi-screenshot-stage-valid.") as temporary:
            root = Path(temporary)
            source_data, source_pixels = make_png(color_type=color_type)
            stage, source, identity, private = prepare(root, source_data)
            arguments = replace(base_command(stage, source, identity, private), "--filename", filename)
            arguments = replace(arguments, "--evidence-id", evidence_id)
            result = run(arguments)
            if result.returncode != 0:
                raise AssertionError(f"valid color type {color_type} rejected:\n{result.stdout}{result.stderr}")
            png_path = stage / filename
            record_path = stage / f"{filename[:-4]}.capture.json"
            width, height, png_hash = validator.parse_png(png_path, (PRIVATE,))
            record = json.loads(record_path.read_text(encoding="utf-8"))
            if (width, height, png_hash) != (3, 2, record["capture"]["sha256"]):
                raise AssertionError("normalized dimensions or digest are inconsistent")
            if record["capture"]["decoded_pixel_sha256"] != hashlib.sha256(source_pixels).hexdigest():
                raise AssertionError("decoded pixels changed during normalization")
            if record["normalization"]["output_chunks"] != ["IHDR", "IDAT", "IEND"]:
                raise AssertionError("normalized PNG retained unexpected chunks")
            if not {"bKGD", "tEXt"}.issubset(record["normalization"]["input_chunks"]):
                raise AssertionError("removed metadata chunk types were not recorded")
            serialized = record_path.read_text(encoding="utf-8")
            if str(root) in serialized or PRIVATE in serialized or record["privacy_review"]["status"] != "pending":
                raise AssertionError("sidecar leaked private context or claimed an unperformed privacy review")
            if stat.S_IMODE(png_path.stat().st_mode) != 0o600 or stat.S_IMODE(record_path.stat().st_mode) != 0o600:
                raise AssertionError("staged outputs are not mode 0600")

    with tempfile.TemporaryDirectory(prefix="smapi-screenshot-stage-e2.") as temporary:
        root = Path(temporary)
        stage, source, identity, private = prepare(root)
        arguments = replace(base_command(stage, source, identity, private), "--evidence-id", "E2")
        arguments = replace(arguments, "--filename", "e2-disk-full.png")
        arguments.extend(("--fault", "disk-full"))
        result = run(arguments)
        if result.returncode != 0:
            raise AssertionError(f"valid E2 source staging failed:\n{result.stdout}{result.stderr}")
        record = json.loads((stage / "e2-disk-full.capture.json").read_text(encoding="utf-8"))
        if record.get("fault") != "disk-full":
            raise AssertionError("E2 sidecar did not retain its exact source fault")

    expect_failure(
        "real evidence downgraded",
        lambda _a, _b, _c, _d, args: replace(
            replace(args, "--evidence-id", "G1"),
            "--evidence-class",
            "controlled_fixture",
        ),
        "G1 requires real_qualification",
    )
    expect_failure(
        "private context",
        lambda _a, _b, _c, _d, args: replace(args, "--fixture-or-injection", f"contains {PRIVATE}"),
        "configured private string",
    )
    expect_failure(
        "absolute provenance path",
        lambda _a, _b, _c, _d, args: replace(
            args, "--capture-command", "import png32:/var/tmp/private-capture.png"
        ),
        "absolute path",
    )
    expect_failure(
        "E2 source missing fault",
        lambda _a, _b, _c, _d, args: replace(args, "--evidence-id", "E2"),
        "E2 staging requires one exact --fault value",
    )
    expect_failure(
        "fault on non-E2 source",
        lambda _a, _b, _c, _d, args: args + ["--fault", "permission"],
        "--fault is permitted only for E2 source staging",
    )

    def public_import(_stage: Path, source: Path, _identity: Path, _private: Path, args: list[str]) -> list[str]:
        os.chmod(source, 0o644)
        return args

    expect_failure("group-readable imported PNG", public_import, "exact mode 0600")

    def public_denylist(_stage: Path, _source: Path, _identity: Path, private: Path, args: list[str]) -> list[str]:
        os.chmod(private, 0o644)
        return args

    expect_failure("group-readable private denylist", public_denylist, "exact mode 0600")

    def interlace(_stage: Path, source: Path, _identity: Path, _private: Path, args: list[str]) -> list[str]:
        source.write_bytes(make_png(interlace=1)[0])
        return args

    expect_failure("interlaced input", interlace, "noninterlaced 8-bit RGB or RGBA")

    def critical(_stage: Path, source: Path, _identity: Path, _private: Path, args: list[str]) -> list[str]:
        source.write_bytes(make_png(critical=b"ABCD")[0])
        return args

    expect_failure("unknown critical chunk", critical, "unsupported critical chunk ABCD")

    def overlong_stream(
        _stage: Path,
        source: Path,
        _identity: Path,
        _private: Path,
        args: list[str],
    ) -> list[str]:
        source.write_bytes(make_png(extra_decoded=b"x")[0])
        return args

    expect_failure(
        "overlong decoded stream",
        overlong_stream,
        "invalid decoded scanline size or trailing compressed data",
    )

    def symlink(_stage: Path, source: Path, _identity: Path, _private: Path, args: list[str]) -> list[str]:
        target = source.with_name("target.png")
        source.rename(target)
        source.symlink_to(target.name)
        return args

    expect_failure("symlink input", symlink, "normalized non-symlink path")

    with tempfile.TemporaryDirectory(prefix="smapi-screenshot-stage-overwrite.") as temporary:
        stage, source, identity, private = prepare(Path(temporary))
        arguments = base_command(stage, source, identity, private)
        first, second = run(arguments), run(arguments)
        if first.returncode != 0 or second.returncode == 0 or "never overwrites" not in second.stderr:
            raise AssertionError(
                f"overwrite refusal failed:\n{first.stdout}{first.stderr}{second.stdout}{second.stderr}"
            )

    with tempfile.TemporaryDirectory(prefix="smapi-screenshot-stage-assets.") as temporary:
        root = Path(temporary)
        _stage, source, identity, private = prepare(root)
        arguments = replace(
            base_command(root, source, identity, private),
            "--stage-directory",
            str(ROOT / "docs/screenshots/linux-gui"),
        )
        result = run(arguments)
        if result.returncode == 0 or "outside the repository" not in result.stderr:
            raise AssertionError(f"repository staging refusal failed:\n{result.stdout}{result.stderr}")

    module_spec = importlib.util.spec_from_file_location("screenshot_stager", TOOL)
    if module_spec is None or module_spec.loader is None:
        raise AssertionError("staging tool could not be loaded")
    stager = importlib.util.module_from_spec(module_spec)
    module_spec.loader.exec_module(stager)
    title_output = (
        '_NET_WM_NAME(UTF8_STRING) = "SMAPI Linux Installer — Local diagnostics"\n'
        'WM_NAME(STRING) = "SMAPI Linux Installer legacy"\n'
    )
    if stager.parse_window_titles(title_output) != {
        "SMAPI Linux Installer — Local diagnostics", "SMAPI Linux Installer legacy"
    }:
        raise AssertionError("window-title parser did not retain exact property values")
    if "SMAPI" in stager.parse_window_titles(title_output):
        raise AssertionError("window-title parser accepted a substring as an exact title")

    try:
        stager.require_file_outside_repository(ROOT / "README.md", "imported PNG")
    except stager.StagingError as exc:
        if "outside the repository" not in str(exc):
            raise
    else:
        raise AssertionError("repository-hosted imported PNG was accepted")

    try:
        stager.require_file_outside_repository(ROOT / ".gitignore", "private-string file")
    except stager.StagingError as exc:
        if "outside the repository" not in str(exc):
            raise
    else:
        raise AssertionError("repository-hosted private-string file was accepted")

    with tempfile.TemporaryDirectory(prefix="smapi-screenshot-stage-executable.") as temporary:
        executable = Path(temporary) / "large-apphost"
        with executable.open("wb") as stream:
            stream.truncate(65 * 1024 * 1024)
        descriptor = os.open(executable, os.O_RDONLY | os.O_CLOEXEC)
        try:
            if len(stager.hash_executable_descriptor(descriptor)) != 64:
                raise AssertionError("executable larger than the PNG limit was not hashed")
        finally:
            os.close(descriptor)
        with executable.open("r+b") as stream:
            stream.truncate(stager.MAX_EXECUTABLE_BYTES + 1)
        descriptor = os.open(executable, os.O_RDONLY | os.O_CLOEXEC)
        try:
            try:
                stager.hash_executable_descriptor(descriptor)
            except stager.StagingError as exc:
                if "bounded regular file" not in str(exc):
                    raise
            else:
                raise AssertionError("executable over the 256 MiB bound was accepted")
        finally:
            os.close(descriptor)

    if len(stager.hash_process_executable(os.getpid())) != 64:
        raise AssertionError("current-user process ownership/executable hashing failed")

    capture_png = make_png()[0]
    exact_title = "SMAPI Linux Installer — Local diagnostics"
    expected_pid = 4321
    expected_hash = "a" * 64

    def fake_capture_run(command: list[str], **_kwargs):
        tool = Path(command[0]).name
        if tool == "xwininfo":
            return subprocess.CompletedProcess(command, 0, "Map State: IsViewable\n", "")
        if tool == "xprop" and "WM_NAME" in command:
            value = (
                f'_NET_WM_NAME(UTF8_STRING) = "{exact_title}"\n'
                f"_NET_WM_PID(CARDINAL) = {expected_pid}\n"
            )
            return subprocess.CompletedProcess(command, 0, value, "")
        if tool == "xprop":
            return subprocess.CompletedProcess(
                command, 0, f"_NET_WM_PID(CARDINAL) = {expected_pid}\n", ""
            )
        if tool == "import" and "-version" in command:
            return subprocess.CompletedProcess(command, 0, "Version: ImageMagick 7.1 fixture\n", "")
        if tool == "import":
            Path(command[-1].removeprefix("png32:")).write_bytes(capture_png)
            return subprocess.CompletedProcess(command, 0, b"", b"")
        raise AssertionError(f"unexpected mocked capture command: {command}")

    with (
        mock.patch.object(stager.shutil, "which", side_effect=lambda name: f"/mock/{name}"),
        mock.patch.object(stager.subprocess, "run", side_effect=fake_capture_run),
        mock.patch.object(stager, "hash_process_executable", side_effect=(expected_hash, expected_hash)) as hasher,
    ):
        data, tool, command = stager.capture_window("0x42", exact_title, expected_pid, expected_hash)
    if data != capture_png or "ImageMagick" not in tool or "0x42" not in command:
        raise AssertionError("successful direct capture did not preserve the selected app-window capture")
    if hasher.call_args_list != [mock.call(expected_pid), mock.call(expected_pid)]:
        raise AssertionError("direct capture did not bind the process executable before and after capture")

    with (
        mock.patch.object(stager.shutil, "which", side_effect=lambda name: f"/mock/{name}"),
        mock.patch.object(stager.subprocess, "run", side_effect=fake_capture_run),
        mock.patch.object(stager, "hash_process_executable", return_value="b" * 64),
    ):
        try:
            stager.capture_window("0x42", exact_title, expected_pid, expected_hash)
        except stager.StagingError as exc:
            if "does not match the reviewed GUI binary" not in str(exc):
                raise
        else:
            raise AssertionError("mismatched direct-capture executable hash was accepted")

    with tempfile.TemporaryDirectory(prefix="smapi-screenshot-stage-arguments.") as temporary:
        stage, source, identity, private = prepare(Path(temporary))
        secret = "/home/private-screenshot-secret"
        result = run(base_command(stage, source, identity, private) + ["--unknown", secret])
        if result.returncode == 0 or secret in result.stdout + result.stderr:
            raise AssertionError("argument rejection exposed caller-supplied private text")
        if (
            result.stdout
            or result.stderr != "Linux GUI screenshot staging failed: invalid command-line arguments\n"
        ):
            raise AssertionError("argument rejection did not return its fixed safe error")

    with tempfile.TemporaryDirectory(prefix="smapi-screenshot-stage-window.") as temporary:
        root = Path(temporary)
        stage, source, identity, private = prepare(root)
        arguments = base_command(stage, source, identity, private)
        index = arguments.index("--input")
        del arguments[index:index + 2]
        for option in ("--capture-tool", "--capture-command"):
            index = arguments.index(option)
            del arguments[index:index + 2]
        arguments.extend((
            "--window-id", "0",
            "--expected-window-title", exact_title,
            "--expected-window-pid", "1",
        ))
        result = run(arguments)
        if result.returncode == 0 or "positive decimal or hexadecimal" not in result.stderr:
            raise AssertionError(f"invalid window ID was not rejected:\n{result.stdout}{result.stderr}")

    print(
        "Linux GUI screenshot staging tests passed "
        "(2 valid formats, E2 provenance, direct-capture identity, executable bounds, "
        "and 18 fail-closed cases)."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
