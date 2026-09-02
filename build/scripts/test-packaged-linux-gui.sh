#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
    echo "Usage: $0 <installer-zip> <release-version>" >&2
    exit 2
fi

archive_path="$(realpath -- "$1")"
release_version="$2"
expected_root="SMAPI $release_version Linux installer"

if [[ ! -f "$archive_path" ]]; then
    echo "Installer archive not found: $archive_path" >&2
    exit 1
fi

for command_name in cmp dirname find grep id mktemp readlink realpath sleep stat timeout unzip xauth xvfb-run zipinfo; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Required packaged-GUI test command is unavailable: $command_name" >&2
        exit 1
    fi
done

test_root="$(mktemp -d)"
active_smoke_pid=""
active_gui_pid=""
cleanup() {
    local active_exe=""
    set +e
    if [[ -n "$active_gui_pid" && -n "${gui_apphost-}" ]]; then
        active_exe="$(readlink -- "/proc/$active_gui_pid/exe" 2>/dev/null || true)"
    fi
    if [[ "$active_exe" == "${gui_apphost-}" ]]; then
        kill -TERM "$active_gui_pid" 2>/dev/null
    fi
    if [[ -n "$active_smoke_pid" ]]; then
        kill -TERM "$active_smoke_pid" 2>/dev/null
        wait "$active_smoke_pid" 2>/dev/null
    fi
    if [[ -n "$test_root" && -d "$test_root" ]]; then
        rm -rf -- "$test_root"
    fi
}
trap cleanup EXIT

entries_path="$test_root/entries.txt"
zipinfo -1 "$archive_path" > "$entries_path"
foreign_entry=false
while IFS= read -r entry; do
    if [[ "$entry" != "$expected_root" && "$entry" != "$expected_root/"* ]]; then
        foreign_entry=true
        break
    fi
done < "$entries_path"
if [[ ! -s "$entries_path" ]] \
    || grep -Eq '(^/|(^|/)\.\.(/|$)|\\)' "$entries_path" \
    || [[ "$foreign_entry" == true ]]; then
    echo "Installer archive has an empty, unsafe, or foreign layout." >&2
    exit 1
fi

unzip -q "$archive_path" -d "$test_root/extracted"
package_root="$test_root/extracted/$expected_root"
launcher="$package_root/install on Linux (graphical).sh"
gui_apphost="$package_root/internal/linux/SMAPI.Installer.Gui"
console_launcher="$package_root/install on Linux.sh"
console_apphost="$package_root/internal/linux/SMAPI.Installer"

