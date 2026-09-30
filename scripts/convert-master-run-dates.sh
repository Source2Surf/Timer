#!/usr/bin/env sh
set -eu

if [ "$#" -ne 1 ] || [ "$1" != "--backup-confirmed" ]; then
    printf '%s\n' 'Stop every writer and verify a restorable database backup first.' >&2
    printf '%s\n' 'Then re-run: sh scripts/convert-master-run-dates.sh --backup-confirmed' >&2
    exit 2
fi

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
project_path="$script_dir/../Backend/Timer.Backend/Timer.Backend.csproj"

if [ ! -f "$project_path" ]; then
    printf '%s\n' "Timer.Backend project not found at $project_path" >&2
    exit 1
fi

# This wrapper intentionally has no database-credential parameters. Supply the
# temporary migration account through an environment-specific configuration source.
exec dotnet run --no-build --no-restore --configuration Release --project "$project_path" -- convert-run-dates --backup-confirmed
