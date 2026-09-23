#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/shell-assignment}"
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
    my $end = $end_start + length($end_marker);
    my $start_prefix = substr($source, 0, $start);
    my $end_prefix = substr($source, 0, $end);
    my $start_line = 1 + ($start_prefix =~ tr/\n//);
    my $end_line = 1 + ($end_prefix =~ tr/\n//);
    my $start_column = $start - (rindex($start_prefix, "\n") + 1) + 1;
    my $end_column = $end - (rindex($end_prefix, "\n") + 1) + 1;
    print "$start $end $start_line $start_column $end_line $end_column\n";
  ' "$context_marker" "$start_marker" "$end_marker" "$source_file"
}

run_group() {
  local config_file="$1"
  local target_output="$2"
  shift 2
  local mutation_args=()
  local pattern
  for pattern in "$@"; do
    mutation_args+=(--mutate "$pattern")
  done

  (
    cd "$test_project"
    dotnet stryker \
      --config-file "$config_file" \
      "${mutation_args[@]}" \
      --output "$target_output" \
      --skip-version-check
  )
}

assert_report() {
  local report="$1"
  local expected_count="$2"
  local tested_count
  local killed_count
  tested_count="$(
    jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report"
  )"
  killed_count="$(jq '[.files[].mutants[] | select(.status == "Killed")] | length' "$report")"

  if [[ "$tested_count" -ne "$expected_count" || "$killed_count" -ne "$expected_count" ]]; then
    echo "Expected $expected_count killed mutants. Found $killed_count killed from $tested_count tested." >&2
    exit 1
  fi
}

assert_target() {
  local report="$1"
  local label="$2"
  local source_file="$3"
  local start_line="$4"
  local start_column="$5"
  local end_line="$6"
  local end_column="$7"
  local expected_count="$8"
  local tested_count
  local killed_count
  local target_filter='def inside_target:
    ((.location.start.line > $start_line)
      or (.location.start.line == $start_line and .location.start.column >= $start_column))
    and ((.location.end.line < $end_line)
      or (.location.end.line == $end_line and .location.end.column <= $end_column));
    [(.files[$source_file].mutants // [])[] | select(inside_target)]'
  local target_mutants
  target_mutants="$(
    jq \
      --arg source_file "$source_file" \
      --argjson start_line "$start_line" \
      --argjson start_column "$start_column" \
      --argjson end_line "$end_line" \
      --argjson end_column "$end_column" \
      "$target_filter" \
      "$report"
  )"
  tested_count="$(
    jq '[.[] | select(.status != "Ignored" and .status != "CompileError")] | length' <<<"$target_mutants"
  )"
  killed_count="$(jq '[.[] | select(.status == "Killed")] | length' <<<"$target_mutants")"

  if [[ "$tested_count" -ne "$expected_count" || "$killed_count" -ne "$expected_count" ]]; then
    echo "$label: expected $expected_count killed mutants. Found $killed_count killed from $tested_count tested." >&2
    exit 1
  fi
}

security_patterns=()
actor_patterns=()

matching_file="$repo_root/src/Netclaw.Security/ApprovalPatternMatching.cs"
read -r matching_start matching_end matching_start_line matching_start_column matching_end_line matching_end_column < <(
  find_span \
    "$matching_file" \
    "private static bool AssignmentConstraintMatches(" \
    "private static bool AssignmentConstraintMatches(" \
    "_ => false,"
)
security_patterns+=("ApprovalPatternMatching.cs{$matching_start..$matching_end}")

analysis_file="$repo_root/src/Netclaw.Security/ShellCommandAnalysis.cs"
read -r span_start span_end span_start_line span_start_column span_end_line span_end_column < <(
  find_span \
    "$analysis_file" \
    "internal bool TryConsume(ShellSyntaxNode node)" \
    "|| !_unconsumed.Remove((start, length), out var assignment))" \
    ".SequenceEqual(assignment.Source.AsSpan());"
)
security_patterns+=("ShellCommandAnalysis.cs{$span_start..$span_end}")

read -r wrapper_start wrapper_end wrapper_start_line wrapper_start_column wrapper_end_line wrapper_end_column < <(
  find_span \
    "$analysis_file" \
    "if (commands.Skip(innerCommandStart).Any" \
    "if (commands.Skip(innerCommandStart).Any" \
    "return ShellAnalysisFailure.Unresolved;"
)
security_patterns+=("ShellCommandAnalysis.cs{$wrapper_start..$wrapper_end}")