assert_single_link_executable() {
    local description="$1"
    local path="$2"
    local mode

    if [[ ! -f "$path" || -L "$path" || ! -s "$path" || ! -x "$path" ]]; then
        echo "$description must be one nonempty ordinary executable: $path" >&2
        exit 1
    fi
    if [[ "$(stat -c %F -- "$path")" != "regular file" || "$(stat -c %h -- "$path")" != 1 ]]; then
        echo "$description must be a single-link regular file: $path" >&2
        exit 1
    fi
    mode="$(stat -c %a -- "$path")"
    if (( (8#$mode & 07000) != 0 )); then
        echo "$description must not have special permission bits: $path" >&2
        exit 1
    fi
}

assert_single_link_executable "The graphical launcher" "$launcher"
assert_single_link_executable "The self-contained graphical apphost" "$gui_apphost"
assert_single_link_executable "The retained console fallback launcher" "$console_launcher"
assert_single_link_executable "The retained console fallback apphost" "$console_apphost"
if [[ "$(dirname -- "$gui_apphost")" != "$(dirname -- "$console_apphost")" ]]; then
    echo "The graphical apphost and protocol/console sibling are not in the same packaged directory." >&2
    exit 1
fi

# Put a failing `dotnet` first on PATH. A genuinely self-contained apphost never invokes it.
guard_bin="$test_root/guard-bin"
dotnet_marker="$test_root/system-dotnet-was-invoked"
mkdir "$guard_bin"
printf '%s\n' \
    '#!/usr/bin/env bash' \
    'set -euo pipefail' \
    ': > "$SMAPI_GUI_DOTNET_MARKER"' \
    'printf "%s\\n" "Packaged GUI unexpectedly invoked dotnet from PATH." >&2' \
    'exit 97' \
    > "$guard_bin/dotnet"
chmod 755 "$guard_bin/dotnet"
guarded_path="$guard_bin:/usr/bin:/bin"

assert_no_runtime_leak() {
    local state_root="$1"
    local output_path="$2"

    if [[ -e "$dotnet_marker" ]]; then
        echo "The packaged graphical apphost invoked a system dotnet command." >&2
        exit 1
    fi
    if grep -Eiq '(^|[^[:alpha:]])(fatal|unhandled exception)([^[:alpha:]]|$)' "$output_path"; then
        echo "The packaged GUI emitted a fatal diagnostic; raw output is withheld from CI logs." >&2
        exit 1
    fi
    if grep -Eiq '(/home/[^/[:space:]]+|/Users/[^/[:space:]]+|authorization:|bearer[[:space:]]|cookie:|[?&](token|signature)=|StardewValley|(^|/)Mods(/|$)|(^|/)Saves(/|$))' "$output_path"; then
        echo "The packaged GUI output contained a private or game-shaped value; raw output is withheld from CI logs." >&2
        exit 1
    fi
    if find "$state_root" -mindepth 1 \
        \( -iname 'StardewValley' -o -iname 'Mods' -o -iname 'Saves' -o -iname '*.log' -o -iname 'install.dat' \) \
        -print -quit | grep -q .; then
        echo "The packaged GUI created game-shaped, installer-bundle, or log state during a smoke test." >&2
        exit 1
    fi
    if find "$state_root/tmp" -mindepth 1 -maxdepth 1 -name 'smapi-installer-gui.*' -print -quit | grep -q .; then
        echo "The graphical launcher left private single-file bundle state behind." >&2
        find "$state_root/tmp" -mindepth 1 -maxdepth 2 -name 'smapi-installer-gui.*' -print >&2
        exit 1
    fi
    local process_exe resolved_exe
    for process_exe in /proc/[0-9]*/exe; do
        resolved_exe="$(readlink -- "$process_exe" 2>/dev/null || true)"
        if [[ "$resolved_exe" == "$gui_apphost" ]]; then
            echo "The bounded smoke left the packaged graphical apphost running." >&2
            exit 1
        fi
    done
}

make_state_root() {
    local name="$1"
    local state_root="$test_root/state-$name"

    mkdir -p \
        "$state_root/home" \
        "$state_root/cache" \
        "$state_root/config" \
        "$state_root/data" \
        "$state_root/runtime" \
        "$state_root/tmp" \
        "$state_root/work"
    chmod 700 \
        "$state_root/home" \
        "$state_root/cache" \
        "$state_root/config" \
        "$state_root/data" \
        "$state_root/runtime" \
        "$state_root/tmp" \
        "$state_root/work"
    printf '%s\n' "$state_root"
}

run_window_smoke() {
    local name="$1"
    local isolate_network="$2"
    shift 2
    local state_root output_path status smoke_pid gui_pid process_exe resolved_exe current_uid current_gid
    local -a isolation_prefix=()

    state_root="$(make_state_root "$name")"
    output_path="$test_root/$name.output"
    if [[ "$isolate_network" == true ]]; then
        current_uid="$(id -u)"
        current_gid="$(id -g)"
        if [[ "$current_uid" -eq 0 ]]; then
            echo "The production packaged-GUI smoke requires a non-root test user." >&2
            exit 1
        elif command -v unshare >/dev/null 2>&1 \
            && unshare --user --map-user="$current_uid" --map-group="$current_gid" --net true >/dev/null 2>&1; then
            isolation_prefix=(unshare --user --map-user="$current_uid" --map-group="$current_gid" --net)
        elif command -v sudo >/dev/null 2>&1 \
            && sudo -n unshare --net --setuid="$current_uid" --setgid="$current_gid" true >/dev/null 2>&1; then
            isolation_prefix=(sudo -n unshare --net --setuid="$current_uid" --setgid="$current_gid")
        else
            echo "The production packaged-GUI smoke requires a private network namespace; unprivileged user/network namespaces and passwordless-sudo unshare are both unavailable." >&2
            exit 1
        fi
    fi

    (
        cd "$state_root/work"
        "${isolation_prefix[@]}" env -i \
            PATH="$guarded_path" \
            HOME="$state_root/home" \
            XDG_CACHE_HOME="$state_root/cache" \
            XDG_CONFIG_HOME="$state_root/config" \
            XDG_DATA_HOME="$state_root/data" \
            XDG_RUNTIME_DIR="$state_root/runtime" \
            TMPDIR="$state_root/tmp" \
            SMAPI_GUI_DOTNET_MARKER="$dotnet_marker" \
            DOTNET_ROOT="$state_root/no-system-dotnet" \
            DOTNET_ROOT_X64="$state_root/no-system-dotnet" \
            DOTNET_MULTILEVEL_LOOKUP=0 \
            DOTNET_EnableDiagnostics=0 \
            DOTNET_CLI_TELEMETRY_OPTOUT=1 \
            DOTNET_NOLOGO=1 \
            HTTP_PROXY=http://127.0.0.1:9 \
            HTTPS_PROXY=http://127.0.0.1:9 \
            ALL_PROXY=http://127.0.0.1:9 \
            http_proxy=http://127.0.0.1:9 \
            https_proxy=http://127.0.0.1:9 \
            all_proxy=http://127.0.0.1:9 \
            NO_PROXY=localhost,127.0.0.1 \
            no_proxy=localhost,127.0.0.1 \
            XDG_SESSION_TYPE=x11 \
            timeout --signal=TERM --kill-after=5s 30s \
                xvfb-run -a "$launcher" "$@"
    ) > "$output_path" 2>&1 &
    smoke_pid=$!
    active_smoke_pid="$smoke_pid"

    # Find the exact packaged child, then require five seconds of health. Terminating the child
    # (instead of the outer Xvfb harness) lets the launcher execute its normal bundle cleanup. The
    # outer timeout remains an independent bounded watchdog for every process in the harness.
    gui_pid=""
    for _ in {1..1500}; do
        for process_exe in /proc/[0-9]*/exe; do
            resolved_exe="$(readlink -- "$process_exe" 2>/dev/null || true)"
            if [[ "$resolved_exe" == "$gui_apphost" ]]; then
                gui_pid="${process_exe#/proc/}"
                gui_pid="${gui_pid%/exe}"
                break 2
            fi
        done
        kill -0 "$smoke_pid" 2>/dev/null || break
        sleep 0.01
    done
    if [[ -z "$gui_pid" ]]; then
        set +e
        wait "$smoke_pid"
        status=$?
        set -e
        active_smoke_pid=""
        echo "The packaged GUI $name apphost did not start before the bounded watchdog exited (exit $status)." >&2
        exit 1
    fi
    active_gui_pid="$gui_pid"
    for _ in {1..50}; do
        if ! kill -0 "$smoke_pid" 2>/dev/null || ! kill -0 "$gui_pid" 2>/dev/null; then
            echo "The packaged GUI $name window exited before the five-second health interval completed." >&2
            exit 1
        fi
        sleep 0.1
    done
    resolved_exe="$(readlink -- "/proc/$gui_pid/exe" 2>/dev/null || true)"
    if [[ "$resolved_exe" != "$gui_apphost" ]]; then
        active_gui_pid=""
        set +e
        wait "$smoke_pid"
        status=$?
        set -e
        active_smoke_pid=""
        echo "The packaged GUI $name apphost identity changed before controlled termination (exit $status)." >&2
        exit 1
    fi
    kill -TERM "$gui_pid"
    set +e
    wait "$smoke_pid"
    status=$?
    set -e
    active_gui_pid=""
    active_smoke_pid=""

    if [[ "$status" -ne 143 ]]; then
        echo "The packaged GUI $name window did not settle through the launcher after the five-second smoke (exit $status)." >&2
        exit 1
    fi
    assert_no_runtime_leak "$state_root" "$output_path"
}

run_launcher_term_smoke() {
    local state_root output_path status

    state_root="$(make_state_root launcher-term)"
    output_path="$test_root/launcher-term.output"
    set +e
    (
        cd "$state_root/work"
        env -i \
            PATH="$guarded_path" \
            HOME="$state_root/home" \
            XDG_CACHE_HOME="$state_root/cache" \
            XDG_CONFIG_HOME="$state_root/config" \
            XDG_DATA_HOME="$state_root/data" \
            XDG_RUNTIME_DIR="$state_root/runtime" \
            TMPDIR="$state_root/tmp" \
            SMAPI_GUI_DOTNET_MARKER="$dotnet_marker" \
            DOTNET_ROOT="$state_root/no-system-dotnet" \
            DOTNET_ROOT_X64="$state_root/no-system-dotnet" \
            DOTNET_MULTILEVEL_LOOKUP=0 \
            DOTNET_EnableDiagnostics=0 \
            DOTNET_CLI_TELEMETRY_OPTOUT=1 \
            DOTNET_NOLOGO=1 \
            XDG_SESSION_TYPE=x11 \
            timeout --signal=TERM --kill-after=5s 30s \
                xvfb-run -a bash -c '
                    set -euo pipefail
                    launcher="$1"
                    gui_apphost="$2"
                    state_root="$3"
                    launcher_pid=""
                    gui_pid=""

                    cleanup_signal_case() {
                        local current_exe=""
                        set +e
                        if [[ -n "$launcher_pid" ]]; then
                            # The launcher is this shell direct, unreaped child, so its PID cannot
                            # be reused before wait returns.
                            kill -TERM "$launcher_pid" 2>/dev/null
                            wait "$launcher_pid" 2>/dev/null
                        fi
                        if [[ -n "$gui_pid" ]]; then
                            current_exe="$(readlink -- "/proc/$gui_pid/exe" 2>/dev/null || true)"
                            if [[ "$current_exe" == "$gui_apphost" ]]; then
                                kill -TERM "$gui_pid" 2>/dev/null
                            fi
                        fi
                    }
                    trap cleanup_signal_case EXIT

                    "$launcher" --demo &
                    launcher_pid=$!
                    for _ in {1..1500}; do
                        if [[ -r "/proc/$launcher_pid/task/$launcher_pid/children" ]]; then
                            IFS= read -r children < "/proc/$launcher_pid/task/$launcher_pid/children" || true
                            for child in $children; do
                                child_exe="$(readlink -- "/proc/$child/exe" 2>/dev/null || true)"
                                if [[ "$child_exe" == "$gui_apphost" ]]; then
                                    gui_pid="$child"
                                    break 2
                                fi
                            done
                        fi
                        kill -0 "$launcher_pid" 2>/dev/null || break
                        sleep 0.01
                    done
                    if [[ -z "$gui_pid" ]]; then
                        exit 1
                    fi

                    # Require a live exact child before signalling the launcher itself. Since the
                    # launcher remains an unreaped direct child, this TERM cannot target a reused PID.
                    for _ in {1..10}; do
                        kill -0 "$launcher_pid"
                        [[ "$(readlink -- "/proc/$gui_pid/exe" 2>/dev/null || true)" == "$gui_apphost" ]]
                        sleep 0.1
                    done
                    kill -TERM "$launcher_pid"
                    set +e
                    wait "$launcher_pid"
                    launcher_status=$?
                    set -e
                    launcher_pid=""
                    if [[ "$launcher_status" -ne 143 ]]; then
                        exit 1
                    fi

                    # The launcher must settle its exact apphost, not merely remove the bundle path.
                    for _ in {1..500}; do
                        current_exe="$(readlink -- "/proc/$gui_pid/exe" 2>/dev/null || true)"
                        if [[ "$current_exe" != "$gui_apphost" ]]; then
                            gui_pid=""
                            break
                        fi
                        sleep 0.01
                    done
                    if [[ -n "$gui_pid" ]]; then
                        exit 1
                    fi
                    if find "$state_root/tmp" -mindepth 1 -maxdepth 1 -name "smapi-installer-gui.*" -print -quit | grep -q .; then
                        exit 1
                    fi
                    trap - EXIT
                ' signal-supervisor "$launcher" "$gui_apphost" "$state_root"
    ) > "$output_path" 2>&1
    status=$?
    set -e
    if [[ "$status" -ne 0 ]]; then
        echo "The packaged graphical launcher did not settle its exact apphost and private bundle after TERM; raw output is withheld from CI logs." >&2
        exit 1
    fi
    assert_no_runtime_leak "$state_root" "$output_path"
}

# The sealed demo proves that the packaged single-file apphost starts without system dotnet. The
# production initial window is exercised with remote traffic denied; catalog failure may render, but
# package download, sibling-backend launch, discovery, logging, and game mutation require user action.
run_window_smoke demo false --demo
run_window_smoke production true
run_launcher_term_smoke

invalid_state="$(make_state_root invalid-arguments)"
set +e
(
    cd "$invalid_state/work"
    env -i \
        PATH="$guarded_path" \
        HOME="$invalid_state/home" \
        XDG_CACHE_HOME="$invalid_state/cache" \
        XDG_CONFIG_HOME="$invalid_state/config" \
        XDG_DATA_HOME="$invalid_state/data" \
        XDG_RUNTIME_DIR="$invalid_state/runtime" \
        TMPDIR="$invalid_state/tmp" \
        SMAPI_GUI_DOTNET_MARKER="$dotnet_marker" \
        DOTNET_ROOT="$invalid_state/no-system-dotnet" \
        DOTNET_ROOT_X64="$invalid_state/no-system-dotnet" \
        DOTNET_MULTILEVEL_LOOKUP=0 \
        "$launcher" --unexpected
) > "$test_root/invalid.stdout" 2> "$test_root/invalid.stderr"
invalid_status=$?
set -e
if [[ "$invalid_status" -ne 2 ]] || [[ -s "$test_root/invalid.stdout" ]]; then
    echo "The packaged graphical launcher did not reject an invalid argument before Avalonia startup." >&2
    exit 1
fi
printf '%s\n' 'The graphical installer accepts either no arguments or exactly --demo.' > "$test_root/invalid.expected"
if ! cmp -s -- "$test_root/invalid.expected" "$test_root/invalid.stderr"; then
    echo "The packaged graphical launcher emitted an unexpected invalid-argument diagnostic." >&2
    exit 1
fi
assert_no_runtime_leak "$invalid_state" "$test_root/invalid.stderr"

root_effects="$test_root/root-effects"
mkdir "$root_effects"
root_status=""
root_runner_description=""
root_output="$test_root/root.stdout"
root_error="$test_root/root.stderr"
root_environment=(
    PATH="$guarded_path"
    HOME="$root_effects/home"
    XDG_CACHE_HOME="$root_effects/cache"
    XDG_CONFIG_HOME="$root_effects/config"
    XDG_DATA_HOME="$root_effects/data"
    XDG_RUNTIME_DIR="$root_effects/runtime"
    TMPDIR="$root_effects/tmp"
    DOTNET_BUNDLE_EXTRACT_BASE_DIR="$root_effects/bundle"
    SMAPI_GUI_DOTNET_MARKER="$root_effects/system-dotnet-was-invoked"
)

set +e
if [[ "$(id -u)" -eq 0 ]]; then
    root_runner_description="the current root user"
    env -i "${root_environment[@]}" "$launcher" --demo > "$root_output" 2> "$root_error"
    root_status=$?
elif command -v unshare >/dev/null 2>&1 && unshare --user --map-root-user true >/dev/null 2>&1; then
    root_runner_description="an isolated user namespace mapped to effective UID 0"
    unshare --user --map-root-user env -i "${root_environment[@]}" "$launcher" --demo \
        > "$root_output" 2> "$root_error"
    root_status=$?
elif command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then
    root_runner_description="non-interactive sudo"
    sudo -n env -i "${root_environment[@]}" "$launcher" --demo > "$root_output" 2> "$root_error"
    root_status=$?
else
    echo "The required packaged graphical-launcher root-refusal gate cannot run because this environment provides neither effective UID 0, an enabled unprivileged user namespace, nor non-interactive sudo." >&2
    exit 1
fi
set -e

if [[ -n "$root_status" ]]; then
    if [[ "$root_status" -ne 2 ]] || [[ -s "$root_output" ]]; then
        echo "The graphical launcher did not refuse $root_runner_description before startup (exit $root_status)." >&2
        exit 1
    fi
    printf '%s\n' 'The SMAPI graphical installer must not be run as root or with sudo. Run it as your normal desktop user instead.' > "$test_root/root.expected"
    if ! cmp -s -- "$test_root/root.expected" "$root_error"; then
        echo "The graphical launcher emitted an unexpected root-refusal diagnostic." >&2
        exit 1
    fi
    if find "$root_effects" -mindepth 1 -print -quit | grep -q .; then
        echo "The graphical launcher created bundle, temp, home, XDG, game, or log state before root refusal." >&2
        exit 1
    fi
fi

echo "Packaged Linux GUI checks passed for $archive_path."
