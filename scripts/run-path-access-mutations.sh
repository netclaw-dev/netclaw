#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Tools/PathAccessPolicy.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/path-access-policy}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

read -r span_start span_end < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "if (context.Audience == TrustAudience.Personal)";
    $end_marker = "roots.AddRange(_sessionRoots);";
    $start = index($_, $start_marker);
    die "The start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end_start = index($_, $end_marker, $start);
    die "The end marker is missing or duplicated.\n"
      if $end_start < 0 || index($_, $end_marker, $end_start + 1) >= 0;
    print "$start ", $end_start + length($end_marker), "\n";
  ' "$source_file"
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --mutate "Tools/PathAccessPolicy.cs{$span_start..$span_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
tested_count="$(
  jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report"
)"
killed_count="$(jq '[.files[].mutants[] | select(.status == "Killed")] | length' "$report")"

if [[ "$tested_count" -ne 2 || "$killed_count" -ne 2 ]]; then
  echo "Expected two killed path-access mutants. Found $killed_count killed from $tested_count tested." >&2
  exit 1
fi
