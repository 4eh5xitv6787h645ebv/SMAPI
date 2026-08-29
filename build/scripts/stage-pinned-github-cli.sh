#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
    echo "Usage: $0 <gh-2.92.0-linux-amd64-archive> <new-output-directory>" >&2
    exit 2
fi

archive_input="$1"
output_directory="$2"
expected_archive_size=14296784
expected_archive_sha256=b57848131bdf0c229cd35e1f2a51aa718199858b2e728410b37e89a428943ec4
expected_binary_size=39805090
expected_binary_sha256=b58e487e37c00c114aa07f14987ce12f5e5abf12b9da8a38937b65ef218f6772
expected_license_size=1068
expected_license_sha256=6da4adc42392c8485e40b4251c7e332fc3352df1947c9ffade71dd60b14a7a4f
archive_root=gh_2.92.0_linux_amd64

if [[ ! -f "$archive_input" || -L "$archive_input" || "$(stat -c %h -- "$archive_input")" != 1 ]]; then
    echo "The pinned GitHub CLI archive must be one single-link ordinary file." >&2
    exit 1
fi
if [[ -e "$output_directory" || -L "$output_directory" ]]; then
    echo "The pinned GitHub CLI output path must not already exist: $output_directory" >&2
    exit 1
fi

archive_path="$(realpath -- "$archive_input")"
if [[ "$(stat -c %s -- "$archive_path")" != "$expected_archive_size" \
    || "$(sha256sum -- "$archive_path" | cut -d ' ' -f 1)" != "$expected_archive_sha256" ]]; then
    echo "The GitHub CLI archive doesn't match the reviewed official 2.92.0 linux-amd64 archive." >&2
    exit 1
fi

temp_root="$(mktemp -d)"
trap 'rm -rf -- "$temp_root"' EXIT
member_list="$temp_root/archive-members.txt"
tar -tzf "$archive_path" > "$member_list"
for required_member in "$archive_root/bin/gh" "$archive_root/LICENSE"; do
    if [[ "$(grep -Fxc -- "$required_member" "$member_list")" != 1 ]]; then
        echo "The reviewed GitHub CLI archive doesn't contain exactly one '$required_member'." >&2
        exit 1
    fi
done

extract_root="$temp_root/extracted"
mkdir -m 0700 -- "$extract_root"
tar -xzf "$archive_path" \
    --directory "$extract_root" \
    --no-same-owner \
    --no-same-permissions \
    -- "$archive_root/bin/gh" "$archive_root/LICENSE"

binary_source="$extract_root/$archive_root/bin/gh"
license_source="$extract_root/$archive_root/LICENSE"
if [[ ! -f "$binary_source" || -L "$binary_source" || "$(stat -c %h -- "$binary_source")" != 1 \
    || "$(stat -c %s -- "$binary_source")" != "$expected_binary_size" \
    || "$(sha256sum -- "$binary_source" | cut -d ' ' -f 1)" != "$expected_binary_sha256" ]]; then
    echo "The extracted GitHub CLI doesn't match the reviewed 2.92.0 binary." >&2
    exit 1
fi
if [[ ! -f "$license_source" || -L "$license_source" || "$(stat -c %h -- "$license_source")" != 1 \
    || "$(stat -c %s -- "$license_source")" != "$expected_license_size" \
    || "$(sha256sum -- "$license_source" | cut -d ' ' -f 1)" != "$expected_license_sha256" ]]; then
    echo "The extracted GitHub CLI license doesn't match the reviewed 2.92.0 license." >&2
    exit 1
fi

install -d -m 0700 -- "$output_directory"
install -m 0555 -- "$binary_source" "$output_directory/gh"
install -m 0444 -- "$license_source" "$output_directory/gh-LICENSE.txt"

if [[ "$(find "$output_directory" -mindepth 1 -maxdepth 1 -printf . | wc -c)" != 2 \
    || ! -f "$output_directory/gh" || -L "$output_directory/gh" || "$(stat -c %h -- "$output_directory/gh")" != 1 \
    || ! -f "$output_directory/gh-LICENSE.txt" || -L "$output_directory/gh-LICENSE.txt" || "$(stat -c %h -- "$output_directory/gh-LICENSE.txt")" != 1 \
    || "$(stat -c %s -- "$output_directory/gh")" != "$expected_binary_size" \
    || "$(sha256sum -- "$output_directory/gh" | cut -d ' ' -f 1)" != "$expected_binary_sha256" \
    || "$(stat -c %s -- "$output_directory/gh-LICENSE.txt")" != "$expected_license_size" \
    || "$(sha256sum -- "$output_directory/gh-LICENSE.txt" | cut -d ' ' -f 1)" != "$expected_license_sha256" ]]; then
    echo "The staged pinned GitHub CLI directory failed its final validation." >&2
    exit 1
fi

echo "Staged the reviewed GitHub CLI 2.92.0 binary and license in $output_directory."
