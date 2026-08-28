#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
    echo "Usage: $0 <release-version> <release-tag> <40-character-source-commit>" >&2
    exit 2
fi

release_version="$1"
release_tag="$2"
source_commit="$3"

if [[ ! "$release_version" =~ ^([0-9]+\.[0-9]+\.[0-9]+)-unofficial\.4eh5xitv6787h645ebv\.linux\.alpha\.([1-9][0-9]*)$ ]]; then
    echo "Invalid release version '$release_version'. Expected x.y.z-unofficial.4eh5xitv6787h645ebv.linux.alpha.N with N >= 1." >&2
    exit 1
fi

expected_tag="fork-4eh5xitv6787h645ebv-linux-v${BASH_REMATCH[1]}-alpha.${BASH_REMATCH[2]}"
if [[ "$release_tag" != "$expected_tag" ]]; then
    echo "Invalid release tag '$release_tag'. Expected '$expected_tag' for version '$release_version'." >&2
    exit 1
fi

if [[ ! "$source_commit" =~ ^[0-9a-f]{40}$ ]]; then
    echo "Invalid source commit '$source_commit'. Expected a lowercase 40-character Git object ID." >&2
    exit 1
fi

printf 'release_version=%s\nrelease_tag=%s\nsource_commit=%s\n' \
    "$release_version" "$release_tag" "$source_commit"
