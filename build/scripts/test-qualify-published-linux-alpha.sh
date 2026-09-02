#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
qualifier="$script_directory/qualify-published-linux-alpha.sh"
# shellcheck source=qualify-published-linux-alpha.sh
source "$qualifier"

test_root="$(mktemp -d)"
chmod 0700 -- "$test_root"
cleanup() {
    chmod -R u+rwX -- "$test_root" 2>/dev/null || true
    rm -rf -- "$test_root"
}
trap cleanup EXIT

release_tag="fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2"
release_version="4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2"
release_commit="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
source_tree="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
repository="4eh5xitv6787h645ebv/SMAPI"
repository_url="https://github.com/$repository"
workflow="$repository/.github/workflows/linux-alpha-release.yml@refs/tags/$release_tag"
package_name="SMAPI-$release_version-linux-x64-installer.zip"
manifest_name="SMAPI-$release_version-linux-x64-install-manifest.json"
bundle_name="SMAPI-$release_version-linux-x64-attestation-bundle.jsonl"

fixture="$test_root/fixture"
mkdir -m 0700 -- "$fixture"
printf 'synthetic public package bytes\n' > "$fixture/$package_name"
package_sha256="$(sha256sum -- "$fixture/$package_name" | cut -d ' ' -f 1)"
package_size="$(stat -c %s -- "$fixture/$package_name")"

jq -cn \
    --arg version "$release_version" \
    --arg tag "$release_tag" \
    --arg repository "$repository_url" \
    --arg repository_name "$repository" \
    --arg commit "$release_commit" \
    --arg tree "$source_tree" \
    --arg workflow "$workflow" \
    --arg package_name "$package_name" \
    --arg package_sha256 "$package_sha256" \
    --argjson package_size "$package_size" \
    --arg manifest_name "$manifest_name" '
        {
            schema_version: 4,
            release: {
                repository: $repository,
                tag: $tag,
                embedded_version: $version,
                package_asset_name: $package_name,
                source_commit: $commit,
                source_tree: $tree,
                package_sha256: $package_sha256,
                package_size_bytes: $package_size,
                build_workflow: $workflow,
                build_configuration: "Release",
                runtime_identifier: "linux-x64"
            },
            entries: [{path: "StardewValley", sha256: $package_sha256, size_bytes: 1, unix_mode: 493, kind: "launcher"}],
            generated_files: [],
            release_authority_policy: {
                kind: "github_artifact_attestation_v1",
                repository: $repository_name,
                source_reference: ("refs/tags/" + $tag),
                source_commit: $commit,
                build_workflow: $workflow,
                runner_environment: "github-hosted",
                trigger: "push",
                repository_identifier: "1336010508",
                repository_owner_identifier: "45441845",
                package_subject_name: $package_name,
                manifest_subject_name: $manifest_name
            }
        }
    ' > "$fixture/$manifest_name"
manifest_sha256="$(sha256sum -- "$fixture/$manifest_name" | cut -d ' ' -f 1)"
manifest_size="$(stat -c %s -- "$fixture/$manifest_name")"
printf '%s  %s\n%s  %s\n' \
    "$manifest_sha256" "$manifest_name" \
    "$package_sha256" "$package_name" \
    > "$fixture/SHA256SUMS"

jq -cn \
    --arg version "$release_version" \
    --arg tag "$release_tag" \
    --arg repository "$repository_url" \
    --arg commit "$release_commit" \
    --arg tree "$source_tree" \
    --arg workflow "$workflow" \
    --arg manifest_name "$manifest_name" \
    --arg manifest_sha256 "$manifest_sha256" \
    --argjson manifest_size "$manifest_size" \
    --arg package_name "$package_name" \
    --arg package_sha256 "$package_sha256" \
    --argjson package_size "$package_size" '
        {
            schema_version: 1,
            release: {version: $version, tag: $tag},
            source: {repository: $repository, commit: $commit, tree: $tree},
            build: {
                workflow: $workflow,
                run: ($repository + "/actions/runs/123/attempts/1"),
                runner_image: "ubuntu-24.04",
                runner_arch: "X64",
                reference_assemblies_commit: "cccccccccccccccccccccccccccccccccccccccc",
                configuration: "Release",
                runtime_identifier: "linux-x64",
                timestamp_utc: "2026-09-03T00:00:00Z",
                dotnet_info: ".NET synthetic test fixture"
            },
            artifacts: [
                {name: $manifest_name, size_bytes: $manifest_size, sha256: $manifest_sha256},
                {name: $package_name, size_bytes: $package_size, sha256: $package_sha256}
            ],
            reproducibility: "Inputs and provenance are recorded; byte-for-byte reproducibility is not claimed."
        }
    ' > "$fixture/build-metadata.json"

