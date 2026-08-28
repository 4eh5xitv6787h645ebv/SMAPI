#!/usr/bin/env python3
"""Fixture-free tests for the real-world benchmark harness."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))


def load_module(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / filename)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


prepare = load_module("linux_benchmark_prepare", "prepare.py")
runner = load_module("linux_benchmark_runner", "run_ab.py")
analyzer = load_module("linux_benchmark_analyzer", "analyze.py")
common = load_module("linux_benchmark_common", "harness_common.py")


def valid_probe_records(steady_draws: int = 300, transition_draws: int = 10) -> list[dict]:
    frequency = 1_000_000
    steady_updates = 3_000
    transition_updates = 100
    records: list[dict] = [{
        "type": "header", "schema": 1, "probeVersion": "1.1.0", "stopwatchFrequency": frequency,
        "warmupSeconds": 60, "measurementSeconds": 180, "transitionSettleTicks": 300,
        "updateCapacity": 30_000, "drawCapacity": 30_000,
        "recordedUpdates": steady_updates + transition_updates,
        "recordedDraws": steady_draws + transition_draws,
        "bufferOverflow": False, "expectedSaveLoaded": True, "invalidWorldStateTicks": 0,
        "locationChangedTicks": 0, "positionChangedTicks": 0,
        "gameTimeAtSteadyStart": 600, "gameTimeAtSteadyEnd": 900,
    }]
    marker_ticks = (
        0, 1_000_000, 2_000_000, 60_000_000, 240_000_000,
        241_000_000, 242_000_000, 247_000_000, 248_000_000,
        249_000_000, 254_000_000, 255_000_000, 256_000_000,
    )
    records.extend(
        {"type": "marker", "name": name, "elapsedTicks": tick}
        for name, tick in zip(runner.REQUIRED_MARKERS, marker_ticks, strict=True)
    )
    records.append({
        "type": "phaseTotals",
        **{
            f"{phase}{metric}": value
            for value, phase in enumerate(("entry", "steadyStart", "steadyEnd", "exit"), start=1)
            for metric in ("AllocatedBytes", "Gc0", "Gc1", "Gc2")
        },
    })
    records.extend(
        {
            "type": "update", "phase": phase, "elapsedTicks": 1_000,
            "baseGameTicks": 700, "allocatedBytes": 64, "gc0": 0, "gc1": 0, "gc2": 0,
        }
        for phase, count in (("steady", steady_updates), ("transition", transition_updates))
        for _ in range(count)
    )
    for phase, count, start, end in (
        ("steady", steady_draws, marker_ticks[3], marker_ticks[4]),
        ("transition", transition_draws, marker_ticks[4], marker_ticks[10]),
    ):
        records.extend(
            {
                "type": "draw", "phase": phase,
                "capturedAtTicks": start + ((index + 1) * (end - start) // (count + 1)),
                "drawTicks": 1_000, "updateTicks": 1_000, "updateCount": 1,
            }
            for index in range(count)
        )
    return records


def write_jsonl(path: Path, records: list[dict]) -> None:
    path.write_text("\n".join(json.dumps(record) for record in records) + "\n", encoding="utf-8")


class HarnessTests(unittest.TestCase):
    def test_jsonc_reader_preserves_comment_tokens_inside_strings(self) -> None:
        text = '''{
          // line comment
          "url": "https://example.test/a/*literal*/",
          "escaped": "quote: \\\" // literal",
          /* block
             comment */
          "enabled": true
        }'''
        self.assertEqual(
            json.loads(common.strip_json_comments(text)),
            {"url": "https://example.test/a/*literal*/", "escaped": 'quote: " // literal', "enabled": True},
        )
        with self.assertRaisesRegex(ValueError, "unterminated JSON block comment"):
            common.strip_json_comments('{"value": 1 /* broken')

    def test_sample_plan_keeps_main_products_alternating_and_balances_diagnostics(self) -> None:
        plan = runner.sample_plan(5, "a")
        main = [(product, sample) for product, sample, diagnostics, series in plan if not diagnostics]
        self.assertEqual(main, [(product, sample) for sample in range(1, 6) for product in ("a", "b")])
        diagnostic = runner.diagnostic_plan(5)
        diagnostic_positions = {
            sample: next(index for index, value in enumerate(diagnostic) if value == ("b", sample, True, "diagnostic-enabled"))
            - next(index for index, value in enumerate(diagnostic) if value == ("b", sample, False, "diagnostic-control"))
            for sample in range(1, 6)
        }
        self.assertEqual(diagnostic_positions, {1: 1, 2: -1, 3: 1, 4: -1, 5: 1})

    def test_final_plan_is_canonical_and_raw_names_are_unique(self) -> None:
        main = runner.sample_plan(5, "a")
        diagnostic = runner.diagnostic_plan(5)
        plan = {
            "schema": 1, "kind": "final", "start": "a", "samples": 5,
            "main": [list(entry) for entry in main],
            "diagnostics": [list(entry) for entry in diagnostic],
        }
        analyzer.validate_final_plan(plan)
        raw_names: set[str] = set()
        for entries in (main, diagnostic):
            for sequence, (product, sample, enabled, series) in enumerate(entries, start=1):
                label = f"{sequence:02d}-{product}{sample}" + ("-diagnostics" if enabled else "")
                name = analyzer.sanitized_raw_filename(series, label)
                self.assertNotIn(name, raw_names)
                raw_names.add(name)
        edited = json.loads(json.dumps(plan))
        edited["main"][2] = edited["main"][0]
        with self.assertRaisesRegex(ValueError, "canonical preregistered"):
            analyzer.validate_final_plan(edited)

    def test_workload_baseline_rejects_malformed_identity(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "baseline.json"
            malformed = (
                {},
                {"schema": 1},
                {"schema": 1, "workloadIdentitySha256": None},
                {"schema": 2, "workloadIdentitySha256": "a" * 64},
                {"schema": 1, "workloadIdentitySha256": "a" * 63},
                {"schema": 1, "workloadIdentitySha256": "A" * 64},
            )
            for baseline in malformed:
                with self.subTest(baseline=baseline):
                    path.write_text(json.dumps(baseline) + "\n", encoding="utf-8")
                    with self.assertRaisesRegex(ValueError, "invalid private preflight"):
                        runner.load_workload_baseline(path)

    def test_runtime_probe_config_accepts_serialization_only_changes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            probe = Path(temporary)
            config = dict(runner.EXPECTED_PROBE_CONFIG)
            config["WarmupSeconds"] = 60.0
            config["MeasurementSeconds"] = 180.0
            (probe / "config.json").write_text(json.dumps(config), encoding="utf-8")
            (probe / "manifest.json").write_text("manifest\n", encoding="utf-8")
            metadata = {"probeManifestSha256": runner.sha256(probe / "manifest.json")}
            runner.validate_runtime_probe_files(probe, metadata)
            config["MeasurementSeconds"] = 179.0
            (probe / "config.json").write_text(json.dumps(config), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "configuration semantics"):
                runner.validate_runtime_probe_files(probe, metadata)
            with self.assertRaisesRegex(ValueError, "configuration semantics"):
                runner.validate_probe_acceptance(probe, probe / "missing-probe.jsonl", metadata)

    def test_tree_manifest_is_deterministic_and_rejects_symlinks(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "nested").mkdir()
            (root / "nested" / "data").write_bytes(b"value")
            first = prepare.tree_manifest(root)
            second = runner.tree_manifest(root)
            self.assertEqual(first, second)
            (root / "link").symlink_to(root / "nested" / "data")
            with self.assertRaisesRegex(ValueError, "symlink"):
                prepare.tree_manifest(root)

    def test_protected_sources_reject_repository_and_live_paths(self) -> None:
        repo = ROOT.parents[1]
        with self.assertRaisesRegex(ValueError, "protected"):
            prepare.reject_protected_source(repo, repo / "fixture", "fixture")
        live = Path.home() / ".config" / "StardewValley" / "Saves"
        with self.assertRaisesRegex(ValueError, "protected"):
            prepare.reject_protected_source(repo, live.resolve(strict=False), "save")

    def test_private_target_rejects_nonexistent_child_of_live_game(self) -> None:
        repo = ROOT.parents[1]
        live_game_child = Path.home() / ".local" / "share" / "Steam" / "steamapps" / "common" / "Stardew Valley" / "benchmark-new"
        with self.assertRaisesRegex(ValueError, "live Stardew data"):
            prepare.private_target(repo, str(live_game_child))

    def test_custom_game_source_overlap_is_detected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            game_source = Path(temporary) / "custom-library" / "Stardew Valley"
            target = game_source / "benchmark-new"
            sibling = game_source.parent / "private-benchmark"
            self.assertTrue(prepare.paths_overlap(target, game_source))
            self.assertFalse(prepare.paths_overlap(sibling, game_source))

    def test_probe_summary_rejects_wrong_schema_and_marker_order(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "probe.jsonl"
            path.write_text(json.dumps({"type": "header", "schema": 999}) + "\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "schema"):
                runner.probe_summary(path)

            header = {
                "type": "header", "schema": 1, "probeVersion": "1.1.0", "stopwatchFrequency": 1_000_000,
                "warmupSeconds": 60, "measurementSeconds": 180, "transitionSettleTicks": 300,
                "bufferOverflow": False, "expectedSaveLoaded": True, "invalidWorldStateTicks": 0,
                "locationChangedTicks": 0, "positionChangedTicks": 0, "gameTimeAtSteadyStart": 600,
                "gameTimeAtSteadyEnd": 900, "recordedUpdates": 0, "recordedDraws": 0,
            }
            lines = [header] + [
                {"type": "marker", "name": name, "elapsedTicks": index}
                for index, name in enumerate(reversed(runner.REQUIRED_MARKERS))
            ]
            path.write_text("\n".join(json.dumps(line) for line in lines) + "\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "marker order"):
                runner.probe_summary(path)

    def test_probe_summary_accepts_complete_full_duration_capture(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "probe.jsonl"
            write_jsonl(path, valid_probe_records())
            result = runner.probe_summary(path)
            self.assertEqual(result["steadyUpdates"], 3_000)
            self.assertEqual(result["transitionUpdates"], 100)
            self.assertEqual(result["steadyDraws"], 300)
            self.assertEqual(result["transitionDraws"], 10)
            self.assertEqual(result["steadySeconds"], 180)

    def test_probe_summary_enforces_draw_count_boundaries(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "probe.jsonl"
            for steady_draws, transition_draws in ((299, 10), (300, 9)):
                with self.subTest(steady=steady_draws, transition=transition_draws):
                    write_jsonl(path, valid_probe_records(steady_draws, transition_draws))
                    with self.assertRaisesRegex(ValueError, "insufficient steady or transition"):
                        runner.probe_summary(path)

    def test_probe_summary_accepts_transition_draw_before_warp_marker(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "probe.jsonl"
            records = valid_probe_records()
            first_transition = next(
                record for record in records
                if record.get("type") == "draw" and record.get("phase") == "transition"
            )
            first_transition["capturedAtTicks"] = 240_500_000
            write_jsonl(path, records)
            self.assertEqual(runner.probe_summary(path)["transitionDraws"], 10)

    def test_probe_summary_rejects_clustered_draw_samples(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "probe.jsonl"
            for phase, timestamp, message in (
                ("steady", 61_000_000, "do not span the measurement"),
                ("transition", 242_000_000, "do not span the transition"),
            ):
                with self.subTest(phase=phase):
                    records = valid_probe_records()
                    for record in records:
                        if record.get("type") == "draw" and record.get("phase") == phase:
                            record["capturedAtTicks"] = timestamp
                    write_jsonl(path, records)
                    with self.assertRaisesRegex(ValueError, message):
                        runner.probe_summary(path)

    def test_analyzer_allowlist_preserves_only_numeric_probe_fields(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "probe.jsonl"
            records = valid_probe_records()
            records[0]["privateCanary"] = "must-not-survive"
            records[1]["privateCanary"] = "must-not-survive"
            write_jsonl(path, records)
            _, allowed = analyzer.load_probe(path)
            self.assertFalse(any("privateCanary" in record for record in allowed))

    def test_log_parser_projects_only_counts_versions_resolution_and_phases(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "log.txt"
            path.write_text(
                "[12:00:00 TRACE SMAPI] Log started at 2026-01-01 UTC\n"
                "[12:00:01 INFO SMAPI] SMAPI 4.5.2 with Stardew Valley 1.6.15 build 24356 on Unix\n"
                "[12:00:02 DEBUG SMAPI] Loading mod metadata...\n"
                "[12:00:03 INFO SMAPI] Loaded 132 mods:\n"
                "[12:00:03 INFO SMAPI] Loaded 176 content packs:\n"
                "[12:00:04 DEBUG SMAPI] Mods loaded and ready!\n"
                "[12:00:05 TRACE game] Window_ClientSizeChanged(); Window.ClientBounds={X:0 Y:0 Width:1280 Height:720}\n",
                encoding="utf-8",
            )
            result = runner.selected_log_metadata(path)
            self.assertEqual(result["loadedCodeMods"], 132)
            self.assertEqual(result["loadedContentPacks"], 176)
            self.assertEqual(result["resolution"], "1280x720")
            self.assertEqual(result["startupPhaseSecondsFromLogStart"]["metadataLoad"], 2)

    def test_log_parser_detects_standard_skipped_mod_banner(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "log.txt"
            path.write_text(
                "[12:00:00 ERROR SMAPI]    Skipped mods\n"
                "[12:00:00 ERROR SMAPI]    --------------------------------------------------\n"
                "[12:00:00 ERROR SMAPI]       These mods could not be added to your game.\n"
                "[12:00:00 ERROR SMAPI]       - fixture content pack because its dependency is missing.\n"
                "[12:00:00 WARN  SMAPI]    Changed save serializer\n",
                encoding="utf-8",
            )
            self.assertEqual(runner.selected_log_metadata(path)["skippedModCount"], 1)

    def test_workload_identity_covers_loaded_and_skipped_entries(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "log.txt"
            template = (
                "[12:00:00 INFO  SMAPI] Loaded 2 mods:\n"
                "[12:00:00 INFO  SMAPI]    Code One 1.0.0 | first\n"
                "[12:00:00 INFO  SMAPI]    Code {second} 1.0.0 | second\n"
                "[12:00:00 INFO  SMAPI] Loaded 1 content packs:\n"
                "[12:00:00 INFO  SMAPI]    Pack One 1.0.0 | pack\n"
                "[12:00:00 ERROR SMAPI]    Skipped mods\n"
                "[12:00:00 ERROR SMAPI]    --------------------------------------------------\n"
                "[12:00:00 ERROR SMAPI]       These mods could not be added to your game.\n"
                "[12:00:00 ERROR SMAPI]       - Missing Pack 1.0.0 because dependency category.\n"
                "[12:00:00 WARN  SMAPI]    Changed save serializer\n"
            )
            path.write_text(template.format(second="Two"), encoding="utf-8")
            first = runner.selected_log_metadata(path)["workloadIdentitySha256"]
            path.write_text(template.format(second="Changed"), encoding="utf-8")
            second = runner.selected_log_metadata(path)["workloadIdentitySha256"]
            self.assertRegex(first, r"^[0-9a-f]{64}$")
            self.assertNotEqual(first, second)

    def test_complete_startup_metadata_is_accepted(self) -> None:
        metadata = {
            "resolution": "1280x720", "loadedCodeMods": 132, "loadedContentPacks": 176,
            "smapiVersion": "4.5.2", "gameVersion": "1.6.15 build 24356", "modsReady": True,
            "skippedModCount": 1,
            "workloadIdentitySha256": "a" * 64,
            "startupPhaseSecondsFromLogStart": {name: index for index, name in enumerate(runner.REQUIRED_STARTUP_PHASES)},
        }
        runner.validate_log_metadata(metadata, 132, 176, 1, "a" * 64)

    def test_nearest_rank_percentile_and_run_variation(self) -> None:
        self.assertEqual(analyzer.percentile([1.0, 2.0, 3.0, 4.0], 0.95), 4.0)
        result = analyzer.run_variation([1.0, 2.0, 3.0, 4.0, 5.0])
        self.assertEqual(result["median"], 3.0)
        self.assertEqual(result["min"], 1.0)
        self.assertEqual(result["max"], 5.0)


if __name__ == "__main__":
    unittest.main()
