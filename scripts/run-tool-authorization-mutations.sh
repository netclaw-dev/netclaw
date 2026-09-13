#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Tools/ToolAccessPolicy.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/tool-authorization}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Resolve each condition separately. Source drift must fail before Stryker starts.
spans="$(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    @markers = (
      "if (!_profileResolver.IsMcpServerAllowed(serverName, context.Invocation))",
      "if (!_profileResolver.IsMcpToolAllowed(\n                serverName,\n                new ToolName(tool.BareToolName),\n                context.Invocation))",
      "if (!hardDenyDecision.Allowed)"
    );
    for $marker (@markers) {
      $start = index($_, $marker);
      die "An authorization condition is missing or duplicated.\n"
        if $start < 0 || index($_, $marker, $start + 1) >= 0;
      $line = 1 + (substr($_, 0, $start) =~ tr/\n/\n/);
      print "$start ", $start + length($marker), " $line\n";
    }
  ' "$source_file"
)"

mutate_args=()
expected_lines=()
while read -r span_start span_end line; do
  mutate_args+=(--mutate "Tools/ToolAccessPolicy.cs{$span_start..$span_end}")
  expected_lines+=("$line")
done <<< "$spans"

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    "${mutate_args[@]}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
for line in "${expected_lines[@]}"; do
  jq -e --arg source "$source_file" --argjson line "$line" '
    [.files[$source].mutants[] | select(.status != "Ignored")
      | select(.location.start.line == $line)] as $mutants
    | ($mutants | length) == 1 and all($mutants[]; .status == "Killed")
  ' "$report" > /dev/null || {
    echo "Expected one killed authorization mutant at line $line." >&2
    exit 1
  }
done

# Stryker can report unrelated compile errors before it applies the span filter.
# Each target above still requires a killed mutant, including when its compilation fails.
jq -e '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length == 3' \
  "$report" > /dev/null || {
  echo "Expected exactly three authorization mutants." >&2
  exit 1
}
