#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Configuration/SecretsFileWriter.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/secrets-plaintext-leaf}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# The span is the plaintext-leaf policy between two marker comments in SecretsFileWriter.cs.
read -r span_start span_end first_line last_line < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "// <secrets-plaintext-leaf>";
    $end_marker = "// </secrets-plaintext-leaf>";
    $start = index($_, $start_marker);
    die "The secrets plaintext leaf start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The secrets plaintext leaf end marker is missing or duplicated.\n"
      if $end < 0 || index($_, $end_marker, $end + 1) >= 0;
    $first = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $last = 1 + (substr($_, 0, $end) =~ tr/\n//);
    print "$start $end $first $last\n";
  ' "$source_file"
)

read -r optional_start optional_end < <(
  perl -0777 -ne '
    $expression = "new FileInfo(file).LinkTarget is null";
    $start = index($_, $expression);
    die "The optional configuration expression is missing or duplicated.\n"
      if $start < 0 || index($_, $expression, $start + 1) >= 0;
    print "$start ", $start + length($expression), "\n";
  ' "$repo_root/src/Netclaw.Configuration/DaemonConfigurationSources.cs"
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Configuration.csproj \
    --mutate "SecretsFileWriter.cs{$span_start..$span_end}" \
    --mutate "DaemonConfigurationSources.cs{$optional_start..$optional_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
expected_count=9
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
optional_mutants="$(
  jq --arg source "$repo_root/src/Netclaw.Configuration/DaemonConfigurationSources.cs" \
    '[(.files[$source].mutants // [])[]
      | select(.mutatorName == "Equality mutation"
          and .replacement == "new FileInfo(file).LinkTarget is not null"
          and .status == "Killed")] | length' "$report"
)"

if [[ "$tested_count" -ne "$expected_count" || "$killed_count" -ne "$expected_count" \
      || "$span_mutants" -ne 8 || "$optional_mutants" -ne 1 ]]; then
  echo "Expected eight killed secrets mutants and the killed optional-file inversion." >&2
  echo "Found $killed_count killed from $tested_count tested; secrets=$span_mutants; optional=$optional_mutants." >&2
  exit 1
fi