printf '%s\n' '{"mediaType":"application/vnd.dev.sigstore.bundle.v0.3+json","verificationMaterial":{},"dsseEnvelope":{}}' \
    > "$fixture/$bundle_name"
bundle_sha256="$(sha256sum -- "$fixture/$bundle_name" | cut -d ' ' -f 1)"
printf '%s  %s\n' "$bundle_sha256" "$bundle_name" > "$fixture/$bundle_name.sha256"

fake_bin="$test_root/fake-bin"
mkdir -m 0700 -- "$fake_bin"
printf '%s\n' \
    '#!/bin/bash' \
    'set -euo pipefail' \
    '[[ -z "${GH_TOKEN+x}" ]]' \
    'printf "%s\n" "$*" >> "$SMAPI_TEST_CURL_ARGUMENT_LOG"' \
    'output=""' \
    'url=""' \
    'while [[ $# -gt 0 ]]; do' \
    '    case "$1" in' \
    '        --output) output="$2"; shift 2 ;;' \
    '        --*) shift ;;' \
    '        *) url="$1"; shift ;;' \
    '    esac' \
    'done' \
    '[[ -n "$output" && -n "$url" ]]' \
    'name="${url##*/}"' \
    'printf "%s\n" "$url" >> "$SMAPI_TEST_CURL_LOG"' \
    'if [[ -f "$SMAPI_TEST_CURL_FAIL_NAME" && "$(<"$SMAPI_TEST_CURL_FAIL_NAME")" == "$name" ]]; then exit 22; fi' \
    'cp -- "$SMAPI_TEST_RELEASE_FIXTURE/$name" "$output"' \
    'if [[ -f "$SMAPI_TEST_CURL_EXTRA_NAME" && "$(<"$SMAPI_TEST_CURL_EXTRA_NAME")" == "$name" ]]; then printf "unexpected\n" > "$(dirname -- "$output")/unexpected"; fi' \
    > "$fake_bin/curl"
chmod 0700 -- "$fake_bin/curl"

printf '%s\n' \
    '#!/bin/bash' \
    'set -euo pipefail' \
    '[[ "$*" != *synthetic-public-read-token* ]]' \
    '[[ -z "${SMAPI_TEST_AMBIENT_SECRET+x}" ]]' \
    'printf "%s\n" "$*" >> "$0.log"' \
    'if [[ -f "$0.fail" ]]; then exit 124; fi' \
    'while [[ "$1" == --* ]]; do shift; done' \
    'shift' \
    'exec "$@"' \
    > "$fake_bin/timeout"
chmod 0700 -- "$fake_bin/timeout"

printf '%s\n' \
    '#!/bin/bash' \
    'set -euo pipefail' \
    'for argument in "$@"; do' \
    '    [[ "$argument" != *synthetic-public-read-token* ]] || exit 98' \
    'done' \
    'printf "%s\n" "$*" >> "$SMAPI_TEST_ENV_ARGUMENT_LOG"' \
    'exec /usr/bin/env "$@"' \
    > "$fake_bin/env"
chmod 0700 -- "$fake_bin/env"

