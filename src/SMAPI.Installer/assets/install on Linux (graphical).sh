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
cleanup() {
    if [[ -n "$bundle_root" && -d "$bundle_root" ]]; then
        rm -rf -- "$bundle_root"
    fi
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

bundle_root="$(mktemp -d "${TMPDIR:-/tmp}/smapi-installer-gui.XXXXXXXX")"
chmod 700 -- "$bundle_root"

set +e
DOTNET_BUNDLE_EXTRACT_BASE_DIR="$bundle_root" "$gui_path" "$@"
status=$?
set -e
exit "$status"
