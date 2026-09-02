#!/usr/bin/env bash
set -euo pipefail

if [[ "$EUID" -eq 0 ]]; then
    printf '%s\n' "The SMAPI graphical installer must not be run as root or with sudo. Run it as your normal desktop user instead." >&2
    exit 2
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
gui_path="$script_dir/internal/linux/SMAPI.Installer.Gui"
if [[ ! -f "$gui_path" || -L "$gui_path" || ! -x "$gui_path" ]]; then
    printf '%s\n' "The packaged SMAPI graphical installer is missing or unsafe. Extract a fresh verified package and try again." >&2
    exit 1
fi

bundle_root=""
child_pid=""
child_start_time=""
child_proc_inode=""
child_identity_ready=false
requested_signal_name=""
requested_exit_status=""
signal_forwarded=false
signal_deadline=0
bundle_cleanup_allowed=true
settlement_failed=false
settlement_status=0
cleanup() {
    if [[ -n "$child_pid" && "$settlement_failed" == false ]]; then
        force_kill_and_settle_child || true
    fi
    if [[ "$bundle_cleanup_allowed" == true && -n "$bundle_root" && -d "$bundle_root" ]]; then
        rm -rf -- "$bundle_root"
    fi
}

is_running_child_job() {
    local active_pid=""

    [[ -n "$child_pid" ]] || return 1
    while IFS= read -r active_pid; do
        if [[ "$active_pid" == "$child_pid" ]]; then
            return 0
        fi
    done < <(jobs -pr 2>/dev/null)
    return 1
} 2>/dev/null

is_active_child_job() {
    local active_pid=""

    [[ -n "$child_pid" ]] || return 1
    while IFS= read -r active_pid; do
        if [[ "$active_pid" == "$child_pid" ]]; then
            return 0
        fi
    done < <(jobs -p 2>/dev/null)
    return 1
} 2>/dev/null

is_stopped_child_job() {
    local stopped_pid=""

    [[ -n "$child_pid" ]] || return 1
    while IFS= read -r stopped_pid; do
        if [[ "$stopped_pid" == "$child_pid" ]]; then
            return 0
        fi
    done < <(jobs -ps 2>/dev/null)
    return 1
} 2>/dev/null

kill_live_direct_job_fallback() {
    # Fail-closed settlement only: `jobs -pr`/`jobs -ps` prove the direct job is still live. A
    # completed `jobs -p` entry is bookkeeping-only because its kernel PID may already be reusable.
    # Normal signal forwarding always requires the stronger Linux /proc identity.
    if ! is_running_child_job && ! is_stopped_child_job; then
        return 1
    fi
    kill -s KILL -- "$child_pid" 2>/dev/null
}

force_kill_and_settle_child() {
    local settlement_deadline=$((SECONDS + 3))

    [[ -n "$child_pid" ]] || return 0
    # Settlement is terminal. The first requested signal/status was already retained, so later
    # handled signals must not interrupt wait or reenter forwarding while KILL/reap is in progress.
    trap '' HUP INT TERM
    if is_active_child_job; then
        if ! send_exact_signal KILL; then
            kill_live_direct_job_fallback || true
        fi
    fi

    while is_active_child_job; do
        if is_running_child_job || is_stopped_child_job; then
            if ! send_exact_signal KILL; then
                kill_live_direct_job_fallback || true
            fi
        fi
        if ! is_running_child_job && ! is_stopped_child_job; then
            set +e
            wait "$child_pid" 2>/dev/null
            settlement_status=$?
            set -e
        fi

        if ! is_active_child_job; then
            child_pid=""
            return 0
        fi
        if (( SECONDS >= settlement_deadline )); then
            settlement_failed=true
            bundle_cleanup_allowed=false
            printf '%s\n' "The graphical installer couldn't safely settle its child process; temporary runtime files were retained." >&2
            return 1
        fi
        sleep 0.05 || true
    done

    child_pid=""
    return 0
}

read_process_state_and_start_time() {
    local process_id="$1"
    local stat_line=""
    local -a stat_fields=()

    [[ -r "/proc/$process_id/stat" ]] || return 1
    IFS= read -r stat_line 2>/dev/null < "/proc/$process_id/stat" || return 1
    stat_line="${stat_line##*) }"
    read -r -a stat_fields <<< "$stat_line"
    [[ "${#stat_fields[@]}" -ge 20 ]] || return 1
    printf '%s %s\n' "${stat_fields[0]}" "${stat_fields[19]}"
}