fake_gh="$test_root/fake-gh/gh"
mkdir -m 0700 -- "$(dirname -- "$fake_gh")"
jq -cn \
    --arg tag "$release_tag" \
    --arg package "$package_name" \
    --arg manifest "$manifest_name" \
    --arg bundle "$bundle_name" \
    --arg package_sha "$package_sha256" \
    --arg manifest_sha "$manifest_sha256" \
    --arg checksums_sha "$(sha256sum -- "$fixture/SHA256SUMS" | cut -d ' ' -f 1)" \
    --arg metadata_sha "$(sha256sum -- "$fixture/build-metadata.json" | cut -d ' ' -f 1)" \
    --arg bundle_sha "$bundle_sha256" \
    --arg bundle_sidecar_sha "$(sha256sum -- "$fixture/$bundle_name.sha256" | cut -d ' ' -f 1)" \
    --argjson package_size "$package_size" \
    --argjson manifest_size "$manifest_size" \
    --argjson checksums_size "$(stat -c %s -- "$fixture/SHA256SUMS")" \
    --argjson metadata_size "$(stat -c %s -- "$fixture/build-metadata.json")" \
    --argjson bundle_size "$(stat -c %s -- "$fixture/$bundle_name")" \
    --argjson bundle_sidecar_size "$(stat -c %s -- "$fixture/$bundle_name.sha256")" '
        [$package, $manifest, "SHA256SUMS", "build-metadata.json", $bundle, ($bundle + ".sha256")] as $names
        | [$package_sha, $manifest_sha, $checksums_sha, $metadata_sha, $bundle_sha, $bundle_sidecar_sha] as $digests
        | [$package_size, $manifest_size, $checksums_size, $metadata_size, $bundle_size, $bundle_sidecar_size] as $sizes
        | [range(0; 6) as $index | {
            id: (1000 + $index),
            name: $names[$index],
            state: "uploaded",
            size: $sizes[$index],
            digest: ("sha256:" + $digests[$index]),
            updated_at: "2026-09-03T00:00:00Z",
            browser_download_url: ("https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/" + $tag + "/" + $names[$index])
        }]
    ' > "$(dirname -- "$fake_gh")/release-assets.json"
printf '%s\n' \
    '#!/bin/bash' \
    'set -euo pipefail' \
    'printf "%s\n" "$*" >> "$(dirname -- "$0")/gh.calls"' \
    'mode_file="$(dirname -- "$0")/gh.mode"' \
    'mode=success' \
    '[[ ! -f "$mode_file" ]] || mode="$(<"$mode_file")"' \
    'if [[ "$1" == api ]]; then' \
    '    [[ "${GH_TOKEN:-}" == synthetic-public-read-token ]]' \
    '    endpoint="${!#}"' \
    '    tag="${endpoint##*/}"' \
    '    asset_json="$(<"$(dirname -- "$0")/release-assets.json")"' \
    '    api_count_file="$(dirname -- "$0")/api.count"' \
    '    api_count=0' \
    '    [[ ! -f "$api_count_file" ]] || api_count="$(<"$api_count_file")"' \
    '    api_count=$((api_count + 1))' \
    '    printf "%s\n" "$api_count" > "$api_count_file"' \
    '    if [[ "$mode" == inventory-extra ]]; then asset_json="$(/usr/bin/jq -c '\''. + [{id:9999,name:"unexpected",state:"uploaded",size:1,digest:null,updated_at:"2026-09-03T00:00:00Z",browser_download_url:"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/"}]'\'' <<<"$asset_json")"; fi' \
    '    if [[ "$mode" == inventory-extra-second && "$api_count" -ge 2 ]]; then asset_json="$(/usr/bin/jq -c '\''. + [{id:9999,name:"unexpected",state:"uploaded",size:1,digest:null,updated_at:"2026-09-03T00:00:00Z",browser_download_url:"https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/download/"}]'\'' <<<"$asset_json")"; fi' \
    '    if [[ "$mode" == inventory-missing ]]; then asset_json="$(/usr/bin/jq -c '\''.[0:-1]'\'' <<<"$asset_json")"; fi' \
    '    if [[ "$mode" == inventory-reuploaded-second && "$api_count" -ge 2 ]]; then asset_json="$(/usr/bin/jq -c '\''.[0].id += 10000 | .[0].updated_at = "2026-09-03T00:01:00Z"'\'' <<<"$asset_json")"; fi' \
    '    if [[ "$mode" == inventory-size ]]; then asset_json="$(/usr/bin/jq -c '\''.[0].size += 1'\'' <<<"$asset_json")"; fi' \
    '    if [[ "$mode" == inventory-digest ]]; then asset_json="$(/usr/bin/jq -c '\''.[0].digest = ("sha256:" + ("0" * 64))'\'' <<<"$asset_json")"; fi' \
    '    if [[ "$mode" == inventory-size-second && "$api_count" -ge 2 ]]; then asset_json="$(/usr/bin/jq -c '\''.[0].size += 1'\'' <<<"$asset_json")"; fi' \
    '    /usr/bin/jq -cn --arg tag "$tag" --argjson assets "$asset_json" '\''{id:900, tag_name: $tag, draft: false, prerelease: true, assets: $assets}'\''' \
    '    exit 0' \
    'fi' \
    '[[ -z "${GH_TOKEN+x}" ]]' \
    '[[ "$1" == attestation && "$2" == verify ]]' \
    'subject="$3"' \
    'directory="$(dirname -- "$subject")"' \
    'name="$(basename -- "$subject")"' \
    'if [[ "$mode" == "fail-package" && "$name" == *installer.zip ]]; then exit 1; fi' \
    'if [[ "$mode" == "fail-manifest" && "$name" == *install-manifest.json ]]; then exit 1; fi' \
    'mapfile -t checksum_lines < "$directory/SHA256SUMS"' \
    'read -r manifest_sha manifest_name <<< "${checksum_lines[0]}"' \
    'read -r package_sha package_name <<< "${checksum_lines[1]}"' \
    'if [[ "$mode" == wrong-statement ]]; then package_sha=dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd; fi' \
    '/usr/bin/jq -cn --arg mn "$manifest_name" --arg ms "$manifest_sha" --arg pn "$package_name" --arg ps "$package_sha" '\''[{verificationResult:{statement:{subject:[{name:$mn,digest:{sha256:$ms}},{name:$pn,digest:{sha256:$ps}}]}}}]'\''' \
    > "$fake_gh"
