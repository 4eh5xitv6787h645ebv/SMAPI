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

for command_name in cmp dirname find grep id mktemp readlink realpath sed sleep stat timeout unzip xauth xvfb-run zipinfo; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Required packaged-GUI test command is unavailable: $command_name" >&2
        exit 1
    fi
done

test_root="$(mktemp -d)"
active_smoke_pid=""
active_gui_pid=""
active_gui_start_time=""

get_process_start_time() {
    local pid="$1"
    local stat_line stat_fields

    IFS= read -r stat_line < "/proc/$pid/stat" 2>/dev/null || return 1
    stat_fields="${stat_line##*) }"
    set -- $stat_fields
    [[ $# -ge 20 ]] || return 1
    printf '%s\n' "${20}"
}

is_exact_gui_identity() {
    local pid="$1"
    local expected_start_time="$2"
    local current_exe current_start_time

    [[ -n "$pid" && -n "$expected_start_time" && -n "${gui_apphost-}" ]] || return 1
    current_exe="$(readlink -- "/proc/$pid/exe" 2>/dev/null || true)"
    [[ "$current_exe" == "$gui_apphost" ]] || return 1
    current_start_time="$(get_process_start_time "$pid" 2>/dev/null || true)"
    [[ "$current_start_time" == "$expected_start_time" ]]
}

is_running_direct_child_job() {
    local pid="$1"
    local active_pid child_pid children="" children_path jobs_snapshot
    local is_running_job=false

    [[ -n "$pid" ]] || return 1
    jobs_snapshot="$test_root/.running-jobs"
    jobs -pr > "$jobs_snapshot"
    while IFS= read -r active_pid; do
        if [[ "$active_pid" == "$pid" ]]; then
            is_running_job=true
            break
        fi
    done < "$jobs_snapshot"
    [[ "$is_running_job" == true ]] || return 1
    children_path="/proc/$BASHPID/task/$BASHPID/children"
    [[ -r "$children_path" ]] || return 1
    IFS= read -r children < "$children_path" 2>/dev/null || [[ -n "$children" ]] || return 1
    for child_pid in $children; do
        [[ "$child_pid" == "$pid" ]] && return 0
    done
    return 1
}

cleanup() {
    set +e
    if is_exact_gui_identity "$active_gui_pid" "$active_gui_start_time"; then
        kill -TERM "$active_gui_pid" 2>/dev/null
    fi
    if [[ -n "$active_smoke_pid" ]]; then
        if is_running_direct_child_job "$active_smoke_pid"; then
            kill -TERM "$active_smoke_pid" 2>/dev/null
        fi
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

# Make only launcher-side Linux executable identity reads fail. The supervisor keeps the real
# readlink on PATH so it can independently retain and settle the exact packaged apphost.
identity_guard_bin="$test_root/identity-guard-bin"
mkdir "$identity_guard_bin"
printf '%s\n' \
    '#!/usr/bin/env bash' \
    'set -euo pipefail' \
    '[[ "$SMAPI_GUI_EXPECTED_LAUNCHER_PID" =~ ^[1-9][0-9]*$ ]] || exec /usr/bin/readlink "$@"' \
    'should_inject=false' \
    'ancestor_pid="$PPID"' \
    'for _ in {1..12}; do' \
    '    [[ "$ancestor_pid" =~ ^[1-9][0-9]*$ && -r "/proc/$ancestor_pid/status" ]] || break' \
    '    ancestor_exe="$(/usr/bin/readlink -e -- "/proc/$ancestor_pid/exe" 2>/dev/null || true)"' \
    '    [[ -n "$ancestor_exe" ]] || exec /usr/bin/readlink "$@"' \
    '    if [[ "$ancestor_exe" == "$SMAPI_GUI_EXPECTED_APPHOST" ]]; then' \
    '        exec /usr/bin/readlink "$@"' \
    '    fi' \
    '    if [[ "$ancestor_pid" == "$SMAPI_GUI_EXPECTED_LAUNCHER_PID" ]]; then' \
    '        [[ "$ancestor_exe" == "$SMAPI_GUI_EXPECTED_LAUNCHER_BASH" && -r "/proc/$ancestor_pid/cmdline" ]] || break' \
    '        launcher_argument=false' \
    '        while IFS= read -r -d "" argument; do' \
    '            if [[ "$argument" == "$SMAPI_GUI_EXPECTED_LAUNCHER" ]]; then' \
    '                launcher_argument=true' \
    '                break' \
    '            fi' \
    '        done < "/proc/$ancestor_pid/cmdline"' \
    '        [[ "$launcher_argument" == true ]] && should_inject=true' \
    '        break' \
    '    fi' \
    '    parent_pid=""' \
    '    while IFS=$'"'"'\t'"'"' read -r key value _; do' \
    '        if [[ "$key" == "PPid:" ]]; then' \
    '            parent_pid="$value"' \
    '            break' \
    '        fi' \
    '    done < "/proc/$ancestor_pid/status"' \
    '    [[ -n "$parent_pid" && "$parent_pid" != "$ancestor_pid" ]] || break' \
    '    ancestor_pid="$parent_pid"' \
    'done' \
    'if [[ "$should_inject" != true ]]; then' \
    '    exec /usr/bin/readlink "$@"' \
    'fi' \
    'for argument in "$@"; do' \
    '    if [[ "$argument" =~ ^/proc/[0-9]+/exe$ ]]; then' \
    '        read_count=0' \
    '        if [[ -f "$SMAPI_GUI_IDENTITY_READ_COUNT" ]]; then' \
    '            IFS= read -r read_count < "$SMAPI_GUI_IDENTITY_READ_COUNT" || [[ -n "$read_count" ]]' \
    '        fi' \
    '        [[ "$read_count" =~ ^[0-9]+$ ]] || exit 1' \
    '        ((read_count += 1))' \
    '        printf "%s\n" "$read_count" > "$SMAPI_GUI_IDENTITY_READ_COUNT"' \
    '        if [[ "${SMAPI_GUI_IDENTITY_READ_DELAY:-0}" != 0 ]]; then' \
    '            sleep "$SMAPI_GUI_IDENTITY_READ_DELAY"' \
    '        fi' \
    '        if (( read_count > SMAPI_GUI_IDENTITY_FAIL_AFTER )); then' \
    '            : > "$SMAPI_GUI_IDENTITY_FAILURE_MARKER"' \
    '            exit 1' \
    '        fi' \
    '    fi' \
    'done' \
    'exec /usr/bin/readlink "$@"' \
    > "$identity_guard_bin/readlink"
chmod 755 "$identity_guard_bin/readlink"
identity_guarded_path="$identity_guard_bin:$guarded_path"
launcher_bash="$(readlink -e -- "$(command -v bash)")"
identity_scope_proof_count="$test_root/non-launcher-identity-read-count"
identity_scope_proof_marker="$test_root/non-launcher-identity-failure"
identity_scope_proof_output="$test_root/non-launcher-readlink.output"
env \
    SMAPI_GUI_EXPECTED_APPHOST="$gui_apphost" \
    SMAPI_GUI_EXPECTED_LAUNCHER="$launcher" \
    SMAPI_GUI_EXPECTED_LAUNCHER_BASH="$launcher_bash" \
    SMAPI_GUI_EXPECTED_LAUNCHER_PID="$BASHPID" \
    SMAPI_GUI_IDENTITY_READ_COUNT="$identity_scope_proof_count" \
    SMAPI_GUI_IDENTITY_FAILURE_MARKER="$identity_scope_proof_marker" \
    SMAPI_GUI_IDENTITY_FAIL_AFTER=0 \
    "$identity_guard_bin/readlink" -e -- "/proc/$BASHPID/exe" > "$identity_scope_proof_output"
printf '%s\n' "$launcher_bash" > "$test_root/non-launcher-readlink.expected"
if ! cmp -s -- "$test_root/non-launcher-readlink.expected" "$identity_scope_proof_output" \
    || [[ -e "$identity_scope_proof_count" || -e "$identity_scope_proof_marker" ]]; then
    echo "The launcher-scoped identity fault injector affected a representative non-launcher read." >&2
    exit 1
fi

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
    local state_root xauthority_path output_path status smoke_pid gui_pid gui_start_time process_exe resolved_exe current_uid current_gid
    local -a isolation_prefix=()

    state_root="$(make_state_root "$name")"
    # Ubuntu's xvfb-run owns a temporary directory only when it also creates the Xauthority file.
    # Its EXIT cleanup can intermittently fail and replace the already-verified launcher status
    # with xvfb-run's reserved exit 5. Keep authorization state in this smoke's private root so
    # wrapper housekeeping can't mask the launcher result we're explicitly testing.
    xauthority_path="$state_root/xauthority"
    : > "$xauthority_path"
    chmod 600 "$xauthority_path"
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
                xvfb-run -a -f "$xauthority_path" "$launcher" "$@"
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
    gui_start_time="$(get_process_start_time "$gui_pid" 2>/dev/null || true)"
    if [[ -z "$gui_start_time" ]] || ! is_exact_gui_identity "$gui_pid" "$gui_start_time"; then
        set +e
        wait "$smoke_pid"
        status=$?
        set -e
        active_smoke_pid=""
        echo "The packaged GUI $name apphost identity could not be retained (exit $status)." >&2
        exit 1
    fi
    active_gui_pid="$gui_pid"
    active_gui_start_time="$gui_start_time"
    for _ in {1..50}; do
        if ! kill -0 "$smoke_pid" 2>/dev/null || ! is_exact_gui_identity "$gui_pid" "$gui_start_time"; then
            echo "The packaged GUI $name window exited before the five-second health interval completed." >&2
            exit 1
        fi
        sleep 0.1
    done
    if ! is_exact_gui_identity "$gui_pid" "$gui_start_time"; then
        active_gui_pid=""
        active_gui_start_time=""
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
    active_gui_start_time=""
    active_smoke_pid=""

    if [[ "$status" -ne 143 ]]; then
        echo "The packaged GUI $name window did not settle through the launcher after the five-second smoke (exit $status)." >&2
        exit 1
    fi
    assert_no_runtime_leak "$state_root" "$output_path"
}

run_launcher_signal_smoke() {
    local signal_name="$1"
    local expected_status="$2"
    local stop_child="$3"
    local repeat_signals="${4:-false}"
    local case_name state_root output_path status

    case_name="launcher-${signal_name,,}"
    if [[ "$stop_child" == true ]]; then
        case_name+="-stopped-child"
    fi
    if [[ "$repeat_signals" == true ]]; then
        case_name+="-repeated-signals"
    fi
    state_root="$(make_state_root "$case_name")"
    output_path="$test_root/$case_name.output"
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
                    signal_name="$4"
                    expected_status="$5"
                    stop_child="$6"
                    repeat_signals="$7"
                    state=""
                    launcher_pid=""
                    gui_pid=""
                    gui_start_time=""

                    fail_case() {
                        printf "QUALIFIER_FAILURE=%s\n" "$1" >&2
                        exit 1
                    }

                    get_start_time() {
                        local pid="$1"
                        local stat_line stat_fields

                        IFS= read -r stat_line < "/proc/$pid/stat" 2>/dev/null || return 1
                        stat_fields="${stat_line##*) }"
                        set -- $stat_fields
                        [[ $# -ge 20 ]] || return 1
                        printf "%s\n" "${20}"
                    }

                    is_exact_gui_identity() {
                        local current_exe current_start_time

                        [[ -n "$gui_pid" && -n "$gui_start_time" ]] || return 1
                        current_exe="$(readlink -- "/proc/$gui_pid/exe" 2>/dev/null || true)"
                        [[ "$current_exe" == "$gui_apphost" ]] || return 1
                        current_start_time="$(get_start_time "$gui_pid" 2>/dev/null || true)"
                        [[ "$current_start_time" == "$gui_start_time" ]]
                    }

                    is_running_direct_launcher_job() {
                        local active_pid child_pid children="" children_path jobs_snapshot
                        local is_running_job=false

                        [[ -n "$launcher_pid" ]] || return 1
                        jobs_snapshot="$state_root/running-launcher-jobs"
                        jobs -pr > "$jobs_snapshot"
                        while IFS= read -r active_pid; do
                            if [[ "$active_pid" == "$launcher_pid" ]]; then
                                is_running_job=true
                                break
                            fi
                        done < "$jobs_snapshot"
                        [[ "$is_running_job" == true ]] || return 1
                        children_path="/proc/$BASHPID/task/$BASHPID/children"
                        [[ -r "$children_path" ]] || return 1
                        IFS= read -r children < "$children_path" 2>/dev/null || [[ -n "$children" ]] || return 1
                        for child_pid in $children; do
                            [[ "$child_pid" == "$launcher_pid" ]] && return 0
                        done
                        return 1
                    }

                    signal_exact_gui() {
                        local requested_signal="$1"

                        is_exact_gui_identity || return 1
                        kill -s "$requested_signal" -- "$gui_pid"
                    }

                    exact_gui_has_pending_term() {
                        local key value
                        local signal_pending=0 shared_pending=0

                        is_exact_gui_identity || return 1
                        while read -r key value _; do
                            case "$key" in
                                SigPnd:)
                                    signal_pending="$value"
                                    ;;
                                ShdPnd:)
                                    shared_pending="$value"
                                    ;;
                            esac
                        done < "/proc/$gui_pid/status"
                        [[ "$signal_pending" =~ ^[0-9a-fA-F]+$ && "$shared_pending" =~ ^[0-9a-fA-F]+$ ]] || return 1
                        (( (16#$signal_pending & 16384) != 0 || (16#$shared_pending & 16384) != 0 ))
                    }

                    cleanup_signal_case() {
                        set +e
                        if is_running_direct_launcher_job; then
                            kill -TERM "$launcher_pid" 2>/dev/null
                        fi
                        if is_exact_gui_identity; then
                            signal_exact_gui CONT 2>/dev/null || true
                        fi
                        if is_exact_gui_identity; then
                            signal_exact_gui TERM 2>/dev/null || true
                        fi
                        for _ in {1..200}; do
                            is_running_direct_launcher_job || break
                            sleep 0.01
                        done
                        if is_running_direct_launcher_job; then
                            kill -KILL "$launcher_pid" 2>/dev/null
                        fi
                        if [[ -n "$launcher_pid" ]]; then
                            wait "$launcher_pid" 2>/dev/null
                            launcher_pid=""
                        fi
                        if is_exact_gui_identity; then
                            signal_exact_gui KILL 2>/dev/null || true
                        fi
                    }
                    trap cleanup_signal_case EXIT

                    /usr/bin/env \
                        --default-signal=HUP \
                        --default-signal=INT \
                        --default-signal=TERM \
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
                        fail_case APPHOST_NOT_FOUND
                    fi
                    gui_start_time="$(get_start_time "$gui_pid" 2>/dev/null || true)"
                    if [[ -z "$gui_start_time" ]] || ! is_exact_gui_identity; then
                        fail_case APPHOST_IDENTITY_NOT_RETAINED
                    fi

                    # Require the launcher to be a currently running direct-child job immediately
                    # before signalling it, and retain the exact child identity separately. A
                    # same-UID check-to-signal race remains outside this shell test boundary.
                    for _ in {1..10}; do
                        is_running_direct_launcher_job || fail_case LAUNCHER_NOT_RUNNING_DIRECT_CHILD
                        is_exact_gui_identity || fail_case APPHOST_IDENTITY_CHANGED_BEFORE_SIGNAL
                        sleep 0.1
                    done
                    if [[ "$stop_child" == true ]]; then
                        signal_exact_gui STOP || fail_case APPHOST_STOP_REJECTED
                        for _ in {1..100}; do
                            state="$(sed -n "s/^State:[[:space:]]*\([^[:space:]]\).*/\1/p" "/proc/$gui_pid/status" 2>/dev/null || true)"
                            [[ "$state" == T || "$state" == t ]] && break
                            is_exact_gui_identity || fail_case APPHOST_IDENTITY_CHANGED_DURING_STOP
                            sleep 0.01
                        done
                        if [[ "$state" != T && "$state" != t ]]; then
                            fail_case APPHOST_DID_NOT_STOP
                        fi
                    fi
                    is_running_direct_launcher_job || fail_case LAUNCHER_AUTHORITY_LOST_BEFORE_SIGNAL
                    kill -s "$signal_name" -- "$launcher_pid" || fail_case LAUNCHER_SIGNAL_REJECTED
                    if [[ "$repeat_signals" == true ]]; then
                        pending_term=false
                        for _ in {1..300}; do
                            is_running_direct_launcher_job || fail_case LAUNCHER_SETTLED_BEFORE_PENDING_TERM
                            is_exact_gui_identity || fail_case APPHOST_IDENTITY_CHANGED_BEFORE_PENDING_TERM
                            if exact_gui_has_pending_term; then
                                pending_term=true
                                break
                            fi
                            sleep 0.01
                        done
                        [[ "$pending_term" == true ]] || fail_case APPHOST_PENDING_TERM_NOT_OBSERVED
                        is_running_direct_launcher_job || fail_case LAUNCHER_SETTLED_BEFORE_REPEATED_HUP
                        kill -HUP -- "$launcher_pid" || fail_case REPEATED_HUP_REJECTED
                        is_running_direct_launcher_job || fail_case LAUNCHER_SETTLED_BEFORE_REPEATED_INT
                        kill -INT -- "$launcher_pid" || fail_case REPEATED_INT_REJECTED
                    fi
                    set +e
                    wait "$launcher_pid"
                    launcher_status=$?
                    set -e
                    launcher_pid=""
                    if [[ "$launcher_status" -ne "$expected_status" ]]; then
                        if [[ "$launcher_status" =~ ^[0-9]+$ ]] && (( launcher_status >= 0 && launcher_status <= 255 )); then
                            fail_case "LAUNCHER_STATUS_$launcher_status"
                        fi
                        fail_case LAUNCHER_STATUS_INVALID
                    fi

                    # The launcher must settle its exact apphost, not merely remove the bundle path.
                    for _ in {1..500}; do
                        if ! is_exact_gui_identity; then
                            gui_pid=""
                            gui_start_time=""
                            break
                        fi
                        sleep 0.01
                    done
                    if [[ -n "$gui_pid" ]]; then
                        fail_case APPHOST_DID_NOT_SETTLE
                    fi
                    if find "$state_root/tmp" -mindepth 1 -maxdepth 1 -name "smapi-installer-gui.*" -print -quit | grep -q .; then
                        fail_case PRIVATE_BUNDLE_REMAINED
                    fi
                    trap - EXIT
                ' signal-supervisor "$launcher" "$gui_apphost" "$state_root" "$signal_name" "$expected_status" "$stop_child" "$repeat_signals"
    ) > "$output_path" 2>&1
    status=$?
    set -e
    if [[ "$status" -ne 0 ]]; then
        local failure_code="UNKNOWN"
        local output_line
        while IFS= read -r output_line; do
            if [[ "$output_line" =~ ^QUALIFIER_FAILURE=([A-Z0-9_-]+)$ ]]; then
                failure_code="${BASH_REMATCH[1]}"
            fi
        done < "$output_path"
        echo "The packaged graphical launcher did not settle its exact apphost and private bundle after $signal_name (stopped child: $stop_child; repeated signals: $repeat_signals; code: $failure_code); raw output is withheld from CI logs." >&2
        exit 1
    fi
    assert_no_runtime_leak "$state_root" "$output_path"
}

run_prelaunch_signal_smoke() {
    local first_signal="$1"
    local expected_status="$2"
    local second_signal="$3"
    local third_signal="$4"
    local case_name="prelaunch-${first_signal,,}-${second_signal,,}-${third_signal,,}"
    local state_root output_path instrumented_root instrumented_launcher child_marker status

    state_root="$(make_state_root "$case_name")"
    output_path="$test_root/$case_name.output"
    instrumented_root="$state_root/instrumented-package"
    instrumented_launcher="$instrumented_root/install on Linux (graphical).sh"
    child_marker="$state_root/prelaunch-child-started"
    mkdir -p "$instrumented_root/internal/linux"

    # Inject synchronized signals immediately after private bundle creation. Bash runs each trap
    # after its kill command, so this deterministically proves first-signal retention and the
    # normal-path pre-launch safe point without adding a production-only test hook.
    sed "/^chmod 700 -- \"\\\$bundle_root\"$/a\\
kill -s $first_signal -- \"\\\$BASHPID\"\\
kill -s $second_signal -- \"\\\$BASHPID\"\\
kill -s $third_signal -- \"\\\$BASHPID\"" \
        "$launcher" > "$instrumented_launcher"
    chmod 755 "$instrumented_launcher"
    printf '%s\n' \
        '#!/usr/bin/env bash' \
        'set -euo pipefail' \
        ': > "$SMAPI_GUI_PRELAUNCH_CHILD_MARKER"' \
        'exit 99' \
        > "$instrumented_root/internal/linux/SMAPI.Installer.Gui"
    chmod 755 "$instrumented_root/internal/linux/SMAPI.Installer.Gui"

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
            SMAPI_GUI_PRELAUNCH_CHILD_MARKER="$child_marker" \
            timeout --signal=TERM --kill-after=5s 10s "$instrumented_launcher" --demo
    ) > "$output_path" 2>&1
    status=$?
    set -e

    if [[ "$status" -ne "$expected_status" ]]; then
        echo "The graphical launcher did not retain the first pre-launch signal ($first_signal, exit $status instead of $expected_status); raw output is withheld from CI logs." >&2
        exit 1
    fi
    if [[ -e "$child_marker" ]]; then
        echo "The graphical launcher started its apphost after a pre-launch signal." >&2
        exit 1
    fi
    assert_no_runtime_leak "$state_root" "$output_path"
}

run_instrumented_signal_boundary_smoke() {
    local scenario="$1"
    local case_name="signal-boundary-$scenario"
    local state_root output_path instrumented_launcher status captured_status
    local signal_retained_marker job_query_marker wait_entry_marker wait_observed_marker
    local wait_returned_marker stale_job_query_marker captured_status_marker

    state_root="$(make_state_root "$case_name")"
    output_path="$test_root/$case_name.output"
    instrumented_launcher="$package_root/install on Linux (graphical) - $scenario test.sh"
    signal_retained_marker="$state_root/signal-retained"
    job_query_marker="$state_root/job-query-boundary"
    wait_entry_marker="$state_root/wait-entry"
    wait_observed_marker="$state_root/wait-observed"
    wait_returned_marker="$state_root/wait-returned"
    stale_job_query_marker="$state_root/stale-job-query"
    captured_status_marker="$state_root/captured-status"
    case "$scenario" in
        job-query)
            # Deliver TERM from the process substitution boundary immediately before the real
            # running/stopped/proc queries. The following normal-path settlement must observe it.
            sed '
                /^    requested_exit_status="\$2"$/a\
    : > "$SMAPI_GUI_SIGNAL_RETAINED_MARKER"
                0,/^    child_appears_live=false$/ {
                    /^    child_appears_live=false$/a\
    : > "$SMAPI_GUI_JOB_QUERY_BOUNDARY_MARKER"\
    while IFS= read -r _; do :; done < <(kill -TERM -- "$$")
                }
            ' \
                "$launcher" > "$instrumented_launcher"
            ;;
        wait)
            # Force the retained live child through the real wait statement, then deliver TERM
            # only after /proc proves Bash is blocked there. Post-wait settlement must reap it.
            sed '
                /^    requested_exit_status="\$2"$/a\
    : > "$SMAPI_GUI_SIGNAL_RETAINED_MARKER"
                /^status=0$/,/^done$/ {
                s/^    if is_running_child_job || is_stopped_child_job || is_retained_child_process_live; then$/    if false; then/
                s/^    if is_active_child_job; then$/    if false; then/
                /^    wait "\$child_pid" 2>\/dev\/null$/i\
    : > "$SMAPI_GUI_WAIT_ENTRY_MARKER"\
    (\
        for _ in {1..300}; do\
            wait_channel=""\
            IFS= read -r wait_channel < "/proc/$$/wchan" 2>/dev/null || true\
            if [[ "$wait_channel" == *wait* ]]; then\
                : > "$SMAPI_GUI_WAIT_OBSERVED_MARKER"\
                kill -TERM -- "$$"\
                exit 0\
            fi\
            sleep 0.01 || true\
        done\
        exit 98\
    ) &
                }
            ' "$launcher" > "$instrumented_launcher"
            ;;
        completed-status)
            # Terminate the retained child without signalling the launcher, force the real normal
            # wait path, and make any post-wait jobs-bookkeeping query observable and stale.
            sed '
                /^is_active_child_job() {$/a\
    if [[ -e "$SMAPI_GUI_WAIT_RETURNED_MARKER" ]]; then\
        : > "$SMAPI_GUI_STALE_JOB_QUERY_MARKER"\
        return 0\
    fi
                /^    requested_exit_status="\$2"$/a\
    : > "$SMAPI_GUI_SIGNAL_RETAINED_MARKER"
                /^status=0$/i\
kill -TERM -- "$child_pid"
                /^status=0$/,/^done$/ {
                    s/^    if is_active_child_job; then$/    if false; then/
                    /^    wait "\$child_pid" 2>\/dev\/null$/i\
    : > "$SMAPI_GUI_WAIT_ENTRY_MARKER"
                    /^    status=\$?$/a\
    : > "$SMAPI_GUI_WAIT_RETURNED_MARKER"\
    printf "%s\\n" "$status" > "$SMAPI_GUI_CAPTURED_STATUS_MARKER"
                }
            ' "$launcher" > "$instrumented_launcher"
            ;;
        *)
            echo "Unknown instrumented signal-boundary scenario: $scenario" >&2
            exit 2
            ;;
    esac
    chmod 755 "$instrumented_launcher"
    bash -n "$instrumented_launcher"
    case "$scenario" in
        job-query)
            [[ "$(grep -Fc 'SMAPI_GUI_JOB_QUERY_BOUNDARY_MARKER' "$instrumented_launcher")" -eq 1 ]] || {
                echo "The job-query boundary test did not instrument exactly one production site." >&2
                exit 1
            }
            ;;
        wait)
            [[ "$(grep -Fc 'SMAPI_GUI_WAIT_ENTRY_MARKER' "$instrumented_launcher")" -eq 1 ]] || {
                echo "The wait boundary test did not instrument exactly one production wait site." >&2
                exit 1
            }
            ;;
        completed-status)
            [[ "$(grep -Fc 'SMAPI_GUI_CAPTURED_STATUS_MARKER' "$instrumented_launcher")" -eq 1 ]] || {
                echo "The completed-status test did not instrument exactly one captured wait status." >&2
                exit 1
            }
            ;;
    esac

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
            SMAPI_GUI_SIGNAL_RETAINED_MARKER="$signal_retained_marker" \
            SMAPI_GUI_JOB_QUERY_BOUNDARY_MARKER="$job_query_marker" \
            SMAPI_GUI_WAIT_ENTRY_MARKER="$wait_entry_marker" \
            SMAPI_GUI_WAIT_OBSERVED_MARKER="$wait_observed_marker" \
            SMAPI_GUI_WAIT_RETURNED_MARKER="$wait_returned_marker" \
            SMAPI_GUI_STALE_JOB_QUERY_MARKER="$stale_job_query_marker" \
            SMAPI_GUI_CAPTURED_STATUS_MARKER="$captured_status_marker" \
            DOTNET_ROOT="$state_root/no-system-dotnet" \
            DOTNET_ROOT_X64="$state_root/no-system-dotnet" \
            DOTNET_MULTILEVEL_LOOKUP=0 \
            DOTNET_EnableDiagnostics=0 \
            DOTNET_CLI_TELEMETRY_OPTOUT=1 \
            DOTNET_NOLOGO=1 \
            XDG_SESSION_TYPE=x11 \
            timeout --signal=TERM --kill-after=5s 20s \
                xvfb-run -a /usr/bin/env \
                    --default-signal=HUP \
                    --default-signal=INT \
                    --default-signal=TERM \
                    "$instrumented_launcher" --demo
    ) > "$output_path" 2>&1
    status=$?
    set -e

    if [[ "$status" -ne 143 ]]; then
        echo "The graphical launcher did not settle TERM at its $scenario boundary (exit $status); raw output is withheld from CI logs." >&2
        exit 1
    fi
    case "$scenario" in
        job-query)
            if [[ ! -f "$job_query_marker" || ! -f "$signal_retained_marker" ]]; then
                echo "The job-query boundary test did not observe both its exact boundary and retained TERM." >&2
                exit 1
            fi
            ;;
        wait)
            if [[ ! -f "$wait_entry_marker" || ! -f "$wait_observed_marker" || ! -f "$signal_retained_marker" ]]; then
                echo "The wait boundary test did not prove that TERM interrupted the exact blocking wait." >&2
                exit 1
            fi
            ;;
        completed-status)
            captured_status=""
            if [[ -f "$captured_status_marker" ]]; then
                IFS= read -r captured_status < "$captured_status_marker" || [[ -n "$captured_status" ]]
            fi
            if [[ ! -f "$wait_entry_marker" || ! -f "$wait_returned_marker" || "$captured_status" != 143 ]]; then
                echo "The completed-status test did not preserve the exact status returned by wait." >&2
                exit 1
            fi
            if [[ -e "$signal_retained_marker" || -e "$stale_job_query_marker" ]]; then
                echo "The completed-status path either retained an unexpected launcher signal or consulted stale job bookkeeping." >&2
                exit 1
            fi
            ;;
    esac
    assert_no_runtime_leak "$state_root" "$output_path"
}

