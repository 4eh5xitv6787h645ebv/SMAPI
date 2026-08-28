#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
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

echo "Linux release input validation tests passed."
