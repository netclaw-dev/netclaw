#!/usr/bin/env bash
set -euo pipefail

# Focused gate for the pure decisions of a parked child run (ChildParkRules). See TOOLING.md, "Child Resume Gate".

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
relative="SubAgents/ChildRunPark.cs"
source_file="$repo_root/src/Netclaw.Actors/$relative"
# The drain acknowledgement wait of running-child-drain-park (SessionExecutionLifetime.WaitsForChildren).
wait_relative="Sessions/Handlers/SessionExecutionLifetime.cs"
wait_file="$repo_root/src/Netclaw.Actors/$wait_relative"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/child-resume-gate}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Prints the character span and the line span between the two markers of one name in one file.
span_of() {
  MARKER="$1" perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "// <$ENV{MARKER}>";
    $end_marker = "// </$ENV{MARKER}>";
    $start = index($_, $start_marker);
    die "The $ENV{MARKER} start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The $ENV{MARKER} end marker is missing or duplicated.\n"
      if $end < 0 || index($_, $end_marker, $end + 1) >= 0;
    $first = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $last = 1 + (substr($_, 0, $end) =~ tr/\n//);
    print "$start $end $first $last\n";
  ' "$2"
}

read -r span_start span_end first_line last_line < <(span_of child-resume-gate "$source_file")
read -r wait_start wait_end wait_first wait_last < <(span_of child-drain-wait "$wait_file")

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Actors.csproj \
    --mutate "$relative{$span_start..$span_end}" \
    --mutate "$wait_relative{$wait_start..$wait_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
expected_count=55
jq -e --arg source "$source_file" --arg wait "$wait_file" \
  --argjson first "$first_line" --argjson last "$last_line" \
  --argjson wfirst "$wait_first" --argjson wlast "$wait_last" \
  --argjson count "$expected_count" '
  ([(.files[$source].mutants // [])[]
    | select(.status != "Ignored" and .status != "CompileError")
    | select(.location.start.line >= $first and .location.start.line <= $last)]
   + [(.files[$wait].mutants // [])[]
    | select(.status != "Ignored" and .status != "CompileError")
    | select(.location.start.line >= $wfirst and .location.start.line <= $wlast)]) as $mutants
  | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
  and ([.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length) == $count
' "$report" > /dev/null || {
  echo "Expected $expected_count killed child-resume-gate mutants in $source_file and $wait_file." >&2
  jq -r '.files | to_entries[] | .key as $file | .value.mutants[]
    | "\(.status) \($file | split("/") | last) line \(.location.start.line): \(.mutatorName) \(.replacement // "")"' \
    "$report" >&2 || true
  exit 1
}
