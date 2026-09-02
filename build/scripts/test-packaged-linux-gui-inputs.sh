#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
qualifier="$script_dir/test-packaged-linux-gui.sh"
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
