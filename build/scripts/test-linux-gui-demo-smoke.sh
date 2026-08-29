#!/usr/bin/env bash
set -euo pipefail

gui_dll="${1:-src/SMAPI.Installer.Gui/bin/Release/net10.0/SMAPI.Installer.Gui.dll}"
if [[ ! -f "$gui_dll" ]]; then
    echo "The built Linux GUI shell was not found: $gui_dll" >&2
    exit 1
fi
for command_name in dotnet timeout xvfb-run; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Required smoke-test command is unavailable: $command_name" >&2
        exit 1
    fi
done

smoke_root="$(mktemp -d)"
cleanup() {
    if [[ -n "$smoke_root" && -d "$smoke_root" ]]; then
        rm -rf -- "$smoke_root"
    fi
}
trap cleanup EXIT

mkdir -p "$smoke_root/home" "$smoke_root/cache" "$smoke_root/config" "$smoke_root/data" "$smoke_root/runtime" "$smoke_root/work"
chmod 700 "$smoke_root/home" "$smoke_root/cache" "$smoke_root/config" "$smoke_root/data" "$smoke_root/runtime" "$smoke_root/work"
gui_dll="$(realpath "$gui_dll")"

set +e
(
    cd "$smoke_root/work"
    env -i \
        PATH="$PATH" \
        HOME="$smoke_root/home" \
        XDG_CACHE_HOME="$smoke_root/cache" \
        XDG_CONFIG_HOME="$smoke_root/config" \
        XDG_DATA_HOME="$smoke_root/data" \
        XDG_RUNTIME_DIR="$smoke_root/runtime" \
        XDG_SESSION_TYPE=x11 \
        DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        DOTNET_NOLOGO=1 \
        timeout --signal=TERM --kill-after=2s 5s \
            xvfb-run -a dotnet "$gui_dll" --demo
) >"$smoke_root/output.log" 2>&1
status=$?
set -e

if [[ "$status" -ne 124 ]]; then
    echo "The GUI shell did not remain healthy for the five-second isolated demo smoke (exit $status)." >&2
    sed -n '1,120p' "$smoke_root/output.log" >&2
    exit 1
fi
if grep -Eiq '(^|[^[:alpha:]])(fatal|unhandled exception)([^[:alpha:]]|$)' "$smoke_root/output.log"; then
    echo "The GUI shell emitted a fatal error during the isolated demo smoke." >&2
    sed -n '1,120p' "$smoke_root/output.log" >&2
    exit 1
fi
if find "$smoke_root" -mindepth 1 -iname '*Stardew*' -o -iname 'Mods' | grep -q .; then
    echo "The GUI shell unexpectedly created a game-shaped path during the isolated demo smoke." >&2
    exit 1
fi

echo "PASS: the safe-demo shell stayed healthy for five seconds with disposable HOME/XDG state under Xvfb."
