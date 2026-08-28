#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
    echo "Usage: $0 <installer-zip> <release-version> <game-reference-assembly-folder>" >&2
    exit 2
fi

archive_path="$(realpath -- "$1")"
release_version="$2"
reference_path="$(realpath -- "$3")"
package_root_name="SMAPI $release_version Linux installer"

for command_name in timeout unzip sha256sum; do
    command -v "$command_name" >/dev/null
done
test -f "$archive_path"
test -f "$reference_path/Stardew Valley.dll"

test_root="$(mktemp -d)"
chmod 700 "$test_root"
cleanup() {
    chmod -R u+rwX "$test_root" 2>/dev/null || true
    rm -rf -- "$test_root"
}
trap cleanup EXIT

unzip -q "$archive_path" -d "$test_root/package"
installer_root="$test_root/package/$package_root_name/internal/linux"
installer="$installer_root/SMAPI.Installer"
test -x "$installer"
test -f "$installer_root/install.dat"

isolated_home="$test_root/home"
isolated_config="$test_root/config"
isolated_data="$test_root/data"
isolated_cache="$test_root/cache"
isolated_runtime="$test_root/runtime"
mkdir -p "$isolated_home" "$isolated_config" "$isolated_data" "$isolated_cache" "$isolated_runtime"
chmod 700 "$isolated_runtime"

run_installer() {
    env \
        HOME="$isolated_home" \
        XDG_CONFIG_HOME="$isolated_config" \
        XDG_DATA_HOME="$isolated_data" \
        XDG_CACHE_HOME="$isolated_cache" \
        XDG_RUNTIME_DIR="$isolated_runtime" \
        timeout 20 "$installer" "$@"
}

expect_exit() {
    local expected_exit="$1"
    shift
    set +e
    run_installer "$@" >"$test_root/expected-failure.stdout" 2>"$test_root/expected-failure.stderr"
    local actual_exit=$?
    set -e
    if [[ $actual_exit -ne $expected_exit ]]; then
        echo "Expected installer exit $expected_exit, got $actual_exit for: $*" >&2
        sed -n '1,120p' "$test_root/expected-failure.stdout" >&2
        sed -n '1,120p' "$test_root/expected-failure.stderr" >&2
        exit 1
    fi
}

# Headless validation failures must be prompt-free and machine-detectable.
expect_exit 2 --no-prompt --install --game-path
expect_exit 2 --no-prompt --install --uninstall --game-path "$test_root/missing-game"
expect_exit 2 --no-prompt --install --game-path "$test_root/missing-game"

game_path="$test_root/game with spaces"
external_initial="$test_root/external-initial-smapi-internal"
mkdir -p "$game_path/Mods/PrivateCustomMod" "$external_initial"
ln -s "$external_initial" "$game_path/smapi-internal"
cp "$reference_path/Stardew Valley.dll" "$game_path/Stardew Valley.dll"
printf '{}\n' > "$game_path/Stardew Valley.deps.json"
printf '#!/usr/bin/env bash\nprintf "vanilla launcher sentinel\\n"\n' > "$game_path/StardewValley"
chmod 755 "$game_path/StardewValley"
printf 'unrelated game data\n' > "$game_path/unrelated-user-file.txt"
printf 'private mod sentinel\n' > "$game_path/Mods/PrivateCustomMod/sentinel.txt"
printf '{"ConsoleColorScheme":"DarkBackground","UserSentinel":"preserve"}\n' > "$game_path/smapi-internal/config.user.json"
printf 'external directory sentinel\n' > "$external_initial/sentinel.txt"

health_reports="$isolated_config/StardewValley/ErrorLogs/HealthReports"
mkdir -p "$health_reports"
printf 'log sentinel\n' > "$isolated_config/StardewValley/ErrorLogs/SMAPI-latest.txt"
printf 'health report sentinel\n' > "$health_reports/report.json"