chmod 0700 -- "$fake_gh"

curl_log="$test_root/curl.log"
curl_argument_log="$test_root/curl-arguments.log"
timeout_log="$fake_bin/timeout.log"
env_argument_log="$test_root/env-arguments.log"
curl_fail_name="$test_root/curl-fail-name"
curl_extra_name="$test_root/curl-extra-name"
export SMAPI_TEST_RELEASE_FIXTURE="$fixture"
export SMAPI_TEST_CURL_LOG="$curl_log"
export SMAPI_TEST_CURL_ARGUMENT_LOG="$curl_argument_log"
export SMAPI_TEST_ENV_ARGUMENT_LOG="$env_argument_log"
export SMAPI_TEST_CURL_FAIL_NAME="$curl_fail_name"
export SMAPI_TEST_CURL_EXTRA_NAME="$curl_extra_name"
export SMAPI_TEST_AMBIENT_SECRET=synthetic-ambient-secret
github_token=synthetic-public-read-token
export SMAPI_TEST_EXPLICIT_TOKEN="$github_token"

new_case_directories() {
    local case_name="$1"
    case_assets="$test_root/$case_name-assets"
    case_verification="$test_root/$case_name-verification"
    mkdir -m 0700 -- "$case_assets" "$case_verification"
}

run_success() {
    local case_name="$1"
    rm -f -- "$curl_fail_name" "$curl_extra_name" "$(dirname -- "$fake_gh")/gh.mode" "$(dirname -- "$fake_gh")/gh.calls" "$(dirname -- "$fake_gh")/api.count"
    : > "$curl_log"
    : > "$curl_argument_log"
    : > "$timeout_log"
    : > "$env_argument_log"
    new_case_directories "$case_name"
    PATH="$fake_bin:$PATH" smapi_download_and_verify_linux_alpha \
        "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh" "$github_token"
}

expect_failure() {
    local case_name="$1"
    shift
    if "$@" > "$test_root/$case_name.stdout" 2> "$test_root/$case_name.stderr"; then
        printf "The published-alpha qualifier accepted invalid case '%s'.\n" "$case_name" >&2
        exit 1
    fi
}

run_success valid
test "$(find "$case_assets" -mindepth 1 -maxdepth 1 -printf . | wc -c)" = 6
test "$(wc -l < "$curl_log")" = 6
test "$(grep -Fc -- '--connect-timeout 15 --max-time 300 --speed-limit 1024 --speed-time 30' "$curl_argument_log")" = 6
test "$(grep -Fc -- '--signal=TERM --kill-after=10s 60s' "$timeout_log")" = 2
test "$(grep -Fc -- '--signal=TERM --kill-after=15s 120s' "$timeout_log")" = 2
test "$(wc -l < "$env_argument_log")" = 2
test "$(wc -l < "$(dirname -- "$fake_gh")/gh.calls")" = 4
test "$(grep -Fc -- "api --method GET --hostname api.github.com" "$(dirname -- "$fake_gh")/gh.calls")" = 2
grep -F -- "attestation verify $case_assets/$package_name" "$(dirname -- "$fake_gh")/gh.calls" >/dev/null
grep -F -- "attestation verify $case_assets/$manifest_name" "$(dirname -- "$fake_gh")/gh.calls" >/dev/null
grep -F -- "--cert-identity https://github.com/$workflow" "$(dirname -- "$fake_gh")/gh.calls" >/dev/null
grep -F -- "--source-digest $release_commit" "$(dirname -- "$fake_gh")/gh.calls" >/dev/null
grep -F -- "--deny-self-hosted-runners" "$(dirname -- "$fake_gh")/gh.calls" >/dev/null
if grep -E -i 'modpack|save|blossom|/home/' "$curl_log" >/dev/null; then
    echo "The qualifier attempted to access private-workload-shaped input." >&2
    exit 1
