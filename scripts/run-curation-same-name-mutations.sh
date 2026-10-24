#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Memory/CurationRulesEvaluator.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/curation-same-name}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Stryker 5 emits one equality mutant for this exact condition.
expected_count=1

# Select only the condition. Exclude the branch body and its reason string.
read -r span_start span_end first_line first_column last_line last_column < <(
  perl -MEncode=encode -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "// <curation-same-name-condition>";
    $end_marker = "// </curation-same-name-condition>";
    $start = index($_, $start_marker);
    die "The same-name start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker);
    die "The same-name end marker is missing, duplicated, or before the start.\n"
      if $end <= $start || index($_, $end_marker, $end + 1) >= 0;
    $comparison = "sameNameMatch is not null";
    $comparison_start = index($_, $comparison, $start);
    $comparison_end = $comparison_start + length($comparison);
    die "The same-name condition is missing or duplicated within the markers.\n"
      if $comparison_start < 0 || $comparison_end > $end
      || (index($_, $comparison, $comparison_start + 1) >= 0
          && index($_, $comparison, $comparison_start + 1) < $end);
    $before = substr($_, 0, $comparison_start);
    $through = substr($_, 0, $comparison_end);
    $first = 1 + ($before =~ tr/\n//);
    $last = 1 + ($through =~ tr/\n//);
    $first_column = 1 + length(encode("UTF-16LE", substr($before, rindex($before, "\n") + 1))) / 2;
    $last_column = 1 + length(encode("UTF-16LE", substr($through, rindex($through, "\n") + 1))) / 2;
    $span_start = length(encode("UTF-16LE", $before)) / 2;
    $span_end = length(encode("UTF-16LE", $through)) / 2;
    print "$span_start $span_end $first $first_column $last $last_column\n";
  ' "$source_file"
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Actors.csproj \
    --mutate "Memory/CurationRulesEvaluator.cs{$span_start..$span_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
read -r target_count killed_count unexpected_count < <(
  jq -r --arg source "$source_file" \
    --argjson first "$first_line" --argjson first_column "$first_column" \
    --argjson last "$last_line" --argjson last_column "$last_column" '
      def within_target:
        (.location.start.line > $first or
          (.location.start.line == $first and .location.start.column >= $first_column)) and
        (.location.end.line < $last or
          (.location.end.line == $last and .location.end.column <= $last_column));
      [(.files[$source].mutants // [])[] | select(within_target)] as $target |
      [.files | to_entries[] | .key as $path | .value.mutants[] |
        select(.status != "Ignored" and .status != "CompileError") |
        select($path != $source or (within_target | not))] as $unexpected |
      [($target | length), ($target | map(select(.status == "Killed")) | length),
        ($unexpected | length)] | @tsv
    ' "$report"
)

# Every target must run and die. Ignore only inactive mutants outside the condition.
if [[ "$expected_count" -le 0 || "$target_count" -ne "$expected_count" \
      || "$killed_count" -ne "$expected_count" || "$unexpected_count" -ne 0 ]]; then
  echo "Expected exactly $expected_count killed same-name condition mutants." >&2
  echo "Found $killed_count killed from $target_count targets; unexpected active mutants=$unexpected_count." >&2
  exit 1
fi