vanilla_sha="$(sha256sum "$game_path/StardewValley" | cut -d ' ' -f 1)"
unrelated_sha="$(sha256sum "$game_path/unrelated-user-file.txt" | cut -d ' ' -f 1)"
mod_sha="$(sha256sum "$game_path/Mods/PrivateCustomMod/sentinel.txt" | cut -d ' ' -f 1)"
config_sha="$(sha256sum "$game_path/smapi-internal/config.user.json" | cut -d ' ' -f 1)"
external_initial_sha="$(sha256sum "$external_initial/sentinel.txt" | cut -d ' ' -f 1)"
log_sha="$(sha256sum "$isolated_config/StardewValley/ErrorLogs/SMAPI-latest.txt" | cut -d ' ' -f 1)"
report_sha="$(sha256sum "$health_reports/report.json" | cut -d ' ' -f 1)"

# Install, then install again as an update. The second pass exercises replacement and backup paths.
run_installer --no-prompt --install --game-path "$game_path" > "$test_root/install.stdout"
test ! -L "$game_path/smapi-internal"
test "$(sha256sum "$external_initial/sentinel.txt" | cut -d ' ' -f 1)" = "$external_initial_sha"
test "$(sha256sum "$game_path/StardewValley-original" | cut -d ' ' -f 1)" = "$vanilla_sha"
test -x "$game_path/StardewValley"
test -x "$game_path/StardewModdingAPI"
test -x "$game_path/StardewModdingAPI-net6"
test -x "$game_path/StardewModdingAPI-net10"
test -f "$game_path/StardewModdingAPI-net6.deps.json"
test -f "$game_path/StardewModdingAPI-net10.deps.json"
test "$(sha256sum "$game_path/smapi-internal/config.user.json" | cut -d ' ' -f 1)" = "$config_sha"

# Replace the installed internal directory with a link before update. The installer must unlink
# only that leaf and must not enumerate or alter the target directory.
external_update="$test_root/external-update-smapi-internal"
mkdir "$external_update"
printf '{"ConsoleColorScheme":"DarkBackground","UserSentinel":"preserve"}\n' > "$external_update/config.user.json"
printf 'external update sentinel\n' > "$external_update/sentinel.txt"
external_update_sha="$(sha256sum "$external_update/sentinel.txt" | cut -d ' ' -f 1)"
mv "$game_path/smapi-internal" "$test_root/installed-internal-before-update"
ln -s "$external_update" "$game_path/smapi-internal"
run_installer --no-prompt --install --game-path "$game_path" > "$test_root/update.stdout"
test ! -L "$game_path/smapi-internal"
test "$(sha256sum "$external_update/sentinel.txt" | cut -d ' ' -f 1)" = "$external_update_sha"
test "$(sha256sum "$game_path/StardewValley-original" | cut -d ' ' -f 1)" = "$vanilla_sha"
test "$(sha256sum "$game_path/smapi-internal/config.user.json" | cut -d ' ' -f 1)" = "$config_sha"

for preserved in \
    "$game_path/unrelated-user-file.txt:$unrelated_sha" \
    "$game_path/Mods/PrivateCustomMod/sentinel.txt:$mod_sha" \
    "$isolated_config/StardewValley/ErrorLogs/SMAPI-latest.txt:$log_sha" \
    "$health_reports/report.json:$report_sha"; do
    path="${preserved%:*}"
    expected_sha="${preserved##*:}"
    test "$(sha256sum "$path" | cut -d ' ' -f 1)" = "$expected_sha"
done

# Repeat the leaf-link assertion for uninstall.
external_uninstall="$test_root/external-uninstall-smapi-internal"
mkdir "$external_uninstall"
printf 'external uninstall sentinel\n' > "$external_uninstall/sentinel.txt"
external_uninstall_sha="$(sha256sum "$external_uninstall/sentinel.txt" | cut -d ' ' -f 1)"
mv "$game_path/smapi-internal" "$test_root/installed-internal-before-uninstall"
ln -s "$external_uninstall" "$game_path/smapi-internal"

