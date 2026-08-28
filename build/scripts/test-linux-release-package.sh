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

temp_root="$(mktemp -d)"
trap 'rm -rf -- "$temp_root"' EXIT

entries_path="$temp_root/entries.txt"
zipinfo -1 "$archive_path" > "$entries_path"
if [[ ! -s "$entries_path" ]]; then
    echo "Installer archive is empty." >&2
    exit 1
fi
if grep -Eq '(^/|(^|/)\.\.(/|$)|\\)' "$entries_path"; then
    echo "Installer archive contains an unsafe path." >&2
    exit 1
fi
if grep -Evq "^${expected_root//./\\.}(/|$)" "$entries_path"; then
    echo "Installer archive contains an entry outside '$expected_root'." >&2
    exit 1
fi
if grep -Eq "/internal/(macOS|windows)(/|$)|install on (macOS|Windows)" "$entries_path"; then
    echo "Linux-only archive contains another platform's payload." >&2
    exit 1
fi

unzip -q "$archive_path" -d "$temp_root/extracted"
package_root="$temp_root/extracted/$expected_root"
test -x "$package_root/install on Linux.sh"
test -f "$package_root/README.txt"
test -f "$package_root/internal/linux/SMAPI.Installer"
test -f "$package_root/internal/linux/install.dat"
test ! -e "$package_root/internal/macOS"
test ! -e "$package_root/internal/windows"

mkdir "$temp_root/bundle"
unzip -q "$package_root/internal/linux/install.dat" -d "$temp_root/bundle"
for required_path in \
    StardewModdingAPI \
    StardewModdingAPI-net6 \
    StardewModdingAPI-net10 \
    StardewModdingAPI-net6.dll \
    StardewModdingAPI-net10.dll \
    StardewModdingAPI-net6.runtimeconfig.json \
    StardewModdingAPI-net10.runtimeconfig.json \
    smapi-internal/dotnet/dotnet; do
    if [[ ! -e "$temp_root/bundle/$required_path" ]]; then
        echo "Linux install payload is missing '$required_path'." >&2
        exit 1
    fi
done

test -x "$temp_root/bundle/StardewModdingAPI"
test -x "$temp_root/bundle/StardewModdingAPI-net6"
test -x "$temp_root/bundle/StardewModdingAPI-net10"
test -x "$temp_root/bundle/smapi-internal/dotnet/dotnet"
if ! find "$temp_root/bundle/smapi-internal/dotnet" -type f -name createdump -executable -print -quit | grep -q .; then
    echo "Packaged private runtime has no executable createdump." >&2
    exit 1
fi

echo "Linux release package checks passed for $archive_path."
