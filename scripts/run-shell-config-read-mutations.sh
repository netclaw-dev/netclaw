#!/usr/bin/env bash
set -euo pipefail

# Decision D6: a read-only shell program can read a config file that a file
# tool may read. Every other form keeps the old denial: a credential, a
# directory that holds one, a glob, a brace or ANSI-C quoted word, a ".." out of the config directory,
# another spelling of the config directory in program text, a
# program that can write, a write redirect, and a path outside the write roots.
# A mutant that widens one of these rules must die.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/shell-config-read}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

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

# Runs Stryker on project spans and requires every tested mutant to be detected.
run_gate() {
  local project="$1" output="$2" expected="$3" name="$4"
  shift 4
  local mutate_args=()
  for span in "$@"; do
    mutate_args+=(--mutate "$span")
  done
  (
    cd "$test_project"
    dotnet stryker \
      --config-file stryker-config.json \
      --project "$project" \
      "${mutate_args[@]}" \
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

policy="$repo_root/src/Netclaw.Actors/Tools/ToolAccessPolicy.cs"
read -r occurrences_start occurrences_end < <(
  find_range 'foreach (var occurrence in analysis.Commands)' 'occurrences.Add(occurrence);' "$policy")
read -r operand_start operand_end < <(
  find_range '=> !ToolPathPolicy.HasUnmodeledExpansion(argument.Element.Raw)' '=> ShellGrantFileWords.NamesEntry(word, directory, out _);' "$policy")
read -r redirect_start redirect_end < <(
  find_range 'var read = readOnly' ': ShellPathRead.None;' "$policy")
read -r relax_start relax_end < <(
  find_range '=> _pathAccessPolicy.EvaluateShellPath(access.Path, context, PathAccessPolicy.FileOperation.Write)' 'EvaluateShellReadPath(access.Path, context, access.Read == ShellPathRead.Operand)' "$policy")
access="$repo_root/src/Netclaw.Actors/Tools/PathAccessPolicy.cs"
read -r gate_start gate_end < <(
  find_range 'if (!path.IsHostStyle || !_fileSystem.IsProtected' 'PathOperation.Write))' "$access")
read -r read_start read_end < <(
  find_range 'var read = Evaluate(path.Value, context, FileOperation.Read);' ': read;' "$access")
run_gate Netclaw.Actors.csproj "$output_path/actors" 24 "read-only shell path" \
  "Tools/ToolAccessPolicy.cs{$occurrences_start..$occurrences_end}" \
  "Tools/ToolAccessPolicy.cs{$operand_start..$operand_end}" \
  "Tools/ToolAccessPolicy.cs{$redirect_start..$redirect_end}" \
  "Tools/ToolAccessPolicy.cs{$relax_start..$relax_end}" \
  "Tools/PathAccessPolicy.cs{$gate_start..$gate_end}" \
  "Tools/PathAccessPolicy.cs{$read_start..$read_end}"

text_policy="$repo_root/src/Netclaw.Security/ToolPathPolicy.cs"
read -r whole_start whole_end < <(
  find_range 'var text = slashCommand;' "&& CanonicalPath.IsWithin(path.Value, directory, CanonicalPath.HostStyle, AllowIgnoresCase));" "$text_policy")
read -r guarded_start guarded_end < <(
  find_range 'var screened = ScreenText' 'if (MentionsGuardedDirectory(screened))' "$text_policy")
read -r proved_start proved_end < <(
  find_range 'var proved = analysis.IsResolved' 'var shell = proved ? FileSystem : _unprovedShell;' "$text_policy")
read -r holds_start holds_end < <(
  find_range 'TryResolveLinks(normalized, out var resolved);' \
    'CanonicalPath.IsWithin(protectedPath, resolved, CanonicalPath.HostStyle,' \
    "$repo_root/src/Netclaw.Security/Authorization/Filesystem/FileSystemAuthority.cs")
run_gate Netclaw.Security.csproj "$output_path/security" 34 "guarded config directory" \
  "ToolPathPolicy.cs{$whole_start..$whole_end}" \
  "ToolPathPolicy.cs{$guarded_start..$guarded_end}" \
  "ToolPathPolicy.cs{$proved_start..$proved_end}" \
  "**/FileSystemAuthority.cs{$holds_start..$holds_end}"
