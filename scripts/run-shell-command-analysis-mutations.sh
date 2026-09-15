#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/shell-command-analysis}"
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

analysis_file="$repo_root/src/Netclaw.Security/ShellCommandAnalysis.cs"
read -r region_start region_end < <(
  find_span \
    "$analysis_file" \
    "private static bool IsAccountedExecutionRegionArgument" \
    "=> argument.Argument.Kind == ArgKind.DynamicSkip" \
    "&& accountedRegionArguments.Contains(argument.Element);"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellCommandAnalysis.cs" \
  "$region_start" \
  "$region_end" \
  "$output_path/execution-region" \
  2

policy_file="$repo_root/src/Netclaw.Security/ShellCommandPolicy.cs"
read -r gate_start gate_end < <(
  find_span \
    "$policy_file" \
    "private ShellCommandDecision EvaluateStructuralAnalysis" \
    "var denyOnlyDecision = EvaluateDenyOnlyClauses" \
    "return denyOnlyDecision;"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellCommandPolicy.cs" \
  "$gate_start" \
  "$gate_end" \
  "$output_path/deny-only-gate" \
  1

read -r trust_start trust_end < <(
  find_span \
    "$policy_file" \
    "private static bool FirstNonFlagMatchesConstraint" \
    "if (!tokens[i].IsKnown)" \
    "return false;"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellCommandPolicy.cs" \
  "$trust_start" \
  "$trust_end" \
  "$output_path/deny-only-token-trust" \
  2

tree_policy_file="$repo_root/src/Netclaw.Security/ShellFileSystemTreeAccessPolicy.cs"
read -r tree_decision_start tree_decision_end < <(
  find_span \
    "$tree_policy_file" \
    "internal static bool RequiresExactApproval(" \
    "var accesses = command.FileSystemTreeAccesses;" \
    "return !CanUseReusableApproval(command, accesses[0]);"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellFileSystemTreeAccessPolicy.cs" \
  "$tree_decision_start" \
  "$tree_decision_end" \
  "$output_path/tree-decision" \
  7

read -r tree_root_start tree_root_end < <(
  find_span \
    "$tree_policy_file" \
    "private static bool CanUseReusableApproval(" \
    "if (!IsReusableTraversal(access.Traversal)" \
    "&& string.Equals(root.Value, cwd.Value, StringComparison.Ordinal);"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellFileSystemTreeAccessPolicy.cs" \
  "$tree_root_start" \
  "$tree_root_end" \
  "$output_path/tree-root" \
  10

read -r root_match_start root_match_end < <(
  find_span \
    "$tree_policy_file" \
    "private static bool RootMatchesArgument(" \
    "=> root switch" \
    "_ => false"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellFileSystemTreeAccessPolicy.cs" \
  "$root_match_start" \
  "$root_match_end" \
  "$output_path/tree-root-correspondence" \
  3

read -r leaf_call_start leaf_call_end < <(
  find_span \
    "$tree_policy_file" \
    "private static bool TryGetLeafPatternCoveringDirectory(" \
    "!IsReusableLeafPattern(pattern)" \
    "!IsReusableLeafPattern(pattern)"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellFileSystemTreeAccessPolicy.cs" \
  "$leaf_call_start" \
  "$leaf_call_end" \
  "$output_path/tree-root-leaf-call" \
  1

read -r leaf_shape_start leaf_shape_end < <(
  find_span \
    "$tree_policy_file" \
    "internal static bool IsReusableLeafPattern(" \
    "internal static bool IsReusableLeafPattern(" \
    "return separator != 0 && !IsIncompleteUncLeafPattern(pattern, separator);"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellFileSystemTreeAccessPolicy.cs" \
  "$leaf_shape_start" \
  "$leaf_shape_end" \
  "$output_path/tree-root-leaf-shape" \
  15

read -r traversal_start traversal_end < <(
  find_span \
    "$tree_policy_file" \
    "internal static bool IsReusableTraversal" \
    "=> Enum.IsDefined(traversal)" \
    "or ShellTreeTraversalMode.RecursiveWithoutFollowingLinks;"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellFileSystemTreeAccessPolicy.cs" \
  "$traversal_start" \
  "$traversal_end" \
  "$output_path/tree-traversal" \
  2

read -r nonfile_start nonfile_end < <(
  find_span \
    "$analysis_file" \
    "internal static bool HasAuditedNonFileSystemValue(AnalyzedArgument argument)" \
    "if (argument.Argument.IsPath" \
    "ShellValueDomain.Unknown => HasMatchingIntegerRange(argument),"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellCommandAnalysis.cs" \
  "$nonfile_start" \
  "$nonfile_end" \
  "$output_path/audited-nonfilesystem" \
  5

read -r range_start range_end < <(
  find_span \
    "$analysis_file" \
    "private static bool HasMatchingIntegerRange" \
    "=> argument.Value is" \
    "< MaximumReviewedIntegerRangeCardinality;"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "ShellCommandAnalysis.cs" \
  "$range_start" \
  "$range_end" \
  "$output_path/integer-range" \
  11

matcher_file="$repo_root/src/Netclaw.Security/IToolApprovalMatcher.cs"
read -r candidate_start candidate_end < <(
  find_span \
    "$matcher_file" \
    "private IReadOnlyList<ApprovalCandidate> ExtractCandidatesViaAnalysis" \
    "if (!result.IsResolved" \
    "return [];"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "IToolApprovalMatcher.cs" \
  "$candidate_start" \
  "$candidate_end" \
  "$output_path/reusable-candidates" \
  5

read -r messy_start messy_end < <(
  find_span \
    "$matcher_file" \
    "private bool IsMessy(ShellCommandAnalysis analysis)" \
    "if (!analysis.IsResolved" \
    "return true;"
)
run_target \
  "stryker-shell-command-analysis.json" \
  "IToolApprovalMatcher.cs" \
  "$messy_start" \
  "$messy_end" \
  "$output_path/messy-analysis" \
  6

tool_policy_file="$repo_root/src/Netclaw.Actors/Tools/ToolAccessPolicy.cs"
read -r mode_start mode_end < <(
  find_span \
    "$tool_policy_file" \
    "internal static ToolApprovalMode ResolveShellApprovalMode(" \
    "=> configuredMode == ToolApprovalMode.Auto" \
    ": configuredMode;"
)
run_target \
  "stryker-config.json" \
  "Tools/ToolAccessPolicy.cs" \
  "$mode_start" \
  "$mode_end" \
  "$output_path/exact-tree-mode" \
  4

path_facts_file="$repo_root/src/Netclaw.Actors/Tools/ShellPolicyPathFacts.cs"
read -r path_fact_start path_fact_end < <(
  find_span \
    "$path_facts_file" \
    "foreach (var access in occurrence.FileSystemTreeAccesses)" \
    "facts.Add(CreateFact(" \
    "ShellPathShape.Unknown));"
)
run_target \
  "stryker-config.json" \
  "Tools/ShellPolicyPathFacts.cs" \
  "$path_fact_start" \
  "$path_fact_end" \
  "$output_path/tree-path-fact" \
  1

reviewed_file="$repo_root/src/Netclaw.Actors/Tools/ReviewedSafeShellPolicy.cs"
read -r reviewed_start reviewed_end < <(
  find_span \
    "$reviewed_file" \
    "private bool IsReviewedDiagnosticSyntax(" \
    "if (ShellFileSystemTreeAccessPolicy.RequiresExactApproval(" \
    "return false;"
)
run_target \
  "stryker-config.json" \
  "Tools/ReviewedSafeShellPolicy.cs" \
  "$reviewed_start" \
  "$reviewed_end" \
  "$output_path/reviewed-safe-tree" \
  4
