#!/usr/bin/env python3
"""Verify and safely inventory or extract the pinned PR #158 runtime fixtures."""

from __future__ import annotations

import argparse
from contextlib import contextmanager
from dataclasses import asdict, dataclass
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
from typing import BinaryIO, Iterator, Optional


@dataclass(frozen=True)
class FixtureProfile:
    name: str
    filename: str
    compression: str
    compressed_bytes: int
    sha256: str
    max_entries: int
    max_file_bytes: int
    max_expanded_bytes: int
    max_path_depth: int
    expected_entries: Optional[int] = None
    expected_files: Optional[int] = None
    expected_directories: Optional[int] = None
    expected_expanded_bytes: Optional[int] = None
    expected_largest_file_bytes: Optional[int] = None


@dataclass(frozen=True)
class ArchiveInventory:
    profile: str
    archive: str
    sha256: str
    compressed_bytes: int
    entries: int
    regular_files: int
    directories: int
    expanded_bytes: int
    largest_file_bytes: int
    maximum_path_depth: int
    extracted: bool


PROFILES = {
    "save": FixtureProfile(
        name="save",
        filename="Blossom_389524656.tar.xz",
        compression="xz",
        compressed_bytes=1_291_524,
        sha256="6f707c73d02a05eed42adee0d9d9b434e92dff84a841e2f00047456921ae4bca",
        max_entries=16,
        max_file_bytes=100 * 1024 * 1024,
        max_expanded_bytes=128 * 1024 * 1024,
        max_path_depth=8,
        expected_entries=6,
        expected_files=4,
        expected_directories=2,
        expected_expanded_bytes=84_028_043,
        expected_largest_file_bytes=82_522_715,
    ),
    "modpack": FixtureProfile(
        name="modpack",
        filename="Mods-2026-08-26.tar.zst",
        compression="zstd",
        compressed_bytes=746_198_040,
        sha256="337d157bb2cf7283eaf6796c259a21d6e2e71baf134cb67a1f2a655e31cb312c",
        max_entries=26_000,
        max_file_bytes=32 * 1024 * 1024,
        max_expanded_bytes=1_342_177_280,  # 1.25 GiB
        max_path_depth=12,
        expected_entries=25_226,
        expected_files=21_984,
        expected_directories=3_242,
        expected_expanded_bytes=1_018_793_776,
        expected_largest_file_bytes=20_696_901,
    ),
}

PROJECTED_MOD_FIELDS = ("name", "id", "version", "contentPackFor", "isCodeMod")


class FixtureAuditError(ValueError):
    """A deterministic fixture-integrity or containment failure."""


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def verify_compressed_asset(path: Path, profile: FixtureProfile) -> str:
    if not path.is_file():
        raise FixtureAuditError(f"missing fixture asset: {path}")
    actual_size = path.stat().st_size
    if actual_size != profile.compressed_bytes:
        raise FixtureAuditError(
            f"compressed size mismatch for {profile.name}: expected {profile.compressed_bytes}, got {actual_size}"
        )
    actual_hash = _sha256(path)
    if actual_hash.lower() != profile.sha256.lower():
        raise FixtureAuditError(
            f"SHA-256 mismatch for {profile.name}: expected {profile.sha256}, got {actual_hash}"
        )
    return actual_hash


def _safe_parts(name: str, max_depth: int) -> tuple[str, ...]:
    if (
        not name
        or "\0" in name
        or "\\" in name
        or name.startswith("/")
        or (len(name) >= 2 and name[0].isalpha() and name[1] == ":")
    ):
        raise FixtureAuditError(f"unsafe archive path: {name!r}")
    normalized = name.rstrip("/")
    raw_parts = normalized.split("/")
    if not normalized or any(part in ("", ".", "..") for part in raw_parts):
        raise FixtureAuditError(f"unsafe archive path: {name!r}")
    pure = PurePosixPath(normalized)
    if pure.is_absolute() or tuple(pure.parts) != tuple(raw_parts):
        raise FixtureAuditError(f"unsafe archive path: {name!r}")
    if len(raw_parts) > max_depth:
        raise FixtureAuditError(f"archive path exceeds {max_depth} components: {name!r}")
    return tuple(raw_parts)


def _ensure_realized_beneath(root: Path, path: Path) -> None:
    root_real = root.resolve(strict=True)
    path_real = path.resolve(strict=True)
    if path_real != root_real and root_real not in path_real.parents:
        raise FixtureAuditError(f"realized extraction path escapes destination: {path}")


def _make_private_directory(path: Path, root: Path) -> None:
    relative = path.relative_to(root)
    current = root
    for part in relative.parts:
        current = current / part
        try:
            metadata = current.lstat()
        except FileNotFoundError:
            current.mkdir(mode=0o700)
            metadata = current.lstat()
        if stat.S_ISLNK(metadata.st_mode) or not stat.S_ISDIR(metadata.st_mode):
            raise FixtureAuditError(f"non-directory or symlink in extraction path: {current}")
        os.chmod(current, 0o700, follow_symlinks=False)
        _ensure_realized_beneath(root, current)


