#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(realpath -- "$script_dir/../..")"
cd "$repo_root"

mapfile -t actual_callers < <(
    git grep -l -F -- '--linux-only' \
        -- '*.yml' '*.yaml' '*.md' '*.ps1' '*.sh' \
        ':!build/scripts/prepare-install-package.ps1' \
        ':!build/scripts/test-pinned-github-cli-callers.sh' \
        | sort
)
expected_callers=(
    .github/workflows/build-smapi.yml
    .github/workflows/linux-alpha-release.yml
    docs/technical/smapi.md
)

if [[ "${actual_callers[*]}" != "${expected_callers[*]}" ]]; then
    echo "The --linux-only caller inventory changed without updating pinned verifier delivery validation." >&2
    printf 'Expected callers:\n  %s\n' "${expected_callers[@]}" >&2
    printf 'Actual callers:\n  %s\n' "${actual_callers[@]}" >&2
    exit 1
fi

for caller in "${actual_callers[@]}"; do
    linux_only_count="$(grep -Fc -- '--linux-only' "$caller" || true)"
    verifier_argument_count="$(grep -Fc -- '--github-cli-directory=' "$caller" || true)"
    if [[ "$linux_only_count" -ne "$verifier_argument_count" \
        || "$(grep -Fc -- 'stage-pinned-github-cli.sh' "$caller" || true)" -lt 1 ]]; then
        echo "Every --linux-only call in '$caller' must stage and pass the pinned GitHub CLI directory." >&2
        exit 1
    fi
done

echo "All Linux-only package callers deliver the pinned GitHub CLI."
