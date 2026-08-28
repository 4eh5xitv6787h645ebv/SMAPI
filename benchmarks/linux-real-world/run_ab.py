#!/usr/bin/env python3
"""Run strict alternating A/B samples in disposable Linux desktop roots."""

from __future__ import annotations

import argparse
from datetime import datetime
import hashlib
import json
import os
import platform
from pathlib import Path
import re
import signal
import shutil
import subprocess
import sys
import time
from typing import Any

from harness_common import load_jsonc


EXPECTED_RESOLUTION = "1280x720"
REQUIRED_STARTUP_PHASES = (
    "logStarted", "waitingForGame", "maliciousScan", "metadataLoad", "assemblyLoad", "entryLaunch", "modsReady", "contentReady",
)
REQUIRED_MARKERS = (
    "probe_entry",
    "game_launched",
    "save_loaded",
    "steady_state_start",
    "steady_state_end",
    "warp_town_start",
    "warp_town_complete",
    "warp_town_settled",
    "warp_farm_start",
    "warp_farm_complete",
    "warp_farm_settled",
    "normal_exit_requested",
    "game_exiting",
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def tree_manifest(root: Path) -> dict[str, Any]:
    digest = hashlib.sha256()
    files = 0
    directories = 0
    bytes_total = 0
    for path in sorted(root.rglob("*"), key=lambda value: value.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        metadata = path.lstat()
        if path.is_symlink():
            raise ValueError(f"tree manifest rejects symlink: {relative}")
        if path.is_dir():
            directories += 1
            digest.update(b"d\0" + relative.encode("utf-8") + b"\0")
        elif path.is_file():
            files += 1
            bytes_total += metadata.st_size
            digest.update(b"f\0" + relative.encode("utf-8") + b"\0" + str(metadata.st_size).encode("ascii") + b"\0")
            with path.open("rb") as stream:
                for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(chunk)
        else:
            raise ValueError(f"tree manifest rejects non-file entry: {relative}")
    return {"sha256": digest.hexdigest(), "files": files, "directories": directories, "bytes": bytes_total}


def command_version(*args: str) -> str:
    try:
        result = subprocess.run(args, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
    except FileNotFoundError:
        return "not-installed"
    return result.stdout.strip().splitlines()[0] if result.stdout.strip() else f"exit-{result.returncode}"


def environment_metadata(cpu_list: str, display: str) -> dict[str, Any]:
    governors: set[str] = set()
    for path in Path("/sys/devices/system/cpu").glob("cpu[0-9]*/cpufreq/scaling_governor"):
        try:
            governors.add(path.read_text(encoding="ascii").strip())
        except OSError:
            pass
    memory: dict[str, int] = {}
    for line in Path("/proc/meminfo").read_text(encoding="ascii").splitlines():
        match = re.match(r"(MemTotal|MemAvailable|SwapTotal|SwapFree):\s+(\d+)", line)
        if match:
            memory[match.group(1) + "KiB"] = int(match.group(2))
    cpu_model = "unknown"
    for line in Path("/proc/cpuinfo").read_text(encoding="utf-8", errors="replace").splitlines():
        if line.lower().startswith("model name") and ":" in line:
            cpu_model = line.split(":", 1)[1].strip()
            break
    display_environment = os.environ.copy()
    display_environment["DISPLAY"] = display
    try:
        renderer_result = subprocess.run(
            ("glxinfo", "-B"),
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            check=False,
            env=display_environment,
        )
        renderer_match = re.search(r"^OpenGL renderer string:\s*(.+)$", renderer_result.stdout, flags=re.MULTILINE)
        renderer = renderer_match.group(1).strip() if renderer_match else f"unavailable (glxinfo exit {renderer_result.returncode})"
    except FileNotFoundError:
        renderer = "unavailable (glxinfo not installed)"
    xvfb_binary = shutil.which("Xvfb")
    if xvfb_binary is None:
        raise ValueError("Xvfb is not installed")
    return {
        "schema": 1,
        "system": platform.system(),
        "kernel": platform.release(),
        "machine": platform.machine(),
        "cpuList": cpu_list,
        "cpuModel": cpu_model,
        "logicalCpuCount": os.cpu_count(),
        "governors": sorted(governors),
        "memory": memory,
        "dotnet": command_version("dotnet", "--version"),
        "bwrap": command_version("bwrap", "--version"),
        "xvfb": {"binarySha256": sha256(Path(xvfb_binary)), "helpSignature": command_version("Xvfb", "-help")},
        "locale": "C.UTF-8",
        "renderer": renderer,
    }


def clone_tree(source: Path, destination: Path) -> None:
    subprocess.run(("cp", "--archive", "--reflink=auto", "--", os.fspath(source), os.fspath(destination)), check=True)


def read_cpu_snapshot(cpus: set[int]) -> dict[int, tuple[int, int]]:
    snapshot: dict[int, tuple[int, int]] = {}
    for line in Path("/proc/stat").read_text(encoding="ascii").splitlines():
        match = re.match(r"cpu(\d+)\s+(.+)", line)
        if not match or int(match.group(1)) not in cpus:
            continue
        values = [int(value) for value in match.group(2).split()]
        idle = values[3] + (values[4] if len(values) > 4 else 0)
        snapshot[int(match.group(1))] = (sum(values), idle)
    return snapshot


def chosen_cpu_busy_percent(cpus: set[int], seconds: float = 5.0) -> dict[str, float]:
    if not cpus:
        raise ValueError("CPU list must not be empty")
    before = read_cpu_snapshot(cpus)
    if set(before) != cpus:
        raise ValueError(f"CPU list contains unavailable CPUs: {sorted(cpus - set(before))}")
    time.sleep(seconds)
    after = read_cpu_snapshot(cpus)
    return cpu_busy_between(before, after, cpus)


def cpu_busy_between(before: dict[int, tuple[int, int]], after: dict[int, tuple[int, int]], cpus: set[int]) -> dict[str, float]:
    busy: list[float] = []
    for cpu in cpus:
        total_delta = after[cpu][0] - before[cpu][0]
        idle_delta = after[cpu][1] - before[cpu][1]
        busy.append(100.0 * (total_delta - idle_delta) / max(1, total_delta))
    return {"mean": sum(busy) / len(busy), "max": max(busy)}


def thermal_metadata() -> list[float]:
    values: list[float] = []
    for path in sorted(Path("/sys/class/thermal").glob("thermal_zone*/temp")):
        try:
            value = float(path.read_text(encoding="ascii").strip()) / 1000.0
        except (OSError, ValueError):
            continue
        if -20 <= value <= 150:
            values.append(value)
    return values


def sample_plan(samples: int, start: str) -> list[tuple[str, int, bool, str]]:
    order = (start, "b" if start == "a" else "a")
    return [(product, sample, False, "main") for sample in range(1, samples + 1) for product in order]


def diagnostic_plan(samples: int) -> list[tuple[str, int, bool, str]]:
    plan: list[tuple[str, int, bool, str]] = []
    for sample in range(1, samples + 1):
        pair = ((False, "diagnostic-control"), (True, "diagnostic-enabled"))
        if sample % 2 == 0:
            pair = tuple(reversed(pair))
        plan.extend(("b", sample, enabled, series) for enabled, series in pair)
    return plan


def configure_sample(run_root: Path, diagnostics: bool, metadata: dict[str, Any]) -> None:
    game_config = run_root / "game" / "smapi-internal" / "config.json"
    values = load_jsonc(game_config)
    if diagnostics:
        values["EnableModPerformanceTracking"] = True
        values["LogModPerformanceTicks"] = False
        values["EnableModHealthReportOnLaunch"] = False
    else:
        for key in ("EnableModPerformanceTracking", "LogModPerformanceTicks", "EnableModHealthReportOnLaunch"):
            if key in values:
                values[key] = False
    common = {key: values[key] for key in metadata["commonSmapiConfigKeys"]}
    common_digest = hashlib.sha256(json.dumps(common, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()
    if common_digest != metadata["commonSmapiConfigSha256"]:
        raise ValueError("effective common SMAPI configuration differs from prepared A/B configuration")
    game_config.write_text(json.dumps(values, indent=2) + "\n", encoding="utf-8")


def probe_summary(path: Path) -> dict[str, Any]:
    header: dict[str, Any] | None = None
    marker_order: list[str] = []
    markers: dict[str, int] = {}
    steady_updates = 0
    transition_updates = 0
    steady_draws = 0
    transition_draws = 0
    update_records = 0
    draw_records = 0
    phase_totals: dict[str, Any] | None = None
    with path.open(encoding="utf-8") as stream:
        for line_number, line in enumerate(stream, start=1):
            record = json.loads(line)
            record_type = record.get("type")
            if record_type == "header":
                if header is not None or line_number != 1:
                    raise ValueError("probe must contain exactly one first-line header")
                header = record
            elif record_type == "marker":
                if record["name"] in markers:
                    raise ValueError(f"duplicate probe marker: {record['name']}")
                marker_order.append(record["name"])
                markers[record["name"]] = record["elapsedTicks"]
            elif record_type == "phaseTotals":
                if phase_totals is not None:
                    raise ValueError("duplicate phaseTotals record")
                phase_totals = record
            elif record_type == "update":
                update_records += 1
                phase = record.get("phase")
                if phase == "steady":
                    steady_updates += 1
                elif phase == "transition":
                    transition_updates += 1
                else:
                    raise ValueError(f"invalid update phase: {phase}")
                elapsed = record.get("elapsedTicks")
                base = record.get("baseGameTicks")
                if not isinstance(elapsed, int) or not isinstance(base, int) or elapsed <= 0 or base < 0 or base > elapsed:
                    raise ValueError("invalid update timing partition")
                if not isinstance(record.get("allocatedBytes"), int) or record["allocatedBytes"] < 0:
                    raise ValueError("invalid update allocation delta")
                if any(not isinstance(record.get(key), int) or record[key] < 0 for key in ("gc0", "gc1", "gc2")):
                    raise ValueError("invalid update GC delta")
            elif record_type == "draw":
                draw_records += 1
                phase = record.get("phase")
                if phase == "steady":
                    steady_draws += 1
                elif phase == "transition":
                    transition_draws += 1
                else:
                    raise ValueError(f"invalid draw phase: {phase}")
                if any(not isinstance(record.get(key), int) or record[key] < 0 for key in ("drawTicks", "updateTicks", "updateCount")):
                    raise ValueError("invalid draw record")
                if record["drawTicks"] <= 0:
                    raise ValueError("draw timing must be positive")
            else:
                raise ValueError(f"unknown probe record type: {record_type}")
    if header is None:
        raise ValueError("probe header missing")
    if header.get("schema") != 1 or header.get("probeVersion") != "1.0.0":
        raise ValueError("unsupported probe schema/version")
    if header.get("warmupSeconds") != 60 or header.get("measurementSeconds") != 180 or header.get("transitionSettleTicks") != 300:
        raise ValueError("unexpected probe timing configuration")
    if marker_order != list(REQUIRED_MARKERS):
        raise ValueError(f"probe marker order mismatch: {marker_order}")
    marker_ticks = [markers[name] for name in REQUIRED_MARKERS]
    if any(not isinstance(value, int) or value < 0 for value in marker_ticks) or marker_ticks != sorted(marker_ticks):
        raise ValueError("probe marker timestamps are invalid or out of order")
    if header["bufferOverflow"]:
        raise ValueError("probe buffer overflow")
    if header.get("expectedSaveLoaded") is not True:
        raise ValueError("probe did not load the expected private save")
    if any(header.get(key) != 0 for key in ("invalidWorldStateTicks", "locationChangedTicks", "positionChangedTicks")):
        raise ValueError("steady state was paused, menu-blocked, moved, or changed location")
    if header.get("gameTimeAtSteadyStart") == header.get("gameTimeAtSteadyEnd"):
        raise ValueError("in-game time did not advance during steady state")
    if header.get("recordedUpdates") != update_records or header.get("recordedDraws") != draw_records:
        raise ValueError("probe record counts do not match header")
    if steady_updates < 3000 or transition_updates < 100 or steady_draws < 1000 or transition_draws < 50:
        raise ValueError("insufficient steady or transition update/draw samples")
    frequency = header.get("stopwatchFrequency")
    if not isinstance(frequency, int) or frequency < 1000 or frequency > 10_000_000_000:
        raise ValueError("invalid stopwatch frequency")
    duration = (markers["steady_state_end"] - markers["steady_state_start"]) / frequency
    if duration < 180:
        raise ValueError(f"steady-state duration too short: {duration:.6f}s")
    if phase_totals is None:
        raise ValueError("phaseTotals record missing")
    allocated_keys = ("entryAllocatedBytes", "steadyStartAllocatedBytes", "steadyEndAllocatedBytes", "exitAllocatedBytes")
    allocated_values = [phase_totals.get(key) for key in allocated_keys]
    if any(not isinstance(value, int) or value < 0 for value in allocated_values) or allocated_values != sorted(allocated_values):
        raise ValueError("process allocation phase totals are invalid")
    for generation in range(3):
        values = [phase_totals.get(f"{phase}Gc{generation}") for phase in ("entry", "steadyStart", "steadyEnd", "exit")]
        if any(not isinstance(value, int) or value < 0 for value in values) or values != sorted(values):
            raise ValueError("process GC phase totals are invalid")
    return {
        "header": header,
        "markers": markers,
        "phaseTotals": phase_totals,
        "steadyUpdates": steady_updates,
        "transitionUpdates": transition_updates,
        "steadyDraws": steady_draws,
        "transitionDraws": transition_draws,
        "steadySeconds": duration,
    }


def selected_log_metadata(log_path: Path) -> dict[str, Any]:
    text = log_path.read_text(encoding="utf-8", errors="replace")
    resolution_matches = re.findall(r"Window\.ClientBounds=\{X:\d+ Y:\d+ Width:(\d+) Height:(\d+)\}", text)
    resolution = f"{resolution_matches[-1][0]}x{resolution_matches[-1][1]}" if resolution_matches else None
    mod_match = re.search(r"Loaded (\d+) mods:", text)
    pack_match = re.search(r"Loaded (\d+) content packs:", text)
    version_match = re.search(r"SMAPI ([^ ]+) with Stardew Valley ([^ ]+ build \d+)", text)
    phase_patterns = {
        "logStarted": r"Log started at ",
        "waitingForGame": r"Waiting for game to launch",
        "maliciousScan": r"Scanning for malicious files",
        "metadataLoad": r"Loading mod metadata",
        "assemblyLoad": r"Loading mods\.\.\.",
        "entryLaunch": r"Launching mods\.\.\.",
        "modsReady": r"Mods loaded and ready!",
        "contentReady": r"Instance_LoadContent\(\) finished",
    }
    phase_clock: dict[str, int] = {}
    for line in text.splitlines():
        timestamp = re.match(r"\[(\d{2}):(\d{2}):(\d{2}) ", line)
        if not timestamp:
            continue
        seconds = int(timestamp.group(1)) * 3600 + int(timestamp.group(2)) * 60 + int(timestamp.group(3))
        for name, pattern in phase_patterns.items():
            if name not in phase_clock and re.search(pattern, line):
                phase_clock[name] = seconds
    if "logStarted" in phase_clock:
        origin = phase_clock["logStarted"]
        phase_clock = {name: (seconds - origin) % 86400 for name, seconds in phase_clock.items()}
    load_failure_patterns = (
        r"Failed loading mod",
        r"because its DLL couldn't be loaded",
        r"because its entry DLL .* doesn't exist",
        r"because it contains files, but none of them are manifest\.json",
        r"\bSkipped mods\b",
        r"These mods could not be added",
    )
    return {
        "resolution": resolution,
        "loadedCodeMods": int(mod_match.group(1)) if mod_match else None,
        "loadedContentPacks": int(pack_match.group(1)) if pack_match else None,
        "smapiVersion": version_match.group(1) if version_match else None,
        "gameVersion": version_match.group(2) if version_match else None,
        "modsReady": "Mods loaded and ready!" in text,
        "loadFailureCount": sum(len(re.findall(pattern, text, flags=re.IGNORECASE)) for pattern in load_failure_patterns),
        "startupPhaseSecondsFromLogStart": phase_clock,
    }


def validate_log_metadata(log_metadata: dict[str, Any], expected_code_mods: int, expected_content_packs: int) -> None:
    if log_metadata["resolution"] != EXPECTED_RESOLUTION:
        raise ValueError(f"unexpected resolution: {log_metadata['resolution']}")
    if not log_metadata["modsReady"]:
        raise ValueError("SMAPI did not report mods ready")
    if log_metadata["loadedCodeMods"] != expected_code_mods or log_metadata["loadedContentPacks"] != expected_content_packs:
        raise ValueError(
            f"incomplete workload: expected {expected_code_mods} code mods/{expected_content_packs} content packs, "
            f"got {log_metadata['loadedCodeMods']}/{log_metadata['loadedContentPacks']}"
        )
    if log_metadata["loadFailureCount"] != 0:
        raise ValueError(f"SMAPI reported {log_metadata['loadFailureCount']} framework mod-load failures")
    if log_metadata["smapiVersion"] is None or log_metadata["gameVersion"] != "1.6.15 build 24356":
        raise ValueError("missing or unexpected SMAPI/game version metadata")
    startup_phases = log_metadata["startupPhaseSecondsFromLogStart"]
    if tuple(startup_phases) != REQUIRED_STARTUP_PHASES:
        raise ValueError(f"missing or reordered startup phases: {list(startup_phases)}")
    startup_values = [startup_phases[name] for name in REQUIRED_STARTUP_PHASES]
    if startup_values != sorted(startup_values):
        raise ValueError("startup phase timestamps are not monotonic")


def validate_saved_sample(
    run_root: Path,
    metadata: dict[str, Any],
    label: str,
    sequence: int,
    product: str,
    sample: int,
    diagnostics: bool,
    series: str,
) -> None:
    saved = json.loads((run_root / "sample.json").read_text(encoding="utf-8"))
    expected_identity = (label, sequence, product, sample, diagnostics, series)
    actual_identity = (
        saved.get("label"), saved.get("sequence"), saved.get("product"), saved.get("sample"),
        saved.get("diagnosticsEnabled"), saved.get("series"),
    )
    if actual_identity != expected_identity:
        raise ValueError(f"saved sample identity mismatch: expected {expected_identity}, got {actual_identity}")
    if saved.get("commit") != metadata["products"][product]["commit"]:
        raise ValueError("saved sample commit mismatch")
    environment_name = "preflight-environment.json" if series == "preflight" else "environment.json"
    if saved.get("suiteEnvironmentSha256") != sha256(run_root.parents[1] / environment_name):
        raise ValueError("saved sample environment/session metadata mismatch")
    game = run_root / "game"
    probe = run_root / "mods" / "SMAPI.BenchmarkProbe"
    critical = {
        "smapiAssemblySha256": sha256(game / "StardewModdingAPI.dll"),
        "gameAssemblySha256": sha256(game / "Stardew Valley.dll"),
        "probeAssemblySha256": sha256(probe / "SMAPI.BenchmarkProbe.dll"),
    }
    if any(saved.get(key) != value for key, value in critical.items()):
        raise ValueError("saved sample critical file hash mismatch")
    prepared_critical = {
        "smapiAssemblySha256": metadata["products"][product]["smapiAssemblySha256"],
        "gameAssemblySha256": metadata["gameAssemblySha256"],
        "probeAssemblySha256": metadata["probeAssemblySha256"],
    }
    if critical != prepared_critical:
        raise ValueError("saved sample critical files differ from prepared immutable metadata")
    if sha256(probe / "config.json") != metadata["probeConfigSha256"] or sha256(probe / "manifest.json") != metadata["probeManifestSha256"]:
        raise ValueError("saved sample probe configuration or manifest differs from prepared metadata")
    if sha256(game / "StardewModdingAPI") != metadata["commonLauncherSha256"]:
        raise ValueError("saved sample common launcher hash mismatch")
    summary = probe_summary(run_root / "probe.jsonl")
    if saved.get("probe") != summary:
        raise ValueError("saved sample probe acceptance summary mismatch")
    log_path = run_root / "home" / ".config" / "StardewValley" / "ErrorLogs" / "SMAPI-latest.txt"
    log_metadata = selected_log_metadata(log_path)
    validate_log_metadata(log_metadata, metadata["expectedLoadedCodeMods"], metadata["expectedLoadedContentPacks"])
    if saved.get("log") != log_metadata:
        raise ValueError("saved sample log projection mismatch")


def run_sample(
    private_root: Path,
    sequence: int,
    product: str,
    sample: int,
    diagnostics: bool,
    display: str,
    cpu_list: str,
    timeout: int,
    max_busy: float,
    expected_code_mods: int,
    expected_content_packs: int,
    run_group: str,
    series: str,
) -> None:
    metadata = json.loads((private_root / "metadata.json").read_text(encoding="utf-8"))
    label = f"{sequence:02d}-{product}{sample}" + ("-diagnostics" if diagnostics else "")
    run_root = private_root / run_group / label
    if run_root.exists():
        raise ValueError(f"sample root already exists; archive the entire interrupted suite before restarting: {run_root}")

    cpus = {int(value) for value in cpu_list.split(",") if value}
    busy = chosen_cpu_busy_percent(cpus)
    if busy["max"] > max_busy:
        raise ValueError(f"chosen CPU load gate failed: busiest CPU {busy['max']:.2f}% > {max_busy:.2f}%")

    run_root.mkdir(mode=0o700, parents=True)
    clone_tree(private_root / "gold" / f"game-{product}", run_root / "game")
    clone_tree(private_root / "gold" / "mods", run_root / "mods")
    home = run_root / "home"
    saves = home / ".config" / "StardewValley" / "Saves"
    saves.mkdir(mode=0o700, parents=True)
    for save in (private_root / "gold" / "saves").iterdir():
        if save.is_dir():
            clone_tree(save, saves / save.name)
    save_directories = [path for path in saves.iterdir() if path.is_dir()]
    if len(save_directories) != 1:
        raise ValueError("sample must contain exactly one private save directory")
    save_digest = hashlib.sha256(save_directories[0].name.encode("utf-8")).hexdigest()
    runtime = run_root / "xdg-runtime"
    runtime.mkdir(mode=0o700)

    if tree_manifest(run_root / "game") != metadata["products"][product]["gameTree"]:
        raise ValueError("sample game tree differs from immutable prepared input")
    if tree_manifest(run_root / "mods") != metadata["modsTree"]:
        raise ValueError("sample Mods tree differs from immutable prepared input")
    if tree_manifest(saves) != metadata["savesTree"]:
        raise ValueError("sample save tree differs from immutable prepared input")
    configure_sample(run_root, diagnostics, metadata)

    smapi_hash = sha256(run_root / "game" / "StardewModdingAPI.dll")
    expected_hash = metadata["products"][product]["smapiAssemblySha256"]
    if smapi_hash != expected_hash:
        raise ValueError("sample SMAPI assembly hash mismatch")
    if sha256(run_root / "game" / "StardewModdingAPI") != metadata["commonLauncherSha256"]:
        raise ValueError("sample common launcher hash mismatch")
    if sha256(run_root / "game" / "Stardew Valley.dll") != metadata["gameAssemblySha256"]:
        raise ValueError("sample game assembly hash mismatch")
    probe_root = run_root / "mods" / "SMAPI.BenchmarkProbe"
    if sha256(probe_root / "SMAPI.BenchmarkProbe.dll") != metadata["probeAssemblySha256"]:
        raise ValueError("sample benchmark probe assembly hash mismatch")
    if sha256(probe_root / "config.json") != metadata["probeConfigSha256"]:
        raise ValueError("sample benchmark probe config hash mismatch")
    if sha256(probe_root / "manifest.json") != metadata["probeManifestSha256"]:
        raise ValueError("sample benchmark probe manifest hash mismatch")

    result_path = run_root / "probe.jsonl"
    console_path = run_root / "console.txt"
    x_socket = Path(f"/tmp/.X11-unix/X{display.lstrip(':')}")
    command = [
        "taskset", "--cpu-list", cpu_list,
        "bwrap", "--die-with-parent", "--new-session", "--unshare-pid", "--unshare-net", "--unshare-ipc", "--unshare-uts",
        "--clearenv",
        "--ro-bind", "/usr", "/usr",
        "--symlink", "usr/bin", "/bin",
        "--symlink", "usr/lib", "/lib",
        "--symlink", "usr/lib", "/lib64",
        "--dir", "/etc",
        "--ro-bind", "/etc/fonts", "/etc/fonts",
        "--dev", "/dev",
        "--proc", "/proc",
        "--tmpfs", "/tmp",
        "--dir", "/tmp/.X11-unix",
        "--ro-bind", os.fspath(x_socket), os.fspath(x_socket),
        "--bind", os.fspath(run_root), os.fspath(run_root),
        "--setenv", "PATH", "/usr/bin:/bin",
        "--setenv", "LANG", "C.UTF-8",
        "--setenv", "LC_ALL", "C.UTF-8",
        "--setenv", "DOTNET_CLI_TELEMETRY_OPTOUT", "1",
        "--setenv", "DOTNET_NOLOGO", "1",
        "--setenv", "HOME", os.fspath(home),
        "--setenv", "XDG_CONFIG_HOME", os.fspath(home / ".config"),
        "--setenv", "XDG_DATA_HOME", os.fspath(home / ".local" / "share"),
        "--setenv", "XDG_CACHE_HOME", os.fspath(home / ".cache"),
        "--setenv", "XDG_RUNTIME_DIR", os.fspath(runtime),
        "--setenv", "DISPLAY", display,
        "--setenv", "XDG_SESSION_TYPE", "x11",
        "--setenv", "SDL_AUDIODRIVER", "dummy",
        "--setenv", "SMAPI_BENCHMARK_OUTPUT", os.fspath(result_path),
        "--setenv", "SMAPI_BENCHMARK_SAVE_SHA256", save_digest,
        "--chdir", os.fspath(run_root / "game"),
        "--", os.fspath(run_root / "game" / "StardewModdingAPI"),
        "--mods-path", os.fspath(run_root / "mods"),
    ]
    started = datetime.now().astimezone().isoformat()
    temperature_before = thermal_metadata()
    cpu_before = read_cpu_snapshot(cpus)
    process: subprocess.Popen[bytes] | None = None
    with console_path.open("wb") as console:
        try:
            process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=console, stderr=subprocess.STDOUT, start_new_session=True)
            exit_code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
            raise ValueError(f"sample timed out after {timeout}s")
        finally:
            if process is not None and process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
    finished = datetime.now().astimezone().isoformat()
    during_busy = cpu_busy_between(cpu_before, read_cpu_snapshot(cpus), cpus)
    if exit_code != 0:
        raise ValueError(f"sample exited with code {exit_code}")
    summary = probe_summary(result_path)
    log_path = home / ".config" / "StardewValley" / "ErrorLogs" / "SMAPI-latest.txt"
    log_metadata = selected_log_metadata(log_path)
    validate_log_metadata(log_metadata, expected_code_mods, expected_content_packs)
    sample_metadata = {
        "schema": 1,
        "label": label,
        "sequence": sequence,
        "product": product,
        "sample": sample,
        "diagnosticsEnabled": diagnostics,
        "series": series,
        "suiteEnvironmentSha256": sha256(private_root / ("preflight-environment.json" if series == "preflight" else "environment.json")),
        "commit": metadata["products"][product]["commit"],
        "smapiAssemblySha256": smapi_hash,
        "gameAssemblySha256": metadata["gameAssemblySha256"],
        "probeAssemblySha256": metadata["probeAssemblySha256"],
        "started": started,
        "finished": finished,
        "displaySession": "x11-xvfb",
        "cpuList": cpu_list,
        "preRunChosenCpuBusyPercent": busy,
        "duringRunChosenCpuBusyPercent": during_busy,
        "loadAverage": list(os.getloadavg()),
        "temperatureCelsiusBefore": temperature_before,
        "temperatureCelsiusAfter": thermal_metadata(),
        "probe": summary,
        "log": log_metadata,
    }
    (run_root / "sample.json").write_text(json.dumps(sample_metadata, indent=2) + "\n", encoding="utf-8")
    os.chmod(run_root / "sample.json", 0o600)
    print(json.dumps({"accepted": label, "steadySeconds": summary["steadySeconds"], "steadyUpdates": summary["steadyUpdates"]}))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--private-root", required=True)
    parser.add_argument("--samples", type=int, default=5)
    parser.add_argument("--start", choices=("a", "b"), default="a")
    parser.add_argument("--cpu-list", default="7,8,9,19,20,21")
    parser.add_argument("--display", default=":98")
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--max-busy-percent", type=float, default=35.0)
    parser.add_argument("--preflight", action="store_true")
    args = parser.parse_args()
    if not args.preflight and args.samples < 5:
        raise ValueError("at least five samples per product are required")
    private_root = Path(args.private_root).expanduser().resolve(strict=True)
    repo = Path(__file__).resolve().parents[2]
    live_root = (Path.home() / ".config" / "StardewValley").resolve(strict=False)
    live_game_roots = (
        (Path.home() / ".steam" / "steam" / "steamapps" / "common" / "Stardew Valley").resolve(strict=False),
        (Path.home() / ".local" / "share" / "Steam" / "steamapps" / "common" / "Stardew Valley").resolve(strict=False),
    )
    if private_root == repo or repo in private_root.parents or private_root in repo.parents:
        raise ValueError("private root must not overlap the repository")
    if private_root == live_root or live_root in private_root.parents or private_root in live_root.parents:
        raise ValueError("private root must not overlap live Stardew saves/config")
    for live_game in live_game_roots:
        if private_root == live_game or live_game in private_root.parents or private_root in live_game.parents:
            raise ValueError("private root must not overlap a live Steam game tree")
    if not (private_root / "metadata.json").is_file():
        raise ValueError("prepared metadata.json not found")
    if args.timeout < 420 or not 0 < args.max_busy_percent <= 100:
        raise ValueError("timeout must be at least 420 seconds and max busy percent must be in (0, 100]")
    try:
        cpus = {int(value) for value in args.cpu_list.split(",") if value}
    except ValueError as error:
        raise ValueError("CPU list must be comma-separated integers") from error
    chosen_cpu_busy_percent(cpus, seconds=0.01)
    metadata = json.loads((private_root / "metadata.json").read_text(encoding="utf-8"))
    if metadata.get("schema") != 1 or metadata.get("officialCommit") != "79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0":
        raise ValueError("prepared metadata schema or official commit mismatch")
    if sha256(Path(__file__).resolve()) != metadata.get("runnerScriptSha256"):
        raise ValueError("runner script differs from prepared committed harness")
    if sha256(Path(__file__).with_name("harness_common.py")) != metadata.get("commonScriptSha256"):
        raise ValueError("common harness helpers differ from the prepared committed harness")

    gold_expected = {
        "game-a": metadata["products"]["a"]["gameTree"],
        "game-b": metadata["products"]["b"]["gameTree"],
        "mods": metadata["modsTree"],
        "saves": metadata["savesTree"],
    }
    for name, expected in gold_expected.items():
        if tree_manifest(private_root / "gold" / name) != expected:
            raise ValueError(f"prepared immutable input changed before suite: {name}")
    if args.preflight:
        planned_main = [("a", 1, False, "preflight"), ("b", 1, False, "preflight")]
        plan_record = {"schema": 1, "kind": "preflight", "start": "a", "samples": 1, "main": planned_main, "diagnostics": []}
        plan_path = private_root / "preflight-plan.json"
    else:
        planned_main = sample_plan(args.samples, args.start)
        planned_diagnostics = diagnostic_plan(args.samples)
        plan_record = {
            "schema": 1, "kind": "final", "start": args.start, "samples": args.samples,
            "main": planned_main, "diagnostics": planned_diagnostics,
        }
        plan_path = private_root / "suite-plan.json"
    serialized_plan = json.dumps(plan_record, indent=2) + "\n"
    if plan_path.exists() and plan_path.read_text(encoding="utf-8") != serialized_plan:
        raise ValueError("existing suite plan differs from requested plan")
    plan_path.write_text(serialized_plan, encoding="utf-8")
    os.chmod(plan_path, 0o600)

    x_socket = Path(f"/tmp/.X11-unix/X{args.display.lstrip(':')}")
    if x_socket.exists():
        raise ValueError(f"X display is already in use: {args.display}")
    xvfb = subprocess.Popen(("Xvfb", args.display, "-screen", "0", f"{EXPECTED_RESOLUTION}x24", "-nolisten", "tcp"), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        for _ in range(100):
            if x_socket.exists():
                break
            if xvfb.poll() is not None:
                raise ValueError("Xvfb exited during startup")
            time.sleep(0.05)
        else:
            raise ValueError("Xvfb socket was not created")

        environment_path = private_root / ("preflight-environment.json" if args.preflight else "environment.json")
        if environment_path.exists():
            raise ValueError("suite environment metadata already exists; archive it with any incomplete runs before restarting")
        environment_path.write_text(json.dumps(environment_metadata(args.cpu_list, args.display), indent=2) + "\n", encoding="utf-8")
        os.chmod(environment_path, 0o600)

        if args.preflight:
            plan = planned_main
            run_group = "preflight-runs"
        else:
            plan = planned_main
            run_group = "runs"
        for sequence, (product, sample, diagnostics, series) in enumerate(plan, start=1):
            run_sample(
                private_root, sequence, product, sample, diagnostics, args.display, args.cpu_list, args.timeout,
                args.max_busy_percent, metadata["expectedLoadedCodeMods"], metadata["expectedLoadedContentPacks"],
                run_group, series,
            )
        if not args.preflight:
            for sequence, (product, sample, diagnostics, series) in enumerate(planned_diagnostics, start=1):
                run_sample(
                    private_root, sequence, product, sample, diagnostics, args.display, args.cpu_list, args.timeout,
                    args.max_busy_percent, metadata["expectedLoadedCodeMods"], metadata["expectedLoadedContentPacks"],
                    "diagnostic-runs", series,
                )
    finally:
        xvfb.terminate()
        try:
            xvfb.wait(timeout=5)
        except subprocess.TimeoutExpired:
            xvfb.kill()
            xvfb.wait()
    for name, expected in gold_expected.items():
        if tree_manifest(private_root / "gold" / name) != expected:
            raise ValueError(f"prepared immutable input changed during suite: {name}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, subprocess.CalledProcessError, ValueError) as error:
        print(f"benchmark failed: {error}", file=sys.stderr)
        raise SystemExit(1)
