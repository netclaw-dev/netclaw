#!/usr/bin/env bash
set -euo pipefail

# A shell grant covers exactly its command words. Both mutants of the length
# equality check must die. The "==" mutant restores prefix matching for a
# longer candidate, so a "gh" grant would cover "gh auth logout".
# Unknown command words get a rewrite correction. A mutant that drops the
# correction turns the call back into a prompt or a denial, so it must die.
# R1: a program path names its file. A mutant that skips the join with the
# working directory, or the "/" boundary of an older "./tool" grant, lets one
# grant run another file with that name, so it must die.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/exact-verb-chain}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

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

# Runs Stryker on one project span and requires every tested mutant to be detected.
run_gate() {
  local project="$1" mutate="$2" output="$3" expected="$4" name="$5"
  (
    cd "$test_project"
    dotnet stryker \
      --config-file stryker-config.json \
      --project "$project" \
      --mutate "$mutate" \
      --output "$output" \
      --skip-version-check
  )

  local report="$output/reports/mutation-report.json"
  local tested detected
  tested="$(jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report")"
  detected="$(jq '[.files[].mutants[] | select(.status == "Killed" or .status == "Timeout")] | length' "$report")"
  if [[ "$tested" -ne "$expected" || "$detected" -ne "$expected" ]]; then
    echo "Expected $expected detected $name mutants. Found $detected detected from $tested tested." >&2
    exit 1
  fi
}

matching_file="$repo_root/src/Netclaw.Security/ApprovalPatternMatching.cs"
read -r span_start span_end < <(
  find_span $'if (grantLength != candidateLength)\n            return false;' "$matching_file")
run_gate Netclaw.Security.csproj "ApprovalPatternMatching.cs{$span_start..$span_end}" \
  "$output_path/security" 2 "exact command words"

coordinator_file="$repo_root/src/Netclaw.Actors/Tools/ShellPolicyCoordinator.cs"
read -r correction_start correction_end < <(
  find_span 'return correction is null ? null : new ToolCorrectionCollection([correction]);' "$coordinator_file")
run_gate Netclaw.Actors.csproj "Tools/ShellPolicyCoordinator.cs{$correction_start..$correction_end}" \
  "$output_path/actors" 3 "command-words correction"

read -r program_start program_end < <(
  find_span $'return Environment.Grammar == ShellGrammar.Bash\n               && ShellProgramPath.TryResolve(programWord, workingDirectory, out programPath)\n               && !string.Equals(programPath, programWord, StringComparison.Ordinal);' \
  "$repo_root/src/Netclaw.Security/IToolApprovalMatcher.cs")
run_gate Netclaw.Security.csproj "IToolApprovalMatcher.cs{$program_start..$program_end}" \
  "$output_path/program-path" 4 "program path identity"

read -r legacy_start legacy_end < <(
  find_span 'return NormalizeAbsolute(candidateProgram).EndsWith("/" + namedSegments, StringComparison.Ordinal);' \
  "$repo_root/src/Netclaw.Configuration/ShellProgramPath.cs")
run_gate Netclaw.Configuration.csproj "ShellProgramPath.cs{$legacy_start..$legacy_end}" \
  "$output_path/legacy-program" 1 "legacy program spelling"