# Uninstall restores the exact vanilla launcher, removes fork hosts, and intentionally leaves mods.
run_installer --no-prompt --uninstall --game-path "$game_path" > "$test_root/uninstall.stdout"
test "$(sha256sum "$external_uninstall/sentinel.txt" | cut -d ' ' -f 1)" = "$external_uninstall_sha"
test "$(sha256sum "$game_path/StardewValley" | cut -d ' ' -f 1)" = "$vanilla_sha"
for removed_path in \
    StardewModdingAPI \
    StardewModdingAPI-net6 \
    StardewModdingAPI-net10 \
    StardewModdingAPI-net6.deps.json \
    StardewModdingAPI-net10.deps.json \
    smapi-internal; do
    test ! -e "$game_path/$removed_path"
done
test "$(sha256sum "$game_path/Mods/PrivateCustomMod/sentinel.txt" | cut -d ' ' -f 1)" = "$mod_sha"
test "$(sha256sum "$isolated_config/StardewValley/ErrorLogs/SMAPI-latest.txt" | cut -d ' ' -f 1)" = "$log_sha"
test "$(sha256sum "$health_reports/report.json" | cut -d ' ' -f 1)" = "$report_sha"

# A linked parent below the selected game directory must be rejected before deleting through it.
linked_parent_game="$test_root/linked-parent-game"
linked_parent_target="$test_root/external-mods"
mkdir -p "$linked_parent_game" "$linked_parent_target/ErrorHandler"
cp "$reference_path/Stardew Valley.dll" "$linked_parent_game/Stardew Valley.dll"
printf '{}\n' > "$linked_parent_game/Stardew Valley.deps.json"
printf '#!/usr/bin/env bash\n' > "$linked_parent_game/StardewValley"
chmod 755 "$linked_parent_game/StardewValley"
printf 'linked parent sentinel\n' > "$linked_parent_target/ErrorHandler/sentinel.txt"
linked_parent_sha="$(sha256sum "$linked_parent_target/ErrorHandler/sentinel.txt" | cut -d ' ' -f 1)"
ln -s "$linked_parent_target" "$linked_parent_game/Mods"
expect_exit 1 --no-prompt --uninstall --game-path "$linked_parent_game"
test "$(sha256sum "$linked_parent_target/ErrorHandler/sentinel.txt" | cut -d ' ' -f 1)" = "$linked_parent_sha"
test -L "$linked_parent_game/Mods"

# A non-interactive filesystem failure must return promptly and nonzero instead of retrying forever.
failure_game="$test_root/read-only-game"
mkdir -p "$failure_game"
cp "$reference_path/Stardew Valley.dll" "$failure_game/Stardew Valley.dll"
printf '{}\n' > "$failure_game/Stardew Valley.deps.json"
printf '#!/usr/bin/env bash\n' > "$failure_game/StardewValley"
chmod 755 "$failure_game/StardewValley"
chmod 555 "$failure_game"
expect_exit 1 --no-prompt --install --game-path "$failure_game"
chmod 755 "$failure_game"

# GitHub-hosted Linux runners provide passwordless sudo. Verify both the normal launcher and the
# directly invoked binary refuse effective UID 0 before extracting or mutating anything.
if command -v sudo >/dev/null && sudo -n true 2>/dev/null; then
    launcher="$test_root/package/$package_root_name/install on Linux.sh"
    for root_command in \
        "'$installer' --no-prompt --install --game-path '$game_path'" \
        "bash '$launcher'"; do
        set +e
        sudo -n env \
            HOME="$isolated_home" \
            XDG_CONFIG_HOME="$isolated_config" \
            XDG_DATA_HOME="$isolated_data" \
            XDG_CACHE_HOME="$isolated_cache" \
            XDG_RUNTIME_DIR="$isolated_runtime" \
            timeout 20 bash -c "$root_command" > "$test_root/root.stdout" 2> "$test_root/root.stderr"
        root_exit=$?
        set -e
        if [[ $root_exit -ne 2 ]]; then
            echo "Expected root installer invocation to exit 2, got $root_exit: $root_command" >&2
            exit 1
        fi
        grep -F "must not be run as root or with sudo" "$test_root/root.stderr" >/dev/null
    done
else
    echo "Skipping effective-UID root invocation check because passwordless sudo isn't available." >&2
fi

echo "Linux installer install/update/uninstall/failure lifecycle checks passed."
