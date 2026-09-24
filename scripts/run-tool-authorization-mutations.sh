#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Tools/ToolAccessPolicy.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/tool-authorization}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

resolve_unique_condition_span() {
  python3 - "$1" "$2" "$3" <<'PY'
from pathlib import Path
import sys

text = Path(sys.argv[1]).read_text(encoding="utf-8")
anchor, condition = sys.argv[2:]
anchor_start = text.find(anchor)
if anchor_start < 0 or text.find(anchor, anchor_start + 1) >= 0:
    raise SystemExit("A mutation boundary is missing or duplicated.")
start = text.index(condition, anchor_start, anchor_start + len(anchor))
print(start, start + len(condition), text.count("\n", 0, start) + 1)
PY
}

# Resolve each condition separately. Source drift must fail before Stryker starts.
spans="$(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    @markers = (
      "if (!_profileResolver.IsMcpServerAllowed(serverName, context.Invocation))",
      "if (!_profileResolver.IsMcpToolAllowed(\n                serverName,\n                new ToolName(tool.BareToolName),\n                context.Invocation))",
      "if (!hardDenyDecision.Allowed)"
    );
    for $marker (@markers) {
      $start = index($_, $marker);
      die "An authorization condition is missing or duplicated.\n"
        if $start < 0 || index($_, $marker, $start + 1) >= 0;
      $line = 1 + (substr($_, 0, $start) =~ tr/\n/\n/);
      print "$start ", $start + length($marker), " $line\n";
    }
  ' "$source_file"
)"

mutate_args=()
expected_lines=()
while read -r span_start span_end line; do
  mutate_args+=(--mutate "Tools/ToolAccessPolicy.cs{$span_start..$span_end}")
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
  jq -e --arg source "$source_file" --argjson line "$line" '
    [.files[$source].mutants[] | select(.status != "Ignored")
      | select(.location.start.line == $line)] as $mutants
    | ($mutants | length) == 1 and all($mutants[]; .status == "Killed")
  ' "$report" > /dev/null || {
    echo "Expected one killed authorization mutant at line $line." >&2
    exit 1
  }
done

# Stryker can report unrelated compile errors before it applies the span filter.
# Each target above still requires a killed mutant, including when its compilation fails.
jq -e '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length == 3' \
  "$report" > /dev/null || {
  echo "Expected exactly three authorization mutants." >&2
  exit 1
}

evidence_source="$repo_root/src/Netclaw.Actors/Tools/ToolApprovalMessages.cs"
read -r evidence_start evidence_end evidence_line < <(
  resolve_unique_condition_span \
    "$evidence_source" \
    "!SourceCandidate.Candidate.HasSameApprovalFacts(candidate.Candidate)" \
    "!SourceCandidate.Candidate.HasSameApprovalFacts(candidate.Candidate)"
)
transition_source="$repo_root/src/Netclaw.Actors/Tools/ShellPolicyEvaluation.cs"
read -r transition_start transition_end transition_line < <(
  resolve_unique_condition_span \
    "$transition_source" \
    $'Coverage != ShellCoverageKind.Uncovered\n                || GrantEvidence is not null' \
    "Coverage != ShellCoverageKind.Uncovered"
)

evidence_output="$output_path/evidence-facts"
(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --mutate "Tools/ToolApprovalMessages.cs{$evidence_start..$evidence_end}" \
    --mutate "Tools/ShellPolicyEvaluation.cs{$transition_start..$transition_end}" \
    --output "$evidence_output" \
    --skip-version-check
)

evidence_report="$evidence_output/reports/mutation-report.json"
jq -e --arg source "$evidence_source" --argjson line "$evidence_line" '
  [.files[$source].mutants[] | select(.status != "Ignored" and .status != "CompileError")
    | select(.location.start.line == $line)] as $mutants
  | ($mutants | length) == 1 and all($mutants[]; .status == "Killed")
' "$evidence_report" > /dev/null || {
  echo "Expected one killed shell evidence identity mutant." >&2
  exit 1
}
jq -e --arg source "$transition_source" --argjson line "$transition_line" '
  [.files[$source].mutants[] | select(.status != "Ignored" and .status != "CompileError")
    | select(.location.start.line == $line)] as $mutants
  | ($mutants | length) == 1 and all($mutants[]; .status == "Killed")
' "$evidence_report" > /dev/null || {
  echo "Expected one killed candidate transition mutant." >&2
  exit 1
}