capture_exact_child_identity() {
    local process_state=""
    local start_time=""
    local proc_inode=""
    local executable=""

    is_running_child_job || return 1
    read -r process_state start_time < <(read_process_state_and_start_time "$child_pid") || return 1
    [[ "$process_state" != "Z" && "$process_state" != "X" ]] || return 1
    proc_inode="$(stat -Lc '%i' -- "/proc/$child_pid")" || return 1
    executable="$(readlink -e -- "/proc/$child_pid/exe")" || return 1
    [[ "$executable" == "$gui_path" ]] || return 1

    child_start_time="$start_time"
    child_proc_inode="$proc_inode"
    child_identity_ready=true
}

is_exact_child_running() {
    local process_state=""
    local current_start_time=""
    local current_proc_inode=""
    local current_executable=""

    [[ "$child_identity_ready" == true ]] || return 1
    is_running_child_job || return 1
    read -r process_state current_start_time < <(read_process_state_and_start_time "$child_pid") || return 1
    [[ "$process_state" != "Z" && "$process_state" != "X" ]] || return 1
    current_proc_inode="$(stat -Lc '%i' -- "/proc/$child_pid")" || return 1
    current_executable="$(readlink -e -- "/proc/$child_pid/exe")" || return 1
    [[
        "$current_start_time" == "$child_start_time"
        && "$current_proc_inode" == "$child_proc_inode"
        && "$current_executable" == "$gui_path"
    ]]
}

send_exact_signal() {
    local signal_name="$1"

    # This closes PID reuse and stale-job windows. A same-UID process can still race Linux /proc
    # inspection and signaling; normal desktop-user isolation is the launcher security boundary.
    is_exact_child_running || return 1
    kill -s "$signal_name" -- "$child_pid" 2>/dev/null
}

forward_pending_signal() {
    [[ -n "$requested_signal_name" && "$signal_forwarded" == false ]] || return 0
    if send_exact_signal "$requested_signal_name"; then
        signal_forwarded=true
        signal_deadline=$((SECONDS + 3))
    fi
}

record_signal() {
    local signal_name="$1"
    local exit_status="$2"

    if [[ -z "$requested_exit_status" ]]; then
        requested_signal_name="$signal_name"
        requested_exit_status="$exit_status"
    fi
    forward_pending_signal
}

trap cleanup EXIT
trap 'record_signal HUP 129' HUP
trap 'record_signal INT 130' INT
trap 'record_signal TERM 143' TERM

bundle_root="$(mktemp -d "${TMPDIR:-/tmp}/smapi-installer-gui.XXXXXXXX")"
chmod 700 -- "$bundle_root"

/usr/bin/env \
    --default-signal=HUP \
    --default-signal=INT \
    --default-signal=TERM \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR="$bundle_root" \
    "$gui_path" "$@" &
child_pid=$!

identity_deadline=$((SECONDS + 3))
while is_running_child_job; do
    if capture_exact_child_identity; then
        break
    fi
    if (( SECONDS >= identity_deadline )); then
        break
    fi
    sleep 0.01 || true
done
forward_pending_signal
if [[ "$child_identity_ready" == false ]] && is_running_child_job; then
    force_kill_and_settle_child || true
    printf '%s\n' "The graphical installer couldn't verify its child process safely, so it was stopped." >&2
    if [[ -n "$requested_exit_status" ]]; then
        exit "$requested_exit_status"
    fi
    exit 1
fi

status=0
kill_sent=false
while true; do
    if [[ "$signal_forwarded" == true ]]; then
        if ! is_exact_child_running; then
            force_kill_and_settle_child || true
            status=$settlement_status
            break
        fi
        if [[ "$kill_sent" == false ]] && (( SECONDS >= signal_deadline )); then
            force_kill_and_settle_child || true
            status=$settlement_status
            kill_sent=true
            break
        fi
        sleep 0.05 || true
        continue
    fi

    set +e
    wait "$child_pid" 2>/dev/null
    status=$?
    set -e
    if ! is_active_child_job; then
        break
    fi
    forward_pending_signal
    if [[ -n "$requested_signal_name" && "$signal_forwarded" == false ]] && is_running_child_job; then
        force_kill_and_settle_child || true
        status=$settlement_status
        break
    fi
    if [[ -z "$requested_signal_name" ]] && is_stopped_child_job; then
        sleep 0.05 || true
    fi
done
if [[ "$settlement_failed" == false ]]; then
    child_pid=""
fi

if [[ -n "$requested_exit_status" ]]; then
    exit "$requested_exit_status"
fi
exit "$status"
