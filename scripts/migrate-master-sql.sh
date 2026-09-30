#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
project_path="$script_dir/../Backend/Timer.Backend/Timer.Backend.csproj"

if [ ! -f "$project_path" ]; then
    printf '%s\n' "Timer.Backend project not found at $project_path" >&2
    exit 1
fi

exec dotnet run --no-build --no-restore --configuration Release --project "$project_path" -- migrate