fi
if grep -F -- "$github_token" "$curl_log" "$curl_argument_log" "$timeout_log" "$env_argument_log" "$(dirname -- "$fake_gh")/gh.calls" >/dev/null; then
    echo "The qualifier exposed its GitHub token in a helper argument or call log." >&2
    exit 1
fi

new_case_directories command-timeout
touch -- "$fake_bin/timeout.fail"
expect_failure command-timeout env PATH="$fake_bin:$PATH" \
    bash -c 'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"
rm -f -- "$fake_bin/timeout.fail"

# Exercise the public entry point with a fake staging helper. The exported token must be captured
# and removed before staging, timeout, curl, or attestation helpers are launched.
entrypoint_root="$test_root/public-entrypoint"
mkdir -m 0700 -- "$entrypoint_root"
cp -- "$qualifier" "$entrypoint_root/qualify-published-linux-alpha.sh"
stage_log="$test_root/stage.log"
printf '%s\n' \
    '#!/bin/bash' \
    'set -euo pipefail' \
    '[[ -z "${GH_TOKEN+x}" ]]' \
    '[[ $# -eq 2 ]]' \
    'printf "token absent\n" > "$SMAPI_TEST_STAGE_LOG"' \
    'mkdir -m 0700 -- "$2"' \
    'cp -- "$SMAPI_TEST_FAKE_GH_DIRECTORY/gh" "$2/gh"' \
    'cp -- "$SMAPI_TEST_FAKE_GH_DIRECTORY/release-assets.json" "$2/release-assets.json"' \
    'chmod 0555 -- "$2/gh"' \
    > "$entrypoint_root/stage-pinned-github-cli.sh"
chmod 0700 -- "$entrypoint_root/stage-pinned-github-cli.sh"
export SMAPI_TEST_STAGE_LOG="$stage_log"
export SMAPI_TEST_FAKE_GH_DIRECTORY
SMAPI_TEST_FAKE_GH_DIRECTORY="$(dirname -- "$fake_gh")"
rm -f -- "$(dirname -- "$fake_gh")/gh.mode" "$(dirname -- "$fake_gh")/api.count"
: > "$curl_log"
: > "$curl_argument_log"
: > "$timeout_log"
: > "$env_argument_log"
published_assets="$test_root/published-assets"
if ! GH_TOKEN="$github_token" PATH="$fake_bin:$PATH" \
    "$entrypoint_root/qualify-published-linux-alpha.sh" \
    "$release_tag" "$release_commit" "$source_tree" "$published_assets" "$test_root/not-used-gh-archive" \
    > "$test_root/public-entrypoint.stdout" 2> "$test_root/public-entrypoint.stderr"; then
    cat -- "$test_root/public-entrypoint.stdout" "$test_root/public-entrypoint.stderr" >&2
    echo "The public entrypoint token-confinement case failed." >&2
    exit 1
fi
test "$(<"$stage_log")" = "token absent"
test "$(find "$published_assets" -mindepth 1 -maxdepth 1 -printf . | wc -c)" = 6
test "$(find "$test_root" -mindepth 1 -maxdepth 1 -name '.smapi-public-alpha.*' -printf . | wc -c)" = 0
test "$(grep -Fc -- '--connect-timeout 15 --max-time 300 --speed-limit 1024 --speed-time 30' "$curl_argument_log")" = 6
test "$(grep -Fc -- '--signal=TERM --kill-after=10s 60s' "$timeout_log")" = 2
test "$(grep -Fc -- '--signal=TERM --kill-after=15s 120s' "$timeout_log")" = 2
test "$(wc -l < "$env_argument_log")" = 2
if grep -F -- "$github_token" \
    "$test_root/public-entrypoint.stdout" "$test_root/public-entrypoint.stderr" \
    "$curl_log" "$curl_argument_log" "$timeout_log" "$env_argument_log" >/dev/null; then
    echo "The public qualifier wrote its GitHub token to terminal output." >&2
    exit 1
