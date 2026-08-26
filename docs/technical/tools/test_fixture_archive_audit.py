from __future__ import annotations

import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tarfile
import tempfile
import unittest

import fixture_archive_audit as audit


class FixtureArchiveAuditTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def _write_tar(self, members: list[tarfile.TarInfo], payloads: dict[str, bytes] | None = None) -> Path:
        path = self.root / "fixture.tar.xz"
        payloads = payloads or {}
        with tarfile.open(path, "w:xz") as archive:
            for member in members:
                payload = payloads.get(member.name)
                archive.addfile(member, io.BytesIO(payload) if payload is not None else None)
        return path

    def _file(self, name: str, contents: bytes = b"data", mode: int = 0o777) -> tuple[tarfile.TarInfo, bytes]:
        member = tarfile.TarInfo(name)
        member.size = len(contents)
        member.mode = mode
        member.uid = 12345
        member.gid = 54321
        return member, contents

    def _profile(self, path: Path, **changes: object) -> audit.FixtureProfile:
        values = {
            "name": "test",
            "filename": path.name,
            "compression": "xz",
            "compressed_bytes": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "max_entries": 20,
            "max_file_bytes": 1024,
            "max_expanded_bytes": 4096,
            "max_path_depth": 5,
        }
        values.update(changes)
        return audit.FixtureProfile(**values)

    def test_missing_asset_is_rejected(self) -> None:
        missing = self.root / "missing.tar.xz"
        profile = audit.FixtureProfile("test", missing.name, "xz", 1, "00" * 32, 1, 1, 1, 1)
        with self.assertRaisesRegex(audit.FixtureAuditError, "missing fixture asset"):
            audit.audit_archive(missing, profile)

    def test_compressed_size_and_hash_mismatches_are_rejected(self) -> None:
        member, contents = self._file("file")
        path = self._write_tar([member], {member.name: contents})
        with self.subTest("size"):
            with self.assertRaisesRegex(audit.FixtureAuditError, "compressed size mismatch"):
                audit.audit_archive(path, self._profile(path, compressed_bytes=path.stat().st_size + 1))
        with self.subTest("hash"):
            with self.assertRaisesRegex(audit.FixtureAuditError, "SHA-256 mismatch"):
                audit.audit_archive(path, self._profile(path, sha256="00" * 32))

    def test_malformed_archive_is_rejected_after_integrity_check(self) -> None:
        path = self.root / "malformed.tar.xz"
        path.write_bytes(b"not an xz tar")
        with self.assertRaisesRegex(audit.FixtureAuditError, "malformed or unreadable"):
            audit.audit_archive(path, self._profile(path))

    def test_entry_file_expanded_and_depth_limits_are_rejected(self) -> None:
        first, first_data = self._file("a", b"1234")
        second, second_data = self._file("b", b"5678")
        path = self._write_tar([first, second], {"a": first_data, "b": second_data})
        cases = (
            ("entries", {"max_entries": 1}, "entries"),
            ("file", {"max_file_bytes": 3}, "file exceeds"),
            ("expanded", {"max_expanded_bytes": 7}, "expanded bytes"),
            ("depth", {"max_path_depth": 1}, None),
        )
        for label, changes, message in cases[:3]:
            with self.subTest(label):
                with self.assertRaisesRegex(audit.FixtureAuditError, message):
                    audit.audit_archive(path, self._profile(path, **changes))
        deep, deep_data = self._file("a/b/c", b"x")
        deep_path = self._write_tar([deep], {deep.name: deep_data})
        with self.subTest("depth"):
            with self.assertRaisesRegex(audit.FixtureAuditError, "path exceeds"):
                audit.audit_archive(deep_path, self._profile(deep_path, max_path_depth=2))

    def test_absolute_and_parent_traversal_paths_are_rejected(self) -> None:
        for unsafe_name in ("/absolute", "../escape", "safe/../../escape", "C:\\private\\file", "\\\\server\\share"):
            with self.subTest(unsafe_name):
                member, contents = self._file(unsafe_name)
                path = self._write_tar([member], {member.name: contents})
                with self.assertRaisesRegex(audit.FixtureAuditError, "unsafe archive path"):
                    audit.audit_archive(path, self._profile(path))

    def test_links_devices_fifo_sparse_and_unknown_entries_are_rejected(self) -> None:
        entry_types = {
            "symlink": tarfile.SYMTYPE,
            "hardlink": tarfile.LNKTYPE,
            "character-device": tarfile.CHRTYPE,
            "block-device": tarfile.BLKTYPE,
            "fifo": tarfile.FIFOTYPE,
            "sparse": tarfile.GNUTYPE_SPARSE,
            "unsupported": b"S",
        }
        for label, entry_type in entry_types.items():
            with self.subTest(label):
                member = tarfile.TarInfo(label)
                member.type = entry_type
                member.linkname = "target"
                path = self._write_tar([member])
                with self.assertRaisesRegex(audit.FixtureAuditError, "unsupported"):
                    audit.audit_archive(path, self._profile(path))

    def test_duplicate_and_file_directory_conflicts_are_rejected(self) -> None:
        first, first_data = self._file("same", b"a")
        second, second_data = self._file("same", b"b")
        duplicate = self._write_tar([first, second], {"same": first_data})
        with self.assertRaisesRegex(audit.FixtureAuditError, "duplicate archive path"):
            audit.audit_archive(duplicate, self._profile(duplicate))

        child, child_data = self._file("parent/child", b"x")
        parent, parent_data = self._file("parent", b"y")
        conflict = self._write_tar([child, parent], {child.name: child_data, parent.name: parent_data})
        with self.assertRaisesRegex(audit.FixtureAuditError, "conflicts with an existing archive directory"):
            audit.audit_archive(conflict, self._profile(conflict))

    def test_realized_symlink_escape_is_rejected(self) -> None:
        root = self.root / "root"
        outside = self.root / "outside"
        root.mkdir()
        outside.mkdir()
        link = root / "link"
        link.symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(audit.FixtureAuditError, "escapes destination"):
            audit._ensure_realized_beneath(root, link)

    def test_extraction_accepts_only_files_and_directories_and_ignores_stored_metadata(self) -> None:
        directory = tarfile.TarInfo("folder/")
        directory.type = tarfile.DIRTYPE
        directory.mode = 0o777
        directory.uid = 12345
        file_member, contents = self._file("folder/file", b"private", mode=0o777)
        path = self._write_tar([directory, file_member], {file_member.name: contents})
        destination = self.root / "extracted"

        inventory = audit.audit_archive(path, self._profile(path), destination)

        self.assertTrue(inventory.extracted)
        self.assertEqual((destination / "folder/file").read_bytes(), contents)
        self.assertEqual(stat.S_IMODE((destination / "folder").stat().st_mode), 0o700)
        self.assertEqual(stat.S_IMODE((destination / "folder/file").stat().st_mode), 0o600)

    @unittest.skipUnless(shutil.which("zstd"), "zstd command is unavailable")
    def test_zstd_tar_is_streamed_through_external_command_without_shell(self) -> None:
        member, contents = self._file("Mods/example", b"payload")
        plain = self.root / "fixture.tar"
        with tarfile.open(plain, "w") as archive:
            archive.addfile(member, io.BytesIO(contents))
        compressed = self.root / "fixture.tar.zst"
        subprocess.run(
            [shutil.which("zstd"), "--quiet", "--force", "-o", os.fspath(compressed), "--", os.fspath(plain)],
            check=True,
            stdin=subprocess.DEVNULL,
        )
        profile = self._profile(compressed, compression="zstd")

        inventory = audit.audit_archive(compressed, profile)

        self.assertEqual(inventory.regular_files, 1)
        self.assertEqual(inventory.expanded_bytes, len(contents))

    def test_missing_zstd_command_is_rejected(self) -> None:
        path = self.root / "fixture.tar.zst"
        path.write_bytes(b"bytes")
        profile = self._profile(path, compression="zstd")
        with self.assertRaisesRegex(audit.FixtureAuditError, "zstd command not found"):
            audit.audit_archive(path, profile, zstd_command="definitely-not-a-real-zstd-command")

    def test_expected_inventory_mismatch_is_rejected(self) -> None:
        member, contents = self._file("file")
        path = self._write_tar([member], {member.name: contents})
        with self.assertRaisesRegex(audit.FixtureAuditError, "inventory mismatch"):
            audit.audit_archive(path, self._profile(path, expected_files=2))

    def test_mods_json_projection_drops_folder_paths_and_non_allowlisted_fields(self) -> None:
        source = self.root / "mods.json"
        source.write_text(
            json.dumps(
                {
                    "generated": "2026-08-26",
                    "modsFolderEntries": ["private-folder"],
                    "mods": [
                        {
                            "folder": "Secret Parent/Mod Folder",
                            "name": "Example",
                            "id": "Example.Mod",
                            "version": "1.2.3",
                            "author": "not needed",
                            "updateKeys": ["private:key"],
                            "contentPackFor": None,
                            "isCodeMod": True,
                            "unknown": {"folder": "/also/dropped"},
                        }
                    ],
                }
            ),
            encoding="utf-8",
        )

        projected = audit.project_mods_json(source)
        rendered = json.dumps(projected, sort_keys=True)

        self.assertNotIn("folder", rendered.lower())
        self.assertNotIn("author", rendered.lower())
        self.assertNotIn("updatekeys", rendered.lower())
        self.assertEqual(projected["mods"][0]["id"], "Example.Mod")
        self.assertEqual(projected["generated"], "2026-08-26")

    def test_mods_json_projection_rejects_nested_allowlisted_values_and_non_date_generation(self) -> None:
        source = self.root / "mods.json"
        malformed_values = (
            ({"mods": [{"name": {"folder": "/home/private"}}]}, "field 'name' has an invalid type"),
            ({"mods": [{"contentPackFor": ["/home/private"]}]}, "field 'contentPackFor' has an invalid type"),
            ({"mods": [{"isCodeMod": 1}]}, "field 'isCodeMod' has an invalid type"),
            ({"generated": "/home/private", "mods": []}, "generated value must be an ISO date"),
            ({"generated": "2026-02-30", "mods": []}, "generated value must be an ISO date"),
        )
        for document, message in malformed_values:
            with self.subTest(document=document):
                source.write_text(json.dumps(document), encoding="utf-8")
                with self.assertRaisesRegex(audit.FixtureAuditError, message):
                    audit.project_mods_json(source)


if __name__ == "__main__":
    unittest.main()
