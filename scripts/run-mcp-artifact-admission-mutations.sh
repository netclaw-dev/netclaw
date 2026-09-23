#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Daemon/Mcp/McpArtifactMaterializer.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/mcp-artifact-admission}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

read -r span_start span_end approval_line verified_line < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "if (!scan.IsAllowed)";
    $end_marker = "if (!scan.VerifiedMimeType.HasValue)\n            return false;";
    $start = index($_, $start_marker);
    die "The approval marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end_start = index($_, $end_marker, $start);
    die "The verified-MIME marker is missing or duplicated.\n"
      if $end_start < 0 || index($_, $end_marker, $end_start + 1) >= 0;
    $approval_line = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $verified_line = 1 + (substr($_, 0, $end_start) =~ tr/\n//);
    print "$start ", $end_start + length($end_marker), " $approval_line $verified_line\n";
  ' "$source_file"
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Daemon.csproj \
    --mutate "Mcp/McpArtifactMaterializer.cs{$span_start..$span_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
tested_count="$(
  jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report"
)"
killed_count="$(jq '[.files[].mutants[] | select(.status == "Killed")] | length' "$report")"
approval_killed="$(
  jq --argjson line "$approval_line" \
    '[.files[].mutants[] | select(.status == "Killed" and .location.start.line >= $line and .location.start.line <= ($line + 1))] | length' \
    "$report"
)"
verified_killed="$(
  jq --argjson line "$verified_line" \
    '[.files[].mutants[] | select(.status == "Killed" and .location.start.line >= $line and .location.start.line <= ($line + 1))] | length' \
    "$report"
)"

if [[ "$tested_count" -ne 4 || "$killed_count" -ne 4 \
      || "$approval_killed" -ne 2 || "$verified_killed" -ne 2 ]]; then
  echo "Expected four killed MCP admission mutants: two approval and two verified-MIME mutants." >&2
  echo "Found $killed_count killed from $tested_count tested; approval=$approval_killed, verified-MIME=$verified_killed." >&2
  exit 1
fi
