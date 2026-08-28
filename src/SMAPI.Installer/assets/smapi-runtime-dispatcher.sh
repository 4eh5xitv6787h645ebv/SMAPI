#!/usr/bin/env bash

# Use the game's .NET 6 runtime by default. The private .NET 10 host remains available through
# SMAPI_DOTNET_RUNTIME=net10 for diagnostics, but isn't safe as the automatic choice while common
# Harmony generic detours can silently become type-unsafe there (issue #146).

get_cgroup_layout() {
    local controller=$1
    local hierarchy controllers cgroup_path unified_path="" controller_path=""
    local line separator_index filesystem super_options mount_root mount_point relative_path current_path
    local -a fields

    [ -r /proc/self/cgroup ] && [ -r /proc/self/mountinfo ] || return 1
    while IFS=: read -r hierarchy controllers cgroup_path; do
        if [ "$hierarchy" = "0" ] && [ -z "$controllers" ]; then
            unified_path=$cgroup_path
        elif [[ ",$controllers," == *",$controller,"* ]]; then
            controller_path=$cgroup_path
        fi
    done < /proc/self/cgroup

    while IFS= read -r line; do
        read -r -a fields <<< "$line"
        (( ${#fields[@]} >= 10 )) || continue

        separator_index=6
        while (( separator_index < ${#fields[@]} )) && [ "${fields[$separator_index]}" != "-" ]; do
            ((separator_index++))
        done
        (( separator_index + 3 < ${#fields[@]} )) || continue

        filesystem=${fields[$((separator_index + 1))]}
        super_options=${fields[$((separator_index + 3))]}
        if [ "$filesystem" = "cgroup2" ] && [ -n "$unified_path" ]; then
            cgroup_path=$unified_path
            hierarchy=2
        elif [ "$filesystem" = "cgroup" ] && [ -n "$controller_path" ] && [[ ",$super_options," == *",$controller,"* ]]; then
            cgroup_path=$controller_path
            hierarchy=1
        else
            continue
        fi

        mount_root=${fields[3]}
        mount_point=${fields[4]}
        # Unusual escaped mount paths are safe to ignore: auto mode then stays on workstation GC.
        if [[ "$mount_root" == *\\* || "$mount_point" == *\\* ]]; then
            continue
        fi
        if [ "$mount_root" = "/" ]; then
            relative_path=$cgroup_path
        elif [ "$cgroup_path" = "$mount_root" ]; then
            relative_path=""
        elif [[ "$cgroup_path" == "$mount_root/"* ]]; then
            relative_path=${cgroup_path#"$mount_root"}
        else
            continue
        fi

        if [ "$mount_point" != "/" ]; then
            mount_point=${mount_point%/}
        fi
        current_path="$mount_point$relative_path"
        if [ "$current_path" != "/" ]; then
            current_path=${current_path%/}
        fi
        [ -d "$current_path" ] || continue
        printf '%s\t%s\t%s\n' "$hierarchy" "$mount_point" "$current_path"
        return 0
    done < /proc/self/mountinfo

    return 1
}

count_cpu_list() {
    local cpu_list=$1 range first last total=0
    local -a ranges

    [[ "$cpu_list" =~ ^[0-9]+(-[0-9]+)?(,[0-9]+(-[0-9]+)?)*$ ]] || return 1
    IFS=, read -r -a ranges <<< "$cpu_list"
    for range in "${ranges[@]}"; do
        if [[ "$range" == *-* ]]; then
            first=${range%-*}
            last=${range#*-}
        else
            first=$range
            last=$range
        fi
        (( 10#$last >= 10#$first )) || return 1
        total=$((total + 10#$last - 10#$first + 1))
    done
    (( total > 0 )) || return 1
    printf '%s\n' "$total"
}

get_effective_cpuset_count() {
    local hierarchy mount_point current_path path cpu_list count minimum=""

    IFS=$'\t' read -r hierarchy mount_point current_path < <(get_cgroup_layout cpuset) || return 1
    while :; do
        path="$current_path/cpuset.cpus.effective"
        if [ "$hierarchy" = "1" ] && [ ! -r "$path" ]; then
            path="$current_path/cpuset.effective_cpus"
        fi
        if [ ! -r "$path" ]; then
            path="$current_path/cpuset.cpus"
        fi
        if [ -r "$path" ]; then
            IFS= read -r cpu_list < "$path" || return 1
            if [ -n "$cpu_list" ]; then
                count=$(count_cpu_list "$cpu_list") || return 1
                if [ -z "$minimum" ] || (( count < minimum )); then
                    minimum=$count
                fi
            fi
        fi

        [ "$current_path" = "$mount_point" ] && break
        current_path=${current_path%/*}
        [[ "$current_path" == "$mount_point" || "$current_path" == "$mount_point/"* ]] || return 1
    done

    [ -n "$minimum" ] || return 1
    printf '%s\n' "$minimum"
}

get_effective_cpu_count() {
    local cpu_count cpuset_count hierarchy mount_point current_path
    local quota period extra quota_count saw_cpu=0

    command -v nproc >/dev/null 2>&1 || return 1
    cpu_count=$(nproc 2>/dev/null) || return 1
    if ! [[ "$cpu_count" =~ ^[0-9]+$ ]] || (( cpu_count < 1 )); then
        return 1
    fi

    # nproc reflects the process affinity mask. Check the cgroup cpuset hierarchy too when available.
    if cpuset_count=$(get_effective_cpuset_count 2>/dev/null); then
        if (( cpuset_count < cpu_count )); then
            cpu_count=$cpuset_count
        fi
    fi

    IFS=$'\t' read -r hierarchy mount_point current_path < <(get_cgroup_layout cpu) || return 1
    while :; do
        if [ "$hierarchy" = "2" ]; then
            if [ -r "$current_path/cpu.max" ]; then
                read -r quota period extra < "$current_path/cpu.max" || return 1
                [ -z "$extra" ] || return 1
                saw_cpu=1
            else
                [ "$current_path" = "$mount_point" ] || return 1
                quota=max
                period=1
            fi
        else
            if [ -r "$current_path/cpu.cfs_quota_us" ] || [ -r "$current_path/cpu.cfs_period_us" ]; then
                [ -r "$current_path/cpu.cfs_quota_us" ] && [ -r "$current_path/cpu.cfs_period_us" ] || return 1
                IFS= read -r quota < "$current_path/cpu.cfs_quota_us" || return 1
                IFS= read -r period < "$current_path/cpu.cfs_period_us" || return 1
                saw_cpu=1
            else
                return 1
            fi
        fi

        if ! [[ "$period" =~ ^[0-9]+$ ]] || (( period < 1 )); then
            return 1
        fi
        if [ "$quota" != "max" ] && [ "$quota" != "-1" ]; then
            [[ "$quota" =~ ^[0-9]+$ ]] || return 1
            quota_count=$((quota / period))
            if (( quota_count < 1 )); then
                quota_count=1
            fi
            if (( quota_count < cpu_count )); then
                cpu_count=$quota_count
            fi
        fi

        [ "$current_path" = "$mount_point" ] && break
        current_path=${current_path%/*}
        [[ "$current_path" == "$mount_point" || "$current_path" == "$mount_point/"* ]] || return 1
    done

    (( saw_cpu == 1 )) || return 1
    printf '%s\n' "$cpu_count"
}

get_effective_available_memory() {
    local available_kib available_bytes hierarchy mount_point current_path current_file memory_current
    local limit_name limit_value remaining saw_memory=0

    available_kib=$(awk '$1 == "MemAvailable:" { print $2; exit }' /proc/meminfo 2>/dev/null) || return 1
    if ! [[ "$available_kib" =~ ^[0-9]+$ ]]; then
        return 1
    fi
    available_bytes=$((available_kib * 1024))

    IFS=$'\t' read -r hierarchy mount_point current_path < <(get_cgroup_layout memory) || return 1
    while :; do
        if [ "$hierarchy" = "2" ]; then
            current_file="$current_path/memory.current"
            limit_name="memory.max memory.high"
        else
            current_file="$current_path/memory.usage_in_bytes"
            limit_name="memory.limit_in_bytes"
        fi
        if [ -r "$current_file" ]; then
            IFS= read -r memory_current < "$current_file" || return 1
            [[ "$memory_current" =~ ^[0-9]+$ ]] && (( ${#memory_current} <= 18 )) || return 1
            saw_memory=1

            for limit_name in $limit_name; do
                [ -r "$current_path/$limit_name" ] || return 1
                IFS= read -r limit_value < "$current_path/$limit_name" || return 1
                if [ "$limit_value" = "max" ]; then
                    continue
                fi
                [[ "$limit_value" =~ ^[0-9]+$ ]] || return 1
                if (( ${#limit_value} > 18 )); then
                    # cgroup v1 uses a near-Int64.MaxValue sentinel for an unlimited hierarchy.
                    [ "$hierarchy" = "1" ] && continue
                    return 1
                fi
                if (( limit_value <= memory_current )); then
                    available_bytes=0
                else
                    remaining=$((limit_value - memory_current))
                    if (( remaining < available_bytes )); then
                        available_bytes=$remaining
                    fi
                fi
            done
        else
            if [ "$hierarchy" != "2" ] || [ "$current_path" != "$mount_point" ] || [ -r "$current_path/memory.max" ] || [ -r "$current_path/memory.high" ]; then
                return 1
            fi
        fi

        [ "$current_path" = "$mount_point" ] && break
        current_path=${current_path%/*}
        [[ "$current_path" == "$mount_point" || "$current_path" == "$mount_point/"* ]] || return 1
    done

    (( saw_memory == 1 )) || return 1
    printf '%s\n' "$available_bytes"
}

configure_net10_gc() {
    local gc_mode=${SMAPI_GC_MODE:-auto}
    local cpu_count available_memory
    local minimum_memory=$((16 * 1024 * 1024 * 1024))

    case "$gc_mode" in
        auto|workstation|server4)
            ;;
        *)
            printf "Invalid SMAPI_GC_MODE value '%s'. Expected 'auto', 'workstation', or 'server4'.\n" "$gc_mode" >&2
            return 64
            ;;
    esac

    # Native runtime settings always take precedence over SMAPI's policy, including a partial override.
    if [ -n "${DOTNET_gcServer+x}" ] || [ -n "${COMPlus_gcServer+x}" ] || [ -n "${DOTNET_GCHeapCount+x}" ] || [ -n "${COMPlus_GCHeapCount+x}" ]; then
        return 0
    fi

    if [ "$gc_mode" = "auto" ]; then
        # A user-specified processor count is part of runtime policy, so don't layer an automatic GC choice over it.
        if [ -n "${DOTNET_PROCESSOR_COUNT+x}" ] || [ -n "${DOTNET_ProcessorCount+x}" ] || [ -n "${COMPlus_PROCESSOR_COUNT+x}" ] || [ -n "${COMPlus_ProcessorCount+x}" ]; then
            return 0
        fi
        cpu_count=$(get_effective_cpu_count) || cpu_count=0
        available_memory=$(get_effective_available_memory) || available_memory=0
        if (( cpu_count < 8 || available_memory < minimum_memory )); then
            gc_mode=workstation
        else
            gc_mode=server4
        fi
    fi

    if [ "$gc_mode" = "server4" ]; then
        export DOTNET_gcServer=1
        export DOTNET_GCHeapCount=4
    else
        export DOTNET_gcServer=0
    fi
}

runtime=${SMAPI_DOTNET_RUNTIME:-auto}
case "$runtime" in
    auto)
        runtime=net6
        ;;
    net6|net10)
        ;;
    *)
        printf "Invalid SMAPI_DOTNET_RUNTIME value '%s'. Expected 'auto', 'net6', or 'net10'.\n" "$runtime" >&2
        exit 64
        ;;
esac

tool_version_contains() {
    local tool_name=$1 expected=$2 version
    command -v "$tool_name" >/dev/null 2>&1 || return 1
    version=$(LC_ALL=C "$tool_name" --version 2>/dev/null) || return 1
    [[ "$version" == *"$expected"* ]]
}

# The validation below intentionally uses exact GNU stat, cmp, and timeout behavior. Probe it
# explicitly so a minimal or non-GNU userland gets one understandable error instead of a misleading
# file error. GNU cmp is shipped by diffutils; GNU stat and timeout are shipped by coreutils.
if ! tool_version_contains stat "GNU coreutils" \
    || ! stat --printf='' -- "${BASH_SOURCE[0]}" >/dev/null 2>&1; then
    printf '%s\n' "SMAPI can't launch because GNU coreutils stat is required. Install GNU coreutils and try again." >&2
    exit 1
fi
if [ "$runtime" = "net6" ] && {
    ! tool_version_contains cmp "GNU diffutils" \
        || ! tool_version_contains timeout "GNU coreutils" \
        || ! timeout --kill-after=1s 1s cmp -s -- /dev/null /dev/null
}; then
    printf '%s\n' "SMAPI can't launch with the game's .NET runtime because GNU cmp and coreutils timeout are required. Install GNU diffutils and coreutils, then try again." >&2
    exit 1
fi

if [ "$runtime" = "net10" ]; then
    configure_net10_gc || exit $?
fi

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P) || exit $?

# Read one bounded, unique regular-file identity without following a symbolic-link leaf. Rechecking
# detects ordinary changes observed during validation, but these pathname checks aren't a race-free
# guarantee against adversarial same-user replacement between validation and exec. This dispatcher
# never repairs files: the installer owns all mutation, journaling, and rollback behavior.
inspect_unique_regular_file() {
    local path=$1 minimum_size=$2 maximum_size=$3 require_executable=$4
    local metadata file_type link_count size_bytes identity remainder

    [ ! -L "$path" ] || return 1
    metadata=$(LC_ALL=C stat --printf='%F\n%h\n%s\n%f:%d:%i:%h:%s:%y:%z' -- "$path" 2>/dev/null) || return 1
    file_type=${metadata%%$'\n'*}
    remainder=${metadata#*$'\n'}
    link_count=${remainder%%$'\n'*}
    remainder=${remainder#*$'\n'}
    size_bytes=${remainder%%$'\n'*}
    identity=${remainder#*$'\n'}

    [ "$file_type" = "regular file" ] || return 1
    [ "$link_count" = "1" ] || return 1
    [[ "$size_bytes" =~ ^[0-9]+$ ]] || return 1
    (( size_bytes >= minimum_size && size_bytes <= maximum_size )) || return 1
    if [ "$require_executable" = "yes" ] && [ ! -x "$path" ]; then
        return 1
    fi
    printf '%s\n' "$identity"
}

print_dependency_repair_guidance() {
    printf '%s\n' "SMAPI can't safely launch with the game's .NET runtime because dependency metadata is missing, unsafe, or out of date." >&2
    printf '%s\n' "Re-run \"install on Linux.sh\" from the same verified installer package and choose Install, then try again." >&2
}

# The game-runtime host needs an exact installer-owned copy of the game's deps file before CoreCLR
# starts. Only the transactional installer may create or repair that copy.
if [ "$runtime" = "net6" ]; then
    source_deps="$script_dir/Stardew Valley.deps.json"
    target_deps="$script_dir/StardewModdingAPI-net6.deps.json"
    maximum_deps_bytes=$((16 * 1024 * 1024))
    source_identity=$(inspect_unique_regular_file "$source_deps" 1 "$maximum_deps_bytes" no) || {
        print_dependency_repair_guidance
        exit 1
    }
    target_identity=$(inspect_unique_regular_file "$target_deps" 1 "$maximum_deps_bytes" no) || {
        print_dependency_repair_guidance
        exit 1
    }
    if [ "${source_identity%%:*}" != "${target_identity%%:*}" ] \
        || ! { timeout --kill-after=1s 5s cmp -s -- "$source_deps" "$target_deps"; } 2>/dev/null; then
        print_dependency_repair_guidance
        exit 1
    fi
    if [ "$(inspect_unique_regular_file "$source_deps" 1 "$maximum_deps_bytes" no)" != "$source_identity" ] \
        || [ "$(inspect_unique_regular_file "$target_deps" 1 "$maximum_deps_bytes" no)" != "$target_identity" ]; then
        print_dependency_repair_guidance
        exit 1
    fi
fi

host_path="$script_dir/StardewModdingAPI-$runtime"
host_identity=$(inspect_unique_regular_file "$host_path" 1 9223372036854775807 yes) || {
    printf "SMAPI's %s runtime host is missing or isn't executable: %s\n" "$runtime" "$host_path" >&2
    exit 1
}
if [ "$(inspect_unique_regular_file "$host_path" 1 9223372036854775807 yes)" != "$host_identity" ]; then
    printf "SMAPI's %s runtime host changed during launch validation: %s\n" "$runtime" "$host_path" >&2
    exit 1
fi

exec "$host_path" "$@"
