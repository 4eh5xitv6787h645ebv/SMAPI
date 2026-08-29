#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "Usage: $0 <official-gh-2.92.0-linux-amd64-archive>" >&2
    exit 2
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
stager="$script_dir/stage-pinned-github-cli.sh"
archive_path="$(realpath -- "$1")"
temp_root="$(mktemp -d)"
trap 'rm -rf -- "$temp_root"' EXIT

expect_failure() {
    local label="$1"
    shift
    if "$@" >"$temp_root/$label.stdout" 2>"$temp_root/$label.stderr"; then
        echo "The staging helper accepted the invalid '$label' case." >&2
        exit 1
    fi
}

staged="$temp_root/staged"
"$stager" "$archive_path" "$staged" >/dev/null
test -x "$staged/gh"
test "$(stat -c %a -- "$staged/gh")" = 555
test "$(stat -c %s -- "$staged/gh")" = 39805090
test "$(sha256sum -- "$staged/gh" | cut -d ' ' -f 1)" = b58e487e37c00c114aa07f14987ce12f5e5abf12b9da8a38937b65ef218f6772
test "$(stat -c %a -- "$staged/gh-LICENSE.txt")" = 444
test "$(stat -c %s -- "$staged/gh-LICENSE.txt")" = 1068
test "$(sha256sum -- "$staged/gh-LICENSE.txt" | cut -d ' ' -f 1)" = 6da4adc42392c8485e40b4251c7e332fc3352df1947c9ffade71dd60b14a7a4f
test "$(find "$staged" -mindepth 1 -maxdepth 1 -printf . | wc -c)" = 2

expect_failure missing-archive "$stager" "$temp_root/missing.tar.gz" "$temp_root/missing-output"
test ! -e "$temp_root/missing-output"

corrupt_archive="$temp_root/corrupt.tar.gz"
cp -- "$archive_path" "$corrupt_archive"
printf '\0' >> "$corrupt_archive"
expect_failure corrupt-archive "$stager" "$corrupt_archive" "$temp_root/corrupt-output"
test ! -e "$temp_root/corrupt-output"

archive_symlink="$temp_root/archive-symlink.tar.gz"
ln -s -- "$archive_path" "$archive_symlink"
expect_failure archive-symlink "$stager" "$archive_symlink" "$temp_root/symlink-output"
test ! -e "$temp_root/symlink-output"

archive_hardlink="$temp_root/archive-hardlink.tar.gz"
ln -- "$archive_path" "$archive_hardlink"
expect_failure archive-hardlink "$stager" "$archive_hardlink" "$temp_root/hardlink-output"
rm -- "$archive_hardlink"
test ! -e "$temp_root/hardlink-output"

mkdir "$temp_root/existing-output"
expect_failure existing-output "$stager" "$archive_path" "$temp_root/existing-output"
test "$(find "$temp_root/existing-output" -mindepth 1 -printf . | wc -c)" = 0

echo "Pinned GitHub CLI staging tests passed."
