#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
qualifier="$script_dir/test-packaged-linux-gui.sh"
launcher_source="$script_dir/../../src/SMAPI.Installer/assets/install on Linux (graphical).sh"
for command_name in grep mktemp timeout zip; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Required packaged-GUI input-test command is unavailable: $command_name" >&2
        exit 1
    fi
done
test_root="$(mktemp -d)"
cleanup() {
    if [[ -n "$test_root" && -d "$test_root" ]]; then
        rm -rf -- "$test_root"
    fi
}
trap cleanup EXIT

launcher_source_text="$(<"$launcher_source")"
settled_diagnostic_guard=$'if force_kill_and_settle_child; then\n        printf \'%s\\n\' "The graphical installer couldn\'t verify its child process safely, so it was stopped." >&2'
if [[ "$launcher_source_text" != *"$settled_diagnostic_guard"* ]] \
    || [[ "$(grep -Fc "so it was stopped." "$launcher_source")" -ne 1 ]]; then
    echo "The launcher must only claim its unverified child stopped after successful bounded settlement." >&2
    exit 1
fi

if [[ "$launcher_source_text" != *$'begin_signal_settlement() {\n    [[ -n "$requested_signal_name" && "$signal_forwarded" == false ]] || return 0\n    # Enter settlement on the normal execution path. Mask all later handled signals before\n    # checking or forwarding the exact child identity so traps can\'t interrupt this sequence.\n    trap \'\' HUP INT TERM'* ]] \
    || [[ "$launcher_source_text" != *$'record_signal() {\n    if (( handled_signal_count > 0 )); then\n        return 0\n    fi\n    handled_signal_count=1\n    requested_signal_name="$1"\n    requested_exit_status="$2"'* ]]; then
    echo "The launcher traps must retain only the first signal and enter masked settlement on the normal execution path." >&2
    exit 1
fi

if [[ "$launcher_source_text" != *$'abort_before_launch_if_requested() {\n    [[ -n "$requested_exit_status" ]] || return 0\n    # A signal can arrive after traps are installed but before the apphost is a retainable child.\n    # Settle that request on the normal path without creating or launching any further state.\n    trap \'\' HUP INT TERM\n    exit "$requested_exit_status"'* ]] \
    || [[ "$launcher_source_text" != *$'trap \'record_signal TERM 143\' TERM\n\nabort_before_launch_if_requested\nbundle_root='* ]] \
    || [[ "$launcher_source_text" != *$'chmod 700 -- "$bundle_root"\nabort_before_launch_if_requested\n\n/usr/bin/env'* ]]; then
    echo "The launcher must settle a retained signal before creating state and again before launching its child." >&2
    exit 1
fi

run_expect_status() {
    local expected_status="$1"
    local expected_text="$2"
    shift 2
    local status

    set +e
    timeout 5 "$qualifier" "$@" > "$test_root/case.stdout" 2> "$test_root/case.stderr"
    status=$?
    set -e
    if [[ "$status" -ne "$expected_status" ]]; then
        echo "Packaged-GUI input case returned $status instead of $expected_status." >&2
        sed -n '1,80p' "$test_root/case.stderr" >&2
        exit 1
    fi
    if ! grep -F "$expected_text" "$test_root/case.stderr" >/dev/null; then
        echo "Packaged-GUI input case omitted its expected diagnostic: $expected_text" >&2
        sed -n '1,80p' "$test_root/case.stderr" >&2
        exit 1
    fi
}

run_expect_status 2 'Usage:'
run_expect_status 2 'Usage:' "$test_root/missing.zip"
run_expect_status 2 'Usage:' "$test_root/missing.zip" 1.2.3 unexpected
run_expect_status 1 'Installer archive not found:' "$test_root/missing.zip" 1.2.3

mkdir "$test_root/foreign"
printf '%s\n' 'not a Linux installer' > "$test_root/foreign/README.txt"
(
    cd "$test_root"
    zip -q foreign.zip foreign/README.txt
)
run_expect_status 1 'Installer archive has an empty, unsafe, or foreign layout.' "$test_root/foreign.zip" 1.2.3

echo "Packaged Linux GUI input checks passed."
