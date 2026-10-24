#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/compound-redirects}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

find_span() {
  local source_file="$1"
  local context_marker="$2"
  local start_marker="$3"
  local end_marker="$4"
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -e '
    my ($context_marker, $start_marker, $end_marker, $source_file) = @ARGV;
    local $/;
    open my $handle, "<", $source_file or die "$source_file: $!\n";
    my $source = <$handle>;
    my $context = index($source, $context_marker);
    die "The context marker is missing.\n" if $context < 0;
    my $start = index($source, $start_marker, $context);
    die "The start marker is missing.\n" if $start < 0;
    my $end_start = index($source, $end_marker, $start);
    die "The end marker is missing.\n" if $end_start < 0;
    my $line = 1 + (substr($source, 0, $start) =~ tr/\n//);
    print "$start ", $end_start + length($end_marker), " $line\n";
  ' "$context_marker" "$start_marker" "$end_marker" "$source_file"
}

projection_file="$repo_root/src/Netclaw.Actors/Tools/ShellPolicyProjection.cs"
read -r owner_start owner_end owner_line < <(
  find_span "$projection_file" \
    "private static bool HasValidCompoundCandidate(" \
    "if (!compoundViews.Any(view => ReferenceEquals(view.Owner.Syntax, owner)))" \
    "if (!compoundViews.Any(view => ReferenceEquals(view.Owner.Syntax, owner)))"
)

policy_file="$repo_root/src/Netclaw.Actors/Tools/ToolAccessPolicy.cs"
read -r path_start path_end path_line < <(
  find_span "$policy_file" \
    "foreach (var path in compound.PathFact.Paths)" \
    "if (!compoundTargetAllowed)" \
    "if (!compoundTargetAllowed)"
)

read -r selector_start selector_end selector_line < <(
  find_span "$policy_file" \
    "foreach (var path in compound.PathFact.Paths)" \
    "read ? ShellPathRead.Operand : ShellPathRead.None" \
    "read ? ShellPathRead.Operand : ShellPathRead.None"
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Actors.csproj \
    --mutate "Tools/ShellPolicyProjection.cs{$owner_start..$owner_end}" \
    --mutate "Tools/ToolAccessPolicy.cs{$path_start..$path_end}" \
    --mutate "Tools/ToolAccessPolicy.cs{$selector_start..$selector_end}" \
    --output "$output_path" \
    --log-to-file \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
check_target() {
  local source_file="$1"
  local line="$2"
  local expected="$3"
  local label="$4"
  local result
  result="$(jq -r --arg file "$repo_root/$source_file" --argjson line "$line" \
    '[.files[$file].mutants[] | select(.location.start.line == $line and .status != "Ignored")] as $target
     | [$target | length, ($target | map(select(.status == "Killed")) | length), ($target | map(select(.status == "CompileError")) | length)] | @tsv' \
    "$report")"
  local observed killed compile_errors
  read -r observed killed compile_errors <<<"$result"
  if [[ "$observed" -ne "$expected" || "$killed" -ne "$expected" || "$compile_errors" -ne 0 ]]; then
    echo "$label target at line $line: expected $expected killed mutants; found $killed killed, $compile_errors compile errors, $observed non-ignored." >&2
    exit 1
  fi
}

check_target "src/Netclaw.Actors/Tools/ShellPolicyProjection.cs" "$owner_line" 2 "owner membership"
check_target "src/Netclaw.Actors/Tools/ToolAccessPolicy.cs" "$path_line" 1 "target denial"
check_target "src/Netclaw.Actors/Tools/ToolAccessPolicy.cs" "$selector_line" 2 "read selector"

tested="$(jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report")"
targeted=5
if [[ "$tested" -lt "$targeted" ]]; then
  echo "Expected at least $targeted tested compound redirect mutants. Found $tested." >&2
  exit 1
fi
