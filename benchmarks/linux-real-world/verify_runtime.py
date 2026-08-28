#!/usr/bin/env python3
"""Verify and publish the runtime identity shared by the isolated A/B gold trees."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile


HARNESS_COMMIT = "3c98eadd2bddc24d43c889afb11b155e92469882"
GAME_ASSEMBLY_SHA256 = "f3e97f01d3fd2b1e6094fc8d2b59950aa6cb9d6cd1bf1b39d72d58edda8aad12"
PRIVATE_TOKENS = ("/home/", "Blossom_", "PRIVATE_", "Mods-2026", "SaveGameInfo", "workloadIdentitySha256")
RAW_SAMPLE_KEYS = {
    "type", "schema", "label", "sequence", "product", "sample", "diagnosticsEnabled", "series", "commit",
    "smapiAssemblySha256", "gameAssemblySha256", "probeAssemblySha256", "suiteEnvironmentSha256", "started", "finished",
    "displaySession", "cpuList", "preRunChosenCpuBusyPercent", "duringRunChosenCpuBusyPercent", "loadAverage",
    "temperatureCelsiusBefore", "temperatureCelsiusAfter", "startupPhaseSecondsFromLogStart", "loadedCodeMods",
    "loadedContentPacks", "skippedModCount", "smapiVersion", "gameVersion", "resolution",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def runtime_version(game_root: Path) -> tuple[str, str, bool]:
    config = json.loads((game_root / "Stardew Valley.runtimeconfig.json").read_text(encoding="utf-8"))
    options = config.get("runtimeOptions", {})
    frameworks = options.get("includedFrameworks")
    if options.get("tfm") != "net6.0" or not isinstance(frameworks, list) or len(frameworks) != 1:
        raise ValueError("unexpected isolated game runtime configuration")
    framework = frameworks[0]
    if set(framework) != {"name", "version"} or framework["name"] != "Microsoft.NETCore.App":
        raise ValueError("unexpected isolated game framework")
    if not isinstance(framework["version"], str) or not re.fullmatch(r"6\.0\.[0-9]+", framework["version"]):
        raise ValueError("unexpected isolated game framework version")
    tiered = options.get("configProperties", {}).get("System.Runtime.TieredCompilation")
    if type(tiered) is not bool:
        raise ValueError("isolated runtime config does not declare tiered compilation")
    return framework["name"], framework["version"], tiered


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--private-root", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    private_root = Path(args.private_root).expanduser().resolve(strict=True)
    output = Path(args.output).expanduser().resolve(strict=False)
    expected_output = Path(__file__).resolve().parent / "results" / "runtime-provenance.json"
    if output != expected_output or output.parent.is_symlink():
        raise ValueError(f"output must be exactly {expected_output}")
    metadata = json.loads((private_root / "metadata.json").read_text(encoding="utf-8"))
    if metadata.get("harnessCommit") != HARNESS_COMMIT or metadata.get("gameAssemblySha256") != GAME_ASSEMBLY_SHA256:
        raise ValueError("unexpected isolated suite identity")
    raw_root = output.parent / "raw"
    raw_entries = sorted(raw_root.iterdir()) if raw_root.is_dir() and not raw_root.is_symlink() else []
    raw_files = [path for path in raw_entries if path.suffix == ".jsonl"]
    if len(raw_entries) != 20 or len(raw_files) != 20 or any(path.is_symlink() or not path.is_file() for path in raw_files):
        raise ValueError("public raw inputs must be regular non-symlink files")
    suite_hashes = set()
    for raw_file in raw_files:
        with raw_file.open("r", encoding="utf-8") as stream:
            first = json.loads(stream.readline())
        if not isinstance(first, dict) or set(first) != RAW_SAMPLE_KEYS or first.get("type") != "sample" or first.get("schema") != 1:
            raise ValueError("unexpected public raw sample header")
        suite_hashes.add(first.get("suiteEnvironmentSha256"))
    if len(raw_files) != 20 or len(suite_hashes) != 1 or not re.fullmatch(r"[0-9a-f]{64}", next(iter(suite_hashes), "")):
        raise ValueError("public raw samples do not share one suite environment")
    suite_environment_sha256 = next(iter(suite_hashes))
    if sha256(private_root / "environment.json") != suite_environment_sha256:
        raise ValueError("selected private suite environment does not match the public raw samples")
    products = []
    for product, directory in (("official", "game-a"), ("fork", "game-b")):
        root = private_root / "gold" / directory
        framework, version, tiered = runtime_version(root)
        products.append({
            "product": product,
            "framework": framework,
            "version": version,
            "tieredCompilationFromRuntimeConfig": tiered,
            "gameAssemblySha256": sha256(root / "Stardew Valley.dll"),
            "coreclrSha256": sha256(root / "libcoreclr.so"),
            "hostfxrSha256": sha256(root / "libhostfxr.so"),
        })
    if any(product["gameAssemblySha256"] != GAME_ASSEMBLY_SHA256 for product in products):
        raise ValueError("isolated product game assembly differs from the measured identity")
    comparable = ("framework", "version", "tieredCompilationFromRuntimeConfig", "gameAssemblySha256", "coreclrSha256", "hostfxrSha256")
    if any(products[0][key] != products[1][key] for key in comparable):
        raise ValueError("isolated A/B products do not share an identical game runtime")
    result = {
        "schema": 1,
        "harnessCommit": HARNESS_COMMIT,
        "suiteEnvironmentSha256": suite_environment_sha256,
        "verifierScriptSha256": sha256(Path(__file__).resolve()),
        "verifiedProducts": ["official", "fork"],
        "framework": products[0]["framework"],
        "version": products[0]["version"],
        "tieredCompilationFromRuntimeConfig": products[0]["tieredCompilationFromRuntimeConfig"],
        "gameAssemblySha256": products[0]["gameAssemblySha256"],
        "coreclrSha256": products[0]["coreclrSha256"],
        "hostfxrSha256": products[0]["hostfxrSha256"],
        "verificationMethod": "SHA-256 and runtime-config fields independently matched across the post-suite official and fork gold game trees; the suite environment digest matches all 20 public raw samples.",
    }
    content = json.dumps(result, indent=2) + "\n"
    for key in ("suiteEnvironmentSha256", "verifierScriptSha256", "gameAssemblySha256", "coreclrSha256", "hostfxrSha256"):
        if not re.fullmatch(r"[0-9a-f]{64}", result[key]):
            raise ValueError(f"invalid public runtime provenance digest: {key}")
    if any(token in content for token in PRIVATE_TOKENS):
        raise ValueError("privacy scan rejected runtime provenance")
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{output.name}.", suffix=".tmp", dir=output.parent)
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"runtime verification failed: {error}")
        raise SystemExit(1)
