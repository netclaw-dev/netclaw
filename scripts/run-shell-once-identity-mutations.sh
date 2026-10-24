#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Tools/OneTimeApprovalKeys.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/shell-once-identity}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

read -r span_start span_end target_line < <(
  python3 - "$source_file" <<'PY'
from pathlib import Path
import sys

text = Path(sys.argv[1]).read_text(encoding="utf-8")
anchor = "if (shellSourceKey is not null)"
start = text.find(anchor)
if start < 0 or text.find(anchor, start + 1) >= 0:
    raise SystemExit("The shell source identity mutation boundary is missing or duplicated.")
print(start, start + len(anchor), text.count("\n", 0, start) + 1)
PY
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --mutate "Tools/OneTimeApprovalKeys.cs{$span_start..$span_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
jq -e --arg source "$source_file" --argjson line "$target_line" '
  [.files[$source].mutants[] | select(.status != "Ignored")
    | select(.location.start.line == $line)] as $mutants
  | ($mutants | length) == 1 and all($mutants[]; .status == "Killed")
' "$report" > /dev/null || {
  echo "Expected one killed shell Once source identity mutant at line $target_line." >&2
  exit 1
}

jq -e '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length == 1' \
  "$report" > /dev/null || {
  echo "Expected exactly one shell Once source identity mutant." >&2
  exit 1
}
