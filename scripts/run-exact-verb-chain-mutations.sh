#!/usr/bin/env bash
set -euo pipefail

# A verb grant (two or more words) covers its command words and any later
# words. A program-only grant covers its word alone. Each mutant of the length
# checks must die: one lets a "gh" grant cover "gh auth logout", one lets a
# grant cover a shorter word list, and one lets an empty grant cover any call.
# The approval matcher and the store hygiene share this one rule.
# Unknown command words get a rewrite correction. A mutant that drops the
# correction turns the call back into a prompt or a denial, so it must die.
# R1: a program path names its file. A mutant that skips the join with the
# working directory, or the "/" boundary of an older "./tool" grant, lets one
# grant run another file with that name, so it must die.
# A file word after the verb slot is an operand. A mutant that keeps it stores
# a file name in a grant. A mutant that drops the program word, the verb slot,
# a link, or a word without a file widens a grant, so it must die.
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

# Prints the span from the start of the first marker to the end of the second marker.
find_range() {
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -e '
    my ($first, $last, $file) = @ARGV;
    local $/;
    open(my $fh, "<", $file) or die "Cannot read $file\n";
    my $text = <$fh>;
    for my $marker ($first, $last) {
      my $at = index($text, $marker);
      die "A target is missing or duplicated: $marker\n"
        if $at < 0 || index($text, $marker, $at + 1) >= 0;
    }
    my $start = index($text, $first);
    my $end = index($text, $last) + length($last);
    die "The range markers are out of order.\n" if $end <= $start;
    print "$start $end\n";
  ' "$1" "$2" "$3"
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

comparer_file="$repo_root/src/Netclaw.Configuration/ToolApprovalEntryComparer.cs"
read -r span_start span_end < <(
  find_range 'if (grantLength == 0 || candidateLength < grantLength)' \
  $'if (grantLength == 1 && candidateLength != 1)\n            return false;' "$comparer_file")
run_gate Netclaw.Configuration.csproj "ToolApprovalEntryComparer.cs{$span_start..$span_end}" \
  "$output_path/command-words" 11 "grant command words"

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

read -r file_word_start file_word_end < <(
  find_range 'if (words is null)' \
  'return new CommandWordProjection(kept.AsReadOnly(), fileWords);' \
  "$repo_root/src/Netclaw.Security/IToolApprovalMatcher.cs")
run_gate Netclaw.Security.csproj "IToolApprovalMatcher.cs{$file_word_start..$file_word_end}" \
  "$output_path/file-word" 6 "file word operand"

read -r link_start link_end < <(
  find_range 'if (string.IsNullOrEmpty(directory)' 'FileAttributes.ReparsePoint) != 0;' \
  "$repo_root/src/Netclaw.Configuration/ShellGrantFileWords.cs")
run_gate Netclaw.Configuration.csproj "ShellGrantFileWords.cs{$link_start..$link_end}" \
  "$output_path/file-word-entry" 16 "file word entry"

# A plain word that names a link to a protected path is denied, command word or
# argument. A mutant that skips the screen, checks no directory, or checks the
# program word changes a decision, so it must die.
read -r link_word_start link_word_end < <(
  find_range '// An unproved directory names no entry.' 'IsShellDenied(shell, link))' \
  "$repo_root/src/Netclaw.Security/ToolPathPolicy.cs")
run_gate Netclaw.Security.csproj "ToolPathPolicy.cs{$link_word_start..$link_word_end}" \
  "$output_path/link-word" 8 "plain word link target"