run_identity_failure_smoke() {
    local scenario="$1"
    local fail_after expected_status case_name launch_argument read_delay expect_failure_marker stop_before_capture
    local state_root output_path launcher_output expected_launcher_output status
    local identity_failure_marker identity_read_count

    case "$scenario" in
        initial)
            fail_after=0
            expected_status=1
            case_name="identity-capture-failure"
            launch_argument="--demo"
            read_delay=0
            expect_failure_marker=true
            stop_before_capture=false
            ;;
        initial-stopped)
            fail_after=0
            expected_status=1
            case_name="identity-capture-stopped-child"
            launch_argument="--demo"
            read_delay=2
            expect_failure_marker=true
            stop_before_capture=true
            ;;
        post-capture)
            fail_after=2
            expected_status=143
            case_name="identity-post-capture-failure"
            launch_argument="--demo"
            read_delay=0
            expect_failure_marker=true
            stop_before_capture=false
            ;;
        completed-job)
            fail_after=999
            expected_status=2
            case_name="identity-completed-job"
            launch_argument="--unexpected"
            read_delay=1
            expect_failure_marker=false
            stop_before_capture=false
            ;;
        *)
            echo "Unknown packaged launcher identity-failure scenario: $scenario" >&2
            exit 2
            ;;
    esac

    state_root="$(make_state_root "$case_name")"
    output_path="$test_root/$case_name.output"
    launcher_output="$state_root/launcher-output"
    expected_launcher_output="$state_root/expected-launcher-output"
    case "$scenario" in
        initial | initial-stopped)
            printf '%s\n' "The graphical installer couldn't verify its child process safely, so it was stopped." \
                > "$expected_launcher_output"
            ;;
        post-capture)
            : > "$expected_launcher_output"
            ;;
        completed-job)
            printf '%s\n' 'The graphical installer accepts either no arguments or exactly --demo.' \
                > "$expected_launcher_output"
            ;;
    esac
    identity_failure_marker="$test_root/$case_name.failure-marker"
    identity_read_count="$test_root/$case_name.read-count"
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
            timeout --signal=TERM --kill-after=5s 15s \
                xvfb-run -a bash -c '
                    set -euo pipefail
                    launcher="$1"
                    gui_apphost="$2"
                    state_root="$3"
                    identity_guarded_path="$4"
                    identity_failure_marker="$5"
                    identity_read_count="$6"
                    fail_after="$7"
                    scenario="$8"
                    expected_status="$9"
                    launcher_bash="${10}"
                    read_delay="${11}"
                    launch_argument="${12}"
                    expect_failure_marker="${13}"
                    launcher_output="${14}"
                    expected_launcher_output="${15}"
                    stop_before_capture="${16}"
                    launcher_pid=""
                    gui_pid=""
                    gui_start_time=""
                    state=""

                    fail_case() {
                        printf "QUALIFIER_FAILURE=%s\n" "$1" >&2
                        exit 1
                    }

                    get_start_time() {
                        local pid="$1"
                        local stat_line stat_fields

                        IFS= read -r stat_line < "/proc/$pid/stat" 2>/dev/null || return 1
                        stat_fields="${stat_line##*) }"
                        set -- $stat_fields
                        [[ $# -ge 20 ]] || return 1
                        printf "%s\n" "${20}"
                    }

                    is_exact_gui_identity() {
                        local current_exe current_start_time

                        [[ -n "$gui_pid" && -n "$gui_start_time" ]] || return 1
                        current_exe="$(/usr/bin/readlink -- "/proc/$gui_pid/exe" 2>/dev/null || true)"
                        [[ "$current_exe" == "$gui_apphost" ]] || return 1
                        current_start_time="$(get_start_time "$gui_pid" 2>/dev/null || true)"
                        [[ "$current_start_time" == "$gui_start_time" ]]
                    }

                    is_running_direct_launcher_job() {
                        local active_pid child_pid children="" children_path jobs_snapshot
                        local is_running_job=false

                        [[ -n "$launcher_pid" ]] || return 1
                        jobs_snapshot="$state_root/identity-failure-running-jobs"
                        jobs -pr > "$jobs_snapshot"
                        while IFS= read -r active_pid; do
                            if [[ "$active_pid" == "$launcher_pid" ]]; then
                                is_running_job=true
                                break
                            fi
                        done < "$jobs_snapshot"
                        [[ "$is_running_job" == true ]] || return 1
                        children_path="/proc/$BASHPID/task/$BASHPID/children"
                        [[ -r "$children_path" ]] || return 1
                        IFS= read -r children < "$children_path" 2>/dev/null || [[ -n "$children" ]] || return 1
                        for child_pid in $children; do
                            [[ "$child_pid" == "$launcher_pid" ]] && return 0
                        done
                        return 1
                    }

                    signal_exact_gui() {
                        local requested_signal="$1"

                        is_exact_gui_identity || return 1
                        kill -s "$requested_signal" -- "$gui_pid"
                    }

                    cleanup_identity_failure_case() {
                        set +e
                        if is_running_direct_launcher_job; then
                            kill -TERM "$launcher_pid" 2>/dev/null
                        fi
                        for _ in {1..500}; do
                            is_running_direct_launcher_job || break
                            sleep 0.01
                        done
                        if is_running_direct_launcher_job; then
                            kill -KILL "$launcher_pid" 2>/dev/null
                        fi
                        if [[ -n "$launcher_pid" ]]; then
                            wait "$launcher_pid" 2>/dev/null
                            launcher_pid=""
                        fi
                        if is_exact_gui_identity; then
                            signal_exact_gui KILL 2>/dev/null || true
                        fi
                    }
                    trap cleanup_identity_failure_case EXIT

                    /usr/bin/env \
                        --default-signal=HUP \
                        --default-signal=INT \
                        --default-signal=TERM \
                        PATH="$identity_guarded_path" \
                        SMAPI_GUI_IDENTITY_FAILURE_MARKER="$identity_failure_marker" \
                        SMAPI_GUI_IDENTITY_READ_COUNT="$identity_read_count" \
                        SMAPI_GUI_IDENTITY_FAIL_AFTER="$fail_after" \
                        SMAPI_GUI_IDENTITY_READ_DELAY="$read_delay" \
                        SMAPI_GUI_EXPECTED_APPHOST="$gui_apphost" \
                        SMAPI_GUI_EXPECTED_LAUNCHER="$launcher" \
                        SMAPI_GUI_EXPECTED_LAUNCHER_BASH="$launcher_bash" \
                        bash -c "export SMAPI_GUI_EXPECTED_LAUNCHER_PID=\"\$BASHPID\"; exec \"\$1\" \"\$2\"" \
                            identity-launcher-trampoline "$launcher" "$launch_argument" \
                            > "$launcher_output" 2>&1 &
                    launcher_pid=$!
                    for _ in {1..1500}; do
                        if [[ -r "/proc/$launcher_pid/task/$launcher_pid/children" ]]; then
                            IFS= read -r children < "/proc/$launcher_pid/task/$launcher_pid/children" || true
                            for child in $children; do
                                child_exe="$(/usr/bin/readlink -- "/proc/$child/exe" 2>/dev/null || true)"
                                if [[ "$child_exe" == "$gui_apphost" ]]; then
                                    gui_pid="$child"
                                    break 2
                                fi
                            done
                        fi
                        is_running_direct_launcher_job || break
                        sleep 0.01
                    done
                    [[ -n "$gui_pid" ]] || fail_case APPHOST_NOT_OBSERVED
                    gui_start_time="$(get_start_time "$gui_pid" 2>/dev/null || true)"
                    if [[ "$scenario" != completed-job ]] \
                        && { [[ -z "$gui_start_time" ]] || ! is_exact_gui_identity; }; then
                        fail_case APPHOST_IDENTITY_NOT_RETAINED
                    fi

                    if [[ "$stop_before_capture" == true ]]; then
                        read_count=0
                        for _ in {1..300}; do
                            if [[ -f "$identity_read_count" ]]; then
                                IFS= read -r read_count < "$identity_read_count" || [[ -n "$read_count" ]]
                            fi
                            [[ "$read_count" =~ ^[0-9]+$ ]] || fail_case INVALID_IDENTITY_READ_COUNT
                            (( read_count >= 1 )) && break
                            is_running_direct_launcher_job || fail_case LAUNCHER_EXITED_BEFORE_CAPTURE_READ
                            is_exact_gui_identity || fail_case APPHOST_IDENTITY_CHANGED_BEFORE_CAPTURE_STOP
                            sleep 0.01
                        done
                        (( read_count >= 1 )) || fail_case CAPTURE_READ_NOT_OBSERVED
                        signal_exact_gui STOP || fail_case PRE_CAPTURE_APPHOST_STOP_REJECTED
                        for _ in {1..100}; do
                            state="$(sed -n "s/^State:[[:space:]]*\([^[:space:]]\).*/\1/p" "/proc/$gui_pid/status" 2>/dev/null || true)"
                            [[ "$state" == T || "$state" == t ]] && break
                            is_exact_gui_identity || fail_case APPHOST_IDENTITY_CHANGED_DURING_CAPTURE_STOP
                            sleep 0.01
                        done
                        if [[ "$state" != T && "$state" != t ]]; then
                            fail_case PRE_CAPTURE_APPHOST_DID_NOT_STOP
                        fi
                        is_running_direct_launcher_job || fail_case LAUNCHER_EXITED_BEFORE_CAPTURE_FAILURE
                    fi

                    if [[ "$scenario" == post-capture ]]; then
                        for _ in {1..300}; do
                            read_count=0
                            if [[ -f "$identity_read_count" ]]; then
                                IFS= read -r read_count < "$identity_read_count" || [[ -n "$read_count" ]]
                            fi
                            [[ "$read_count" =~ ^[0-9]+$ ]] || fail_case INVALID_IDENTITY_READ_COUNT
                            (( read_count >= 1 )) && break
                            is_running_direct_launcher_job || fail_case LAUNCHER_EXITED_BEFORE_CAPTURE
                            sleep 0.01
                        done
                        (( read_count >= 1 )) || fail_case CAPTURE_READ_NOT_OBSERVED
                        for _ in {1..10}; do
                            is_running_direct_launcher_job || fail_case LAUNCHER_EXITED_AFTER_CAPTURE
                            is_exact_gui_identity || fail_case APPHOST_IDENTITY_CHANGED_AFTER_CAPTURE
                            sleep 0.01
                        done
                        signal_exact_gui STOP || fail_case APPHOST_STOP_REJECTED
                        for _ in {1..100}; do
                            state="$(sed -n "s/^State:[[:space:]]*\([^[:space:]]\).*/\1/p" "/proc/$gui_pid/status" 2>/dev/null || true)"
                            [[ "$state" == T || "$state" == t ]] && break
                            is_exact_gui_identity || fail_case APPHOST_IDENTITY_CHANGED_DURING_STOP
                            sleep 0.01
                        done
                        if [[ "$state" != T && "$state" != t ]]; then
                            fail_case APPHOST_DID_NOT_STOP
                        fi
                        is_running_direct_launcher_job || fail_case LAUNCHER_AUTHORITY_LOST_BEFORE_SIGNAL
                        kill -TERM "$launcher_pid" || fail_case LAUNCHER_SIGNAL_REJECTED
                    fi

                    for _ in {1..700}; do
                        is_running_direct_launcher_job || break
                        sleep 0.01
                    done
                    if is_running_direct_launcher_job; then
                        fail_case LAUNCHER_DID_NOT_SETTLE
                    fi
                    set +e
                    wait "$launcher_pid"
                    launcher_status=$?
                    set -e
                    launcher_pid=""
                    [[ "$launcher_status" -eq "$expected_status" ]] || fail_case LAUNCHER_STATUS_MISMATCH
                    cmp -s -- "$expected_launcher_output" "$launcher_output" || fail_case LAUNCHER_OUTPUT_MISMATCH
                    if [[ "$expect_failure_marker" == true ]]; then
                        [[ -f "$identity_failure_marker" ]] || fail_case FAILURE_INJECTION_NOT_OBSERVED
                    else
                        [[ ! -e "$identity_failure_marker" ]] || fail_case UNEXPECTED_FAILURE_INJECTION
                    fi
                    if [[ "$scenario" == post-capture ]]; then
                        IFS= read -r read_count < "$identity_read_count" || [[ -n "$read_count" ]]
                        [[ "$read_count" =~ ^[0-9]+$ ]] || fail_case INVALID_FINAL_IDENTITY_READ_COUNT
                        (( read_count >= 3 )) || fail_case POST_FORWARD_REVALIDATION_NOT_OBSERVED
                    elif [[ "$scenario" == completed-job ]]; then
                        IFS= read -r read_count < "$identity_read_count" || [[ -n "$read_count" ]]
                        [[ "$read_count" =~ ^[0-9]+$ ]] || fail_case INVALID_COMPLETED_IDENTITY_READ_COUNT
                        (( read_count >= 1 )) || fail_case COMPLETED_JOB_READ_NOT_OBSERVED
                    fi

                    for _ in {1..500}; do
                        if ! is_exact_gui_identity; then
                            gui_pid=""
                            gui_start_time=""
                            break
                        fi
                        sleep 0.01
                    done
                    [[ -z "$gui_pid" ]] || fail_case APPHOST_DID_NOT_SETTLE
                    if find "$state_root/tmp" -mindepth 1 -maxdepth 1 -name "smapi-installer-gui.*" -print -quit | grep -q .; then
                        fail_case PRIVATE_BUNDLE_REMAINED
                    fi
                    trap - EXIT
                ' identity-failure-supervisor "$launcher" "$gui_apphost" "$state_root" "$identity_guarded_path" "$identity_failure_marker" "$identity_read_count" "$fail_after" "$scenario" "$expected_status" "$launcher_bash" "$read_delay" "$launch_argument" "$expect_failure_marker" "$launcher_output" "$expected_launcher_output" "$stop_before_capture"
    ) > "$output_path" 2>&1
    status=$?
    set -e
    if [[ "$status" -ne 0 ]]; then
        local failure_code="UNKNOWN"
        local output_line
        while IFS= read -r output_line; do
            if [[ "$output_line" =~ ^QUALIFIER_FAILURE=([A-Z0-9_-]+)$ ]]; then
                failure_code="${BASH_REMATCH[1]}"
            fi
        done < "$output_path"
        echo "The packaged graphical launcher did not fail closed in the $scenario child-identity scenario (code: $failure_code); raw output is withheld from CI logs." >&2
        exit 1
    fi
    if [[ -s "$output_path" ]]; then
        echo "The packaged graphical launcher identity-failure supervisor emitted unexpected output in the $scenario scenario; raw output is withheld from CI logs." >&2
        exit 1
    fi
    assert_no_runtime_leak "$state_root" "$output_path"
}

