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

old_coreutils="$temp_root/old-coreutils"
mkdir "$old_coreutils"
printf '%s\n' \
    '#!/usr/bin/env bash' \
    'if [[ "${1:-}" == "--version" ]]; then' \
    '    echo "mv (GNU coreutils) 8.29"' \
    '    exit 0' \
    'fi' \
    'exec /usr/bin/mv "$@"' \
    > "$old_coreutils/mv"
chmod 0700 "$old_coreutils/mv"
expect_failure old-coreutils \
    env PATH="$old_coreutils:$PATH" "$stager" "$archive_path" "$temp_root/old-coreutils-output"
test ! -e "$temp_root/old-coreutils-output"

failed_capability_probe="$temp_root/failed-capability-probe"
mkdir "$failed_capability_probe"
printf '%s\n' '#!/usr/bin/env bash' 'exit 72' > "$failed_capability_probe/python3"
chmod 0700 "$failed_capability_probe/python3"
expect_failure failed-capability-probe \
    env PATH="$failed_capability_probe:$PATH" "$stager" "$archive_path" "$temp_root/failed-capability-output"
test ! -e "$temp_root/failed-capability-output"
if find "$temp_root" -mindepth 1 -maxdepth 1 -type d -name '.smapi-pinned-gh.*' -print -quit | grep -q .; then
    echo "The staging helper left a private partial directory after a failed capability probe." >&2
    exit 1
fi

substitution_hook="$temp_root/substitute-output.sh"
printf '%s\n' \
    '#!/usr/bin/env bash' \
    'set -euo pipefail' \
    'case "$SMAPI_TEST_SUBSTITUTION_KIND" in' \
    '    symlink) ln -s -- "$SMAPI_TEST_SUBSTITUTION_TARGET" "$1" ;;' \
    '    directory) mkdir -- "$1"; printf "attacker marker\\n" > "$1/marker" ;;' \
    '    *) exit 2 ;;' \
    'esac' \
    > "$substitution_hook"
chmod 0700 "$substitution_hook"

substitution_target="$temp_root/substitution-target"
mkdir "$substitution_target"
printf 'unrelated marker\n' > "$substitution_target/marker"
symlink_output="$temp_root/symlink-race-output"
expect_failure symlink-substitution \
    env \
        SMAPI_TEST_PINNED_GH_BEFORE_PUBLISH_HOOK="$substitution_hook" \
        SMAPI_TEST_SUBSTITUTION_KIND=symlink \
        SMAPI_TEST_SUBSTITUTION_TARGET="$substitution_target" \
        "$stager" "$archive_path" "$symlink_output"
test -L "$symlink_output"
test "$(cat "$substitution_target/marker")" = "unrelated marker"
test "$(find "$substitution_target" -mindepth 1 -maxdepth 1 -printf . | wc -c)" = 1
test ! -e "$substitution_target/gh"
test ! -e "$substitution_target/gh-LICENSE.txt"
unlink "$symlink_output"

directory_output="$temp_root/directory-race-output"
expect_failure directory-substitution \
    env \
        SMAPI_TEST_PINNED_GH_BEFORE_PUBLISH_HOOK="$substitution_hook" \
        SMAPI_TEST_SUBSTITUTION_KIND=directory \
        SMAPI_TEST_SUBSTITUTION_TARGET="$substitution_target" \
        "$stager" "$archive_path" "$directory_output"
test -d "$directory_output"
test "$(cat "$directory_output/marker")" = "attacker marker"
test "$(find "$directory_output" -mindepth 1 -maxdepth 1 -printf . | wc -c)" = 1
test ! -e "$directory_output/gh"
test ! -e "$directory_output/gh-LICENSE.txt"

if find "$temp_root" -mindepth 1 -maxdepth 1 -type d -name '.smapi-pinned-gh.*' -print -quit | grep -q .; then
    echo "The staging helper left a private partial directory after output substitution." >&2
    exit 1
fi

echo "Pinned GitHub CLI staging tests passed."
