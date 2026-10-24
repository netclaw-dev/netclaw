#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Cli/Daemon/ChatClientActor.cs"
test_project="$repo_root/src/Netclaw.Cli.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/chat-close-dispatch-expiry}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# The span selects the elapsed-time comparison within the Start guard.
read -r span_start span_end first_line first_column last_line last_column < <(
  perl -MEncode=encode -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "// chat-close-dispatch-expiry:begin";
    $end_marker = "// chat-close-dispatch-expiry:end";
    $start = index($_, $start_marker);
    die "The chat close expiry start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker);
    die "The chat close expiry end marker is missing, duplicated, or before the start.\n"
      if $end <= $start || index($_, $end_marker, $end + 1) >= 0;
    $comparison = "_clock.GetElapsedTime(close.StartedAt) >= TimeSpan.FromSeconds(2)";
    $comparison_start = index($_, $comparison, $start);
    die "The elapsed-time comparison is missing or duplicated in the guard.\n"
      if $comparison_start < 0 || $comparison_start >= $end
      || (index($_, $comparison, $comparison_start + 1) >= 0
          && index($_, $comparison, $comparison_start + 1) < $end);
    $comparison_end = $comparison_start + length($comparison);
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
    --project Netclaw.Cli.csproj \
    --mutate "Daemon/ChatClientActor.cs{$span_start..$span_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
expected_count=2
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

# Stryker also records ignored and compile-error mutants outside the selected comparison.
# Every target mutant must run and die. No other active mutant can satisfy this gate.
if [[ "$target_count" -ne "$expected_count" || "$killed_count" -ne "$expected_count" \
      || "$unexpected_count" -ne 0 ]]; then
  echo "Expected $expected_count killed chat close expiry comparison mutants." >&2
  echo "Found $killed_count killed from $target_count targets; unexpected active mutants=$unexpected_count." >&2
  exit 1
fi