fi

printf '%s\n' inventory-extra > "$(dirname -- "$fake_gh")/gh.mode"
new_case_directories inventory-extra
expect_failure inventory-extra env PATH="$fake_bin:$PATH" GH_TOKEN="$github_token" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"

printf '%s\n' inventory-missing > "$(dirname -- "$fake_gh")/gh.mode"
new_case_directories inventory-missing
expect_failure inventory-missing env PATH="$fake_bin:$PATH" GH_TOKEN="$github_token" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"
rm -f -- "$(dirname -- "$fake_gh")/gh.mode"

printf '%s\n' inventory-extra-second > "$(dirname -- "$fake_gh")/gh.mode"
rm -f -- "$(dirname -- "$fake_gh")/api.count"
new_case_directories inventory-changed-after-verification
expect_failure inventory-changed-after-verification env PATH="$fake_bin:$PATH" GH_TOKEN="$github_token" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"
rm -f -- "$(dirname -- "$fake_gh")/gh.mode" "$(dirname -- "$fake_gh")/api.count"

printf '%s\n' inventory-reuploaded-second > "$(dirname -- "$fake_gh")/gh.mode"
new_case_directories inventory-reuploaded
expect_failure inventory-reuploaded env PATH="$fake_bin:$PATH" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"
rm -f -- "$(dirname -- "$fake_gh")/gh.mode" "$(dirname -- "$fake_gh")/api.count"

printf '%s\n' inventory-size-second > "$(dirname -- "$fake_gh")/gh.mode"
new_case_directories inventory-size-changed
expect_failure inventory-size-changed env PATH="$fake_bin:$PATH" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"
rm -f -- "$(dirname -- "$fake_gh")/gh.mode" "$(dirname -- "$fake_gh")/api.count"

printf '%s\n' inventory-size > "$(dirname -- "$fake_gh")/gh.mode"
new_case_directories inventory-download-size
expect_failure inventory-download-size env PATH="$fake_bin:$PATH" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"
rm -f -- "$(dirname -- "$fake_gh")/gh.mode" "$(dirname -- "$fake_gh")/api.count"

printf '%s\n' inventory-digest > "$(dirname -- "$fake_gh")/gh.mode"
new_case_directories inventory-download-digest
expect_failure inventory-download-digest env PATH="$fake_bin:$PATH" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"
rm -f -- "$(dirname -- "$fake_gh")/gh.mode" "$(dirname -- "$fake_gh")/api.count"

new_case_directories invalid-tag
expect_failure invalid-tag env PATH="$fake_bin:$PATH" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha bad-tag "$2" "$3" "$4" "$5" "$6" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"

new_case_directories invalid-commit
expect_failure invalid-commit env PATH="$fake_bin:$PATH" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha "$2" bad "$3" "$4" "$5" "$6" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"

printf '%s\n' "$bundle_name.sha256" > "$curl_extra_name"
new_case_directories extra-entry
expect_failure extra-entry env PATH="$fake_bin:$PATH" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"
rm -f -- "$curl_extra_name"

printf '%s\n' build-metadata.json > "$curl_fail_name"
new_case_directories interrupted-download
expect_failure interrupted-download env PATH="$fake_bin:$PATH" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"
rm -f -- "$curl_fail_name"

corrupt_fixture="$test_root/corrupt-fixture"
cp -a -- "$fixture" "$corrupt_fixture"
printf 'corrupt\n' >> "$corrupt_fixture/$package_name"
new_case_directories corrupt-package
expect_failure corrupt-package env \
    PATH="$fake_bin:$PATH" \
    SMAPI_TEST_RELEASE_FIXTURE="$corrupt_fixture" \
    bash -c 'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"

oversized_fixture="$test_root/oversized-fixture"
cp -a -- "$fixture" "$oversized_fixture"
head -c 1025 /dev/zero > "$oversized_fixture/$bundle_name.sha256"
new_case_directories oversized-asset
expect_failure oversized-asset env \
    PATH="$fake_bin:$PATH" \
    SMAPI_TEST_RELEASE_FIXTURE="$oversized_fixture" \
    bash -c 'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"

