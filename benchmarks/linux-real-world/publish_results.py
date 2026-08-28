#!/usr/bin/env python3
"""Complete and render the public Linux A/B benchmark summary from sanitized results."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import statistics
import tempfile
from typing import Any


GROUPS = {
    "official": ("main", "official", False),
    "forkDiagnosticsDisabled": ("main", "fork", False),
    "forkDiagnosticControls": ("diagnostic-control", "fork", False),
    "forkDiagnosticsEnabled": ("diagnostic-enabled", "fork", True),
}

DRAW_LIMITATION = (
    "Draw cadence and update-and-draw distributions were measured under Xvfb with llvmpipe software rendering; "
    "they are renderer diagnostics, not desktop FPS. Official steady draw counts varied from 473 to 1,144 per run."
)
DRAW_TAIL_LIMITATION = (
    "At the 300-draw acceptance floor, draw p99 is supported by only roughly the worst three observations and is "
    "less stable than update p99."
)
CPU_LIMITATION = (
    "Chosen-core mean busy time was higher for the fork in every main pair and headless draw cadence was much higher; "
    "these captures do not show lower CPU use, lower power, or general efficiency. Busy time includes llvmpipe and other host work."
)
ORDER_LIMITATION = (
    "Every main pair ran official A before fork B. Product is therefore confounded with within-pair order and filesystem/cache warming, "
    "especially for save-load timing; the observed magnitude is evidence only for this fixed-order session."
)
ADVERSE_SIGNAL_LIMITATION = (
    "Fork process-wide Gen1 collections were 2–5 higher in every main pair, while Farm warp-observed timing was slower in four of five pairs; "
    "GC pause duration was not measured and warp observations were noisy, so neither is classified as a confirmed regression."
)
GAME_ASSEMBLY_SHA256 = "f3e97f01d3fd2b1e6094fc8d2b59950aa6cb9d6cd1bf1b39d72d58edda8aad12"
PROBE_ASSEMBLY_SHA256 = "34e3a3b36a9456931437a96fdf7be79bd12aed3d8580a89bbb312302fee82663"
PRIVATE_TOKENS = (
    "/home/", "Blossom_", "PRIVATE_", "Mods-2026", "SaveGameInfo",
    "modpackArchiveSha256", "saveArchiveSha256", "workloadIdentitySha256",
    "337d157bb2cf7283eaf6796c259a21d6e2e71baf134cb67a1f2a655e31cb312c",
    "6f707c73d02a05eed42adee0d9d9b434e92dff84a841e2f00047456921ae4bca",
)
BASE_LIMITATIONS = (
    "One shared Linux workstation and one private workload; results are not universal FPS claims.",
    "The framework envelope includes mod callbacks dispatched outside the base-game window and identical probe overhead.",
    "GC counts are process-wide correlations, not attribution; coincident counts cover only outer updates.",
    "SMAPI startup log boundaries have one-second timestamp resolution.",
    "Shared-host load and filesystem caches cannot be eliminated completely; per-run variation is retained.",
    "Probe startup timing begins at mod entry and cannot observe native launcher/runtime work before that boundary.",
    "Tiered compilation is disabled to avoid a reproducible .NET 6 JIT crash in this workload, so results do not represent the default tiered-runtime configuration.",
    "A null audio backend is used consistently because the isolated Xvfb session has no audio device.",
)
STARTUP_PHASES = (
    "logStarted", "waitingForGame", "maliciousScan", "metadataLoad",
    "assemblyLoad", "entryLaunch", "modsReady", "contentReady",
)
TRANSITIONS = (
    "probeEntryToGameLaunched", "gameLaunchedToSaveLoaded", "warpTownObserved",
    "warpTownSettled", "warpFarmObserved", "warpFarmSettled",
)
RAW_KEYS = {
    "sample": frozenset((
        "type", "schema", "label", "sequence", "product", "sample", "diagnosticsEnabled", "series", "commit",
        "smapiAssemblySha256", "gameAssemblySha256", "probeAssemblySha256", "suiteEnvironmentSha256", "started", "finished",
        "displaySession", "cpuList", "preRunChosenCpuBusyPercent", "duringRunChosenCpuBusyPercent", "loadAverage",
        "temperatureCelsiusBefore", "temperatureCelsiusAfter", "startupPhaseSecondsFromLogStart", "loadedCodeMods",
        "loadedContentPacks", "skippedModCount", "smapiVersion", "gameVersion", "resolution",
    )),
    "header": frozenset((
        "type", "schema", "probeVersion", "stopwatchFrequency", "warmupSeconds", "measurementSeconds", "transitionSettleTicks",
        "updateCapacity", "drawCapacity", "recordedUpdates", "recordedDraws", "bufferOverflow", "expectedSaveLoaded",
        "invalidWorldStateTicks", "locationChangedTicks", "positionChangedTicks", "gameTimeAtSteadyStart", "gameTimeAtSteadyEnd",
    )),
    "marker": frozenset(("type", "name", "elapsedTicks")),
    "phaseTotals": frozenset((
        "type", "entryAllocatedBytes", "entryGc0", "entryGc1", "entryGc2", "steadyStartAllocatedBytes", "steadyStartGc0",
        "steadyStartGc1", "steadyStartGc2", "steadyEndAllocatedBytes", "steadyEndGc0", "steadyEndGc1", "steadyEndGc2",
        "exitAllocatedBytes", "exitGc0", "exitGc1", "exitGc2",
    )),
    "update": frozenset(("type", "phase", "elapsedTicks", "baseGameTicks", "allocatedBytes", "gc0", "gc1", "gc2")),
    "draw": frozenset(("type", "phase", "capturedAtTicks", "drawTicks", "updateTicks", "updateCount")),
}
MARKER_ORDER = (
    "probe_entry", "game_launched", "save_loaded", "steady_state_start", "steady_state_end", "warp_town_start",
    "warp_town_complete", "warp_town_settled", "warp_farm_start", "warp_farm_complete", "warp_farm_settled",
    "normal_exit_requested", "game_exiting",
)
MARKER_NAMES = frozenset(MARKER_ORDER)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def require_keys(value: Any, expected: frozenset[str] | set[str], context: str) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != set(expected):
        actual = sorted(value) if isinstance(value, dict) else type(value).__name__
        raise ValueError(f"unexpected {context} schema: {actual}")
    return value


def require_number(value: Any, context: str) -> None:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)):
        raise ValueError(f"invalid numeric value in {context}")


def validate_distribution(value: Any, context: str) -> None:
    distribution = require_keys(value, {"count", "mean", "p50", "p95", "p99", "max"}, context)
    for key, item in distribution.items():
        require_number(item, f"{context}.{key}")
    if distribution["count"] <= 0:
        raise ValueError(f"empty distribution in {context}")


def distribution(values: list[float]) -> dict[str, float | int]:
    if not values:
        raise ValueError("cannot summarize an empty raw series")
    ordered = sorted(values)
    return {
        "count": len(ordered),
        "mean": statistics.fmean(ordered),
        "p50": percentile(ordered, 0.50),
        "p95": percentile(ordered, 0.95),
        "p99": percentile(ordered, 0.99),
        "max": ordered[-1],
    }


def validate_sample(sample: Any) -> None:
    required = {
        "label", "sequence", "product", "sample", "diagnosticsEnabled", "series", "steadySeconds", "updateMilliseconds",
        "baseGameMilliseconds", "frameworkEnvelopeMilliseconds", "drawMilliseconds", "updateAndDrawMilliseconds",
        "steadyDrawsPerMeasuredSecond", "mainThreadAllocatedBytesPerUpdate", "processAllocatedBytesPerUpdate",
        "processGcCollections", "coincidentGcCollections", "slowUpdateCounts", "transitionsMilliseconds",
        "startupPhaseSecondsFromLogStart", "workload", "preRunChosenCpuBusyPercent", "duringRunChosenCpuBusyPercent",
    }
    actual = set(sample) if isinstance(sample, dict) else set()
    if actual not in (required, required | {"publicSampleId"}):
        raise ValueError(f"unexpected public sample schema: {sorted(actual)}")
    if sample["product"] not in ("official", "fork") or sample["series"] not in ("main", "diagnostic-control", "diagnostic-enabled"):
        raise ValueError("unexpected public sample identity")
    if not re.fullmatch(r"[0-9]{2}-[ab][1-5](?:-diagnostics)?", sample["label"]):
        raise ValueError("unexpected public sample label")
    if type(sample["diagnosticsEnabled"]) is not bool or sample["sample"] not in range(1, 6):
        raise ValueError("unexpected public sample controls")
    for family in (
        "updateMilliseconds", "baseGameMilliseconds", "frameworkEnvelopeMilliseconds", "drawMilliseconds",
        "updateAndDrawMilliseconds", "mainThreadAllocatedBytesPerUpdate",
    ):
        validate_distribution(sample[family], f"sample.{family}")
    for key in ("steadySeconds", "steadyDrawsPerMeasuredSecond", "processAllocatedBytesPerUpdate", "sequence"):
        require_number(sample[key], f"sample.{key}")
    for key in ("processGcCollections", "coincidentGcCollections"):
        nested = require_keys(sample[key], {"gc0", "gc1", "gc2"}, f"sample.{key}")
        for name, value in nested.items():
            require_number(value, f"sample.{key}.{name}")
    slow = require_keys(sample["slowUpdateCounts"], {"16.667", "33.333", "50"}, "sample.slowUpdateCounts")
    for name, value in slow.items():
        require_number(value, f"sample.slowUpdateCounts.{name}")
    transitions = require_keys(sample["transitionsMilliseconds"], set(TRANSITIONS), "sample.transitionsMilliseconds")
    startup = require_keys(sample["startupPhaseSecondsFromLogStart"], set(STARTUP_PHASES), "sample.startupPhaseSecondsFromLogStart")
    for name, value in (*transitions.items(), *startup.items()):
        require_number(value, f"sample timing {name}")
    workload = require_keys(sample["workload"], {"loadedCodeMods", "loadedContentPacks", "skippedModCount", "identityMatched"}, "sample.workload")
    if workload["identityMatched"] is not True:
        raise ValueError("unmatched public workload")
    for name in ("loadedCodeMods", "loadedContentPacks", "skippedModCount"):
        require_number(workload[name], f"sample.workload.{name}")
    for key in ("preRunChosenCpuBusyPercent", "duringRunChosenCpuBusyPercent"):
        busy = require_keys(sample[key], {"mean", "max"}, f"sample.{key}")
        for name, value in busy.items():
            require_number(value, f"sample.{key}.{name}")


def require_samples_match(source_samples: list[dict[str, Any]], raw_samples: list[dict[str, Any]]) -> None:
    source_by_identity = {(sample["series"], sample["label"]): sample for sample in source_samples}
    raw_by_identity = {(sample["series"], sample["label"]): sample for sample in raw_samples}
    comparable_source = {
        identity: {key: value for key, value in sample.items() if key != "publicSampleId"}
        for identity, sample in source_by_identity.items()
    }
    if comparable_source != raw_by_identity:
        raise ValueError("public summary sample metrics differ from retained raw records")


def summarize_raw_records(records: list[dict[str, Any]]) -> dict[str, Any]:
    private = records[0]
    header = records[1]
    frequency = header["stopwatchFrequency"]
    markers = {record["name"]: record["elapsedTicks"] for record in records if record["type"] == "marker"}
    updates = [record for record in records if record["type"] == "update" and record["phase"] == "steady"]
    draws = [record for record in records if record["type"] == "draw" and record["phase"] == "steady"]
    totals = next(record for record in records if record["type"] == "phaseTotals")
    to_ms = 1000.0 / frequency
    update_ms = [record["elapsedTicks"] * to_ms for record in updates]
    base_game_ms = [record["baseGameTicks"] * to_ms for record in updates]
    framework_ms = [(record["elapsedTicks"] - record["baseGameTicks"]) * to_ms for record in updates]
    draw_ms = [record["drawTicks"] * to_ms for record in draws]
    update_draw_ms = [(record["drawTicks"] + record["updateTicks"]) * to_ms for record in draws]
    steady_seconds = (markers["steady_state_end"] - markers["steady_state_start"]) / frequency
    process_allocated = totals["steadyEndAllocatedBytes"] - totals["steadyStartAllocatedBytes"]
    return {
        "label": private["label"],
        "sequence": private["sequence"],
        "product": private["product"],
        "sample": private["sample"],
        "diagnosticsEnabled": private["diagnosticsEnabled"],
        "series": private["series"],
        "steadySeconds": steady_seconds,
        "updateMilliseconds": distribution(update_ms),
        "baseGameMilliseconds": distribution(base_game_ms),
        "frameworkEnvelopeMilliseconds": distribution(framework_ms),
        "drawMilliseconds": distribution(draw_ms),
        "updateAndDrawMilliseconds": distribution(update_draw_ms),
        "steadyDrawsPerMeasuredSecond": len(draws) / steady_seconds,
        "mainThreadAllocatedBytesPerUpdate": distribution([float(record["allocatedBytes"]) for record in updates]),
        "processAllocatedBytesPerUpdate": process_allocated / len(updates),
        "processGcCollections": {
            f"gc{generation}": totals[f"steadyEndGc{generation}"] - totals[f"steadyStartGc{generation}"]
            for generation in range(3)
        },
        "coincidentGcCollections": {
            f"gc{generation}": sum(record[f"gc{generation}"] for record in updates)
            for generation in range(3)
        },
        "slowUpdateCounts": {
            threshold: sum(value > float(threshold) for value in update_ms)
            for threshold in ("16.667", "33.333", "50")
        },
        "transitionsMilliseconds": {
            "probeEntryToGameLaunched": (markers["game_launched"] - markers["probe_entry"]) * to_ms,
            "gameLaunchedToSaveLoaded": (markers["save_loaded"] - markers["game_launched"]) * to_ms,
            "warpTownObserved": (markers["warp_town_complete"] - markers["warp_town_start"]) * to_ms,
            "warpTownSettled": (markers["warp_town_settled"] - markers["warp_town_start"]) * to_ms,
            "warpFarmObserved": (markers["warp_farm_complete"] - markers["warp_farm_start"]) * to_ms,
            "warpFarmSettled": (markers["warp_farm_settled"] - markers["warp_farm_start"]) * to_ms,
        },
        "startupPhaseSecondsFromLogStart": private["startupPhaseSecondsFromLogStart"],
        "workload": {
            "loadedCodeMods": private["loadedCodeMods"],
            "loadedContentPacks": private["loadedContentPacks"],
            "skippedModCount": private["skippedModCount"],
            "identityMatched": True,
        },
        "preRunChosenCpuBusyPercent": private["preRunChosenCpuBusyPercent"],
        "duringRunChosenCpuBusyPercent": private["duringRunChosenCpuBusyPercent"],
    }


def validate_raw_probe_semantics(records: list[dict[str, Any]], filename: str) -> None:
    header = records[1]
    if (
        header["warmupSeconds"] != 60
        or header["measurementSeconds"] != 180
        or header["transitionSettleTicks"] != 300
        or header["updateCapacity"] != 30000
        or header["drawCapacity"] != 30000
        or header["bufferOverflow"] is not False
        or header["expectedSaveLoaded"] is not True
        or any(header[key] != 0 for key in ("invalidWorldStateTicks", "locationChangedTicks", "positionChangedTicks"))
        or header["gameTimeAtSteadyStart"] == header["gameTimeAtSteadyEnd"]
    ):
        raise ValueError(f"raw probe controls were not accepted: {filename}")
    frequency = header["stopwatchFrequency"]
    if not isinstance(frequency, int) or isinstance(frequency, bool) or not 1000 <= frequency <= 10_000_000_000:
        raise ValueError(f"invalid raw stopwatch frequency: {filename}")
    marker_records = [record for record in records if record["type"] == "marker"]
    if [record["name"] for record in marker_records] != list(MARKER_ORDER):
        raise ValueError(f"raw marker order differs from the accepted scenario: {filename}")
    marker_ticks = [record["elapsedTicks"] for record in marker_records]
    if any(not isinstance(value, int) or isinstance(value, bool) or value < 0 for value in marker_ticks) or marker_ticks != sorted(marker_ticks):
        raise ValueError(f"invalid raw marker timestamps: {filename}")
    markers = {record["name"]: record["elapsedTicks"] for record in marker_records}
    updates = [record for record in records if record["type"] == "update"]
    draws = [record for record in records if record["type"] == "draw"]
    if header["recordedUpdates"] != len(updates) or header["recordedDraws"] != len(draws):
        raise ValueError(f"raw header record counts do not match records: {filename}")
    for record in updates:
        numeric = (record["elapsedTicks"], record["baseGameTicks"], record["allocatedBytes"], record["gc0"], record["gc1"], record["gc2"])
        if any(not isinstance(value, int) or isinstance(value, bool) or value < 0 for value in numeric) or record["elapsedTicks"] <= 0 or record["baseGameTicks"] > record["elapsedTicks"]:
            raise ValueError(f"invalid raw update record: {filename}")
    for record in draws:
        numeric = (record["capturedAtTicks"], record["drawTicks"], record["updateTicks"], record["updateCount"])
        if any(not isinstance(value, int) or isinstance(value, bool) or value < 0 for value in numeric) or record["drawTicks"] <= 0:
            raise ValueError(f"invalid raw draw record: {filename}")
    steady_updates = [record for record in updates if record["phase"] == "steady"]
    transition_updates = [record for record in updates if record["phase"] == "transition"]
    steady_draws = [record for record in draws if record["phase"] == "steady"]
    transition_draws = [record for record in draws if record["phase"] == "transition"]
    if len(steady_updates) < 3000 or len(transition_updates) < 100 or len(steady_draws) < 300 or len(transition_draws) < 10:
        raise ValueError(f"insufficient accepted raw samples: {filename}")
    steady_seconds = (markers["steady_state_end"] - markers["steady_state_start"]) / frequency
    if steady_seconds < 180:
        raise ValueError(f"raw steady-state duration is below 180 seconds: {filename}")
    all_draw_ticks = [record["capturedAtTicks"] for record in draws]
    if all_draw_ticks != sorted(all_draw_ticks):
        raise ValueError(f"raw draw captures are not monotonic: {filename}")
    steady_capture = [record["capturedAtTicks"] for record in steady_draws]
    transition_capture = [record["capturedAtTicks"] for record in transition_draws]
    if (
        steady_capture[0] < markers["steady_state_start"]
        or steady_capture[-1] > markers["steady_state_end"]
        or steady_capture[0] > markers["steady_state_start"] + 5 * frequency
        or steady_capture[-1] < markers["steady_state_end"] - 5 * frequency
        or transition_capture[0] < markers["steady_state_end"]
        or transition_capture[-1] > markers["warp_farm_settled"]
        or transition_capture[0] > markers["steady_state_end"] + 2 * frequency
        or transition_capture[-1] < markers["warp_farm_settled"] - 2 * frequency
    ):
        raise ValueError(f"raw draw captures do not span the accepted windows: {filename}")
    totals = next(record for record in records if record["type"] == "phaseTotals")
    for suffix in ("AllocatedBytes", "Gc0", "Gc1", "Gc2"):
        values = [totals[f"{phase}{suffix}"] for phase in ("entry", "steadyStart", "steadyEnd", "exit")]
        if any(not isinstance(value, int) or isinstance(value, bool) or value < 0 for value in values) or values != sorted(values):
            raise ValueError(f"raw phase totals are invalid: {filename}")


def validate_raw_results(raw_root: Path) -> tuple[list[dict[str, Any]], str]:
    if raw_root.is_symlink() or not raw_root.is_dir():
        raise ValueError("public raw results path must be a real directory")
    entries = sorted(raw_root.iterdir())
    files = [path for path in entries if path.suffix == ".jsonl"]
    if len(files) != 20 or len(entries) != 20 or any(path.is_symlink() or not path.is_file() for path in files):
        raise ValueError("public raw results must contain exactly 20 regular JSONL files")
    sample_ids: set[tuple[str, str]] = set()
    suite_hashes: set[str] = set()
    summarized_samples: list[dict[str, Any]] = []
    for path in files:
        type_counts = {record_type: 0 for record_type in RAW_KEYS}
        marker_names: set[str] = set()
        lines = path.read_text(encoding="utf-8").splitlines()
        parsed_records: list[dict[str, Any]] = []
        if not lines:
            raise ValueError(f"empty public raw result: {path.name}")
        for line_number, line in enumerate(lines, start=1):
            if any(token in line for token in PRIVATE_TOKENS):
                raise ValueError(f"privacy scan rejected {path.name}:{line_number}")
            record = json.loads(line)
            parsed_records.append(record)
            record_type = record.get("type") if isinstance(record, dict) else None
            if record_type not in RAW_KEYS:
                raise ValueError(f"unexpected raw record type in {path.name}:{line_number}")
            require_keys(record, RAW_KEYS[record_type], f"raw {record_type}")
            type_counts[record_type] += 1
            string_keys = {"type"}
            if record_type == "sample":
                string_keys |= {
                    "label", "product", "series", "commit", "smapiAssemblySha256", "gameAssemblySha256", "probeAssemblySha256",
                    "suiteEnvironmentSha256", "started", "finished", "displaySession", "cpuList", "smapiVersion", "gameVersion", "resolution",
                }
                if line_number != 1 or record["product"] not in ("official", "fork") or record["series"] not in ("main", "diagnostic-control", "diagnostic-enabled"):
                    raise ValueError(f"unexpected raw sample identity in {path.name}")
                if not re.fullmatch(r"[0-9]{2}-[ab][1-5](?:-diagnostics)?", record["label"]):
                    raise ValueError(f"unexpected raw sample label in {path.name}")
                expected_filename = f"{record['series']}-{record['label']}.jsonl"
                if path.name != expected_filename:
                    raise ValueError(f"raw filename does not match its sample identity: {path.name}")
                if record["commit"] not in (
                    "79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0", "3c98eadd2bddc24d43c889afb11b155e92469882",
                ) or record["gameAssemblySha256"] != GAME_ASSEMBLY_SHA256:
                    raise ValueError(f"unexpected raw commit or game assembly in {path.name}")
                expected_commit = "79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0" if record["product"] == "official" else "3c98eadd2bddc24d43c889afb11b155e92469882"
                if record["commit"] != expected_commit or record["probeAssemblySha256"] != PROBE_ASSEMBLY_SHA256:
                    raise ValueError(f"raw product is not bound to its accepted commit/probe: {path.name}")
                for hash_key in ("commit", "smapiAssemblySha256", "gameAssemblySha256", "probeAssemblySha256", "suiteEnvironmentSha256"):
                    if not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", record[hash_key]):
                        raise ValueError(f"invalid hash in {path.name}")
                if not re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.+-]+", record["started"]) or not re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.+-]+", record["finished"]):
                    raise ValueError(f"invalid timestamp in {path.name}")
                expected_diagnostics = record["series"] == "diagnostic-enabled"
                if (
                    record["displaySession"] != "x11-xvfb"
                    or record["resolution"] != "1280x720"
                    or record["smapiVersion"] != "4.5.2"
                    or record["cpuList"] != "12,13,14,15,16,17"
                    or record["diagnosticsEnabled"] is not expected_diagnostics
                    or (record["series"] != "main" and record["product"] != "fork")
                    or record["loadedCodeMods"] != 132
                    or record["loadedContentPacks"] != 176
                    or record["skippedModCount"] != 1
                ):
                    raise ValueError(f"unexpected raw runtime controls in {path.name}")
                for nested_key in ("preRunChosenCpuBusyPercent", "duringRunChosenCpuBusyPercent"):
                    nested = require_keys(record[nested_key], {"mean", "max"}, f"raw sample.{nested_key}")
                    for value in nested.values():
                        require_number(value, f"raw sample.{nested_key}")
                startup = require_keys(record["startupPhaseSecondsFromLogStart"], set(STARTUP_PHASES), "raw startup phases")
                for value in startup.values():
                    require_number(value, "raw startup phase")
                for array_key in ("loadAverage", "temperatureCelsiusBefore", "temperatureCelsiusAfter"):
                    if not isinstance(record[array_key], list):
                        raise ValueError(f"invalid numeric array in {path.name}")
                    for value in record[array_key]:
                        require_number(value, f"raw sample.{array_key}")
                sample_ids.add((record["series"], record["label"]))
                suite_hashes.add(record["suiteEnvironmentSha256"])
            elif record_type == "header":
                string_keys.add("probeVersion")
                if line_number != 2 or record["schema"] != 1 or record["probeVersion"] != "1.1.0" or type(record["bufferOverflow"]) is not bool or type(record["expectedSaveLoaded"]) is not bool:
                    raise ValueError(f"unexpected raw header in {path.name}")
            elif record_type == "marker":
                string_keys.add("name")
                if record["name"] not in MARKER_NAMES or record["name"] in marker_names:
                    raise ValueError(f"unexpected raw marker in {path.name}")
                marker_names.add(record["name"])
            elif record_type in ("update", "draw"):
                string_keys.add("phase")
                if record["phase"] not in ("steady", "transition"):
                    raise ValueError(f"unexpected raw phase in {path.name}")
            for key, value in record.items():
                if key in string_keys or isinstance(value, (dict, list, bool)):
                    continue
                require_number(value, f"raw {record_type}.{key}")
        if type_counts["sample"] != 1 or type_counts["header"] != 1 or type_counts["phaseTotals"] != 1 or marker_names != MARKER_NAMES or not type_counts["update"] or not type_counts["draw"]:
            raise ValueError(f"incomplete public raw record set: {path.name}")
        validate_raw_probe_semantics(parsed_records, path.name)
        summarized_samples.append(summarize_raw_records(parsed_records))
    if len(sample_ids) != 20 or len(suite_hashes) != 1:
        raise ValueError("public raw sample identities or suite environments are not unique")
    return summarized_samples, next(iter(suite_hashes))


def atomic_write_texts(files: dict[Path, str]) -> None:
    for path, content in files.items():
        if any(token in content for token in PRIVATE_TOKENS):
            raise ValueError(f"privacy scan rejected generated public output: {path.name}")
    temporary_paths: dict[Path, Path] = {}
    try:
        for path, content in files.items():
            descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
            temporary_path = Path(temporary_name)
            temporary_paths[path] = temporary_path
            with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
                stream.write(content)
                stream.flush()
                os.fsync(stream.fileno())
        for path, temporary_path in temporary_paths.items():
            os.replace(temporary_path, path)
    finally:
        for temporary_path in temporary_paths.values():
            temporary_path.unlink(missing_ok=True)


def load_runtime_provenance(results: Path, suite_environment_sha256: str) -> dict[str, Any]:
    path = results / "runtime-provenance.json"
    provenance = require_keys(json.loads(path.read_text(encoding="utf-8")), {
        "schema", "harnessCommit", "suiteEnvironmentSha256", "verifierScriptSha256", "verifiedProducts", "framework", "version",
        "tieredCompilationFromRuntimeConfig", "gameAssemblySha256", "coreclrSha256", "hostfxrSha256", "verificationMethod",
    }, "runtime provenance")
    if provenance["schema"] != 1 or provenance["harnessCommit"] != "3c98eadd2bddc24d43c889afb11b155e92469882":
        raise ValueError("unexpected runtime provenance identity")
    if provenance["suiteEnvironmentSha256"] != suite_environment_sha256 or provenance["gameAssemblySha256"] != GAME_ASSEMBLY_SHA256:
        raise ValueError("runtime provenance is not linked to the public raw suite")
    verifier_sha256 = sha256(Path(__file__).with_name("verify_runtime.py"))
    if provenance["verifierScriptSha256"] != verifier_sha256:
        raise ValueError("runtime provenance was generated by a different verifier")
    if provenance["verifiedProducts"] != ["official", "fork"] or provenance["framework"] != "Microsoft.NETCore.App":
        raise ValueError("runtime provenance does not cover both canonical products")
    if not re.fullmatch(r"6\.0\.[0-9]+", provenance["version"]) or provenance["tieredCompilationFromRuntimeConfig"] is not False:
        raise ValueError("unexpected verified runtime configuration")
    for key in ("suiteEnvironmentSha256", "verifierScriptSha256", "gameAssemblySha256", "coreclrSha256", "hostfxrSha256"):
        if not re.fullmatch(r"[0-9a-f]{64}", provenance[key]):
            raise ValueError(f"invalid runtime provenance hash: {key}")
    if provenance["verificationMethod"] != "SHA-256 and runtime-config fields independently matched across the post-suite official and fork gold game trees; the suite environment digest matches all 20 public raw samples.":
        raise ValueError("unexpected runtime verification method")
    return {
        "framework": provenance["framework"],
        "version": provenance["version"],
        "coreclrSha256": provenance["coreclrSha256"],
        "hostfxrSha256": provenance["hostfxrSha256"],
        "tieredCompilationFromRuntimeConfig": provenance["tieredCompilationFromRuntimeConfig"],
        "suiteEnvironmentSha256": provenance["suiteEnvironmentSha256"],
        "verificationScriptSha256": verifier_sha256,
        "verificationMethod": provenance["verificationMethod"],
    }


def public_metadata(source: Any, verified_runtime: dict[str, Any]) -> dict[str, Any]:
    allowed = {
        "schema", "officialCommit", "forkCommit", "gameAssemblySha256", "modpackArchiveSha256", "saveArchiveSha256",
        "probeAssemblySha256", "commonLauncherSha256", "commonDepsSha256", "expectedLoadedCodeMods",
        "expectedLoadedContentPacks", "expectedSkippedMods", "harnessCommit", "calculationMethod", "environment", "publicSummary",
    }
    if not isinstance(source, dict) or not set(source).issubset(allowed) or not allowed.difference({"modpackArchiveSha256", "saveArchiveSha256", "publicSummary"}).issubset(source):
        raise ValueError("unexpected source metadata schema")
    if source["schema"] != 1 or source["officialCommit"] != "79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0" or source["forkCommit"] != "3c98eadd2bddc24d43c889afb11b155e92469882":
        raise ValueError("unexpected benchmark commit metadata")
    if source["gameAssemblySha256"] != GAME_ASSEMBLY_SHA256 or source["harnessCommit"] != source["forkCommit"]:
        raise ValueError("unexpected benchmark harness or game metadata")
    if (
        source["probeAssemblySha256"] != PROBE_ASSEMBLY_SHA256
        or source["expectedLoadedCodeMods"] != 132
        or source["expectedLoadedContentPacks"] != 176
        or source["expectedSkippedMods"] != 1
    ):
        raise ValueError("unexpected benchmark probe or workload metadata")
    environment_source = source["environment"]
    environment_allowed = {
        "schema", "system", "kernel", "machine", "cpuList", "cpuModel", "logicalCpuCount", "governors", "memory",
        "dotnet", "buildDotnetSdk", "bwrap", "xvfb", "locale", "renderer", "runtimeSettings", "measuredGameRuntime",
        "verifiedGameRuntime",
    }
    if not isinstance(environment_source, dict) or not set(environment_source).issubset(environment_allowed):
        raise ValueError("unexpected benchmark environment schema")
    required_environment = environment_allowed.difference({"dotnet", "buildDotnetSdk", "measuredGameRuntime", "verifiedGameRuntime"})
    if not required_environment.issubset(environment_source) or ("dotnet" in environment_source) == ("buildDotnetSdk" in environment_source):
        raise ValueError("incomplete or ambiguous benchmark environment")
    if environment_source["cpuList"] != "12,13,14,15,16,17" or environment_source["runtimeSettings"] != {"DOTNET_TieredCompilation": "0", "ALSOFT_DRIVERS": "null"}:
        raise ValueError("benchmark environment controls differ from the accepted suite")
    require_keys(environment_source["memory"], {"MemTotalKiB", "MemAvailableKiB", "SwapTotalKiB", "SwapFreeKiB"}, "environment memory")
    require_keys(environment_source["xvfb"], {"binarySha256", "helpSignature"}, "environment Xvfb")
    require_keys(environment_source["runtimeSettings"], {"DOTNET_TieredCompilation", "ALSOFT_DRIVERS"}, "environment runtime settings")
    build_sdk = environment_source.get("dotnet", environment_source.get("buildDotnetSdk"))
    environment = {
        key: json.loads(json.dumps(environment_source[key]))
        for key in (
            "schema", "system", "kernel", "machine", "cpuList", "cpuModel", "logicalCpuCount", "governors", "memory",
            "bwrap", "xvfb", "locale", "renderer", "runtimeSettings",
        )
    }
    environment["buildDotnetSdk"] = build_sdk
    environment["verifiedGameRuntime"] = verified_runtime
    result = {
        key: json.loads(json.dumps(source[key]))
        for key in (
            "schema", "officialCommit", "forkCommit", "gameAssemblySha256", "probeAssemblySha256", "commonLauncherSha256",
            "commonDepsSha256", "expectedLoadedCodeMods", "expectedLoadedContentPacks", "expectedSkippedMods", "harnessCommit",
            "calculationMethod",
        )
    }
    result["environment"] = environment
    return result


def percentile(values: list[float], fraction: float) -> float:
    ordered = sorted(values)
    rank = max(1, math.ceil(fraction * len(ordered)))
    return ordered[rank - 1]


def variation(values: list[float | int]) -> dict[str, float | int]:
    numeric = [float(value) for value in values]
    mean = statistics.fmean(numeric)
    return {
        "count": len(numeric),
        "mean": mean,
        "median": statistics.median(numeric),
        "min": min(numeric),
        "max": max(numeric),
        "iqr": percentile(numeric, 0.75) - percentile(numeric, 0.25),
        "coefficientOfVariation": statistics.stdev(numeric) / mean if len(numeric) > 1 and mean else 0.0,
    }


def additional_variation(samples: list[dict[str, Any]]) -> dict[str, dict[str, float | int]]:
    metrics: dict[str, list[float | int]] = {}
    for family, prefix in (
        ("updateMilliseconds", "UpdateMilliseconds"),
        ("baseGameMilliseconds", "BaseGameMilliseconds"),
        ("frameworkEnvelopeMilliseconds", "FrameworkEnvelopeMilliseconds"),
        ("drawMilliseconds", "DrawMilliseconds"),
        ("updateAndDrawMilliseconds", "AccumulatedUpdateAndDrawMillisecondsPerDrawInterval"),
        ("mainThreadAllocatedBytesPerUpdate", "MainThreadAllocatedBytesPerUpdate"),
    ):
        for statistic in ("mean", "p50", "p95", "p99", "max"):
            name = statistic + prefix
            metrics[name[0].lower() + name[1:]] = [sample[family][statistic] for sample in samples]
    for generation in range(3):
        metrics[f"processGc{generation}Collections"] = [sample["processGcCollections"][f"gc{generation}"] for sample in samples]
        metrics[f"coincidentGc{generation}Collections"] = [sample["coincidentGcCollections"][f"gc{generation}"] for sample in samples]
    metrics["processAllocatedBytesPerUpdate"] = [sample["processAllocatedBytesPerUpdate"] for sample in samples]
    metrics["steadyDrawCount"] = [sample["drawMilliseconds"]["count"] for sample in samples]
    metrics["steadyDrawsPerMeasuredSecond"] = [sample["steadyDrawsPerMeasuredSecond"] for sample in samples]
    for threshold, suffix in (("16.667", "16_667"), ("33.333", "33_333"), ("50", "50")):
        metrics[f"slowUpdateCountOver{suffix}Milliseconds"] = [sample["slowUpdateCounts"][threshold] for sample in samples]
        metrics[f"slowUpdatePercentOver{suffix}Milliseconds"] = [
            100.0 * sample["slowUpdateCounts"][threshold] / sample["updateMilliseconds"]["count"]
            for sample in samples
        ]
    for timing, suffix in (("preRunChosenCpuBusyPercent", "PreRunChosenCpuBusyPercent"), ("duringRunChosenCpuBusyPercent", "DuringRunChosenCpuBusyPercent")):
        for statistic in ("mean", "max"):
            name = statistic + suffix
            metrics[name[0].lower() + name[1:]] = [sample[timing][statistic] for sample in samples]
    return {name: variation(values) for name, values in metrics.items()}


def read_metric(sample: dict[str, Any], path: tuple[str, ...]) -> float:
    value: Any = sample
    for key in path:
        value = value[key]
    return float(value)


def paired_metric(left: list[dict[str, Any]], right: list[dict[str, Any]], path: tuple[str, ...]) -> list[dict[str, float | int | None]]:
    left_by_sample = {sample["sample"]: sample for sample in left}
    right_by_sample = {sample["sample"]: sample for sample in right}
    pairs = []
    for sample_number in range(1, 6):
        left_value = read_metric(left_by_sample[sample_number], path)
        right_value = read_metric(right_by_sample[sample_number], path)
        pairs.append({
            "sample": sample_number,
            "left": left_value,
            "right": right_value,
            "absoluteDifference": right_value - left_value,
            "relativeDifferencePercent": 100.0 * (right_value - left_value) / left_value if left_value else None,
        })
    return pairs


def paired_values(left_values: dict[int, float], right_values: dict[int, float]) -> list[dict[str, float | int | None]]:
    pairs = []
    for sample_number in range(1, 6):
        left_value = left_values[sample_number]
        right_value = right_values[sample_number]
        pairs.append({
            "sample": sample_number,
            "left": left_value,
            "right": right_value,
            "absoluteDifference": right_value - left_value,
            "relativeDifferencePercent": 100.0 * (right_value - left_value) / left_value if left_value else None,
        })
    return pairs


def paired_metrics(left: list[dict[str, Any]], right: list[dict[str, Any]]) -> dict[str, list[dict[str, float | int | None]]]:
    paths: dict[str, tuple[str, ...]] = {
        "meanMainThreadAllocatedBytesPerUpdate": ("mainThreadAllocatedBytesPerUpdate", "mean"),
        "processAllocatedBytesPerUpdate": ("processAllocatedBytesPerUpdate",),
        "steadyDrawCount": ("drawMilliseconds", "count"),
        "steadyDrawsPerMeasuredSecond": ("steadyDrawsPerMeasuredSecond",),
    }
    for family, prefix in (
        ("updateMilliseconds", "UpdateMilliseconds"),
        ("baseGameMilliseconds", "BaseGameMilliseconds"),
        ("frameworkEnvelopeMilliseconds", "FrameworkEnvelopeMilliseconds"),
        ("drawMilliseconds", "DrawMilliseconds"),
        ("updateAndDrawMilliseconds", "AccumulatedUpdateAndDrawMillisecondsPerDrawInterval"),
        ("mainThreadAllocatedBytesPerUpdate", "MainThreadAllocatedBytesPerUpdate"),
    ):
        for statistic in ("mean", "p50", "p95", "p99", "max"):
            paths[f"{statistic}{prefix[0].upper()}{prefix[1:]}"] = (family, statistic)
    for source, prefix in (("processGcCollections", "processGc"), ("coincidentGcCollections", "coincidentGc")):
        for generation in range(3):
            paths[f"{prefix}{generation}Collections"] = (source, f"gc{generation}")
    for threshold, suffix in (("16.667", "16_667"), ("33.333", "33_333"), ("50", "50")):
        paths[f"slowUpdateCountOver{suffix}Milliseconds"] = ("slowUpdateCounts", threshold)
    for timing, prefix in (("preRunChosenCpuBusyPercent", "PreRunChosenCpuBusyPercent"), ("duringRunChosenCpuBusyPercent", "DuringRunChosenCpuBusyPercent")):
        for statistic in ("mean", "max"):
            paths[f"{statistic}{prefix[0].upper()}{prefix[1:]}"] = (timing, statistic)
    for transition in TRANSITIONS:
        paths[f"{transition}Milliseconds"] = ("transitionsMilliseconds", transition)
    for phase in STARTUP_PHASES:
        paths[f"{phase}SecondsFromLogStart"] = ("startupPhaseSecondsFromLogStart", phase)
    result = {name: paired_metric(left, right, path) for name, path in paths.items()}
    for threshold, suffix in (("16.667", "16_667"), ("33.333", "33_333"), ("50", "50")):
        left_values = {
            sample["sample"]: 100.0 * sample["slowUpdateCounts"][threshold] / sample["updateMilliseconds"]["count"]
            for sample in left
        }
        right_values = {
            sample["sample"]: 100.0 * sample["slowUpdateCounts"][threshold] / sample["updateMilliseconds"]["count"]
            for sample in right
        }
        result[f"slowUpdatePercentOver{suffix}Milliseconds"] = paired_values(left_values, right_values)
    return result


def complete_variation(samples: list[dict[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = additional_variation(samples)
    for transition in TRANSITIONS:
        result[f"{transition}Milliseconds"] = variation([sample["transitionsMilliseconds"][transition] for sample in samples])
    result["startupPhaseSecondsFromLogStart"] = {
        phase: variation([sample["startupPhaseSecondsFromLogStart"][phase] for sample in samples])
        for phase in STARTUP_PHASES
    }
    return result


def group_samples(summary: dict[str, Any]) -> dict[str, list[dict[str, Any]]]:
    groups: dict[str, list[dict[str, Any]]] = {}
    for name, (series, product, diagnostics) in GROUPS.items():
        samples = [
            sample for sample in summary["samples"]
            if sample["series"] == series
            and sample["product"] == product
            and sample["diagnosticsEnabled"] is diagnostics
        ]
        if len(samples) != 5 or sorted(sample["sample"] for sample in samples) != [1, 2, 3, 4, 5]:
            raise ValueError(f"public summary does not contain five canonical samples for {name}")
        if not all(sample["workload"].get("identityMatched") is True for sample in samples):
            raise ValueError(f"public summary contains an unmatched workload in {name}")
        groups[name] = samples
    if sum(len(samples) for samples in groups.values()) != len(summary["samples"]):
        raise ValueError("public summary contains an unexpected sample")
    main_sequence = [
        (sample["sequence"], sample["product"], sample["sample"])
        for sample in sorted((sample for sample in summary["samples"] if sample["series"] == "main"), key=lambda sample: sample["sequence"])
    ]
    expected_main = [entry for sample in range(1, 6) for entry in ((sample * 2 - 1, "official", sample), (sample * 2, "fork", sample))]
    if main_sequence != expected_main:
        raise ValueError("main samples do not follow the canonical fixed A-before-B order")
    diagnostic_sequence = [
        (sample["sequence"], sample["series"], sample["sample"], sample["diagnosticsEnabled"])
        for sample in sorted((sample for sample in summary["samples"] if sample["series"] != "main"), key=lambda sample: sample["sequence"])
    ]
    expected_diagnostics = [
        (1, "diagnostic-control", 1, False),
        (2, "diagnostic-enabled", 1, True),
        (3, "diagnostic-enabled", 2, True),
        (4, "diagnostic-control", 2, False),
        (5, "diagnostic-control", 3, False),
        (6, "diagnostic-enabled", 3, True),
        (7, "diagnostic-enabled", 4, True),
        (8, "diagnostic-control", 4, False),
        (9, "diagnostic-control", 5, False),
        (10, "diagnostic-enabled", 5, True),
    ]
    if diagnostic_sequence != expected_diagnostics:
        raise ValueError("diagnostic samples do not follow the canonical paired sequence")
    return groups


def build_limitations(groups: dict[str, list[dict[str, Any]]]) -> list[str]:
    official = groups["official"]
    fork = groups["forkDiagnosticsDisabled"]
    official_draw_counts = [sample["drawMilliseconds"]["count"] for sample in official]
    cpu_pairs = paired_metric(official, fork, ("duringRunChosenCpuBusyPercent", "mean"))
    gc1_pairs = paired_metric(official, fork, ("processGcCollections", "gc1"))
    farm_pairs = paired_metric(official, fork, ("transitionsMilliseconds", "warpFarmObserved"))
    higher_cpu = sum(pair["right"] > pair["left"] for pair in cpu_pairs)
    gc1_differences = [int(pair["absoluteDifference"]) for pair in gc1_pairs]
    slower_farm = sum(pair["right"] > pair["left"] for pair in farm_pairs)
    return list(BASE_LIMITATIONS) + [
        "Draw cadence and update-and-draw distributions were measured under Xvfb with llvmpipe software rendering; "
        f"they are renderer diagnostics, not desktop FPS. Official steady draw counts varied from {min(official_draw_counts):,} to {max(official_draw_counts):,} per run.",
        DRAW_TAIL_LIMITATION,
        f"Chosen-core mean busy time was higher for the fork in {higher_cpu} of 5 main pairs and headless draw cadence was much higher; "
        "these captures do not show lower CPU use, lower power, or general efficiency. Busy time includes llvmpipe and other host work.",
        ORDER_LIMITATION,
        f"Fork process-wide Gen1 collections were {min(gc1_differences)}–{max(gc1_differences)} higher across the main pairs, while Farm warp-observed timing "
        f"was slower in {slower_farm} of 5 pairs; GC pause duration was not measured and warp observations were noisy, so neither is classified as a confirmed regression.",
    ]


def metric(summary: dict[str, Any], group: str, name: str) -> dict[str, float | int]:
    return summary["runVariation"][group][name]


def median(summary: dict[str, Any], group: str, name: str) -> float:
    return float(metric(summary, group, name)["median"])


def render_markdown(summary: dict[str, Any]) -> str:
    official = "official"
    fork = "forkDiagnosticsDisabled"
    control = "forkDiagnosticControls"
    enabled = "forkDiagnosticsEnabled"
    paired_main = [float(pair["relativeDifferencePercent"]) for pair in summary["pairedMetrics"]["officialToFork"]["meanUpdateMilliseconds"]]
    paired_diagnostics = [float(pair["relativeDifferencePercent"]) for pair in summary["pairedMetrics"]["forkDiagnosticsDisabledToEnabled"]["meanUpdateMilliseconds"]]
    lower_update_pairs = sum(difference < 0 for difference in paired_main)
    faster_save_pairs = sum(pair["right"] < pair["left"] for pair in summary["pairedMetrics"]["officialToFork"]["gameLaunchedToSaveLoadedMilliseconds"])
    slower_farm_pairs = sum(pair["right"] > pair["left"] for pair in summary["pairedMetrics"]["officialToFork"]["warpFarmObservedMilliseconds"])
    higher_cpu_pairs = sum(pair["right"] > pair["left"] for pair in summary["pairedMetrics"]["officialToFork"]["meanDuringRunChosenCpuBusyPercent"])
    lines = [
        "# Linux 4.5.2 A/B benchmark results", "",
        "These are descriptive one-machine results for one pinned private workload, not universal FPS claims. "
        "Each value below is the median of five separate full-duration processes unless stated otherwise.", "",
        "## Update and draw timing", "",
        "| Metric | Official 4.5.2 | Fork, diagnostics disabled |",
        "| --- | ---: | ---: |",
        f"| Mean update | {median(summary, official, 'meanUpdateMilliseconds'):.3f} ms | {median(summary, fork, 'meanUpdateMilliseconds'):.3f} ms |",
        f"| p50 update | {median(summary, official, 'p50UpdateMilliseconds'):.3f} ms | {median(summary, fork, 'p50UpdateMilliseconds'):.3f} ms |",
        f"| p95 update | {median(summary, official, 'p95UpdateMilliseconds'):.3f} ms | {median(summary, fork, 'p95UpdateMilliseconds'):.3f} ms |",
        f"| p99 update | {median(summary, official, 'p99UpdateMilliseconds'):.3f} ms | {median(summary, fork, 'p99UpdateMilliseconds'):.3f} ms |",
        f"| Maximum update | {median(summary, official, 'maxUpdateMilliseconds'):.3f} ms | {median(summary, fork, 'maxUpdateMilliseconds'):.3f} ms |",
        f"| Mean framework envelope | {median(summary, official, 'meanFrameworkEnvelopeMilliseconds'):.3f} ms | {median(summary, fork, 'meanFrameworkEnvelopeMilliseconds'):.3f} ms |",
        f"| Accumulated measured update+draw elapsed duration per draw interval, mean | {median(summary, official, 'meanAccumulatedUpdateAndDrawMillisecondsPerDrawInterval'):.3f} ms | {median(summary, fork, 'meanAccumulatedUpdateAndDrawMillisecondsPerDrawInterval'):.3f} ms |", "",
        f"Fork mean update time was lower in {lower_update_pairs} of 5 paired runs; paired differences ranged from {min(paired_main):.1f}% to {max(paired_main):.1f}% "
        f"(mean {statistics.fmean(paired_main):.1f}%). The framework envelope includes identical probe overhead and observed mod callbacks dispatched outside the base-game window.", "",
        "## Allocation, GC, and slow updates", "",
        "| Metric | Official 4.5.2 | Fork, diagnostics disabled |",
        "| --- | ---: | ---: |",
        f"| Main-thread allocation/update | {median(summary, official, 'meanMainThreadAllocatedBytesPerUpdate') / 1024:.1f} KiB | {median(summary, fork, 'meanMainThreadAllocatedBytesPerUpdate') / 1024:.1f} KiB |",
        f"| Process allocation/update | {median(summary, official, 'processAllocatedBytesPerUpdate') / 1024:.1f} KiB | {median(summary, fork, 'processAllocatedBytesPerUpdate') / 1024:.1f} KiB |",
        f"| Process GC0 collections/180 s | {median(summary, official, 'processGc0Collections'):.0f} | {median(summary, fork, 'processGc0Collections'):.0f} |",
        f"| Process GC1 collections/180 s | {median(summary, official, 'processGc1Collections'):.0f} | {median(summary, fork, 'processGc1Collections'):.0f} |",
        f"| Process GC2 collections/180 s | {median(summary, official, 'processGc2Collections'):.0f} | {median(summary, fork, 'processGc2Collections'):.0f} |",
        f"| Updates over 16.667 ms | {median(summary, official, 'slowUpdatePercentOver16_667Milliseconds'):.2f}% | {median(summary, fork, 'slowUpdatePercentOver16_667Milliseconds'):.2f}% |",
        f"| Updates over 33.333 ms | {median(summary, official, 'slowUpdatePercentOver33_333Milliseconds'):.2f}% | {median(summary, fork, 'slowUpdatePercentOver33_333Milliseconds'):.2f}% |",
        f"| Updates over 50 ms | {median(summary, official, 'slowUpdatePercentOver50Milliseconds'):.3f}% | {median(summary, fork, 'slowUpdatePercentOver50Milliseconds'):.3f}% |", "",
        "GC counts are process-wide correlations, not attribution to SMAPI or a mod.", "",
        "## Startup, save loading, and transitions", "",
        "| Boundary | Official 4.5.2 | Fork, diagnostics disabled |",
        "| --- | ---: | ---: |",
        f"| Probe entry to game launched | {median(summary, official, 'probeEntryToGameLaunchedMilliseconds') / 1000:.3f} s | {median(summary, fork, 'probeEntryToGameLaunchedMilliseconds') / 1000:.3f} s |",
        f"| Game launched to save loaded | {median(summary, official, 'gameLaunchedToSaveLoadedMilliseconds') / 1000:.3f} s | {median(summary, fork, 'gameLaunchedToSaveLoadedMilliseconds') / 1000:.3f} s |",
        f"| Town warp observed | {median(summary, official, 'warpTownObservedMilliseconds'):.1f} ms | {median(summary, fork, 'warpTownObservedMilliseconds'):.1f} ms |",
        f"| Town warp settled | {median(summary, official, 'warpTownSettledMilliseconds'):.1f} ms | {median(summary, fork, 'warpTownSettledMilliseconds'):.1f} ms |",
        f"| Farm warp observed | {median(summary, official, 'warpFarmObservedMilliseconds'):.1f} ms | {median(summary, fork, 'warpFarmObservedMilliseconds'):.1f} ms |",
        f"| Farm warp settled | {median(summary, official, 'warpFarmSettledMilliseconds'):.1f} ms | {median(summary, fork, 'warpFarmSettledMilliseconds'):.1f} ms |", "",
        f"The fork loaded the save faster in {faster_save_pairs} of 5 fixed-order pairs, but A always preceded B, so the magnitude cannot be separated from order and cache warming. Individual observed warp boundaries were noisy; Farm-observed timing was slower for the fork in {slower_farm_pairs} of 5 pairs. Settled durations and full per-run ranges are retained in `summary.json`.", "",
        "## Diagnostic overhead", "",
        "| Metric | Disabled control | Enabled |",
        "| --- | ---: | ---: |",
        f"| Mean update | {median(summary, control, 'meanUpdateMilliseconds'):.3f} ms | {median(summary, enabled, 'meanUpdateMilliseconds'):.3f} ms |",
        f"| p95 update | {median(summary, control, 'p95UpdateMilliseconds'):.3f} ms | {median(summary, enabled, 'p95UpdateMilliseconds'):.3f} ms |",
        f"| Main-thread allocation/update | {median(summary, control, 'meanMainThreadAllocatedBytesPerUpdate') / 1024:.1f} KiB | {median(summary, enabled, 'meanMainThreadAllocatedBytesPerUpdate') / 1024:.1f} KiB |", "",
        f"Paired mean-update overhead ranged from {min(paired_diagnostics):.1f}% to {max(paired_diagnostics):.1f}% (mean {statistics.fmean(paired_diagnostics):.1f}%).", "",
        "## Host CPU and headless draw cadence", "",
        "| Metric | Official 4.5.2 | Fork, diagnostics disabled |",
        "| --- | ---: | ---: |",
        f"| Selected-core mean busy time | {median(summary, official, 'meanDuringRunChosenCpuBusyPercent'):.1f}% | {median(summary, fork, 'meanDuringRunChosenCpuBusyPercent'):.1f}% |",
        f"| Headless steady draws/second | {median(summary, official, 'steadyDrawsPerMeasuredSecond'):.2f} | {median(summary, fork, 'steadyDrawsPerMeasuredSecond'):.2f} |", "",
        f"Selected-core busy time was higher for the fork in {higher_cpu_pairs} of 5 pairs and coincided with the much higher Xvfb/llvmpipe draw cadence. These captures do not support claims of lower CPU use, lower power, general efficiency, or desktop FPS.", "",
        "## Variation and limitations", "",
        f"Median-run mean-update CV was {metric(summary, official, 'meanUpdateMilliseconds')['coefficientOfVariation']:.3f} for official and "
        f"{metric(summary, fork, 'meanUpdateMilliseconds')['coefficientOfVariation']:.3f} for the fork. "
        f"Steady draw counts ranged from {metric(summary, official, 'steadyDrawCount')['min']:.0f}–{metric(summary, official, 'steadyDrawCount')['max']:.0f} official and "
        f"{metric(summary, fork, 'steadyDrawCount')['min']:.0f}–{metric(summary, fork, 'steadyDrawCount')['max']:.0f} fork.", "",
    ]
    lines.extend(f"- {limitation}" for limitation in summary["limitations"])
    lines.extend(["", "See `summary.json` for every per-run distribution, cross-run variation, paired difference, allocation/GC count, slow-update count, transition, environment field, exact commit, and calculation method. The `raw/` files retain the sanitized numeric records.", ""])
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--results", required=True)
    args = parser.parse_args()
    expected = Path(__file__).resolve().parent / "results"
    results = Path(args.results).expanduser().resolve(strict=True)
    if results != expected:
        raise ValueError(f"results must be exactly {expected}")
    expected_entries = {"summary.json", "summary.md", "environment.json", "runtime-provenance.json", "raw"}
    entries = {path.name: path for path in results.iterdir()}
    if set(entries) != expected_entries or entries["raw"].is_symlink() or not entries["raw"].is_dir():
        raise ValueError("public results contain an unexpected top-level entry")
    if any(entries[name].is_symlink() or not entries[name].is_file() for name in expected_entries - {"raw"}):
        raise ValueError("public result documents must be regular non-symlink files")
    summary_path = results / "summary.json"
    source = json.loads(summary_path.read_text(encoding="utf-8"))
    allowed_top = {"schema", "metadata", "samples", "runVariation", "pairedOfficialToFork", "pairedForkDisabledToEnabled", "limitations", "pairedMetrics", "metricSemantics"}
    if not isinstance(source, dict) or not {"schema", "metadata", "samples", "limitations"}.issubset(source) or not set(source).issubset(allowed_top) or source["schema"] != 1:
        raise ValueError("unexpected public result schema")
    allowed_limitations = set(BASE_LIMITATIONS) | {DRAW_LIMITATION, DRAW_TAIL_LIMITATION, CPU_LIMITATION, ORDER_LIMITATION, ADVERSE_SIGNAL_LIMITATION}
    dynamic_prefixes = ("Draw cadence and update-and-draw distributions", "Chosen-core mean busy time", "Fork process-wide Gen1 collections")
    if not isinstance(source["limitations"], list) or any(
        not isinstance(limitation, str) or (limitation not in allowed_limitations and not limitation.startswith(dynamic_prefixes))
        for limitation in source["limitations"]
    ):
        raise ValueError("unexpected public result limitation")
    raw_samples, suite_environment_sha256 = validate_raw_results(results / "raw")
    verified_runtime = load_runtime_provenance(results, suite_environment_sha256)
    metadata = public_metadata(source["metadata"], verified_runtime)
    environment_path = results / "environment.json"
    environment_source = json.loads(environment_path.read_text(encoding="utf-8"))
    if public_metadata(environment_source, verified_runtime) != metadata:
        raise ValueError("summary and environment metadata differ")
    samples = json.loads(json.dumps(source["samples"]))
    if not isinstance(samples, list) or len(samples) != 20:
        raise ValueError("public summary must contain exactly 20 samples")
    for sample in samples:
        validate_sample(sample)
    require_samples_match(samples, raw_samples)
    samples = raw_samples
    summary = {"schema": 1, "metadata": metadata, "samples": samples}
    groups = group_samples(summary)
    public_ids = set()
    for sample in summary["samples"]:
        sample["publicSampleId"] = f"{sample['series']}:{sample['label']}"
        public_ids.add(sample["publicSampleId"])
    if len(public_ids) != len(summary["samples"]):
        raise ValueError("public sample identifiers are not unique")
    summary["runVariation"] = {name: complete_variation(samples) for name, samples in groups.items()}
    summary["pairedMetrics"] = {
        "officialToFork": paired_metrics(groups["official"], groups["forkDiagnosticsDisabled"]),
        "forkDiagnosticsDisabledToEnabled": paired_metrics(groups["forkDiagnosticControls"], groups["forkDiagnosticsEnabled"]),
    }
    summary["metricSemantics"] = {
        "updateAndDrawMilliseconds": "Per draw interval: elapsed duration spent in the measured draw call plus elapsed duration in all measured outer-update calls since the previous draw. This includes descheduling and is not CPU time, wall-frame latency, FPS, or a conventional per-frame duration.",
        "frameworkEnvelopeMilliseconds": "Outer-update elapsed duration minus the measured base-game update window; includes descheduling, identical probe overhead, and observed mod callbacks outside that base-game window.",
        "chosenCpuBusyPercent": "Host busy percentage sampled over the selected CPU set; includes game, runtime, Xvfb/llvmpipe, and other host work and is not SMAPI-only attribution.",
        "gcCollections": "Process-wide collection-count deltas correlated with a run, not pause duration or attribution to SMAPI or a mod.",
    }
    summary["limitations"] = build_limitations(groups)
    summary["metadata"]["calculationMethod"] = "nearest-rank per-run percentiles; descriptive median and variation across five separate processes; paired differences match preregistered sample numbers; slow-update percentages normalize counts by accepted update count"
    summary["metadata"]["publicSummary"] = {
        "scriptSha256": sha256(Path(__file__).resolve()),
        "calculationMethod": summary["metadata"]["calculationMethod"],
    }
    public_environment = json.loads(json.dumps(summary["metadata"]))
    markdown = render_markdown(summary)
    atomic_write_texts({
        summary_path: json.dumps(summary, indent=2) + "\n",
        results / "summary.md": markdown,
        environment_path: json.dumps(public_environment, indent=2) + "\n",
    })
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"result publication failed: {error}")
        raise SystemExit(1)