def _validate_member_type(member: tarfile.TarInfo) -> str:
    if member.sparse is not None or member.type == tarfile.GNUTYPE_SPARSE:
        raise FixtureAuditError(f"unsupported sparse archive entry: {member.name!r}")
    if member.isdir():
        return "directory"
    if member.isreg():
        return "file"
    if member.issym():
        kind = "symbolic link"
    elif member.islnk():
        kind = "hard link"
    elif member.ischr() or member.isblk():
        kind = "device"
    elif member.isfifo():
        kind = "FIFO"
    else:
        kind = "unsupported"
    raise FixtureAuditError(f"unsupported {kind} archive entry: {member.name!r}")


def _read_zstd_error(handle: BinaryIO) -> str:
    handle.seek(0)
    return handle.read(4096).decode("utf-8", errors="replace").strip()


@contextmanager
def _open_tar(path: Path, compression: str, zstd_command: str) -> Iterator[tarfile.TarFile]:
    if compression == "xz":
        try:
            with tarfile.open(path, mode="r:xz", errorlevel=2) as source:
                yield source
        except (lzma_error_types()) as exc:
            raise FixtureAuditError(f"malformed or unreadable xz/tar archive: {path.name}") from exc
        return
    if compression != "zstd":
        raise FixtureAuditError(f"unsupported fixture compression: {compression}")

    executable = shutil.which(zstd_command)
    if executable is None:
        raise FixtureAuditError(f"zstd command not found: {zstd_command}")
    with tempfile.TemporaryFile() as error_output:
        process = subprocess.Popen(
            [executable, "--decompress", "--stdout", "--no-progress", "--", os.fspath(path)],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=error_output,
            close_fds=True,
        )
        assert process.stdout is not None
        completed = False
        try:
            with tarfile.open(fileobj=process.stdout, mode="r|", errorlevel=2) as source:
                yield source
            while process.stdout.read(1024 * 1024):
                pass
            return_code = process.wait()
            completed = True
            if return_code != 0:
                detail = _read_zstd_error(error_output)
                raise FixtureAuditError(f"zstd decompression failed: {detail or f'exit {return_code}'}")
        except (tarfile.TarError, EOFError, OSError) as exc:
            raise FixtureAuditError(f"malformed or unreadable zstd/tar archive: {path.name}") from exc
        finally:
            process.stdout.close()
            if not completed and process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()


def lzma_error_types() -> tuple[type[BaseException], ...]:
    # tarfile wraps most decoder failures as ReadError, but direct LZMA errors can
    # escape on some supported Python versions.
    import lzma

    return (tarfile.TarError, EOFError, OSError, lzma.LZMAError)


def _copy_regular_file(source: tarfile.TarFile, member: tarfile.TarInfo, target: Path) -> None:
    extracted = source.extractfile(member)
    if extracted is None:
        raise FixtureAuditError(f"could not read regular archive file: {member.name!r}")
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    descriptor = os.open(target, flags, 0o600)
    written = 0
    try:
        os.fchmod(descriptor, 0o600)
        with extracted, os.fdopen(descriptor, "wb") as output:
            descriptor = -1
            while True:
                chunk = extracted.read(1024 * 1024)
                if not chunk:
                    break
                written += len(chunk)
                if written > member.size:
                    raise FixtureAuditError(f"expanded size exceeds header for {member.name!r}")
                output.write(chunk)
    finally:
        if descriptor >= 0:
            os.close(descriptor)
    if written != member.size or target.stat().st_size != member.size:
        raise FixtureAuditError(f"expanded size mismatch for {member.name!r}")


def _check_expected_inventory(profile: FixtureProfile, values: dict[str, int]) -> None:
    expected = {
        "entries": profile.expected_entries,
        "regular_files": profile.expected_files,
        "directories": profile.expected_directories,
        "expanded_bytes": profile.expected_expanded_bytes,
        "largest_file_bytes": profile.expected_largest_file_bytes,
    }
    for field, expected_value in expected.items():
        if expected_value is not None and values[field] != expected_value:
            raise FixtureAuditError(
                f"{profile.name} inventory mismatch for {field}: expected {expected_value}, got {values[field]}"
            )


