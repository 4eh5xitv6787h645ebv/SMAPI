#!/usr/bin/env python3
"""Create allowlisted numeric raw data and distribution summaries from private A/B runs."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import statistics
import sys
from typing import Any

import run_ab as runner


REQUIRED_MARKERS = (
    "probe_entry", "game_launched", "save_loaded", "steady_state_start", "steady_state_end",
    "warp_town_start", "warp_town_complete", "warp_town_settled", "warp_farm_start", "warp_farm_complete", "warp_farm_settled",
    "normal_exit_requested", "game_exiting",
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def percentile(values: list[float], fraction: float) -> float:
    if not values:
        raise ValueError("cannot calculate a percentile of an empty series")
    ordered = sorted(values)
    rank = max(1, math.ceil(fraction * len(ordered)))
    return ordered[rank - 1]


def distribution(values: list[float]) -> dict[str, float | int]:
    ordered = sorted(values)
    return {
        "count": len(ordered),
        "mean": statistics.fmean(ordered),
        "p50": percentile(ordered, 0.50),
        "p95": percentile(ordered, 0.95),
        "p99": percentile(ordered, 0.99),
        "max": ordered[-1],
    }


def run_variation(values: list[float]) -> dict[str, float | int]:
    ordered = sorted(values)
    mean = statistics.fmean(ordered)
    q1 = percentile(ordered, 0.25)
    q3 = percentile(ordered, 0.75)
    return {
        "count": len(ordered),
        "mean": mean,
        "median": statistics.median(ordered),
        "min": ordered[0],
        "max": ordered[-1],
        "iqr": q3 - q1,
        "coefficientOfVariation": statistics.stdev(ordered) / mean if len(ordered) > 1 and mean else 0.0,
    }


def sanitized_raw_filename(series: str, label: str) -> str:
    if not re.fullmatch(r"[a-z-]+", series) or not re.fullmatch(r"[0-9]{2}-[ab][0-9]+(?:-diagnostics)?", label):
        raise ValueError("invalid sanitized raw identity")
    return f"{series}-{label}.jsonl"


def validate_final_plan(plan: dict[str, Any]) -> None:
    if plan.get("schema") != 1 or plan.get("kind") != "final" or plan.get("samples", 0) < 5:
        raise ValueError("missing or invalid final suite plan")
    if plan.get("start") not in ("a", "b"):
        raise ValueError("invalid final suite starting product")
    canonical_main = [list(entry) for entry in runner.sample_plan(plan["samples"], plan["start"])]
    canonical_diagnostics = [list(entry) for entry in runner.diagnostic_plan(plan["samples"])]
    if plan.get("main") != canonical_main or plan.get("diagnostics") != canonical_diagnostics:
        raise ValueError("final suite plan differs from the canonical preregistered sequence")


def load_probe(path: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    records = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
    if not records or records[0].get("type") != "header" or records[0].get("schema") != 1:
        raise ValueError(f"invalid probe header: {path}")
    allowed: list[dict[str, Any]] = []
    header = records[0]
    allowed.append({key: header[key] for key in (
        "type", "schema", "probeVersion", "stopwatchFrequency", "warmupSeconds", "measurementSeconds",
        "transitionSettleTicks", "updateCapacity", "drawCapacity", "recordedUpdates", "recordedDraws",
        "bufferOverflow", "expectedSaveLoaded", "invalidWorldStateTicks", "locationChangedTicks",
        "positionChangedTicks", "gameTimeAtSteadyStart", "gameTimeAtSteadyEnd",
    )})
    for record in records[1:]:
        record_type = record.get("type")
        if record_type == "marker":
            if record.get("name") not in REQUIRED_MARKERS:
                raise ValueError("unexpected marker name")
            allowed.append({key: record[key] for key in ("type", "name", "elapsedTicks")})
        elif record_type == "phaseTotals":
            keys = ["type"] + [f"{phase}{metric}" for phase in ("entry", "steadyStart", "steadyEnd", "exit") for metric in ("AllocatedBytes", "Gc0", "Gc1", "Gc2")]
            allowed.append({key: record[key] for key in keys})
        elif record_type == "update":
            allowed.append({key: record[key] for key in ("type", "phase", "elapsedTicks", "baseGameTicks", "allocatedBytes", "gc0", "gc1", "gc2")})
        elif record_type == "draw":
            allowed.append({key: record[key] for key in ("type", "phase", "capturedAtTicks", "drawTicks", "updateTicks", "updateCount")})
        else:
            raise ValueError(f"unexpected probe record type: {record_type}")
    return header, allowed


def summarize_sample(run_root: Path, raw_root: Path) -> dict[str, Any]:
    private = json.loads((run_root / "sample.json").read_text(encoding="utf-8"))
    header, records = load_probe(run_root / "probe.jsonl")
    frequency = header["stopwatchFrequency"]
    markers = {record["name"]: record["elapsedTicks"] for record in records if record["type"] == "marker"}
    updates = [record for record in records if record["type"] == "update" and record["phase"] == "steady"]
    draws = [record for record in records if record["type"] == "draw" and record["phase"] == "steady"]
    totals = next(record for record in records if record["type"] == "phaseTotals")
    to_ms = 1000.0 / frequency
    update_ms = [record["elapsedTicks"] * to_ms for record in updates]
    framework_ms = [(record["elapsedTicks"] - record["baseGameTicks"]) * to_ms for record in updates]
    base_game_ms = [record["baseGameTicks"] * to_ms for record in updates]
    allocation_bytes = [float(record["allocatedBytes"]) for record in updates]
    draw_ms = [record["drawTicks"] * to_ms for record in draws]
    update_draw_ms = [(record["drawTicks"] + record["updateTicks"]) * to_ms for record in draws]

    product = "fork" if private["product"] == "b" else "official"
    label = private["label"]
    raw_path = raw_root / sanitized_raw_filename(private["series"], label)
    if raw_path.exists():
        raise ValueError(f"duplicate sanitized raw destination: {raw_path.name}")
    with raw_path.open("w", encoding="utf-8", newline="\n") as stream:
        raw_header = {
            "type": "sample",
            "schema": 1,
            "label": label,
            "sequence": private["sequence"],
            "product": product,
            "sample": private["sample"],
            "diagnosticsEnabled": private["diagnosticsEnabled"],
            "series": private["series"],
            "commit": private["commit"],
            "smapiAssemblySha256": private["smapiAssemblySha256"],
            "gameAssemblySha256": private["gameAssemblySha256"],
            "probeAssemblySha256": private["probeAssemblySha256"],
            "suiteEnvironmentSha256": private["suiteEnvironmentSha256"],
            "started": private["started"],
            "finished": private["finished"],
            "displaySession": private["displaySession"],
            "cpuList": private["cpuList"],
            "preRunChosenCpuBusyPercent": private["preRunChosenCpuBusyPercent"],
            "duringRunChosenCpuBusyPercent": private["duringRunChosenCpuBusyPercent"],
            "loadAverage": private["loadAverage"],
            "temperatureCelsiusBefore": private["temperatureCelsiusBefore"],
            "temperatureCelsiusAfter": private["temperatureCelsiusAfter"],
            "startupPhaseSecondsFromLogStart": private["log"]["startupPhaseSecondsFromLogStart"],
            "loadedCodeMods": private["log"]["loadedCodeMods"],
            "loadedContentPacks": private["log"]["loadedContentPacks"],
            "skippedModCount": private["log"]["skippedModCount"],
            "smapiVersion": private["log"]["smapiVersion"],
            "gameVersion": private["log"]["gameVersion"],
            "resolution": private["log"]["resolution"],
        }
        stream.write(json.dumps(raw_header, separators=(",", ":")) + "\n")
        for record in records:
            stream.write(json.dumps(record, separators=(",", ":")) + "\n")

    steady_process_allocated = totals["steadyEndAllocatedBytes"] - totals["steadyStartAllocatedBytes"]
    steady_gc = {f"gc{generation}": totals[f"steadyEndGc{generation}"] - totals[f"steadyStartGc{generation}"] for generation in range(3)}
    return {
        "label": label,
        "sequence": private["sequence"],
        "product": product,
        "sample": private["sample"],
        "diagnosticsEnabled": private["diagnosticsEnabled"],
        "series": private["series"],
        "steadySeconds": private["probe"]["steadySeconds"],
        "updateMilliseconds": distribution(update_ms),
        "baseGameMilliseconds": distribution(base_game_ms),
        "frameworkEnvelopeMilliseconds": distribution(framework_ms),
        "drawMilliseconds": distribution(draw_ms),
        "updateAndDrawMilliseconds": distribution(update_draw_ms),
        "steadyDrawsPerMeasuredSecond": len(draws) / private["probe"]["steadySeconds"],
        "mainThreadAllocatedBytesPerUpdate": distribution(allocation_bytes),
        "processAllocatedBytesPerUpdate": steady_process_allocated / len(updates),
        "processGcCollections": steady_gc,
        "coincidentGcCollections": {f"gc{generation}": sum(record[f"gc{generation}"] for record in updates) for generation in range(3)},
        "slowUpdateCounts": {threshold: sum(value > float(threshold) for value in update_ms) for threshold in ("16.667", "33.333", "50")},
        "transitionsMilliseconds": {
            "probeEntryToGameLaunched": (markers["game_launched"] - markers["probe_entry"]) * to_ms,
            "gameLaunchedToSaveLoaded": (markers["save_loaded"] - markers["game_launched"]) * to_ms,
            "warpTownObserved": (markers["warp_town_complete"] - markers["warp_town_start"]) * to_ms,
            "warpTownSettled": (markers["warp_town_settled"] - markers["warp_town_start"]) * to_ms,
            "warpFarmObserved": (markers["warp_farm_complete"] - markers["warp_farm_start"]) * to_ms,
            "warpFarmSettled": (markers["warp_farm_settled"] - markers["warp_farm_start"]) * to_ms,
        },
        "startupPhaseSecondsFromLogStart": private["log"]["startupPhaseSecondsFromLogStart"],
        "workload": {
            "loadedCodeMods": private["log"]["loadedCodeMods"],
            "loadedContentPacks": private["log"]["loadedContentPacks"],
            "skippedModCount": private["log"]["skippedModCount"],
            "identityMatched": True,
        },
        "preRunChosenCpuBusyPercent": private["preRunChosenCpuBusyPercent"],
        "duringRunChosenCpuBusyPercent": private["duringRunChosenCpuBusyPercent"],
    }


def aggregate(samples: list[dict[str, Any]]) -> dict[str, Any]:
    metrics = {
        "meanUpdateMilliseconds": [sample["updateMilliseconds"]["mean"] for sample in samples],
        "p95UpdateMilliseconds": [sample["updateMilliseconds"]["p95"] for sample in samples],
        "p99UpdateMilliseconds": [sample["updateMilliseconds"]["p99"] for sample in samples],
        "maxUpdateMilliseconds": [sample["updateMilliseconds"]["max"] for sample in samples],
        "meanFrameworkEnvelopeMilliseconds": [sample["frameworkEnvelopeMilliseconds"]["mean"] for sample in samples],
        "meanUpdateAndDrawMilliseconds": [sample["updateAndDrawMilliseconds"]["mean"] for sample in samples],
        "steadyDrawCount": [sample["drawMilliseconds"]["count"] for sample in samples],
        "steadyDrawsPerMeasuredSecond": [sample["steadyDrawsPerMeasuredSecond"] for sample in samples],
        "meanMainThreadAllocatedBytesPerUpdate": [sample["mainThreadAllocatedBytesPerUpdate"]["mean"] for sample in samples],
        "processAllocatedBytesPerUpdate": [sample["processAllocatedBytesPerUpdate"] for sample in samples],
        "warpTownObservedMilliseconds": [sample["transitionsMilliseconds"]["warpTownObserved"] for sample in samples],
        "warpTownSettledMilliseconds": [sample["transitionsMilliseconds"]["warpTownSettled"] for sample in samples],
        "warpFarmObservedMilliseconds": [sample["transitionsMilliseconds"]["warpFarmObserved"] for sample in samples],
        "warpFarmSettledMilliseconds": [sample["transitionsMilliseconds"]["warpFarmSettled"] for sample in samples],
        "probeEntryToGameLaunchedMilliseconds": [sample["transitionsMilliseconds"]["probeEntryToGameLaunched"] for sample in samples],
        "gameLaunchedToSaveLoadedMilliseconds": [sample["transitionsMilliseconds"]["gameLaunchedToSaveLoaded"] for sample in samples],
    }
    aggregated = {name: run_variation(values) for name, values in metrics.items()}
    phase_names = tuple(samples[0]["startupPhaseSecondsFromLogStart"])
    aggregated["startupPhaseSecondsFromLogStart"] = {
        name: run_variation([sample["startupPhaseSecondsFromLogStart"][name] for sample in samples])
        for name in phase_names
    }
    return aggregated


def paired_differences(left: list[dict[str, Any]], right: list[dict[str, Any]]) -> list[dict[str, Any]]:
    by_sample_left = {sample["sample"]: sample for sample in left}
    by_sample_right = {sample["sample"]: sample for sample in right}
    pairs: list[dict[str, Any]] = []
    for sample_number in sorted(set(by_sample_left) & set(by_sample_right)):
        left_value = by_sample_left[sample_number]["updateMilliseconds"]["mean"]
        right_value = by_sample_right[sample_number]["updateMilliseconds"]["mean"]
        pairs.append({
            "sample": sample_number,
            "leftMeanUpdateMilliseconds": left_value,
            "rightMeanUpdateMilliseconds": right_value,
            "absoluteDifferenceMilliseconds": right_value - left_value,
            "relativeDifferencePercent": 100.0 * (right_value - left_value) / left_value if left_value else None,
        })
    return pairs


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--private-root", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    private_root = Path(args.private_root).expanduser().resolve(strict=True)
    final_output = Path(args.output).expanduser().resolve(strict=False)
    repo = Path(__file__).resolve().parents[2]
    expected_parent = repo / "benchmarks" / "linux-real-world" / "results"
    if final_output != expected_parent:
        raise ValueError(f"output must be exactly {expected_parent}")
    if final_output.exists():
        raise ValueError("results output already exists; move the old evidence aside before regenerating")
    if private_root.stat().st_dev != final_output.parent.stat().st_dev:
        raise ValueError("private root and repository output must share a filesystem for atomic promotion")
    prepared = json.loads((private_root / "metadata.json").read_text(encoding="utf-8"))
    if sha256(Path(__file__).resolve()) != prepared["analyzerScriptSha256"]:
        raise ValueError("analyzer script differs from the prepared committed harness")
    if sha256(Path(__file__).with_name("run_ab.py")) != prepared["runnerScriptSha256"]:
        raise ValueError("runner script differs from the prepared committed harness")
    if sha256(Path(__file__).with_name("harness_common.py")) != prepared["commonScriptSha256"]:
        raise ValueError("common harness helpers differ from the prepared committed harness")
    plan = json.loads((private_root / "suite-plan.json").read_text(encoding="utf-8"))
    validate_final_plan(plan)
    for sequence, (product, sample, diagnostics, series) in enumerate(
        (("a", 1, False, "preflight"), ("b", 1, False, "preflight")), start=1
    ):
        label = f"{sequence:02d}-{product}{sample}"
        runner.validate_saved_sample(
            private_root / "preflight-runs" / label,
            prepared,
            label,
            sequence,
            product,
            sample,
            diagnostics,
            series,
        )
    expected_runs: list[Path] = []
    for group, entries in (("runs", plan["main"]), ("diagnostic-runs", plan["diagnostics"])):
        for sequence, entry in enumerate(entries, start=1):
            product, sample, diagnostics, series = entry
            label = f"{sequence:02d}-{product}{sample}" + ("-diagnostics" if diagnostics else "")
            run_root = private_root / group / label
            runner.validate_saved_sample(run_root, prepared, label, sequence, product, sample, diagnostics, series)
            expected_runs.append(run_root)
    if len(plan["main"]) != plan["samples"] * 2 or len(plan["diagnostics"]) != plan["samples"] * 2:
        raise ValueError("suite plan does not contain exact main and diagnostic pairs")

    output_root = private_root / f"sanitized-results-staging-{os.getpid()}"
    if output_root.exists():
        raise ValueError("private sanitized staging output already exists")
    raw_root = output_root / "raw"
    raw_root.mkdir(mode=0o755, parents=True)

    samples = [summarize_sample(run_root, raw_root) for run_root in expected_runs]
    official = [sample for sample in samples if sample["series"] == "main" and sample["product"] == "official"]
    fork = [sample for sample in samples if sample["series"] == "main" and sample["product"] == "fork"]
    diagnostic_control = [sample for sample in samples if sample["series"] == "diagnostic-control"]
    diagnostic = [sample for sample in samples if sample["series"] == "diagnostic-enabled"]
    expected_count = plan["samples"]
    if any(len(group) != expected_count for group in (official, fork, diagnostic_control, diagnostic)):
        raise ValueError("analysis requires the exact planned official/fork/diagnostic sample sets")

    environment_private = json.loads((private_root / "environment.json").read_text(encoding="utf-8"))
    environment = {key: environment_private[key] for key in (
        "schema", "system", "kernel", "machine", "cpuList", "cpuModel", "logicalCpuCount", "governors",
        "memory", "dotnet", "bwrap", "xvfb", "locale", "renderer",
        "runtimeSettings",
    )}
    metadata = {
        "schema": 1,
        "officialCommit": prepared["officialCommit"],
        "forkCommit": prepared["forkCommit"],
        "gameAssemblySha256": prepared["gameAssemblySha256"],
        "modpackArchiveSha256": prepared["modpackArchiveSha256"],
        "saveArchiveSha256": prepared["saveArchiveSha256"],
        "probeAssemblySha256": prepared["probeAssemblySha256"],
        "commonLauncherSha256": prepared["commonLauncherSha256"],
        "commonDepsSha256": prepared["commonDepsSha256"],
        "expectedLoadedCodeMods": prepared["expectedLoadedCodeMods"],
        "expectedLoadedContentPacks": prepared["expectedLoadedContentPacks"],
        "expectedSkippedMods": prepared["expectedSkippedMods"],
        "harnessCommit": prepared["harnessCommit"],
        "calculationMethod": "nearest-rank tick percentiles; per-run metrics aggregated descriptively across independent processes",
        "environment": environment,
    }
    summary = {
        "schema": 1,
        "metadata": metadata,
        "samples": samples,
        "runVariation": {
            "official": aggregate(official),
            "forkDiagnosticsDisabled": aggregate(fork),
            "forkDiagnosticControls": aggregate(diagnostic_control),
            "forkDiagnosticsEnabled": aggregate(diagnostic),
        },
        "pairedOfficialToFork": paired_differences(official, fork),
        "pairedForkDisabledToEnabled": paired_differences(diagnostic_control, diagnostic),
        "limitations": [
            "One shared Linux workstation and one private workload; results are not universal FPS claims.",
            "The framework envelope includes mod callbacks dispatched outside the base-game window and identical probe overhead.",
            "GC counts are process-wide correlations, not attribution; coincident counts cover only outer updates.",
            "SMAPI startup log boundaries have one-second timestamp resolution.",
            "Shared-host load and filesystem caches cannot be eliminated completely; per-run variation is retained.",
            "Probe startup timing begins at mod entry and cannot observe native launcher/runtime work before that boundary.",
            "Tiered compilation is disabled to avoid a reproducible .NET 6 JIT crash in this workload, so results do not represent the default tiered-runtime configuration.",
            "A null audio backend is used consistently because the isolated Xvfb session has no audio device.",
        ],
    }
    (output_root / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    (output_root / "environment.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")

    markdown = [
        "# Linux 4.5.2 A/B benchmark results", "",
        "These are descriptive one-machine results for the pinned private workload, not universal FPS claims.", "",
        "| Product | samples | median run mean update (ms) | min–max | run CV |",
        "| --- | ---: | ---: | ---: | ---: |",
    ]
    for label, group in (("Official 4.5.2", official), ("Fork, diagnostics disabled", fork), ("Fork, diagnostics enabled", diagnostic)):
        variation = aggregate(group)["meanUpdateMilliseconds"]
        markdown.append(f"| {label} | {variation['count']} | {variation['median']:.3f} | {variation['min']:.3f}–{variation['max']:.3f} | {variation['coefficientOfVariation']:.3f} |")
    markdown.extend(["", "See `summary.json` for all per-run distributions, allocation/GC data, transitions, paired differences, and limitations.", ""])
    (output_root / "summary.md").write_text("\n".join(markdown), encoding="utf-8")

    for path in output_root.rglob("*"):
        if path.is_file():
            text = path.read_text(encoding="utf-8")
            if any(token in text for token in ("/home/", "Blossom_", "PRIVATE_", "Mods-2026", "SaveGameInfo")):
                raise ValueError(f"privacy scan rejected generated output: {path}")
    output_root.rename(final_output)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"analysis failed: {error}", file=sys.stderr)
        raise SystemExit(1)
