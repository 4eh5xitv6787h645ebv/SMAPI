#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 4 ]]; then
    echo "Usage: $0 <fork-installer-zip> <fork-version> <official-4.5.2-installer-zip> <game-reference-folder>" >&2
    exit 2
fi

fork_archive="$(realpath -- "$1")"
fork_version="$2"
official_archive="$(realpath -- "$3")"
reference_path="$(realpath -- "$4")"

test -f "$fork_archive"
test -f "$official_archive"
test -f "$reference_path/Stardew Valley.dll"

test_root="$(mktemp -d)"
chmod 700 "$test_root"
trap 'chmod -R u+rwX "$test_root" 2>/dev/null || true; rm -rf -- "$test_root"' EXIT

unzip -q "$fork_archive" -d "$test_root/fork"
unzip -q "$official_archive" -d "$test_root/official"
fork_installer="$test_root/fork/SMAPI $fork_version Linux installer/internal/linux/SMAPI.Installer"
official_installer="$test_root/official/SMAPI 4.5.2 installer/internal/linux/SMAPI.Installer"
test -x "$fork_installer"
test -x "$official_installer"

isolated_home="$test_root/home"
isolated_config="$test_root/config"
isolated_data="$test_root/data"
isolated_cache="$test_root/cache"
isolated_runtime="$test_root/runtime"
mkdir -p "$isolated_home" "$isolated_config" "$isolated_data" "$isolated_cache" "$isolated_runtime"
chmod 700 "$isolated_runtime"

run_isolated() {
    local installer="$1"
    shift
    env \
        HOME="$isolated_home" \
        XDG_CONFIG_HOME="$isolated_config" \
        XDG_DATA_HOME="$isolated_data" \
        XDG_CACHE_HOME="$isolated_cache" \
        XDG_RUNTIME_DIR="$isolated_runtime" \
        timeout 30 "$installer" "$@"
}

game_path="$test_root/game"
mkdir -p "$game_path/Mods/PrivateCustomMod" "$game_path/smapi-internal"
cp "$reference_path/Stardew Valley.dll" "$game_path/Stardew Valley.dll"
printf '{}\n' > "$game_path/Stardew Valley.deps.json"
printf '#!/usr/bin/env bash\nprintf "vanilla rollback sentinel\\n"\n' > "$game_path/StardewValley"
chmod 755 "$game_path/StardewValley"
printf 'private mod sentinel\n' > "$game_path/Mods/PrivateCustomMod/sentinel.txt"
printf 'unrelated game sentinel\n' > "$game_path/unrelated-user-file.txt"
printf '{"ConsoleColorScheme":"DarkBackground"}\n' > "$game_path/smapi-internal/config.user.json"
mkdir -p "$isolated_config/StardewValley/ErrorLogs/HealthReports"
printf 'log sentinel\n' > "$isolated_config/StardewValley/ErrorLogs/SMAPI-latest.txt"
printf 'report sentinel\n' > "$isolated_config/StardewValley/ErrorLogs/HealthReports/report.json"

vanilla_sha="$(sha256sum "$game_path/StardewValley" | cut -d ' ' -f 1)"
mod_sha="$(sha256sum "$game_path/Mods/PrivateCustomMod/sentinel.txt" | cut -d ' ' -f 1)"
unrelated_sha="$(sha256sum "$game_path/unrelated-user-file.txt" | cut -d ' ' -f 1)"
log_sha="$(sha256sum "$isolated_config/StardewValley/ErrorLogs/SMAPI-latest.txt" | cut -d ' ' -f 1)"
report_sha="$(sha256sum "$isolated_config/StardewValley/ErrorLogs/HealthReports/report.json" | cut -d ' ' -f 1)"

run_isolated "$fork_installer" --no-prompt --install --game-path "$game_path" > "$test_root/fork-install.stdout"
cp "$game_path/smapi-internal/config.user.json" "$test_root/config.user.backup.json"
cp -a "$isolated_config/StardewValley/ErrorLogs" "$test_root/ErrorLogs.backup"
config_sha="$(sha256sum "$test_root/config.user.backup.json" | cut -d ' ' -f 1)"

# The fork uninstaller must run before official 4.5.2 so fork-only hosts don't remain as residue.
run_isolated "$fork_installer" --no-prompt --uninstall --game-path "$game_path" > "$test_root/fork-uninstall.stdout"
test "$(sha256sum "$game_path/StardewValley" | cut -d ' ' -f 1)" = "$vanilla_sha"
for fork_only_path in \
    StardewModdingAPI-net6 \
    StardewModdingAPI-net6.dll \
    StardewModdingAPI-net6.deps.json \
    StardewModdingAPI-net6.runtimeconfig.json \
    StardewModdingAPI-net10 \
    StardewModdingAPI-net10.dll \
    StardewModdingAPI-net10.deps.json \
    StardewModdingAPI-net10.runtimeconfig.json; do
    test ! -e "$game_path/$fork_only_path"
done

run_isolated "$official_installer" --no-prompt --install --game-path "$game_path" > "$test_root/official-install.stdout"
test -x "$game_path/StardewValley"
test -e "$game_path/StardewModdingAPI"
test ! -e "$game_path/StardewModdingAPI-net10"
cp "$test_root/config.user.backup.json" "$game_path/smapi-internal/config.user.json"
rm -rf "$isolated_config/StardewValley/ErrorLogs"
cp -a "$test_root/ErrorLogs.backup" "$isolated_config/StardewValley/ErrorLogs"
test "$(sha256sum "$game_path/smapi-internal/config.user.json" | cut -d ' ' -f 1)" = "$config_sha"

test "$(sha256sum "$game_path/Mods/PrivateCustomMod/sentinel.txt" | cut -d ' ' -f 1)" = "$mod_sha"
test "$(sha256sum "$game_path/unrelated-user-file.txt" | cut -d ' ' -f 1)" = "$unrelated_sha"
test "$(sha256sum "$isolated_config/StardewValley/ErrorLogs/SMAPI-latest.txt" | cut -d ' ' -f 1)" = "$log_sha"
test "$(sha256sum "$isolated_config/StardewValley/ErrorLogs/HealthReports/report.json" | cut -d ' ' -f 1)" = "$report_sha"

echo "Linux rollback from fork alpha to official SMAPI 4.5.2 passed."
