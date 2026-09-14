#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Security/ApprovalPatternMatching.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/approval-directory}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Resolve each boundary separately. Source drift must fail before Stryker starts.
spans="$(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    @markers = (
      "!IsWithinWindowsRoot(normalizedCandidate, normalizedRoot)",
      "if (!PathUtility.IsNormalizedWithinRoot(normalizedCandidate, entry.Directory))\n                return ShellApprovalScopeResult.OutsideDirectory;",
      "return PathUtility.ContainsSymlinkSegment(entry.Directory, effectiveDirectory)\n                ? ShellApprovalScopeResult.Symlink\n                : ShellApprovalScopeResult.Match;"
    );
    @counts = (1, 1, 2);
    for $i (0 .. $#markers) {
      $marker = $markers[$i];
      $start = index($_, $marker);
      die "An approval boundary is missing or duplicated.\n"
        if $start < 0 || index($_, $marker, $start + 1) >= 0;
      $line = 1 + (substr($_, 0, $start) =~ tr/\n/\n/);
      print "$start ", $start + length($marker), " $line $counts[$i]\n";
    }
  ' "$source_file"
)"

mutate_args=()
while read -r span_start span_end _line _count; do
  mutate_args+=(--mutate "ApprovalPatternMatching.cs{$span_start..$span_end}")
done <<< "$spans"

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Security.csproj \
    "${mutate_args[@]}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
while read -r _span_start _span_end line count; do
  jq -e --arg source "$source_file" --argjson line "$line" --argjson count "$count" '
    [.files[$source].mutants[] | select(.status != "Ignored")
      | select(.location.start.line == $line)] as $mutants
    | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
  ' "$report" > /dev/null || {
    echo "Expected $count killed approval directory mutants at line $line." >&2
    exit 1
  }
done <<< "$spans"

# Each target above must die even if Stryker reports unrelated compiler errors.
jq -e '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length == 4' \
  "$report" > /dev/null || {
  echo "Expected exactly four approval directory mutants." >&2
  exit 1
}
