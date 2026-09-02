#!/usr/bin/env bash
set -euo pipefail

readonly SMAPI_RELEASE_REPOSITORY="4eh5xitv6787h645ebv/SMAPI"
readonly SMAPI_RELEASE_REPOSITORY_URL="https://github.com/$SMAPI_RELEASE_REPOSITORY"
readonly SMAPI_RELEASE_WORKFLOW=".github/workflows/linux-alpha-release.yml"

smapi_release_error() {
    printf '%s\n' "$1" >&2
    return 1
}

smapi_assert_private_directory() {
    local directory="$1"
    local description="$2"
    if [[ ! -d "$directory" || -L "$directory" \
        || "$(stat -c %u -- "$directory")" != "$EUID" \
        || "$(stat -c %a -- "$directory")" != 700 ]]; then
        smapi_release_error "The $description must be one real current-user mode-0700 directory."
    fi
}

smapi_assert_downloaded_asset() {
    local path="$1"
    local maximum_size="$2"
    local size mode
    if [[ ! -f "$path" || -L "$path" \
        || "$(stat -c %h -- "$path")" != 1 \
        || "$(stat -c %u -- "$path")" != "$EUID" ]]; then
        smapi_release_error "A downloaded release asset is not one current-user single-link ordinary file."
        return
    fi
    size="$(stat -c %s -- "$path")"
    mode="$(stat -c %a -- "$path")"
    if (( size <= 0 || size > maximum_size || (8#$mode & 0133) != 0 )); then
        smapi_release_error "A downloaded release asset violates its fixed size or mode bound."
    fi
}

# Internal testable core. The public entry point below always supplies a GitHub CLI staged by the
# repository's exact 2.92.0 archive/hash verifier. Callers should use the script entry point, not this function.
smapi_download_and_verify_linux_alpha() {
    if [[ $# -ne 6 ]]; then
        smapi_release_error "Internal usage: smapi_download_and_verify_linux_alpha TAG COMMIT TREE ASSET-DIR VERIFY-DIR PINNED-GH"
        return
    fi

    local release_tag="$1"
    local release_commit="$2"
    local source_tree="$3"
    local asset_directory="$4"
    local verification_directory="$5"
    local pinned_gh="$6"
    local version_base alpha_number release_version
    if [[ ! "$release_tag" =~ ^fork-4eh5xitv6787h645ebv-linux-v([0-9]+\.[0-9]+\.[0-9]+)-alpha\.([1-9][0-9]*)$ ]]; then
        smapi_release_error "The release tag is not one canonical SMAPI Linux fork alpha tag."
        return
    fi
    version_base="${BASH_REMATCH[1]}"
    alpha_number="${BASH_REMATCH[2]}"
    release_version="$version_base-unofficial.4eh5xitv6787h645ebv.linux.alpha.$alpha_number"
    if [[ ! "$release_commit" =~ ^[0-9a-f]{40}$ || ! "$source_tree" =~ ^[0-9a-f]{40}$ ]]; then
        smapi_release_error "The release commit and source tree must be full lowercase Git object IDs."
        return
    fi
    smapi_assert_private_directory "$asset_directory" "release-asset directory" || return
    smapi_assert_private_directory "$verification_directory" "verification directory" || return
    if [[ "$(find "$asset_directory" -mindepth 1 -maxdepth 1 -printf . | wc -c)" != 0 \
        || "$(find "$verification_directory" -mindepth 1 -maxdepth 1 -printf . | wc -c)" != 0 ]]; then
        smapi_release_error "Release qualification requires new empty asset and verification directories."
        return
    fi
    if [[ ! -f "$pinned_gh" || -L "$pinned_gh" || ! -x "$pinned_gh" ]]; then
        smapi_release_error "The staged pinned GitHub CLI is unavailable."
        return
    fi
    local github_token="${GH_TOKEN:-}"
    if [[ -z "$github_token" ]]; then
        smapi_release_error "GH_TOKEN is required for the pinned GitHub CLI to verify the public release asset inventory."
        return
    fi

    local package_name="SMAPI-$release_version-linux-x64-installer.zip"
    local manifest_name="SMAPI-$release_version-linux-x64-install-manifest.json"
    local checksums_name="SHA256SUMS"
    local metadata_name="build-metadata.json"
    local bundle_name="SMAPI-$release_version-linux-x64-attestation-bundle.jsonl"
    local bundle_checksum_name="$bundle_name.sha256"
    local -a asset_names=(
        "$package_name"
        "$manifest_name"
        "$checksums_name"
        "$metadata_name"
        "$bundle_name"
        "$bundle_checksum_name"
    )
    local -a maximum_sizes=(
        $((512 * 1024 * 1024))
        $((16 * 1024 * 1024))
        $((64 * 1024))
        $((256 * 1024))
        $((2 * 1024 * 1024))
        1024
    )

    local private_home="$verification_directory/private-home"
    install -d -m 0700 -- "$private_home"
    local inventory_stage inventory_output
    for inventory_stage in before-download; do
        inventory_output="$verification_directory/release-inventory-$inventory_stage.json"
        env -i \
            HOME="$private_home" \
            GH_CONFIG_DIR="$private_home" \
            XDG_CONFIG_HOME="$private_home" \
            XDG_CACHE_HOME="$private_home" \
            XDG_RUNTIME_DIR="$private_home" \
            TMPDIR="$private_home" \
            GH_TOKEN="$github_token" \
            GH_PROMPT_DISABLED=1 \
            GH_NO_UPDATE_NOTIFIER=1 \
            GH_NO_EXTENSION_UPDATE_NOTIFIER=1 \
            GH_PAGER= \
            PAGER= \
            NO_COLOR=1 \
            TERM=dumb \
            LANG=C.UTF-8 \
            LC_ALL=C.UTF-8 \
            "$pinned_gh" api \
                --method GET \
                --hostname api.github.com \
                -H 'Accept: application/vnd.github+json' \
                -H 'X-GitHub-Api-Version: 2022-11-28' \
                "repos/$SMAPI_RELEASE_REPOSITORY/releases/tags/$release_tag" \
                > "$inventory_output"
        smapi_assert_downloaded_asset "$inventory_output" $((2 * 1024 * 1024)) || return
        jq -e \
            --arg tag "$release_tag" \
            --arg base "$SMAPI_RELEASE_REPOSITORY_URL/releases/download/$release_tag/" \
            --arg package "$package_name" \
            --arg manifest "$manifest_name" \
            --arg checksums "$checksums_name" \
            --arg metadata "$metadata_name" \
            --arg bundle "$bundle_name" \
            --arg bundle_checksum "$bundle_checksum_name" '
                type == "object"
                and .tag_name == $tag
                and .draft == false
                and .prerelease == true
                and (.assets | type == "array" and length == 6)
                and (.assets | map(.name) | sort) == ([$package, $manifest, $checksums, $metadata, $bundle, $bundle_checksum] | sort)
                and all(.assets[];
                    (.name | type == "string")
                    and .state == "uploaded"
                    and (.size | type == "number" and . > 0)
                    and .browser_download_url == ($base + .name))
            ' "$inventory_output" >/dev/null
    done

    local index name path url
    for index in "${!asset_names[@]}"; do
        name="${asset_names[$index]}"
        path="$asset_directory/$name"
        url="$SMAPI_RELEASE_REPOSITORY_URL/releases/download/$release_tag/$name"
        curl \
            --fail \
            --silent \
            --show-error \
            --location \
            --max-redirs 3 \
            --max-filesize "${maximum_sizes[$index]}" \
            --retry 3 \
            --retry-all-errors \
            --proto '=https' \
            --proto-redir '=https' \
            --output "$path" \
            "$url"
        smapi_assert_downloaded_asset "$path" "${maximum_sizes[$index]}" || return
    done
    if [[ "$(find "$asset_directory" -mindepth 1 -maxdepth 1 -printf . | wc -c)" != 6 ]]; then
        smapi_release_error "The downloaded release directory does not contain exactly six entries."
        return
    fi

    local package_sha256 manifest_sha256 metadata_sha256 package_size manifest_size
    package_sha256="$(sha256sum -- "$asset_directory/$package_name" | cut -d ' ' -f 1)"
    manifest_sha256="$(sha256sum -- "$asset_directory/$manifest_name" | cut -d ' ' -f 1)"
    metadata_sha256="$(sha256sum -- "$asset_directory/$metadata_name" | cut -d ' ' -f 1)"
    package_size="$(stat -c %s -- "$asset_directory/$package_name")"
    manifest_size="$(stat -c %s -- "$asset_directory/$manifest_name")"
    local expected_checksums="$verification_directory/expected-SHA256SUMS"
    printf '%s  %s\n%s  %s\n' \
        "$manifest_sha256" "$manifest_name" \
        "$package_sha256" "$package_name" \
        > "$expected_checksums"
    cmp --silent -- "$expected_checksums" "$asset_directory/$checksums_name" \
        || smapi_release_error "SHA256SUMS is not the exact ordered two-subject document." || return
    (cd "$asset_directory" && sha256sum --check --strict "$checksums_name") >/dev/null

    local expected_workflow="$SMAPI_RELEASE_REPOSITORY/$SMAPI_RELEASE_WORKFLOW@refs/tags/$release_tag"
    jq -e \
        --arg version "$release_version" \
        --arg tag "$release_tag" \
        --arg repository "$SMAPI_RELEASE_REPOSITORY_URL" \
        --arg commit "$release_commit" \
        --arg tree "$source_tree" \
        --arg workflow "$expected_workflow" \
        --arg manifest_name "$manifest_name" \
        --arg manifest_sha256 "$manifest_sha256" \
        --argjson manifest_size "$manifest_size" \
        --arg package_name "$package_name" \
        --arg package_sha256 "$package_sha256" \
        --argjson package_size "$package_size" '
            type == "object"
            and keys == ["artifacts", "build", "release", "reproducibility", "schema_version", "source"]
            and .schema_version == 1
            and (.release | type == "object" and keys == ["tag", "version"]
                and .version == $version and .tag == $tag)
            and (.source | type == "object" and keys == ["commit", "repository", "tree"]
                and .repository == $repository and .commit == $commit and .tree == $tree)
            and (.build | type == "object"
                and keys == ["configuration", "dotnet_info", "reference_assemblies_commit", "run", "runner_arch", "runner_image", "runtime_identifier", "timestamp_utc", "workflow"]
                and .workflow == $workflow
                and .configuration == "Release"
                and .runtime_identifier == "linux-x64"
                and (.run | type == "string" and startswith($repository + "/actions/runs/") and length <= 2048)
                and (.runner_arch | type == "string" and length > 0 and length <= 64)
                and (.runner_image | type == "string" and length > 0 and length <= 256)
                and (.reference_assemblies_commit | type == "string" and test("^[0-9a-f]{40}$"))
                and (.timestamp_utc | type == "string" and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$"))
                and (.dotnet_info | type == "string" and length > 0))
            and .artifacts == [
                {"name": $manifest_name, "size_bytes": $manifest_size, "sha256": $manifest_sha256},
                {"name": $package_name, "size_bytes": $package_size, "sha256": $package_sha256}
            ]
            and .reproducibility == "Inputs and provenance are recorded; byte-for-byte reproducibility is not claimed."
        ' "$asset_directory/$metadata_name" >/dev/null

    jq -e \
        --arg version "$release_version" \
        --arg tag "$release_tag" \
        --arg repository "$SMAPI_RELEASE_REPOSITORY_URL" \
        --arg repository_name "$SMAPI_RELEASE_REPOSITORY" \
        --arg commit "$release_commit" \
        --arg tree "$source_tree" \
        --arg workflow "$expected_workflow" \
        --arg manifest_name "$manifest_name" \
        --arg package_name "$package_name" \
        --arg package_sha256 "$package_sha256" \
        --argjson package_size "$package_size" '
            type == "object"
            and keys == ["entries", "generated_files", "release", "release_authority_policy", "schema_version"]
            and .schema_version == 4
            and (.entries | type == "array" and length > 0)
            and (.generated_files | type == "array")
            and (.release | type == "object"
                and keys == ["build_configuration", "build_workflow", "embedded_version", "package_asset_name", "package_sha256", "package_size_bytes", "repository", "runtime_identifier", "source_commit", "source_tree", "tag"]
                and .repository == $repository
                and .tag == $tag
                and .embedded_version == $version
                and .package_asset_name == $package_name
                and .source_commit == $commit
                and .source_tree == $tree
                and .package_sha256 == $package_sha256
                and .package_size_bytes == $package_size
                and .build_workflow == $workflow
                and .build_configuration == "Release"
                and .runtime_identifier == "linux-x64")
            and (.release_authority_policy | type == "object"
                and keys == ["build_workflow", "kind", "manifest_subject_name", "package_subject_name", "repository", "repository_identifier", "repository_owner_identifier", "runner_environment", "source_commit", "source_reference", "trigger"]
                and .kind == "github_artifact_attestation_v1"
                and .repository == $repository_name
                and .source_reference == ("refs/tags/" + $tag)
                and .source_commit == $commit
                and .build_workflow == $workflow
                and .runner_environment == "github-hosted"
                and .trigger == "push"
                and .repository_identifier == "1336010508"
                and .repository_owner_identifier == "45441845"
                and .package_subject_name == $package_name
                and .manifest_subject_name == $manifest_name)
        ' "$asset_directory/$manifest_name" >/dev/null

    local bundle_sha256 expected_bundle_checksum="$verification_directory/expected-bundle.sha256"
    bundle_sha256="$(sha256sum -- "$asset_directory/$bundle_name" | cut -d ' ' -f 1)"
    printf '%s  %s\n' "$bundle_sha256" "$bundle_name" > "$expected_bundle_checksum"
    cmp --silent -- "$expected_bundle_checksum" "$asset_directory/$bundle_checksum_name" \
        || smapi_release_error "The attestation-bundle checksum sidecar is not canonical." || return
    (cd "$asset_directory" && sha256sum --check --strict "$bundle_checksum_name") >/dev/null
    jq -e -s '
        length == 1
        and (.[0] | type == "object"
            and keys == ["dsseEnvelope", "mediaType", "verificationMaterial"]
            and .mediaType == "application/vnd.dev.sigstore.bundle.v0.3+json"
            and (.dsseEnvelope | type) == "object"
            and (.verificationMaterial | type) == "object")
    ' "$asset_directory/$bundle_name" >/dev/null

    local -a attestation_policy=(
        --bundle "$asset_directory/$bundle_name"
        --hostname github.com
        --repo "$SMAPI_RELEASE_REPOSITORY"
        --predicate-type https://slsa.dev/provenance/v1
        --cert-oidc-issuer https://token.actions.githubusercontent.com
        --cert-identity "https://github.com/$expected_workflow"
        --signer-digest "$release_commit"
        --source-ref "refs/tags/$release_tag"
        --source-digest "$release_commit"
        --deny-self-hosted-runners
        --limit 2
        --format json
    )
    local subject verification_output
    for subject in "$package_name" "$manifest_name"; do
        verification_output="$verification_directory/$subject.attestation.json"
        env -i \
            HOME="$private_home" \
            GH_CONFIG_DIR="$private_home" \
            XDG_CONFIG_HOME="$private_home" \
            XDG_CACHE_HOME="$private_home" \
            XDG_RUNTIME_DIR="$private_home" \
            TMPDIR="$private_home" \
            DBUS_SESSION_BUS_ADDRESS="unix:path=$private_home/session-bus-unavailable" \
            DBUS_SYSTEM_BUS_ADDRESS="unix:path=$private_home/system-bus-unavailable" \
            GH_PROMPT_DISABLED=1 \
            GH_NO_UPDATE_NOTIFIER=1 \
            GH_NO_EXTENSION_UPDATE_NOTIFIER=1 \
            GH_PAGER= \
            PAGER= \
            NO_COLOR=1 \
            TERM=dumb \
            LANG=C.UTF-8 \
            LC_ALL=C.UTF-8 \
            "$pinned_gh" attestation verify "$asset_directory/$subject" \
                "${attestation_policy[@]}" > "$verification_output"
        smapi_assert_downloaded_asset "$verification_output" $((2 * 1024 * 1024)) || return
        jq -e \
            --arg manifest_name "$manifest_name" \
            --arg manifest_sha256 "$manifest_sha256" \
            --arg package_name "$package_name" \
            --arg package_sha256 "$package_sha256" '
                type == "array"
                and length == 1
                and .[0].verificationResult.statement.subject == [
                    {"name": $manifest_name, "digest": {"sha256": $manifest_sha256}},
                    {"name": $package_name, "digest": {"sha256": $package_sha256}}
                ]
            ' "$verification_output" >/dev/null
    done

    for index in "${!asset_names[@]}"; do
        smapi_assert_downloaded_asset "$asset_directory/${asset_names[$index]}" "${maximum_sizes[$index]}" || return
    done
    if [[ "$(find "$asset_directory" -mindepth 1 -maxdepth 1 -printf . | wc -c)" != 6 ]]; then
        smapi_release_error "The verified release directory changed before qualification completed."
        return
    fi
    cmp --silent -- "$expected_checksums" "$asset_directory/$checksums_name" \
        || smapi_release_error "SHA256SUMS changed during qualification." || return
    (cd "$asset_directory" && sha256sum --check --strict "$checksums_name") >/dev/null
    cmp --silent -- "$expected_bundle_checksum" "$asset_directory/$bundle_checksum_name" \
        || smapi_release_error "The bundle sidecar changed during qualification." || return
    (cd "$asset_directory" && sha256sum --check --strict "$bundle_checksum_name") >/dev/null
    if [[ "$(sha256sum -- "$asset_directory/$metadata_name" | cut -d ' ' -f 1)" != "$metadata_sha256" ]]; then
        smapi_release_error "build-metadata.json changed during qualification."
        return
    fi

    inventory_output="$verification_directory/release-inventory-after-verification.json"
    env -i \
        HOME="$private_home" \
        GH_CONFIG_DIR="$private_home" \
        XDG_CONFIG_HOME="$private_home" \
        XDG_CACHE_HOME="$private_home" \
        XDG_RUNTIME_DIR="$private_home" \
        TMPDIR="$private_home" \
        GH_TOKEN="$github_token" \
        GH_PROMPT_DISABLED=1 \
        GH_NO_UPDATE_NOTIFIER=1 \
        GH_NO_EXTENSION_UPDATE_NOTIFIER=1 \
        GH_PAGER= \
        PAGER= \
        NO_COLOR=1 \
        TERM=dumb \
        LANG=C.UTF-8 \
        LC_ALL=C.UTF-8 \
        "$pinned_gh" api \
            --method GET \
            --hostname api.github.com \
            -H 'Accept: application/vnd.github+json' \
            -H 'X-GitHub-Api-Version: 2022-11-28' \
            "repos/$SMAPI_RELEASE_REPOSITORY/releases/tags/$release_tag" \
            > "$inventory_output"
    smapi_assert_downloaded_asset "$inventory_output" $((2 * 1024 * 1024)) || return
    jq -e \
        --arg tag "$release_tag" \
        --arg base "$SMAPI_RELEASE_REPOSITORY_URL/releases/download/$release_tag/" \
        --arg package "$package_name" \
        --arg manifest "$manifest_name" \
        --arg checksums "$checksums_name" \
        --arg metadata "$metadata_name" \
        --arg bundle "$bundle_name" \
        --arg bundle_checksum "$bundle_checksum_name" '
            type == "object"
            and .tag_name == $tag
            and .draft == false
            and .prerelease == true
            and (.assets | type == "array" and length == 6)
            and (.assets | map(.name) | sort) == ([$package, $manifest, $checksums, $metadata, $bundle, $bundle_checksum] | sort)
            and all(.assets[];
                (.name | type == "string")
                and .state == "uploaded"
                and (.size | type == "number" and . > 0)
                and .browser_download_url == ($base + .name))
        ' "$inventory_output" >/dev/null
}

qualify_published_linux_alpha_main() {
    if [[ $# -ne 5 ]]; then
        printf '%s\n' "Usage: $0 <release-tag> <source-commit> <source-tree> <new-download-directory> <official-gh-2.92.0-linux-amd64-archive>" >&2
        return 2
    fi
    if [[ "$EUID" -eq 0 ]]; then
        smapi_release_error "Published Linux alpha qualification must run as a normal user, never root."
        return
    fi

    local release_tag="$1"
    local release_commit="$2"
    local source_tree="$3"
    local destination_input="$4"
    local gh_archive="$5"
    local destination_name destination_parent_input destination_parent destination
    destination_name="$(basename -- "$destination_input")"
    destination_parent_input="$(dirname -- "$destination_input")"
    if [[ "$destination_name" == "." || "$destination_name" == ".." \
        || ! -d "$destination_parent_input" || -L "$destination_parent_input" ]]; then
        smapi_release_error "The download destination must name a new child of one existing real directory."
        return
    fi
    destination_parent="$(realpath -- "$destination_parent_input")"
    destination="$destination_parent/$destination_name"
    if [[ -e "$destination" || -L "$destination" ]]; then
        smapi_release_error "The download destination must not already exist."
        return
    fi

    local script_directory
    script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
    local scratch scratch_identity assets verification pinned
    umask 077
    scratch="$(mktemp -d --tmpdir="$destination_parent" .smapi-public-alpha.XXXXXXXX)"
    chmod 0700 -- "$scratch"
    scratch_identity="$(stat -c '%d:%i' -- "$scratch")"
    cleanup_published_alpha_qualification() {
        if [[ -d "$scratch" && ! -L "$scratch" \
            && "$(stat -c '%d:%i' -- "$scratch")" == "$scratch_identity" ]]; then
            rm -rf --one-file-system -- "$scratch"
        fi
    }
    trap cleanup_published_alpha_qualification EXIT

    assets="$scratch/assets"
    verification="$scratch/verification"
    pinned="$scratch/pinned-gh"
    install -d -m 0700 -- "$assets" "$verification"
    "$script_directory/stage-pinned-github-cli.sh" "$gh_archive" "$pinned" >/dev/null
    smapi_download_and_verify_linux_alpha \
        "$release_tag" "$release_commit" "$source_tree" "$assets" "$verification" "$pinned/gh"

    local assets_identity
    assets_identity="$(stat -c '%d:%i' -- "$assets")"
    mv --no-clobber --no-target-directory -- "$assets" "$destination"
    if [[ -e "$assets" || -L "$assets" \
        || ! -d "$destination" || -L "$destination" \
        || "$(stat -c '%d:%i' -- "$destination")" != "$assets_identity" ]]; then
        smapi_release_error "The download destination was substituted before verified assets could be published."
        return
    fi

    printf 'Verified published Linux alpha %s at commit %s into %s.\n' \
        "$release_tag" "$release_commit" "$destination"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    qualify_published_linux_alpha_main "$@"
fi