def audit_archive(
    archive: Path,
    profile: FixtureProfile,
    destination: Optional[Path] = None,
    zstd_command: str = "zstd",
) -> ArchiveInventory:
    archive = archive.resolve(strict=False)
    actual_hash = verify_compressed_asset(archive, profile)
    root: Optional[Path] = None
    if destination is not None:
        destination = destination.resolve(strict=False)
        if destination.exists() or destination.is_symlink():
            raise FixtureAuditError(f"extraction destination already exists: {destination}")
        destination.mkdir(mode=0o700, parents=False)
        os.chmod(destination, 0o700, follow_symlinks=False)
        root = destination.resolve(strict=True)

    values = {
        "entries": 0,
        "regular_files": 0,
        "directories": 0,
        "expanded_bytes": 0,
        "largest_file_bytes": 0,
        "maximum_path_depth": 0,
    }
    seen: set[tuple[str, ...]] = set()
    required_directories: set[tuple[str, ...]] = set()
    try:
        try:
            with _open_tar(archive, profile.compression, zstd_command) as source:
                for member in source:
                    values["entries"] += 1
                    if values["entries"] > profile.max_entries:
                        raise FixtureAuditError(f"archive exceeds {profile.max_entries} entries")
                    parts = _safe_parts(member.name, profile.max_path_depth)
                    values["maximum_path_depth"] = max(values["maximum_path_depth"], len(parts))
                    if parts in seen:
                        raise FixtureAuditError(f"duplicate archive path: {member.name!r}")
                    if any(parts[:depth] in seen and parts[:depth] not in required_directories for depth in range(1, len(parts))):
                        raise FixtureAuditError(f"archive path descends through a regular file: {member.name!r}")
                    kind = _validate_member_type(member)
                    if kind == "file" and parts in required_directories:
                        raise FixtureAuditError(f"regular file conflicts with an existing archive directory: {member.name!r}")
                    seen.add(parts)
                    for depth in range(1, len(parts)):
                        required_directories.add(parts[:depth])

                    target = root.joinpath(*parts) if root is not None else None
                    if kind == "directory":
                        required_directories.add(parts)
                        values["directories"] += 1
                        if target is not None:
                            _make_private_directory(target, root)
                        continue

                    if member.size < 0 or member.size > profile.max_file_bytes:
                        raise FixtureAuditError(
                            f"archive file exceeds {profile.max_file_bytes} bytes: {member.name!r} ({member.size})"
                        )
                    values["expanded_bytes"] += member.size
                    if values["expanded_bytes"] > profile.max_expanded_bytes:
                        raise FixtureAuditError(f"archive exceeds {profile.max_expanded_bytes} expanded bytes")
                    values["largest_file_bytes"] = max(values["largest_file_bytes"], member.size)
                    values["regular_files"] += 1
                    if target is not None:
                        _make_private_directory(target.parent, root)
                        _copy_regular_file(source, member, target)
                        _ensure_realized_beneath(root, target)
        except FixtureAuditError:
            raise
        except (tarfile.TarError, EOFError, OSError) as exc:
            raise FixtureAuditError(f"malformed or unreadable archive: {archive.name}") from exc
        _check_expected_inventory(profile, values)
    except BaseException:
        if root is not None:
            if destination.is_symlink():
                destination.unlink(missing_ok=True)
            else:
                shutil.rmtree(destination, ignore_errors=True)
        raise

    return ArchiveInventory(
        profile=profile.name,
        archive=archive.name,
        sha256=actual_hash,
        compressed_bytes=profile.compressed_bytes,
        entries=values["entries"],
        regular_files=values["regular_files"],
        directories=values["directories"],
        expanded_bytes=values["expanded_bytes"],
        largest_file_bytes=values["largest_file_bytes"],
        maximum_path_depth=values["maximum_path_depth"],
        extracted=root is not None,
    )


def project_mods_json(source: Path) -> dict[str, object]:
    if not source.is_file():
        raise FixtureAuditError(f"missing mods.json: {source}")
    try:
        document = json.loads(source.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise FixtureAuditError(f"malformed mods.json: {source}") from exc
    if not isinstance(document, dict) or not isinstance(document.get("mods"), list):
        raise FixtureAuditError("mods.json must contain a top-level mods array")
    projected_mods = []
    for index, entry in enumerate(document["mods"]):
        if not isinstance(entry, dict):
            raise FixtureAuditError(f"mods.json entry {index} is not an object")
        projected_mods.append({field: entry[field] for field in PROJECTED_MOD_FIELDS if field in entry})
    result: dict[str, object] = {"mods": projected_mods}
    if isinstance(document.get("generated"), str):
        result["generated"] = document["generated"]
    return result


def _write_json(value: object, output: Optional[Path]) -> None:
    rendered = json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    if output is None:
        sys.stdout.write(rendered)
        return
    output.write_text(rendered, encoding="utf-8", newline="\n")


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    audit = commands.add_parser("audit", help="verify and inventory a pinned archive; extraction is opt-in")
    audit.add_argument("profile", choices=sorted(PROFILES))
    audit.add_argument("archive", type=Path)
    audit.add_argument("--extract", type=Path, metavar="NEW_DIRECTORY")
    audit.add_argument("--zstd-command", default="zstd", help="zstd executable name or absolute path")
    projection = commands.add_parser("project-mods", help="emit an allowlisted mods.json projection without folder paths")
    projection.add_argument("source", type=Path)
    projection.add_argument("--output", type=Path)
    return parser


def main(arguments: Optional[list[str]] = None) -> int:
    parser = _build_parser()
    args = parser.parse_args(arguments)
    try:
        if args.command == "audit":
            inventory = audit_archive(args.archive, PROFILES[args.profile], args.extract, args.zstd_command)
            _write_json(asdict(inventory), None)
        else:
            _write_json(project_mods_json(args.source), args.output)
        return 0
    except FixtureAuditError as exc:
        parser.exit(2, f"fixture audit failed: {exc}\n")


if __name__ == "__main__":
    main()
