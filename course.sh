#!/usr/bin/env bash
set -euo pipefail
export MSYS2_ARG_CONV_EXCL="*"
export MSYS_NO_PATHCONV=1
host_dir="$(pwd -W 2>/dev/null || pwd)"
exec docker compose run --rm -T -v "$host_dir/task/week2/autocheck/fixtures:/autocheck/input:ro" cli "$@"