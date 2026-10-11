#!/usr/bin/env bash
# Focused mutation gate for owner decision F1: the strictest literal twin result
# decides a call. One denied twin denies the call, and the candidates of every
# twin replace the candidates of their source command.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/literal-twin}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Prints the character span and the line and column range of one target. A
# missing or duplicated context marker fails before Stryker starts.
find_span() {
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -e '
    my ($context_marker, $start_marker, $end_marker, $source_file) = @ARGV;
    local $/;
    open my $handle, "<", $source_file or die "$source_file: $!\n";
    my $source = <$handle>;
    my $context = index($source, $context_marker);
    die "The context marker is missing or duplicated: $context_marker\n"
      if $context < 0 || index($source, $context_marker, $context + 1) >= 0;
    my $start = index($source, $start_marker, $context);
    die "The start marker is missing.\n" if $start < 0;
    my $end_start = index($source, $end_marker, $start);
    die "The end marker is missing.\n" if $end_start < 0;
    my $end = $end_start + length($end_marker);
    my $start_prefix = substr($source, 0, $start);
    my $end_prefix = substr($source, 0, $end);
    my $start_line = 1 + ($start_prefix =~ tr/\n//);
    my $end_line = 1 + ($end_prefix =~ tr/\n//);
    my $start_column = $start - (rindex($start_prefix, "\n") + 1) + 1;
    my $end_column = $end - (rindex($end_prefix, "\n") + 1) + 1;
    print "$start $end $start_line $start_column $end_line $end_column\n";
  ' "$2" "$3" "$4" "$1"
}

# Requires every tested mutant inside one target to be killed, and the count to match.
assert_target() {
  local report="$1" label="$2" source_file="$3"
  local start_line="$4" start_column="$5" end_line="$6" end_column="$7" expected="$8"
  local mutants tested killed
  mutants="$(
    jq \
      --arg source_file "$source_file" \
      --argjson start_line "$start_line" \
      --argjson start_column "$start_column" \
      --argjson end_line "$end_line" \
      --argjson end_column "$end_column" \
      'def inside_target:
        ((.location.start.line > $start_line)
          or (.location.start.line == $start_line and .location.start.column >= $start_column))
        and ((.location.end.line < $end_line)
          or (.location.end.line == $end_line and .location.end.column <= $end_column));
        [(.files[$source_file].mutants // [])[] | select(inside_target)]' \
      "$report"
  )"
  tested="$(jq '[.[] | select(.status != "Ignored" and .status != "CompileError")] | length' <<<"$mutants")"
  killed="$(jq '[.[] | select(.status == "Killed")] | length' <<<"$mutants")"
  if [[ "$tested" -ne "$expected" || "$killed" -ne "$expected" ]]; then
    echo "$label: expected $expected killed mutants. Found $killed killed from $tested tested." >&2
    exit 1
  fi
}

twins_file="$repo_root/src/Netclaw.Actors/Tools/BashLiteralTwinSlices.cs"
read -r apply_start apply_end apply_start_line apply_start_column apply_end_line apply_end_column < <(
  find_span \
    "$twins_file" \
    "internal ShellApprovalAnalysis Apply(ShellApprovalAnalysis approval)" \
    "if (approval.IsMessy)" \
    'throw new InvalidOperationException("A command with literal twins has no candidate in the approval.");'
)

policy_file="$repo_root/src/Netclaw.Actors/Tools/ToolAccessPolicy.cs"
read -r screen_start screen_end screen_start_line screen_start_column screen_end_line screen_end_column < <(
  find_span \
    "$policy_file" \
    "private ToolAuthorizationDecision? ScreenScopedSlices(" \
    "if (denial is not null)" \
    "return denial;"
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --mutate "Tools/BashLiteralTwinSlices.cs{$apply_start..$apply_end}" \
    --mutate "Tools/ToolAccessPolicy.cs{$screen_start..$screen_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
assert_target "$report" "twin candidate merge" "$twins_file" \
  "$apply_start_line" "$apply_start_column" "$apply_end_line" "$apply_end_column" 8
assert_target "$report" "twin screen" "$policy_file" \
  "$screen_start_line" "$screen_start_column" "$screen_end_line" "$screen_end_column" 1
