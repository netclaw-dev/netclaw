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
    print "$start ", $end_start + length($end_marker), "\n";
  ' "$context_marker" "$start_marker" "$end_marker" "$source_file"
}

run_target() {
  local config_file="$1"
  local source_name="$2"
  local span_start="$3"
  local span_end="$4"
  local target_output="$5"
  local expected_count="$6"

  (
    cd "$test_project"
    dotnet stryker \
      --config-file "$config_file" \
      --mutate "$source_name{$span_start..$span_end}" \
      --output "$target_output" \
      --skip-version-check
  )

  local report="$target_output/reports/mutation-report.json"
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

matching_file="$repo_root/src/Netclaw.Security/ApprovalPatternMatching.cs"
read -r matching_start matching_end < <(
  find_span \
    "$matching_file" \
    "private static bool AssignmentConstraintMatches(" \
    "private static bool AssignmentConstraintMatches(" \
    "_ => false,"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ApprovalPatternMatching.cs" \
  "$matching_start" \
  "$matching_end" \
  "$output_path/constraint-match" \
  5

analysis_file="$repo_root/src/Netclaw.Security/ShellCommandAnalysis.cs"
read -r span_start span_end < <(
  find_span \
    "$analysis_file" \
    "internal bool TryConsume(ShellSyntaxNode node)" \
    "|| !_unconsumed.Remove((start, length), out var assignment))" \
    ".SequenceEqual(assignment.Source.AsSpan());"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellCommandAnalysis.cs" \
  "$span_start" \
  "$span_end" \
  "$output_path/assignment-span" \
  1

read -r wrapper_start wrapper_end < <(
  find_span \
    "$analysis_file" \
    "if (commands.Skip(innerCommandStart).Any" \
    "if (commands.Skip(innerCommandStart).Any" \
    "return ShellAnalysisFailure.Unresolved;"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellCommandAnalysis.cs" \
  "$wrapper_start" \
  "$wrapper_end" \
  "$output_path/fallback-wrapper-assignments" \
  4

environment_file="$repo_root/src/Netclaw.Security/ShellExecutionEnvironment.cs"
read -r mode_start mode_end < <(
  find_span \
    "$environment_file" \
    "private BashInitialStateMode BashInitialStateMode" \
    "private BashInitialStateMode BashInitialStateMode" \
    ": BashInitialStateMode.Unknown;"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellExecutionEnvironment.cs" \
  "$mode_start" \
  "$mode_end" \
  "$output_path/bash-initial-state" \
  7

reviewed_file="$repo_root/src/Netclaw.Actors/Tools/ReviewedSafeShellPolicy.cs"
read -r reviewed_start reviewed_end < <(
  find_span \
    "$reviewed_file" \
    "private bool IsReviewedDiagnosticSyntax(" \
    "if (candidate.AssignmentConstraint is not" \
    "return false;"
)
run_target \
  "stryker-config.json" \
  "Tools/ReviewedSafeShellPolicy.cs" \
  "$reviewed_start" \
  "$reviewed_end" \
  "$output_path/reviewed-safe" \
  2

access_policy_file="$repo_root/src/Netclaw.Actors/Tools/ToolAccessPolicy.cs"
read -r option_start option_end < <(
  find_span \
    "$access_policy_file" \
    "internal static bool HasAssignmentConstraint(" \
    "internal static bool HasAssignmentConstraint(" \
    "candidate.AssignmentConstraint.Kind == ApprovalAssignmentConstraintKind.ExactDigest);"
)
run_target \
  "stryker-config.json" \
  "Tools/ToolAccessPolicy.cs" \
  "$option_start" \
  "$option_end" \
  "$output_path/rollback-safe-options" \
  2

session_actor_file="$repo_root/src/Netclaw.Actors/Sessions/LlmSessionActor.cs"
read -r offered_start offered_end < <(
  find_span \
    "$session_actor_file" \
    "internal static bool IsOfferedApprovalOption(" \
    "=> ApprovalOptionKeys.IsAssignmentVariant(selectedKey)" \
    ": optionKeys.Count == 0 || optionKeys.Contains(selectedKey, StringComparer.Ordinal);"
)
run_target \
  "stryker-config.json" \
  "Sessions/LlmSessionActor.cs" \
  "$offered_start" \
  "$offered_end" \
  "$output_path/legacy-prompt-options" \
  11

read -r sanitizer_start sanitizer_end < <(
  find_span \
    "$environment_file" \
    "internal static void RemoveBashStartupOverrides" \
    "foreach (var key in environment.Keys.Where" \
    "|| key.StartsWith(\"DYLD_\", StringComparison.Ordinal)).ToArray())"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellExecutionEnvironment.cs" \
  "$sanitizer_start" \
  "$sanitizer_end" \
  "$output_path/bash-sanitizer" \
  27