environment_file="$repo_root/src/Netclaw.Security/ShellExecutionEnvironment.cs"
read -r mode_start mode_end mode_start_line mode_start_column mode_end_line mode_end_column < <(
  find_span \
    "$environment_file" \
    "private BashInitialStateMode BashInitialStateMode" \
    "private BashInitialStateMode BashInitialStateMode" \
    ": BashInitialStateMode.Unknown;"
)
security_patterns+=("ShellExecutionEnvironment.cs{$mode_start..$mode_end}")

reviewed_file="$repo_root/src/Netclaw.Actors/Tools/ReviewedSafeShellPolicy.cs"
read -r reviewed_start reviewed_end reviewed_start_line reviewed_start_column reviewed_end_line reviewed_end_column < <(
  find_span \
    "$reviewed_file" \
    "private bool IsReviewedDiagnosticSyntax(" \
    "if (candidate.AssignmentConstraint is not" \
    "return false;"
)
actor_patterns+=("Tools/ReviewedSafeShellPolicy.cs{$reviewed_start..$reviewed_end}")

access_policy_file="$repo_root/src/Netclaw.Actors/Tools/ToolAccessPolicy.cs"
read -r option_start option_end option_start_line option_start_column option_end_line option_end_column < <(
  find_span \
    "$access_policy_file" \
    "internal static bool HasAssignmentConstraint(" \
    "internal static bool HasAssignmentConstraint(" \
    "candidate.AssignmentConstraint.Kind == ApprovalAssignmentConstraintKind.ExactDigest);"
)
actor_patterns+=("Tools/ToolAccessPolicy.cs{$option_start..$option_end}")

session_actor_file="$repo_root/src/Netclaw.Actors/Sessions/LlmSessionActor.cs"
read -r offered_start offered_end offered_start_line offered_start_column offered_end_line offered_end_column < <(
  find_span \
    "$session_actor_file" \
    "internal static bool IsOfferedApprovalOption(" \
    "=> ApprovalOptionKeys.IsAssignmentVariant(selectedKey)" \
    ": optionKeys.Count == 0 || optionKeys.Contains(selectedKey, StringComparer.Ordinal);"
)
actor_patterns+=("Sessions/LlmSessionActor.cs{$offered_start..$offered_end}")

read -r sanitizer_start sanitizer_end sanitizer_start_line sanitizer_start_column sanitizer_end_line sanitizer_end_column < <(
  find_span \
    "$environment_file" \
    "internal static void RemoveBashStartupOverrides" \
    "foreach (var key in environment.Keys.Where" \
    "|| key.StartsWith(\"DYLD_\", StringComparison.Ordinal)).ToArray())"
)
security_patterns+=("ShellExecutionEnvironment.cs{$sanitizer_start..$sanitizer_end}")

security_output="$output_path/security"
run_group "stryker-shell-command-analysis.json" "$security_output" "${security_patterns[@]}"
security_report="$security_output/reports/mutation-report.json"
assert_report "$security_report" 44
assert_target "$security_report" "constraint-match" "$matching_file" "$matching_start_line" "$matching_start_column" "$matching_end_line" "$matching_end_column" 5
assert_target "$security_report" "assignment-span" "$analysis_file" "$span_start_line" "$span_start_column" "$span_end_line" "$span_end_column" 1
assert_target "$security_report" "fallback-wrapper-assignments" "$analysis_file" "$wrapper_start_line" "$wrapper_start_column" "$wrapper_end_line" "$wrapper_end_column" 4
assert_target "$security_report" "bash-initial-state" "$environment_file" "$mode_start_line" "$mode_start_column" "$mode_end_line" "$mode_end_column" 7
assert_target "$security_report" "bash-sanitizer" "$environment_file" "$sanitizer_start_line" "$sanitizer_start_column" "$sanitizer_end_line" "$sanitizer_end_column" 27

actor_output="$output_path/actors"
run_group "stryker-config.json" "$actor_output" "${actor_patterns[@]}"
actor_report="$actor_output/reports/mutation-report.json"
assert_report "$actor_report" 15
assert_target "$actor_report" "reviewed-safe" "$reviewed_file" "$reviewed_start_line" "$reviewed_start_column" "$reviewed_end_line" "$reviewed_end_column" 2
assert_target "$actor_report" "rollback-safe-options" "$access_policy_file" "$option_start_line" "$option_start_column" "$option_end_line" "$option_end_column" 2
assert_target "$actor_report" "legacy-prompt-options" "$session_actor_file" "$offered_start_line" "$offered_start_column" "$offered_end_line" "$offered_end_column" 11
