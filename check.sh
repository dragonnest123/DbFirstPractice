#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

exec python3 "$script_dir/task/autocheck/public_check.py" \
  --repo "$script_dir" \
  --fixtures "$script_dir/task/autocheck/fixtures" \
  --output "$script_dir/week-1-public-report.json" \
  "$@"