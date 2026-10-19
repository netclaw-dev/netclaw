#!/usr/bin/env bash
set -euo pipefail

# Focused gate for the requester check that owns child status and cancel.
# Each mutant drops or inverts one requester field. TurnContextRequesterTests
# must kill every mutant: a stranger must not own a child, and a later turn of
# the same requester must keep it.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Channels/TurnContext.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/child-owner}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

read -r span_start span_end first_line last_line < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "internal bool HasSameRequester(TurnContext right)";
    $end_marker = "&& RequesterPrincipal == right.RequesterPrincipal;";
    $start = index($_, $start_marker);
    die "The requester check marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The requester check end marker is missing or duplicated.\n"
      if $end < 0 || index($_, $end_marker, $end + 1) >= 0;
    $end += length($end_marker);
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
    --mutate "Channels/TurnContext.cs{$span_start..$span_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
expected_count=11
jq -e --arg source "$source_file" \
  --argjson first "$first_line" --argjson last "$last_line" \
  --argjson count "$expected_count" '
  [(.files[$source].mutants // [])[]
    | select(.status != "Ignored")
    | select(.location.start.line >= $first and .location.start.line <= $last)] as $mutants
  | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
' "$report" > /dev/null || {
  echo "Expected $expected_count killed requester check mutants in the selected span." >&2
  jq -r --arg source "$source_file" \
    '(.files[$source].mutants // [])[] | "\(.status) line \(.location.start.line): \(.mutatorName) \(.replacement // "")"' \
    "$report" >&2 || true
  exit 1
}

jq -e --argjson count "$expected_count" '
  [.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")]
  | length == $count
' "$report" > /dev/null || {
  echo "Expected exactly $expected_count tested requester check mutants." >&2
  exit 1
}
