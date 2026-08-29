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
protocol_pid=""
cleanup() {
    set +e
    if [[ -n "$protocol_pid" ]]; then
        kill -KILL "$protocol_pid" 2>/dev/null
        wait "$protocol_pid" 2>/dev/null
    fi
    exec 9>&- 2>/dev/null
    rm -rf -- "$temp_root"
}
trap cleanup EXIT

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
grep -F 'must not be run as root or with sudo' "$package_root/install on Linux.sh" >/dev/null
test -f "$package_root/README.txt"
test -f "$package_root/internal/linux/SMAPI.Installer"
test -f "$package_root/internal/linux/SMAPI.Installer.Core.dll"
test -f "$package_root/internal/linux/install.dat"
test -f "$package_root/internal/linux/gh"
test ! -L "$package_root/internal/linux/gh"
test "$(stat -c %h -- "$package_root/internal/linux/gh")" = 1
test -x "$package_root/internal/linux/gh"
test "$(stat -c %a -- "$package_root/internal/linux/gh")" = 555
test "$(stat -c %s -- "$package_root/internal/linux/gh")" = 39805090
test "$(sha256sum -- "$package_root/internal/linux/gh" | cut -d ' ' -f 1)" = b58e487e37c00c114aa07f14987ce12f5e5abf12b9da8a38937b65ef218f6772
test -f "$package_root/internal/linux/gh-LICENSE.txt"
test ! -L "$package_root/internal/linux/gh-LICENSE.txt"
test "$(stat -c %h -- "$package_root/internal/linux/gh-LICENSE.txt")" = 1
test "$(stat -c %a -- "$package_root/internal/linux/gh-LICENSE.txt")" = 444
test "$(stat -c %s -- "$package_root/internal/linux/gh-LICENSE.txt")" = 1068
test "$(sha256sum -- "$package_root/internal/linux/gh-LICENSE.txt" | cut -d ' ' -f 1)" = 6da4adc42392c8485e40b4251c7e332fc3352df1947c9ffade71dd60b14a7a4f
test ! -e "$package_root/internal/macOS"
test ! -e "$package_root/internal/windows"

# The JSONL backend must run directly from the trimmed published installer without inspecting or
# extracting the legacy install.dat payload. Exercise both a missing and poisoned ambient bundle.
protocol_root="$temp_root/protocol-host"
cp -a "$package_root/internal/linux" "$protocol_root"
rm "$protocol_root/install.dat"
protocol_request='{"protocolVersion":1,"messageType":"handshake.request","payload":{"commandId":"11111111111111111111111111111111","clientName":"package-test","clientVersion":"1"}}'
for ambient_bundle in missing poisoned; do
    if [[ "$ambient_bundle" == poisoned ]]; then
        printf 'not an installer archive\n' > "$protocol_root/install.dat"
    fi
    printf '%s\n' "$protocol_request" \
        | "$protocol_root/SMAPI.Installer" --linux-protocol-v1-jsonl \
            > "$temp_root/protocol-$ambient_bundle.stdout" \
            2> "$temp_root/protocol-$ambient_bundle.stderr"
    test ! -s "$temp_root/protocol-$ambient_bundle.stderr"
    python3 - "$temp_root/protocol-$ambient_bundle.stdout" <<'PY'
import json
import pathlib
import sys

lines = pathlib.Path(sys.argv[1]).read_bytes().splitlines()
assert len(lines) == 1
message = json.loads(lines[0].decode("utf-8", errors="strict"))
assert set(message) == {"protocolVersion", "messageType", "payload"}
assert message["protocolVersion"] == 1
assert message["messageType"] == "handshake.event"
assert message["payload"]["commandId"] == "11111111111111111111111111111111"
assert message["payload"]["serverVersion"]
assert "verified-local-package" in message["payload"]["capabilities"]
PY
done

set +e
"$protocol_root/SMAPI.Installer" --linux-protocol-v1-jsonl unexpected \
    > "$temp_root/protocol-mixed.stdout" 2> "$temp_root/protocol-mixed.stderr"
mixed_exit=$?
set -e
test "$mixed_exit" = 2
test ! -s "$temp_root/protocol-mixed.stdout"
grep -Fx 'The Linux protocol host requires exactly --linux-protocol-v1-jsonl on Linux.' "$temp_root/protocol-mixed.stderr" >/dev/null

