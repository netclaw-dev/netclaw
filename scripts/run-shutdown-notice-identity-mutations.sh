#!/usr/bin/env bash
set -euo pipefail

# Mutation gate for #2527 rule 1: the identity check of POST /api/lifecycle/shutdown.
# The check sits between the markers `// shutdown-identity:begin` and `// shutdown-identity:end`
# in LifecycleEndpointRouteBuilderExtensions.cs. The span holds the parse of the PID, the parse
# of the home, and the comparison of both with the PID and the home of this daemon.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Daemon/Lifecycle/LifecycleEndpointRouteBuilderExtensions.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/shutdown-notice-identity}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

read -r span_start span_end first_line last_line < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "// shutdown-identity:begin";
    $end_marker = "// shutdown-identity:end";
    $start = index($_, $start_marker);
    die "The shutdown-identity begin marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The shutdown-identity end marker is missing or duplicated.\n"
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
    --project Netclaw.Daemon.csproj \
    --mutate "Lifecycle/LifecycleEndpointRouteBuilderExtensions.cs{$span_start..$span_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
minimum_count=8
tested_count="$(
  jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report"
)"
killed_count="$(jq '[.files[].mutants[] | select(.status == "Killed")] | length' "$report")"
span_mutants="$(
  jq --arg source "$source_file" \
    --argjson first "$first_line" --argjson last "$last_line" \
    '[(.files[$source].mutants // [])[] | select(.status != "Ignored" and .status != "CompileError")
      | select(.location.start.line >= $first and .location.start.line < $last)] | length' \
    "$report"
)"

# Every tested mutant must die, and the span must hold at least the mutants that the check needs:
# PID parse and range, home parse, the PID comparison, and the home comparison.
if [[ "$tested_count" -lt "$minimum_count" || "$killed_count" -ne "$tested_count" \
      || "$span_mutants" -ne "$tested_count" ]]; then
  echo "Expected at least $minimum_count mutants in the shutdown identity check, and all killed." >&2
  echo "Found $killed_count killed from $tested_count tested; span=$span_mutants." >&2
  exit 1
fi
