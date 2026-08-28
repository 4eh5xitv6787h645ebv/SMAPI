#!/usr/bin/env python3
"""Prepare immutable private inputs for the Linux real-world A/B benchmark."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
from typing import Any

from harness_common import load_jsonc


OFFICIAL_COMMIT = "79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0"
GAME_DLL_SHA256 = "f3e97f01d3fd2b1e6094fc8d2b59950aa6cb9d6cd1bf1b39d72d58edda8aad12"
MODPACK_SHA256 = "337d157bb2cf7283eaf6796c259a21d6e2e71baf134cb67a1f2a655e31cb312c"
SAVE_SHA256 = "6f707c73d02a05eed42adee0d9d9b434e92dff84a841e2f00047456921ae4bca"


def run(*args: str, cwd: Path | None = None) -> None:
    subprocess.run(args, cwd=cwd, check=True)


def output(*args: str, cwd: Path | None = None) -> str:
    return subprocess.check_output(args, cwd=cwd, text=True).strip()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def resolved(path: str) -> Path:
    return Path(path).expanduser().resolve(strict=True)


def private_target(repo: Path, root_argument: str) -> Path:
    root = Path(root_argument).expanduser().resolve(strict=False)
    if root == repo or repo in root.parents:
        raise ValueError("private root must be outside the repository")
    protected = (
        (Path.home() / ".config" / "StardewValley").resolve(strict=False),
        (Path.home() / ".steam" / "steam" / "steamapps" / "common" / "Stardew Valley").resolve(strict=False),
        (Path.home() / ".local" / "share" / "Steam" / "steamapps" / "common" / "Stardew Valley").resolve(strict=False),
    )
    for live_root in protected:
        if root == live_root or live_root in root.parents or root in live_root.parents:
            raise ValueError(f"private root must not overlap live Stardew data: {live_root}")
    if root.exists():
        raise ValueError(f"private root already exists: {root}")
    return root


def paths_overlap(left: Path, right: Path) -> bool:
    return left == right or left in right.parents or right in left.parents


def reject_protected_source(repo: Path, source: Path, label: str) -> None:
    protected = (
        repo,
        (Path.home() / ".config" / "StardewValley").resolve(strict=False),
        (Path.home() / ".steam" / "steam" / "steamapps" / "common" / "Stardew Valley").resolve(strict=False),
        (Path.home() / ".local" / "share" / "Steam" / "steamapps" / "common" / "Stardew Valley").resolve(strict=False),
    )
    for root in protected:
        if source == root or root in source.parents or source in root.parents:
            raise ValueError(f"{label} must not overlap protected repository/live path: {root}")


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


def clone_tree(source: Path, destination: Path) -> None:
    run("cp", "--archive", "--reflink=auto", "--", os.fspath(source), os.fspath(destination))


def build_product(worktree: Path, game: Path) -> None:
    run(
        "dotnet",
        "build",
        "src/SMAPI/SMAPI.csproj",
        "--configuration",
        "Release",
        f"-p:GamePath={game}",
        "-p:CopyToGameFolder=true",
        cwd=worktree,
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", required=True)
    parser.add_argument("--private-root", required=True)
    parser.add_argument("--game-source", required=True)
    parser.add_argument("--modpack-archive", required=True)
    parser.add_argument("--save-archive", required=True)
    parser.add_argument("--fork-commit", required=True)
    parser.add_argument("--official-commit", default=OFFICIAL_COMMIT)
    args = parser.parse_args()

    repo = resolved(args.repo)
    game_source = resolved(args.game_source)
    modpack_archive = resolved(args.modpack_archive)
    save_archive = resolved(args.save_archive)
    target_root = private_target(repo, args.private_root)
    for path, label in ((game_source, "game source"), (modpack_archive, "modpack archive"), (save_archive, "save archive")):
        reject_protected_source(repo, path, label)
    if paths_overlap(target_root, game_source):
        raise ValueError("private root must not overlap the selected game source")

    if sha256(modpack_archive) != MODPACK_SHA256:
        raise ValueError("trusted modpack archive hash mismatch")
    if sha256(save_archive) != SAVE_SHA256:
        raise ValueError("trusted save archive hash mismatch")

    if sha256(game_source / "Stardew Valley.dll") != GAME_DLL_SHA256:
        raise ValueError("unexpected native game build hash")
    game_source_tree = tree_manifest(game_source)
    official_commit = output("git", "rev-parse", f"{args.official_commit}^{{commit}}", cwd=repo)
    if official_commit != OFFICIAL_COMMIT:
        raise ValueError(f"official commit must resolve exactly to {OFFICIAL_COMMIT}")
    fork_commit = output("git", "rev-parse", f"{args.fork_commit}^{{commit}}", cwd=repo)
    if output("git", "status", "--porcelain", cwd=repo):
        raise ValueError("repository must be clean so harness script hashes match a committed revision")
    private_root = target_root.with_name(f"{target_root.name}.preparing-{os.getpid()}")
    if private_root.exists():
        raise ValueError(f"staging root already exists: {private_root}")
    private_root.mkdir(mode=0o700, parents=False)
    worktrees = private_root / "worktrees"
    gold = private_root / "gold"
    worktrees.mkdir(mode=0o700)
    gold.mkdir(mode=0o700)

    added_worktrees: list[Path] = []
    try:
        audit_tool = repo / "docs" / "technical" / "tools" / "fixture_archive_audit.py"
        extracted_modpack = private_root / "verified-modpack"
        extracted_save = private_root / "verified-save"
        run(sys.executable, os.fspath(audit_tool), "audit", "modpack", os.fspath(modpack_archive), "--extract", os.fspath(extracted_modpack), cwd=repo)
        run(sys.executable, os.fspath(audit_tool), "audit", "save", os.fspath(save_archive), "--extract", os.fspath(extracted_save), cwd=repo)
        modpack_root = extracted_modpack / "Mods"
        save_root = extracted_save
        if not (modpack_root / "AutoLoadSave" / "manifest.json").is_file():
            raise ValueError("verified workload AutoLoadSave mod is missing")
        save_directories = [path for path in save_root.iterdir() if path.is_dir()]
        if len(save_directories) != 1:
            raise ValueError("verified save fixture must contain exactly one top-level save directory")

        for product, commit in (("a", official_commit), ("b", fork_commit)):
            worktree = worktrees / product
            game = gold / f"game-{product}"
            run("git", "worktree", "add", "--detach", os.fspath(worktree), commit, cwd=repo)
            added_worktrees.append(worktree)
            clone_tree(game_source, game)
            if tree_manifest(game) != game_source_tree:
                raise ValueError("copied game tree differs from the prevalidated source")
            build_product(worktree, game)

        common_launcher = gold / "game-a" / "StardewModdingAPI"
        common_launcher_hash = sha256(common_launcher)
        shutil.copy2(common_launcher, gold / "game-b" / "StardewModdingAPI")
        common_deps = gold / "game-a" / "Stardew Valley.deps.json"
        common_deps_hash = sha256(common_deps)
        for product in ("a", "b"):
            shutil.copy2(common_deps, gold / f"game-{product}" / "StardewModdingAPI.deps.json")
        fork_runtime_config = gold / "game-b" / "StardewModdingAPI.runtimeconfig.json"
        if fork_runtime_config.exists():
            fork_runtime_config.unlink()

        config_paths = {
            product: gold / f"game-{product}" / "smapi-internal" / "config.json"
            for product in ("a", "b")
        }
        configs = {product: load_jsonc(path) for product, path in config_paths.items()}
        diagnostic_keys = {"EnableModPerformanceTracking", "LogModPerformanceTicks", "EnableModHealthReportOnLaunch"}
        common_keys = sorted((set(configs["a"]) & set(configs["b"])) - diagnostic_keys)
        for key in common_keys:
            configs["b"][key] = configs["a"][key]
        for product in ("a", "b"):
            for key in ("EnableModPerformanceTracking", "LogModPerformanceTicks", "EnableModHealthReportOnLaunch"):
                if key in configs[product]:
                    configs[product][key] = False
            config_paths[product].write_text(json.dumps(configs[product], indent=2) + "\n", encoding="utf-8")
        common_config = {key: configs["a"][key] for key in common_keys}
        common_config_digest = hashlib.sha256(
            json.dumps(common_config, sort_keys=True, separators=(",", ":")).encode("utf-8")
        ).hexdigest()

        mods = gold / "mods"
        clone_tree(modpack_root, mods)
        idle_config = mods / "IdleAutoPause" / "config.json"
        idle_values = load_jsonc(idle_config)
        idle_values["IdleSecondsBeforePause"] = 3600.0
        idle_config.write_text(json.dumps(idle_values, indent=2) + "\n", encoding="utf-8")

        probe_project = repo / "benchmarks" / "linux-real-world" / "SMAPI.BenchmarkProbe"
        run(
            "dotnet",
            "build",
            os.fspath(probe_project / "SMAPI.BenchmarkProbe.csproj"),
            "--configuration",
            "Release",
            f"-p:GamePath={gold / 'game-a'}",
            "-p:CopyToGameFolder=false",
            cwd=repo,
        )
        probe_destination = mods / "SMAPI.BenchmarkProbe"
        probe_destination.mkdir(mode=0o700)
        shutil.copy2(probe_project / "manifest.json", probe_destination / "manifest.json")
        shutil.copy2(probe_project / "config.json", probe_destination / "config.json")
        shutil.copy2(
            probe_project / "bin" / "Release" / "net6.0" / "SMAPI.BenchmarkProbe.dll",
            probe_destination / "SMAPI.BenchmarkProbe.dll",
        )

        saves = gold / "saves"
        clone_tree(save_root, saves)

        products: dict[str, dict[str, Any]] = {}
        for product, commit in (("a", official_commit), ("b", fork_commit)):
            game = gold / f"game-{product}"
            products[product] = {
                "commit": commit,
                "smapiAssemblySha256": sha256(game / "StardewModdingAPI.dll"),
                "gameTree": tree_manifest(game),
            }
        metadata = {
            "schema": 1,
            "officialCommit": official_commit,
            "forkCommit": fork_commit,
            "gameAssemblySha256": GAME_DLL_SHA256,
            "gameSourceTree": game_source_tree,
            "modpackArchiveSha256": MODPACK_SHA256,
            "saveArchiveSha256": SAVE_SHA256,
            "probeAssemblySha256": sha256(probe_destination / "SMAPI.BenchmarkProbe.dll"),
            "probeConfigSha256": sha256(probe_destination / "config.json"),
            "probeManifestSha256": sha256(probe_destination / "manifest.json"),
            "commonSmapiConfigSha256": common_config_digest,
            "commonSmapiConfigKeys": common_keys,
            "dotnetSdk": output("dotnet", "--version"),
            "harnessCommit": output("git", "rev-parse", "HEAD", cwd=repo),
            "prepareScriptSha256": sha256(Path(__file__).resolve()),
            "runnerScriptSha256": sha256(repo / "benchmarks" / "linux-real-world" / "run_ab.py"),
            "analyzerScriptSha256": sha256(repo / "benchmarks" / "linux-real-world" / "analyze.py"),
            "commonScriptSha256": sha256(repo / "benchmarks" / "linux-real-world" / "harness_common.py"),
            "commonLauncherSha256": common_launcher_hash,
            "commonDepsSha256": common_deps_hash,
            "expectedLoadedCodeMods": 132,
            "expectedLoadedContentPacks": 176,
            "expectedSkippedMods": 1,
            "modsTree": tree_manifest(mods),
            "savesTree": tree_manifest(saves),
            "products": products,
        }
        (private_root / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        os.chmod(private_root / "metadata.json", 0o600)
        for worktree in reversed(added_worktrees):
            run("git", "worktree", "remove", "--force", os.fspath(worktree), cwd=repo)
        added_worktrees.clear()
        private_root.rename(target_root)
    except BaseException:
        for worktree in reversed(added_worktrees):
            subprocess.run(("git", "worktree", "remove", "--force", os.fspath(worktree)), cwd=repo, check=False)
        failed_root = private_root.with_name(f"{private_root.name}.failed-{int(time.time())}")
        if private_root.exists():
            private_root.rename(failed_root)
            print(f"preparation state retained for inspection: {failed_root}", file=sys.stderr)
        raise
    print(json.dumps(metadata, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, subprocess.CalledProcessError, ValueError) as error:
        print(f"prepare failed: {error}", file=sys.stderr)
        raise SystemExit(1)
