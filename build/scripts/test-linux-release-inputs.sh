#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"
validator="$script_dir/validate-linux-release-inputs.sh"
valid_commit="0123456789abcdef0123456789abcdef01234567"

"$validator" "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1" "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1" "$valid_commit" >/dev/null
"$validator" "10.12.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.42" "fork-4eh5xitv6787h645ebv-linux-v10.12.3-alpha.42" "$valid_commit" >/dev/null

invalid_cases=(
    "4.5.3|fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1|$valid_commit"
    "4.5.3-alpha.1|fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1|$valid_commit"
    "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.0|fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.0|$valid_commit"
    "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.01|fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.01|$valid_commit"
    "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1|4.5.3|$valid_commit"
    "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1|fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2|$valid_commit"
    "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1|fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1|deadbeef"
    "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1|fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1|0123456789ABCDEF0123456789ABCDEF01234567"
)

for test_case in "${invalid_cases[@]}"; do
    IFS='|' read -r version tag commit <<< "$test_case"
    if "$validator" "$version" "$tag" "$commit" >/dev/null 2>&1; then
        echo "Validator accepted invalid inputs: $test_case" >&2
        exit 1
    fi
done

source_version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$repo_root/build/common.targets")"
api_version="$(sed -n 's:.*RawApiVersion = "\([^"]*\)";.*:\1:p' "$repo_root/src/SMAPI/Constants.cs")"
[[ -n "$source_version" && "$api_version" == "$source_version" ]]
for manifest in \
    "$repo_root/src/SMAPI.Mods.ConsoleCommands/manifest.json" \
    "$repo_root/src/SMAPI.Mods.SaveBackup/manifest.json"; do
    [[ "$(jq -r .Version "$manifest")" == "$source_version" ]]
    [[ "$(jq -r .MinimumApiVersion "$manifest")" == "$source_version" ]]
done

if [[ ! "$source_version" =~ ^([0-9]+\.[0-9]+\.[0-9]+)-unofficial\.4eh5xitv6787h645ebv\.linux\.alpha\.([1-9][0-9]*)$ ]]; then
    echo "Committed SMAPI version isn't a valid fork alpha: $source_version" >&2
    exit 1
fi
source_tag="fork-4eh5xitv6787h645ebv-linux-v${BASH_REMATCH[1]}-alpha.${BASH_REMATCH[2]}"
"$validator" "$source_version" "$source_tag" "$valid_commit" >/dev/null

release_workflow="$repo_root/.github/workflows/linux-alpha-release.yml"
if grep -Fq "local-package picker is not available" "$release_workflow"; then
    echo "Generated release notes incorrectly say the local-package picker is unavailable." >&2
    exit 1
fi
grep -Fq 'exact six files' "$release_workflow"
grep -Fq 'For rollback, choose **Load or refresh history**, select one authenticated generation, then use **Inspect rollback**, **Confirm reviewed plan**, and **Run rollback**.' "$release_workflow"
grep -Fq 'use **Uninstall** first' "$release_workflow"
grep -Fq 'never treats the folder path or metadata as verified identity' "$release_workflow"

echo "Linux release input validation tests passed."
