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
requested_signal_name=""
requested_exit_status=""
cleanup() {
    if is_active_child_job; then
        kill -s KILL -- "$child_pid" 2>/dev/null || true
        wait "$child_pid" 2>/dev/null || true
        child_pid=""
    fi
    if [[ -n "$bundle_root" && -d "$bundle_root" ]]; then
        rm -rf -- "$bundle_root"
    fi
}

is_active_child_job() {
    local active_pid=""

    [[ -n "$child_pid" ]] || return 1
    while IFS= read -r active_pid; do
        if [[ "$active_pid" == "$child_pid" ]]; then
            return 0
        fi
    done < <(jobs -p)
    return 1
}

forward_signal() {
    local signal_name="$1"
    local exit_status="$2"

    if [[ -z "$requested_exit_status" ]]; then
        requested_signal_name="$signal_name"
        requested_exit_status="$exit_status"
    fi
    if is_active_child_job; then
        kill -s "$signal_name" -- "$child_pid" 2>/dev/null || true
    fi
}

trap cleanup EXIT
trap 'forward_signal HUP 129' HUP
trap 'forward_signal INT 130' INT
trap 'forward_signal TERM 143' TERM

bundle_root="$(mktemp -d "${TMPDIR:-/tmp}/smapi-installer-gui.XXXXXXXX")"
chmod 700 -- "$bundle_root"

env \
    --default-signal=HUP \
    --default-signal=INT \
    --default-signal=TERM \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR="$bundle_root" \
    "$gui_path" "$@" &
child_pid=$!
if [[ -n "$requested_signal_name" ]] && is_active_child_job; then
    kill -s "$requested_signal_name" -- "$child_pid" 2>/dev/null || true
fi

status=0
while true; do
    set +e
    wait "$child_pid"
    status=$?
    set -e

    if ! is_active_child_job; then
        break
    fi
done
child_pid=""

if [[ -n "$requested_exit_status" ]]; then
    exit "$requested_exit_status"
fi
exit "$status"
