#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Cli/HeadlessChannel.cs"
test_project="$repo_root/src/Netclaw.Cli.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/headless-stop-rule}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# The span is the stop decision between two marker comments in HeadlessChannel.cs.
read -r span_start span_end first_line last_line < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "// <headless-stop-rule>";
    $end_marker = "// </headless-stop-rule>";
    $start = index($_, $start_marker);
    die "The headless stop rule start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The headless stop rule end marker is missing or duplicated.\n"
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
    --project Netclaw.Cli.csproj \
    --mutate "HeadlessChannel.cs{$span_start..$span_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
expected_count=3
tested_count="$(
  jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report"
)"
# A mutant that makes the run wait forever hits the fault ceiling of the tests. Stryker reports it as a timeout.
# That is a detection, and it is the failure that the rule exists to prevent.
detected_count="$(
  jq '[.files[].mutants[] | select(.status == "Killed" or .status == "Timeout")] | length' "$report"
)"
span_mutants="$(
  jq --arg source "$source_file" \
    --argjson first "$first_line" --argjson last "$last_line" \
    '[(.files[$source].mutants // [])[] | select(.status != "Ignored" and .status != "CompileError")
      | select(.location.start.line >= $first and .location.start.line < $last)] | length' \
    "$report"
)"

if [[ "$tested_count" -ne "$expected_count" || "$detected_count" -ne "$expected_count" \
      || "$span_mutants" -ne "$expected_count" ]]; then
  echo "Expected $expected_count detected headless stop rule mutants." >&2
  echo "Found $detected_count detected from $tested_count tested; span=$span_mutants." >&2
  exit 1
fi