# A packaged host blocked on controller input must handle SIGTERM through its graceful cancellation
# path, without extracting install.dat, polluting stdout, or leaving a child process behind.
protocol_fifo="$temp_root/protocol-input.fifo"
protocol_tmp="$temp_root/protocol-tmp"
mkdir "$protocol_tmp"
mkfifo "$protocol_fifo"
exec 9<> "$protocol_fifo"
TMPDIR="$protocol_tmp" "$protocol_root/SMAPI.Installer" --linux-protocol-v1-jsonl \
    < "$protocol_fifo" > "$temp_root/protocol-sigterm.stdout" 2> "$temp_root/protocol-sigterm.stderr" &
protocol_pid=$!
for _ in {1..100}; do
    [[ -e "/proc/$protocol_pid/status" ]] && break
    sleep 0.01
done
test -e "/proc/$protocol_pid/status"
printf '%s\n' "$protocol_request" >&9
for _ in {1..500}; do
    [[ -s "$temp_root/protocol-sigterm.stdout" ]] && break
    kill -0 "$protocol_pid" 2>/dev/null || break
    sleep 0.01
done
test -s "$temp_root/protocol-sigterm.stdout"
mapfile -t protocol_children < <(pgrep -P "$protocol_pid" || true)
kill -TERM "$protocol_pid"
set +e
wait "$protocol_pid"
sigterm_exit=$?
set -e
protocol_pid=""
exec 9>&-
test "$sigterm_exit" = 130
grep -Fx 'Protocol host was cancelled.' "$temp_root/protocol-sigterm.stderr" >/dev/null
python3 - "$temp_root/protocol-sigterm.stdout" <<'PY'
import json
import pathlib
import sys

lines = pathlib.Path(sys.argv[1]).read_bytes().splitlines()
assert len(lines) == 1
message = json.loads(lines[0].decode("utf-8", errors="strict"))
assert message["protocolVersion"] == 1
assert message["messageType"] == "handshake.event"
assert message["payload"]["commandId"] == "11111111111111111111111111111111"
PY
test -z "$(find "$protocol_tmp" -mindepth 1 -print -quit)"
for child in "${protocol_children[@]}"; do
    if kill -0 "$child" 2>/dev/null; then
        echo "Protocol host left child process $child after SIGTERM." >&2
        exit 1
    fi
done

mkdir "$temp_root/bundle"
unzip -q "$package_root/internal/linux/install.dat" -d "$temp_root/bundle"
test ! -e "$temp_root/bundle/gh"
test ! -e "$temp_root/bundle/gh-LICENSE.txt"
for required_path in \
    StardewModdingAPI \
    StardewModdingAPI-net6 \
    StardewModdingAPI-net10 \
    StardewModdingAPI-net6.dll \
    StardewModdingAPI-net10.dll \
    StardewModdingAPI-net6.runtimeconfig.json \
    StardewModdingAPI-net10.runtimeconfig.json; do
    if [[ ! -e "$temp_root/bundle/$required_path" ]]; then
        echo "Linux install payload is missing '$required_path'." >&2
        exit 1
    fi
done

test -x "$temp_root/bundle/StardewModdingAPI"
test -x "$temp_root/bundle/StardewModdingAPI-net6"
test -x "$temp_root/bundle/StardewModdingAPI-net10"
private_runtime="$temp_root/bundle/smapi-internal/dotnet"
mapfile -t private_hostfxr < <(find "$private_runtime/host/fxr" -mindepth 2 -maxdepth 2 -type f -name libhostfxr.so -print 2>/dev/null)
if [[ ${#private_hostfxr[@]} -ne 1 ]]; then
    echo "Packaged private runtime must contain exactly one host/fxr/<version>/libhostfxr.so." >&2
    exit 1
fi
private_runtime_version="$(basename "$(dirname "${private_hostfxr[0]}")")"
test -f "$private_runtime/shared/Microsoft.NETCore.App/$private_runtime_version/Microsoft.NETCore.App.runtimeconfig.json"
test -f "$private_runtime/shared/Microsoft.NETCore.App/$private_runtime_version/libhostpolicy.so"
test -f "$private_runtime/shared/Microsoft.NETCore.App/$private_runtime_version/libcoreclr.so"
if ! find "$private_runtime" -type f -name createdump -executable -print -quit | grep -q .; then
    echo "Packaged private runtime has no executable createdump." >&2
    exit 1
fi

echo "Linux release package checks passed for $archive_path."
