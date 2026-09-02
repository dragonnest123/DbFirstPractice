#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

exec python3 "$script_dir/task/week2/autocheck/public_check.py" \
  --repo "$script_dir" \
  --fixtures "$script_dir/task/week2/autocheck/fixtures" \
  --compose-wrapper "$script_dir/compose-wrapper.sh" \
  --output "$script_dir/week-2-public-report.json" \
  "$@"