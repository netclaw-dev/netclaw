#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/skill-manage-guard}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# skill_manage asks the filesystem authority for the link and protection
# decisions. Two targets are in the authority (Netclaw.Security). The temporary
# path target stays in SkillManageTool (Netclaw.Actors). Source drift must fail
# before Stryker starts.
# Each marker argument is "<expected mutant count>|<exact source text>".
find_spans() {
  local source_file="$1"
  shift
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -e '
    my ($source_file, @targets) = @ARGV;
    local $/;
    open my $handle, "<", $source_file or die "Cannot read $source_file.\n";
    my $source = <$handle>;
    for my $target (@targets) {
      my ($count, $marker) = split /\|/, $target, 2;
      my $start = index($source, $marker);
      die "A skill_manage guard marker is missing or duplicated.\n"
        if $start < 0 || index($source, $marker, $start + 1) >= 0;
      my $line = 1 + (substr($source, 0, $start) =~ tr/\n/\n/);
      print "$start ", $start + length($marker), " $line $count\n";
    }
  ' "$source_file" "$@"
}

# Runs one Stryker group and requires every mutant at each marker line to die.
run_group() {
  local project="$1"
  local relative_file="$2"
  local source_file="$3"
  local group_output="$4"
  local spans="$5"

  local mutate_args=()
  local expected_lines=()
  local expected_counts=()
  local expected_total=0
  while read -r span_start span_end line count; do
    mutate_args+=(--mutate "$relative_file{$span_start..$span_end}")
    expected_lines+=("$line")
    expected_counts+=("$count")
    expected_total=$((expected_total + count))
  done <<< "$spans"

  (
    cd "$test_project"
    dotnet stryker \
      --config-file stryker-config.json \
      --project "$project" \
      "${mutate_args[@]}" \
      --output "$group_output" \
      --skip-version-check
  )

  local report="$group_output/reports/mutation-report.json"
  local index
  for index in "${!expected_lines[@]}"; do
    jq -e --arg source "$source_file" \
      --argjson line "${expected_lines[$index]}" \
      --argjson count "${expected_counts[$index]}" '
      [.files[$source].mutants[] | select(.status != "Ignored" and .status != "CompileError")
        | select(.location.start.line == $line)] as $mutants
      | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
    ' "$report" > /dev/null || {
      echo "Expected ${expected_counts[$index]} killed skill_manage guard mutants at $source_file:${expected_lines[$index]}." >&2
      exit 1
    }
  done

  # Stryker can report unrelated compile errors before it applies the span filter.
  # Each target above still requires its killed mutants.
  jq -e --argjson count "$expected_total" \
    '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length == $count' \
    "$report" > /dev/null || {
    echo "Expected exactly $expected_total skill_manage guard mutants in $relative_file." >&2
    exit 1
  }
}

authority_file="$repo_root/src/Netclaw.Security/Authorization/Filesystem/FileSystemAuthority.cs"
authority_spans="$(find_spans "$authority_file" \
  "2|crossesLink ? PathDecision.CrossesLink : PathDecision.Allowed" \
  "2|IsProtected(path.Value, operation) ? PathDecision.Protected : membership")"
run_group \
  "Netclaw.Security.csproj" \
  "Authorization/Filesystem/FileSystemAuthority.cs" \
  "$authority_file" \
  "$output_path/authority" \
  "$authority_spans"

tool_file="$repo_root/src/Netclaw.Actors/Tools/SkillManageTool.cs"
tool_spans="$(find_spans "$tool_file" \
  "1|paths.Add(targetPath + AtomicTempSuffix);")"
run_group \
  "Netclaw.Actors.csproj" \
  "Tools/SkillManageTool.cs" \
  "$tool_file" \
  "$output_path/tool" \
  "$tool_spans"
