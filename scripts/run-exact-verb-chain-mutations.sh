#!/usr/bin/env bash
set -euo pipefail

# A shell grant covers exactly its verb chain. Both mutants of the length
# equality check must die. The "==" mutant restores prefix matching for a
# longer candidate, so a "gh" grant would cover "gh auth logout".
# The digit rule ends a chain at the first word with a digit. Each mutant of
# that loop changes which words are the chain, so each must die. Removal of
# "length++" loops forever; Stryker reports that mutant as Timeout (detected).
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Security/ApprovalPatternMatching.cs"
digit_file="$repo_root/src/Netclaw.Security/IToolApprovalMatcher.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/exact-verb-chain}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi
expected_mutants=10

find_span() {
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -e '
    my ($marker, $file) = @ARGV;
    local $/;
    open(my $fh, "<", $file) or die "Cannot read $file\n";
    my $text = <$fh>;
    my $start = index($text, $marker);
    die "A target is missing or duplicated: $marker\n"
      if $start < 0 || index($text, $marker, $start + 1) >= 0;
    print "$start ", $start + length($marker), "\n";
  ' "$1" "$2"
}

read -r span_start span_end < <(
  find_span $'if (grantLength != candidateLength)\n            return false;' "$source_file")
read -r digit_start digit_end < <(
  find_span $'while (length < count && !parserTokens[length].Any(char.IsAsciiDigit))\n            length++;' "$digit_file")

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Security.csproj \
    --mutate "ApprovalPatternMatching.cs{$span_start..$span_end}" \
    --mutate "IToolApprovalMatcher.cs{$digit_start..$digit_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
tested_count="$(
  jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report"
)"
killed_count="$(jq '[.files[].mutants[] | select(.status == "Killed" or .status == "Timeout")] | length' "$report")"

if [[ "$tested_count" -ne "$expected_mutants" || "$killed_count" -ne "$expected_mutants" ]]; then
  echo "Expected $expected_mutants killed exact verb chain mutants. Found $killed_count killed from $tested_count tested." >&2
  exit 1
fi
