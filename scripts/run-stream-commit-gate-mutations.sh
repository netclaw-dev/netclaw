#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Daemon/Configuration/StreamCommitGate.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/stream-commit-gate}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

read -r span_start span_end first_line last_line < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "if (StreamProgress.IsSubstantive(update))";
    $end_marker = "    /// <summary>\n    /// A content-free update that carries nothing a consumer would fold into the response.";
    $start = index($_, $start_marker);
    die "The stream-commit start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The stream-commit end marker is missing or duplicated.\n"
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
    --mutate "Configuration/StreamCommitGate.cs{$span_start..$span_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
expected_count=8
tested_count="$(
  jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report"
)"
killed_count="$(jq '[.files[].mutants[] | select(.status == "Killed")] | length' "$report")"
span_mutants="$(
  jq --arg source "$repo_root/src/Netclaw.Daemon/Configuration/StreamCommitGate.cs" \
    --argjson first "$first_line" --argjson last "$last_line" \
    '[.files[$source].mutants[] | select(.status != "Ignored" and .status != "CompileError")
      | select(.location.start.line >= $first and .location.start.line < $last)] | length' \
    "$report"
)"

if [[ "$tested_count" -ne "$expected_count" || "$killed_count" -ne "$expected_count" \
      || "$span_mutants" -ne "$expected_count" ]]; then
  echo "Expected $expected_count killed stream-commit gate mutants." >&2
  echo "Found $killed_count killed from $tested_count tested; span=$span_mutants." >&2
  exit 1
fi
