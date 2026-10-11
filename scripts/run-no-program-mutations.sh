#!/usr/bin/env bash
# Focused mutation gate for the owner decision of October 2026: a shell command
# that runs no program gets no prompt. Only a plain file target qualifies (a
# Bash special device such as /dev/tcp does not), and an input redirect of such
# a command must pass the file_read rules of the audience.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/no-program}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Prints the character span from the first marker to the end of the last
# marker. A missing or duplicated marker fails before Stryker starts.
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

# Runs Stryker on one project span and requires every tested mutant to be killed.
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
  local tested killed
  tested="$(jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report")"
  killed="$(jq '[.files[].mutants[] | select(.status == "Killed")] | length' "$report")"
  if [[ "$tested" -ne "$expected" || "$killed" -ne "$expected" ]]; then
    echo "Expected $expected killed $name mutants. Found $killed killed from $tested tested." >&2
    exit 1
  fi
}

analysis_file="$repo_root/src/Netclaw.Security/ShellCommandAnalysis.cs"
read -r target_start target_end < <(
  find_range 'if (redirect.Target is not ShellValueDomain.Exact { Value: { Length: > 0 } value }' \
  '|| !path.Value.StartsWith("/dev/", StringComparison.Ordinal);' "$analysis_file")
run_gate Netclaw.Security.csproj "ShellCommandAnalysis.cs{$target_start..$target_end}" \
  "$output_path/plain-target" 5 "plain file target"

policy_file="$repo_root/src/Netclaw.Actors/Tools/ToolAccessPolicy.cs"
read -r read_start read_end < <(
  find_range $'if (!candidate.RunsNoProgram)\n                continue;' \
  '$"this audience may not use {FileToolName(read)}, and a redirect gets the same decision");' "$policy_file")
run_gate Netclaw.Actors.csproj "Tools/ToolAccessPolicy.cs{$read_start..$read_end}" \
  "$output_path/redirect-checks" 15 "redirect check"
