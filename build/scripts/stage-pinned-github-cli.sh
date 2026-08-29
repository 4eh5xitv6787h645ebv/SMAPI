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
output_name="$(basename -- "$output_directory")"
output_parent_input="$(dirname -- "$output_directory")"
if [[ "$output_name" == "." || "$output_name" == ".." || ! -d "$output_parent_input" || -L "$output_parent_input" ]]; then
    echo "The pinned GitHub CLI output must name a new child of an existing ordinary directory." >&2
    exit 1
fi
output_parent="$(realpath -- "$output_parent_input")"
output_directory="$output_parent/$output_name"
if [[ -e "$output_directory" || -L "$output_directory" ]]; then
    echo "The pinned GitHub CLI output path must not already exist: $output_directory" >&2
    exit 1
fi
mv_version_line="$(mv --version | sed -n '1p')"
if [[ "$mv_version_line" != *"GNU coreutils"* \
    || ! "$mv_version_line" =~ \ ([0-9]+)\.([0-9]+)(\.|[[:space:]]|$) ]]; then
    echo "Pinned-verifier staging requires GNU coreutils mv 8.30 or later and GNU stat on Linux." >&2
    exit 1
fi
mv_version_major="${BASH_REMATCH[1]}"
mv_version_minor="${BASH_REMATCH[2]}"
if (( mv_version_major < 8 || (mv_version_major == 8 && mv_version_minor < 30) )) \
    || [[ "$(stat --version | sed -n '1p')" != *"GNU coreutils"* ]]; then
    echo "Pinned-verifier staging requires GNU coreutils mv 8.30 or later and GNU stat on Linux." >&2
    exit 1
fi
if ! command -v python3 >/dev/null; then
    echo "Pinned-verifier staging requires Python 3 for the atomic no-replace capability probe." >&2
    exit 1
fi

archive_path="$(realpath -- "$archive_input")"
if [[ "$(stat -c %s -- "$archive_path")" != "$expected_archive_size" \
    || "$(sha256sum -- "$archive_path" | cut -d ' ' -f 1)" != "$expected_archive_sha256" ]]; then
    echo "The GitHub CLI archive doesn't match the reviewed official 2.92.0 linux-amd64 archive." >&2
    exit 1
fi

# Build in a private directory on the destination filesystem, then place the
# complete directory with GNU rename-no-replace semantics. This ensures a
# caller-path symlink/directory substituted during validation receives no
# writes. The release workflow trusts its same-UID process environment; a
# hostile same-UID process could still mutate final files after this returns,
# so package construction independently revalidates their exact bytes.
temp_root="$(mktemp -d --tmpdir="$output_parent" .smapi-pinned-gh.XXXXXXXX)"
temp_identity="$(stat -c '%d:%i' -- "$temp_root")"
cleanup() {
    if [[ -d "$temp_root" && ! -L "$temp_root" && "$(stat -c '%d:%i' -- "$temp_root")" == "$temp_identity" ]]; then
        rm -rf --one-file-system -- "$temp_root"
    fi
}
trap cleanup EXIT

# Coreutils 8.30 fixed mv -n's lookup/rename race when the platform supplies an
# atomic no-replace rename. Probe that exact syscall and destination filesystem;
# EEXIST with both directory identities unchanged proves no replacement occurred.
python3 - "$temp_root" <<'PY'
import ctypes
import errno
import os
import sys

root = sys.argv[1]
source = os.path.join(root, "rename-noreplace-source")
destination = os.path.join(root, "rename-noreplace-destination")
os.mkdir(source, 0o700)
os.mkdir(destination, 0o700)
source_identity = (os.stat(source).st_dev, os.stat(source).st_ino)
destination_identity = (os.stat(destination).st_dev, os.stat(destination).st_ino)

libc = ctypes.CDLL(None, use_errno=True)
try:
    renameat2 = libc.renameat2
except AttributeError:
    raise SystemExit("The C library does not expose renameat2 for atomic verifier staging.")
renameat2.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
renameat2.restype = ctypes.c_int
at_fdcwd = -100
rename_noreplace = 1
result = renameat2(at_fdcwd, os.fsencode(source), at_fdcwd, os.fsencode(destination), rename_noreplace)
error = ctypes.get_errno()
if result != -1 or error != errno.EEXIST:
    raise SystemExit(f"The destination filesystem rejected atomic no-replace rename (errno {error}).")
if (
    (os.stat(source).st_dev, os.stat(source).st_ino) != source_identity
    or (os.stat(destination).st_dev, os.stat(destination).st_ino) != destination_identity
):
    raise SystemExit("The atomic no-replace capability probe changed a directory identity.")
os.rmdir(source)
os.rmdir(destination)
PY

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

staged_directory="$temp_root/publish"
install -d -m 0700 -- "$staged_directory"
install -m 0555 -- "$binary_source" "$staged_directory/gh"
install -m 0444 -- "$license_source" "$staged_directory/gh-LICENSE.txt"

if [[ "$(find "$staged_directory" -mindepth 1 -maxdepth 1 -printf . | wc -c)" != 2 \
    || ! -f "$staged_directory/gh" || -L "$staged_directory/gh" || "$(stat -c %h -- "$staged_directory/gh")" != 1 \
    || ! -f "$staged_directory/gh-LICENSE.txt" || -L "$staged_directory/gh-LICENSE.txt" || "$(stat -c %h -- "$staged_directory/gh-LICENSE.txt")" != 1 \
    || "$(stat -c %a -- "$staged_directory/gh")" != 555 \
    || "$(stat -c %a -- "$staged_directory/gh-LICENSE.txt")" != 444 \
    || "$(stat -c %s -- "$staged_directory/gh")" != "$expected_binary_size" \
    || "$(sha256sum -- "$staged_directory/gh" | cut -d ' ' -f 1)" != "$expected_binary_sha256" \
    || "$(stat -c %s -- "$staged_directory/gh-LICENSE.txt")" != "$expected_license_size" \
    || "$(sha256sum -- "$staged_directory/gh-LICENSE.txt" | cut -d ' ' -f 1)" != "$expected_license_sha256" ]]; then
    echo "The staged pinned GitHub CLI directory failed its final validation." >&2
    exit 1
fi

staged_identity="$(stat -c '%d:%i' -- "$staged_directory")"
if [[ -n "${SMAPI_TEST_PINNED_GH_BEFORE_PUBLISH_HOOK:-}" ]]; then
    "$SMAPI_TEST_PINNED_GH_BEFORE_PUBLISH_HOOK" "$output_directory"
fi
mv --no-clobber --no-target-directory -- "$staged_directory" "$output_directory"
if [[ -e "$staged_directory" || -L "$staged_directory" \
    || ! -d "$output_directory" || -L "$output_directory" \
    || "$(stat -c '%d:%i' -- "$output_directory")" != "$staged_identity" ]]; then
    echo "The pinned GitHub CLI output path was substituted before atomic placement." >&2
    exit 1
fi

echo "Staged the reviewed GitHub CLI 2.92.0 binary and license in $output_directory."