# The sealed demo proves that the packaged single-file apphost starts without system dotnet. The
# production initial window is exercised with remote traffic denied; catalog failure may render, but
# package download, sibling-backend launch, discovery, logging, and game mutation require user action.
run_window_smoke demo false --demo
run_window_smoke production true
signal_stress_repetitions="${SMAPI_GUI_SIGNAL_STRESS_REPETITIONS:-1}"
if [[ ! "$signal_stress_repetitions" =~ ^[1-9][0-9]*$ ]] || (( signal_stress_repetitions > 20 )); then
    echo "SMAPI_GUI_SIGNAL_STRESS_REPETITIONS must be an integer from 1 through 20." >&2
    exit 1
fi
for ((signal_stress_iteration = 1; signal_stress_iteration <= signal_stress_repetitions; signal_stress_iteration++)); do
    run_launcher_signal_smoke HUP 129 false
    run_launcher_signal_smoke INT 130 false
    run_launcher_signal_smoke TERM 143 false
    run_launcher_signal_smoke TERM 143 true
    run_launcher_signal_smoke TERM 143 true true
done
run_prelaunch_signal_smoke HUP 129 TERM INT
run_prelaunch_signal_smoke HUP 129 INT TERM
run_prelaunch_signal_smoke INT 130 HUP TERM
run_prelaunch_signal_smoke INT 130 TERM HUP
run_prelaunch_signal_smoke TERM 143 HUP INT
run_prelaunch_signal_smoke TERM 143 INT HUP
run_instrumented_signal_boundary_smoke job-query
run_instrumented_signal_boundary_smoke wait
run_instrumented_signal_boundary_smoke completed-status
run_identity_failure_smoke initial
run_identity_failure_smoke initial-stopped
run_identity_failure_smoke post-capture
run_identity_failure_smoke completed-job

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
