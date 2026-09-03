#!/usr/bin/env python3
"""Fixture-free tests for the deterministic E2 contact-sheet assembler."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / "build/scripts/assemble-linux-gui-e2-contact-sheet.py"
STAGER = ROOT / "build/scripts/stage-linux-gui-screenshot.py"
ORDER = ("permission", "read-only", "disk-full", "cross-device")


def load(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise AssertionError(f"can't import {name}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run(arguments: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["python3", str(TOOL), *arguments], text=True, capture_output=True, check=False)


def arguments(sources: dict[str, Path], output: Path) -> list[str]:
    result: list[str] = []
    for fault in ORDER:
        result.extend((f"--{fault}", str(sources[fault])))
    return [*result, "--output-directory", str(output)]


def main() -> None:
    assembler = load(TOOL, "e2_assembler")
    codec = load(STAGER, "png_codec")
    with tempfile.TemporaryDirectory(prefix="smapi-e2-contact-sheet.") as temporary:
        root = Path(temporary)
        root.chmod(0o700)
        sources: dict[str, Path] = {}
        colors = ((255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 255, 255), (255, 255, 0, 255))
        for fault, color in zip(ORDER, colors, strict=True):
            path = root / f"{fault}.png"
            path.write_bytes(assembler.encode(3, 2, bytes(color) * 6))
            path.chmod(0o600)
            sources[fault] = path
        output = root / "private-output"
        output.mkdir(mode=0o700)

        result = run(arguments(sources, output))
        if result.returncode != 0 or json.loads(result.stdout) != {
            "ok": True, "schemaVersion": 1, "sourceCount": 4, "status": "assembled-private"
        } or result.stderr:
            raise AssertionError(f"valid assembly failed: {result.stdout}{result.stderr}")
        png = output / "e2-filesystem-failures.png"
        sidecar = output / "e2-filesystem-failures.sources.json"
        if stat.S_IMODE(png.stat().st_mode) != 0o600 or stat.S_IMODE(sidecar.stat().st_mode) != 0o600:
            raise AssertionError("outputs are not private")
        width, height, color_type, pixels, chunks = codec.decode_png(png.read_bytes(), "contact sheet")
        if (width, height, color_type, chunks) != (54, 52, 6, ("IHDR", "IDAT", "IEND")):
            raise AssertionError("unexpected canonical sheet geometry")
        for index, color in enumerate(colors):
            column, row = index % 2, index // 2
            x = 16 + column * 19 + 1
            y = 16 + row * 18
            start = (y * width + x) * 4
            if tuple(pixels[start:start + 4]) != color:
                raise AssertionError(f"source order changed at {ORDER[index]}")
        record = json.loads(sidecar.read_text())
        if record["layout"]["order"] != list(ORDER) or [item["fault"] for item in record["sources"]] != list(ORDER):
            raise AssertionError("sidecar order is not exact")
        first_digest = hashlib.sha256(png.read_bytes()).hexdigest()

        second = root / "private-output-2"
        second.mkdir(mode=0o700)
        repeated = run(arguments(sources, second))
        if repeated.returncode != 0 or hashlib.sha256((second / png.name).read_bytes()).hexdigest() != first_digest:
            raise AssertionError("repeat output is not deterministic")

        overwrite = run(arguments(sources, output))
        if overwrite.returncode != 2 or json.loads(overwrite.stdout).get("code") != "output-exists":
            raise AssertionError("overwrite was not rejected")

        linked_output = root / "linked-output"
        linked_output.symlink_to(output, target_is_directory=True)
        linked = run(arguments(sources, linked_output))
        if linked.returncode != 2 or json.loads(linked.stdout).get("code") != "directory":
            raise AssertionError("linked output directory was not rejected")

        source_target = root / "source-target.png"
        source_target.write_bytes(sources["permission"].read_bytes())
        source_target.chmod(0o600)
        linked_source = root / "linked-source.png"
        linked_source.symlink_to(source_target.name)
        bad_sources = dict(sources)
        bad_sources["permission"] = linked_source
        third = root / "private-output-3"
        third.mkdir(mode=0o700)
        rejected = run(arguments(bad_sources, third))
        if rejected.returncode != 2 or json.loads(rejected.stdout).get("code") != "source":
            raise AssertionError("linked source was not rejected")

        raced_source = sources["permission"]
        real_open = assembler.os.open
        changed = False

        def raced_open(path, *open_arguments, **open_keywords):
            nonlocal changed
            if not changed and Path(path) == raced_source:
                changed = True
                raced_source.chmod(0o400)
            return real_open(path, *open_arguments, **open_keywords)

        assembler.os.open = raced_open
        try:
            try:
                assembler.read_private_png(raced_source)
            except assembler.ContactSheetError as error:
                if error.code != "source" or not changed:
                    raise AssertionError("metadata race produced the wrong rejection") from error
            else:
                raise AssertionError("metadata race was accepted")
        finally:
            assembler.os.open = real_open
            raced_source.chmod(0o600)

        cleanup_output = root / "cleanup-output"
        cleanup_output.mkdir(mode=0o700)
        replacement = cleanup_output / "e2-filesystem-failures.png"
        real_write_new = assembler.write_new
        writes = 0

        def fail_after_replacement(path: Path, data: bytes):
            nonlocal writes
            writes += 1
            if writes == 1:
                return real_write_new(path, data)
            replacement.unlink()
            replacement.write_bytes(b"same-user replacement")
            replacement.chmod(0o600)
            raise assembler.ContactSheetError("output")

        assembler.write_new = fail_after_replacement
        try:
            try:
                assembler.assemble(sources, cleanup_output, "e2-filesystem-failures.png")
            except assembler.ContactSheetError as error:
                if error.code != "output":
                    raise AssertionError("sidecar failure produced the wrong rejection") from error
            else:
                raise AssertionError("sidecar failure was accepted")
        finally:
            assembler.write_new = real_write_new
        if replacement.read_bytes() != b"same-user replacement":
            raise AssertionError("failure cleanup deleted a replaced output path")

        partial = cleanup_output / "partial-output.png"
        real_os_write = assembler.os.write
        partial_calls = 0

        def interrupted_write(descriptor: int, data: bytes) -> int:
            nonlocal partial_calls
            partial_calls += 1
            if partial_calls == 1:
                return real_os_write(descriptor, data[:1])
            return 0

        assembler.os.write = interrupted_write
        try:
            try:
                assembler.write_new(partial, b"private partial output")
            except assembler.ContactSheetError as error:
                if error.code != "output":
                    raise AssertionError("partial write produced the wrong rejection") from error
            else:
                raise AssertionError("partial write was accepted")
        finally:
            assembler.os.write = real_os_write
        if partial.exists():
            raise AssertionError("partial owned output survived failure cleanup")

    print("Linux GUI E2 contact-sheet tests passed.")


if __name__ == "__main__":
    main()