checksums_fixture="$test_root/checksums-fixture"
cp -a -- "$fixture" "$checksums_fixture"
printf '%064d  unexpected\n' 0 >> "$checksums_fixture/SHA256SUMS"
new_case_directories checksum-extra-subject
expect_failure checksum-extra-subject env \
    PATH="$fake_bin:$PATH" \
    SMAPI_TEST_RELEASE_FIXTURE="$checksums_fixture" \
    bash -c 'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"

metadata_cases=(
    'metadata-tag|.release.tag = "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.9"'
    'metadata-commit|.source.commit = "dddddddddddddddddddddddddddddddddddddddd"'
    'metadata-tree|.source.tree = "dddddddddddddddddddddddddddddddddddddddd"'
    'metadata-package-identity|.artifacts[1].name = "unexpected-installer.zip"'
)
for metadata_case in "${metadata_cases[@]}"; do
    metadata_case_name="${metadata_case%%|*}"
    metadata_filter="${metadata_case#*|}"
    metadata_fixture="$test_root/$metadata_case_name-fixture"
    cp -a -- "$fixture" "$metadata_fixture"
    jq -c "$metadata_filter" "$fixture/build-metadata.json" > "$metadata_fixture/build-metadata.json"
    new_case_directories "$metadata_case_name"
    expect_failure "$metadata_case_name" env \
        PATH="$fake_bin:$PATH" \
        SMAPI_TEST_RELEASE_FIXTURE="$metadata_fixture" \
        bash -c 'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
        _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"
done

manifest_fixture="$test_root/manifest-fixture"
cp -a -- "$fixture" "$manifest_fixture"
jq -c '.release.source_tree = "dddddddddddddddddddddddddddddddddddddddd"' \
    "$fixture/$manifest_name" > "$manifest_fixture/$manifest_name"
changed_manifest_sha256="$(sha256sum -- "$manifest_fixture/$manifest_name" | cut -d ' ' -f 1)"
changed_manifest_size="$(stat -c %s -- "$manifest_fixture/$manifest_name")"
printf '%s  %s\n%s  %s\n' \
    "$changed_manifest_sha256" "$manifest_name" \
    "$package_sha256" "$package_name" \
    > "$manifest_fixture/SHA256SUMS"
jq -c \
    --arg sha "$changed_manifest_sha256" \
    --argjson size "$changed_manifest_size" \
    '.artifacts[0].sha256 = $sha | .artifacts[0].size_bytes = $size' \
    "$fixture/build-metadata.json" > "$manifest_fixture/build-metadata.json"
new_case_directories manifest-identity
expect_failure manifest-identity env \
    PATH="$fake_bin:$PATH" \
    SMAPI_TEST_RELEASE_FIXTURE="$manifest_fixture" \
    bash -c 'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"

sidecar_fixture="$test_root/sidecar-fixture"
cp -a -- "$fixture" "$sidecar_fixture"
printf '%064d  %s\n' 0 "$bundle_name" > "$sidecar_fixture/$bundle_name.sha256"
new_case_directories bundle-sidecar
expect_failure bundle-sidecar env \
    PATH="$fake_bin:$PATH" \
    SMAPI_TEST_RELEASE_FIXTURE="$sidecar_fixture" \
    bash -c 'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"

printf '%s\n' fail-package > "$(dirname -- "$fake_gh")/gh.mode"
new_case_directories package-attestation
expect_failure package-attestation env PATH="$fake_bin:$PATH" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"

printf '%s\n' fail-manifest > "$(dirname -- "$fake_gh")/gh.mode"
new_case_directories manifest-attestation
expect_failure manifest-attestation env PATH="$fake_bin:$PATH" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"

printf '%s\n' wrong-statement > "$(dirname -- "$fake_gh")/gh.mode"
new_case_directories wrong-statement
expect_failure wrong-statement env PATH="$fake_bin:$PATH" bash -c \
    'source "$1"; smapi_download_and_verify_linux_alpha "$2" "$3" "$4" "$5" "$6" "$7" "$SMAPI_TEST_EXPLICIT_TOKEN"' \
    _ "$qualifier" "$release_tag" "$release_commit" "$source_tree" "$case_assets" "$case_verification" "$fake_gh"

expect_failure existing-destination "$qualifier" \
    "$release_tag" "$release_commit" "$source_tree" "$test_root" "$test_root/not-an-archive"
expect_failure bad-arguments "$qualifier"

echo "Published Linux alpha download/verification qualification tests passed."
