#!/usr/bin/env bash
set -euo pipefail

script_path="$(cd "$(dirname "$0")" && pwd)/finalize-install-package.sh"
test_root="$(mktemp -d)"
trap 'rmdir "$test_root"' EXIT
missing_source="$test_root/intentionally-missing"

run_failure_case() {
    local name="$1"
    local stdin_text="$2"
    shift 2

    local output status
    set +e
    output="$(printf '%b' "$stdin_text" | timeout 5 bash "$script_path" "$@" 2>&1)"
    status=$?
    set -e

    if [[ $status -eq 0 || $status -eq 124 ]]; then
        echo "$name: expected a prompt-complete copy failure, got exit $status." >&2
        exit 1
    fi
    if [[ "$output" == *"unbound variable"* ]]; then
        echo "$name: strict mode expanded a missing positional argument before prompting." >&2
        exit 1
    fi
    grep -F "copying '" <<< "$output" >/dev/null
}

zero_args_output="$(printf '9.9.9-test\n%s\n' "$missing_source" | timeout 5 bash "$script_path" 2>&1 || true)"
grep -F "SMAPI release version" <<< "$zero_args_output" >/dev/null
grep -F "Windows compiled bin path" <<< "$zero_args_output" >/dev/null
run_failure_case "zero arguments" "9.9.9-test\n$missing_source\n"

one_arg_output="$(printf '%s\n' "$missing_source" | timeout 5 bash "$script_path" 9.9.9-test 2>&1 || true)"
if [[ "$one_arg_output" == *"SMAPI release version"* ]]; then
    echo "one argument: unexpectedly prompted for the supplied version." >&2
    exit 1
fi
grep -F "Windows compiled bin path" <<< "$one_arg_output" >/dev/null
run_failure_case "one argument" "$missing_source\n" 9.9.9-test

two_args_output="$(timeout 5 bash "$script_path" 9.9.9-test "$missing_source" 2>&1 || true)"
if [[ "$two_args_output" == *"SMAPI release version"* || "$two_args_output" == *"Windows compiled bin path"* ]]; then
    echo "two arguments: unexpectedly prompted for supplied values." >&2
    exit 1
fi
run_failure_case "two arguments" "" 9.9.9-test "$missing_source"

echo "Finalize-install-package strict-mode input checks passed."
