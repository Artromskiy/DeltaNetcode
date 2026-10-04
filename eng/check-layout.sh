#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
project_name="$(basename "$repo_root")"
required_directories=(
    src
    tests
    benchmarks
    samples
    probes
    playground
    tools
    adr
    docs
    eng
    artifacts
    assets
)
failed=0

for directory in "${required_directories[@]}"; do
    path="$repo_root/$directory"
    if [[ ! -d "$path" ]]; then
        printf 'layout: missing required directory: %s\n' "$directory" >&2
        failed=1
    fi
done

while IFS= read -r directory_path; do
    directory_name="$(basename "$directory_path")"
    case "$directory_name" in
        .github|.vscode|src|tests|benchmarks|samples|probes|playground|tools|adr|docs|eng|artifacts|assets)
            ;;
        *)
            printf 'layout: unexpected top-level directory: %s\n' "$directory_name" >&2
            failed=1
            ;;
    esac
done < <(
    find "$repo_root" -mindepth 1 -maxdepth 1 -type d \
        ! -name .git \
        ! -name bin \
        ! -name obj \
        -print | sort
)

primary_source="$repo_root/src/$project_name"
if [[ ! -d "$primary_source" ]]; then
    printf 'layout: missing primary source directory: src/%s\n' "$project_name" >&2
    failed=1
fi

source_root="$repo_root/src"
if [[ -d "$source_root" ]]; then
    while IFS= read -r source_path; do
        source_name="$(basename "$source_path")"
        case "$source_name" in
            "$project_name"|"$project_name".*)
                ;;
            *)
                printf 'layout: source directory must be %s or %s.<Area>: src/%s\n' \
                    "$project_name" "$project_name" "$source_name" >&2
                failed=1
                ;;
        esac
    done < <(find "$source_root" -mindepth 1 -maxdepth 1 -type d -print | sort)
fi

if (( failed != 0 )); then
    exit 1
fi

printf 'layout: %s is valid\n' "$project_name"
