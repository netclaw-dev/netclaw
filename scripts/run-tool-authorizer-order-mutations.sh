#!/usr/bin/env bash
set -euo pipefail

# Proves the rule order of ToolAuthorizer. Each selected line is one rule in the
# shell rule list. Stryker turns "??=" into "=": the rule then runs after an
# earlier decision and replaces it, so the rule moves ahead of every earlier
# rule. Each mutant must fail ToolAuthorizerOrderMutationTests.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Authorization/ToolAuthorizer.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/tool-authorizer-order}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Each marker is one rule line. A missing or duplicated line fails before Stryker starts.
markers=(
  "decision ??= HardDeny(call);"
  "decision ??= TrustedRoot(call);"
  "decision ??= await CoveringGrantAsync(call, ct);"
)
expected_per_line=1
expected_total=$(( ${#markers[@]} * expected_per_line ))

spans="$(
  python3 - "$source_file" "${markers[@]}" <<'PY'
from pathlib import Path
import sys

text = Path(sys.argv[1]).read_text(encoding="utf-8")
for marker in sys.argv[2:]:
    start = text.find(marker)
    if start < 0 or text.find(marker, start + 1) >= 0:
        raise SystemExit(f"A rule line is missing or duplicated: {marker}")
    print(start, start + len(marker), text.count("\n", 0, start) + 1)
PY
)"

mutate_args=()
expected_lines=()
while read -r span_start span_end line; do
  mutate_args+=(--mutate "Authorization/ToolAuthorizer.cs{$span_start..$span_end}")
  expected_lines+=("$line")
done <<< "$spans"

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    "${mutate_args[@]}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
for line in "${expected_lines[@]}"; do
  jq -e --arg source "$source_file" --argjson line "$line" --argjson expected "$expected_per_line" '
    [.files[$source].mutants[] | select(.status != "Ignored")
      | select(.location.start.line == $line)] as $mutants
    | ($mutants | length) == $expected and all($mutants[]; .status == "Killed")
  ' "$report" > /dev/null || {
    echo "Expected $expected_per_line killed rule-order mutants at line $line." >&2
    exit 1
  }
done

# Stryker can report unrelated compile errors before it applies the span filter.
# Each rule line above still requires its killed mutants.
jq -e --argjson expected "$expected_total" \
  '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length == $expected' \
  "$report" > /dev/null || {
  echo "Expected exactly $expected_total rule-order mutants." >&2
  exit 1
}
