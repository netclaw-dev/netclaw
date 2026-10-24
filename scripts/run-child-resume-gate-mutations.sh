#!/usr/bin/env bash
set -euo pipefail

# Focused gate for the pure decisions of a parked child run (ChildParkRules). See TOOLING.md, "Child Resume Gate".

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
relative="SubAgents/ChildRunPark.cs"
source_file="$repo_root/src/Netclaw.Actors/$relative"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/child-resume-gate}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

read -r span_start span_end first_line last_line < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "// <child-resume-gate>";
    $end_marker = "// </child-resume-gate>";
    $start = index($_, $start_marker);
    die "The child-resume-gate start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The child-resume-gate end marker is missing or duplicated.\n"
      if $end < 0 || index($_, $end_marker, $end + 1) >= 0;
    $first = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $last = 1 + (substr($_, 0, $end) =~ tr/\n//);
    print "$start $end $first $last\n";
  ' "$source_file"
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Actors.csproj \
    --mutate "$relative{$span_start..$span_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
expected_count=44
jq -e --arg source "$source_file" \
  --argjson first "$first_line" --argjson last "$last_line" \
  --argjson count "$expected_count" '
  [(.files[$source].mutants // [])[]
    | select(.status != "Ignored" and .status != "CompileError")
    | select(.location.start.line >= $first and .location.start.line <= $last)] as $mutants
  | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
  and ([.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length) == $count
' "$report" > /dev/null || {
  echo "Expected $expected_count killed child-resume-gate mutants in $source_file." >&2
  jq -r '.files[].mutants[] | "\(.status) line \(.location.start.line): \(.mutatorName) \(.replacement // "")"' \
    "$report" >&2 || true
  exit 1
}
