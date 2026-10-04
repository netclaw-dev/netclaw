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

run_group() {
  local config_file="$1"
  local target_output="$2"
  local expected_count="$3"
  shift 3

  local mutate_args=()
  local mutation
  for mutation in "$@"; do
    mutate_args+=(--mutate "$mutation")
  done

  (
    cd "$test_project"
    dotnet stryker \
      --config-file "$config_file" \
      "${mutate_args[@]}" \
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

security_mutations=()

analysis_file="$repo_root/src/Netclaw.Security/ShellCommandAnalysis.cs"
read -r region_start region_end < <(
  find_span \
    "$analysis_file" \
    "private static bool IsAccountedExecutionRegionArgument" \
    "=> argument.Argument.Kind == ArgKind.DynamicSkip" \
    "&& accountedRegionArguments.Contains(argument.Element);"
)
security_mutations+=("ShellCommandAnalysis.cs{$region_start..$region_end}")

policy_file="$repo_root/src/Netclaw.Security/ShellCommandPolicy.cs"
read -r gate_start gate_end < <(
  find_span \
    "$policy_file" \
    "private ShellCommandDecision EvaluateStructuralAnalysis" \
    "var denyOnlyDecision = EvaluateDenyOnlyClauses" \
    "return denyOnlyDecision;"
)
security_mutations+=("ShellCommandPolicy.cs{$gate_start..$gate_end}")

read -r trust_start trust_end < <(
  find_span \
    "$policy_file" \
    "private static bool FirstNonFlagMatchesConstraint" \
    "if (!tokens[i].IsKnown)" \
    "return false;"
)
security_mutations+=("ShellCommandPolicy.cs{$trust_start..$trust_end}")

tree_policy_file="$repo_root/src/Netclaw.Security/ShellFileSystemTreeAccessPolicy.cs"
read -r tree_decision_start tree_decision_end < <(
  find_span \
    "$tree_policy_file" \
    "internal static bool RequiresExactApproval(" \
    "var accesses = command.FileSystemTreeAccesses;" \
    "return !CanUseReusableApproval(command, accesses[0]);"
)
security_mutations+=("ShellFileSystemTreeAccessPolicy.cs{$tree_decision_start..$tree_decision_end}")

read -r tree_root_start tree_root_end < <(
  find_span \
    "$tree_policy_file" \
    "private static bool CanUseReusableApproval(" \
    "if (!IsReusableTraversal(access.Traversal)" \
    "&& string.Equals(root.Value, cwd.Value, StringComparison.Ordinal);"
)
security_mutations+=("ShellFileSystemTreeAccessPolicy.cs{$tree_root_start..$tree_root_end}")

read -r root_match_start root_match_end < <(
  find_span \
    "$tree_policy_file" \
    "private static bool RootMatchesArgument(" \
    "=> root switch" \
    "_ => false"
)
security_mutations+=("ShellFileSystemTreeAccessPolicy.cs{$root_match_start..$root_match_end}")

read -r leaf_call_start leaf_call_end < <(
  find_span \
    "$tree_policy_file" \
    "private static bool TryGetLeafPatternCoveringDirectory(" \
    "!IsReusableLeafPattern(pattern)" \
    "!IsReusableLeafPattern(pattern)"
)
security_mutations+=("ShellFileSystemTreeAccessPolicy.cs{$leaf_call_start..$leaf_call_end}")

read -r leaf_shape_start leaf_shape_end < <(
  find_span \
    "$tree_policy_file" \
    "internal static bool IsReusableLeafPattern(" \
    "internal static bool IsReusableLeafPattern(" \
    "return separator != 0 && !IsIncompleteUncLeafPattern(pattern, separator);"
)
security_mutations+=("ShellFileSystemTreeAccessPolicy.cs{$leaf_shape_start..$leaf_shape_end}")

read -r traversal_start traversal_end < <(
  find_span \
    "$tree_policy_file" \
    "internal static bool IsReusableTraversal" \
    "=> Enum.IsDefined(traversal)" \
    "or ShellTreeTraversalMode.RecursiveWithoutFollowingLinks;"
)
security_mutations+=("ShellFileSystemTreeAccessPolicy.cs{$traversal_start..$traversal_end}")

read -r kill_start kill_end < <(
  find_span \
    "$policy_file" \
    "internal sealed record DaemonProcessKillDenyPattern(" \
    "return KillVerbs.Contains(verb)" \
    "token.AuthoredValue.Contains(DaemonName, StringComparison.OrdinalIgnoreCase));"
)
security_mutations+=("ShellCommandPolicy.cs{$kill_start..$kill_end}")

read -r nonfile_start nonfile_end < <(
  find_span \
    "$analysis_file" \
    "internal static bool HasAuditedNonFileSystemValue(AnalyzedArgument argument)" \
    "if (argument.Argument.IsPath" \
    "ShellValueDomain.Unknown => HasMatchingIntegerRange(argument),"
)
security_mutations+=("ShellCommandAnalysis.cs{$nonfile_start..$nonfile_end}")

read -r range_start range_end < <(
  find_span \
    "$analysis_file" \
    "private static bool HasMatchingIntegerRange" \
    "=> argument.Value is" \
    "< MaximumReviewedIntegerRangeCardinality;"
)
security_mutations+=("ShellCommandAnalysis.cs{$range_start..$range_end}")

read -r status_start status_end < <(
  find_span \
    "$analysis_file" \
    "private static bool IsUnknownOutputData(" \
    "argument.Argument.Raw == \"\$?\"" \
    "argument.Argument.Raw == \"\$?\""
)
security_mutations+=("ShellCommandAnalysis.cs{$status_start..$status_end}")

read -r data_start data_end < <(
  find_span \
    "$analysis_file" \
    "private bool HasOnlyDataOperands(" \
    "=> Environment.Grammar == ShellGrammar.Bash" \
    "&& ShellVerbPolicyData.SingleTokenSideEffectVerbs.Contains(verb);"
)
security_mutations+=("ShellCommandAnalysis.cs{$data_start..$data_end}")

read -r data_use_start data_use_end < <(
  find_span \
    "$analysis_file" \
    "private ShellUnresolvedPart ClassifyUnresolvedPart(" \
    "return !HasOnlyDataOperands(command)" \
    ": ShellUnresolvedPart.None;"
)
security_mutations+=("ShellCommandAnalysis.cs{$data_use_start..$data_use_end}")

matcher_file="$repo_root/src/Netclaw.Security/IToolApprovalMatcher.cs"
read -r candidate_start candidate_end < <(
  find_span \
    "$matcher_file" \
    "private IReadOnlyList<ApprovalCandidate> ExtractCandidatesViaAnalysis" \
    "if (!result.IsResolved" \
    "return [];"
)
security_mutations+=("IToolApprovalMatcher.cs{$candidate_start..$candidate_end}")

read -r control_start control_end < <(
  find_span \
    "$matcher_file" \
    "private static string? ResolveControlCharacterScope(" \
    "var firstControl = resolved.AsSpan().IndexOfAny(ControlCharacters);" \
    ": GetRedirectDirectory(resolved![..firstControl], pathStyle);"
)
security_mutations+=("IToolApprovalMatcher.cs{$control_start..$control_end}")

read -r absent_guard_start absent_guard_end < <(
  find_span \
    "$matcher_file" \
    "private static bool HasAbsentTopLevelDirectory(" \
    "if (!CanonicalPath.IsHostPathStyle(pathStyle)" \
    "|| !Directory.Exists(workingDirectory))"
)
security_mutations+=("IToolApprovalMatcher.cs{$absent_guard_start..$absent_guard_end}")

read -r absent_start absent_end < <(
  find_span \
    "$matcher_file" \
    "private static bool HasAbsentTopLevelDirectory(" \
    "return !Path.Exists(topLevel);" \
    "return !Path.Exists(topLevel);"
)
security_mutations+=("IToolApprovalMatcher.cs{$absent_start..$absent_end}")

read -r split_start split_end < <(
  find_span \
    "$matcher_file" \
    "private IReadOnlyList<ApprovalCandidate> ExtractCommandCandidates(" \
    "var part = occurrence.WorkingDirectory is ShellValueDomain.Exact" \
    ": ShellUnresolvedPart.Command;"
)
security_mutations+=("IToolApprovalMatcher.cs{$split_start..$split_end}")

read -r exact_start exact_end < <(
  find_span \
    "$matcher_file" \
    "private ApprovalCandidate? CreateExactCandidate(" \
    "var unresolved = part == ShellUnresolvedPart.None" \
    "Unresolved = unresolved,"
)
security_mutations+=("IToolApprovalMatcher.cs{$exact_start..$exact_end}")

read -r messy_start messy_end < <(
  find_span \
    "$matcher_file" \
    "private bool IsMessy(ShellCommandAnalysis analysis, LinkRule hostLinks)" \
    "if (!analysis.IsResolved" \
    "return true;"
)
security_mutations+=("IToolApprovalMatcher.cs{$messy_start..$messy_end}")

run_group \
  "stryker-shell-command-analysis.json" \
  "$output_path/security" \
  92 \
  "${security_mutations[@]}"

actor_mutations=()

tool_policy_file="$repo_root/src/Netclaw.Actors/Tools/ToolAccessPolicy.cs"
read -r mode_start mode_end < <(
  find_span \
    "$tool_policy_file" \
    "internal static ToolApprovalMode ResolveShellApprovalMode(" \
    "=> configuredMode == ToolApprovalMode.Auto" \
    ": configuredMode;"
)
actor_mutations+=("Tools/ToolAccessPolicy.cs{$mode_start..$mode_end}")

path_facts_file="$repo_root/src/Netclaw.Actors/Tools/ShellPolicyPathFacts.cs"
read -r path_fact_start path_fact_end < <(
  find_span \
    "$path_facts_file" \
    "foreach (var access in occurrence.FileSystemTreeAccesses)" \
    "facts.Add(CreateFact(" \
    "ShellPathShape.Unknown));"
)
actor_mutations+=("Tools/ShellPolicyPathFacts.cs{$path_fact_start..$path_fact_end}")

reviewed_file="$repo_root/src/Netclaw.Actors/Tools/ReviewedSafeShellPolicy.cs"
read -r reviewed_start reviewed_end < <(
  find_span \
    "$reviewed_file" \
    "private bool IsReviewedDiagnosticSyntax(" \
    "if (ShellFileSystemTreeAccessPolicy.RequiresExactApproval(" \
    "return false;"
)
actor_mutations+=("Tools/ReviewedSafeShellPolicy.cs{$reviewed_start..$reviewed_end}")

coordinator_file="$repo_root/src/Netclaw.Actors/Tools/ShellPolicyCoordinator.cs"
read -r d1_grant_start d1_grant_end < <(
  find_span \
    "$coordinator_file" \
    "private static ShellApprovalMatchResult KeepUnknownOperandGlobalGrants(" \
    "return candidate.Unresolved == ShellUnresolvedPart.None" \
    "&& evidence.Grant is { Scope: GrantScope.Everywhere }"
)
actor_mutations+=("Tools/ShellPolicyCoordinator.cs{$d1_grant_start..$d1_grant_end}")

read -r split_use_start split_use_end < <(
  find_span \
    "$tool_policy_file" \
    "internal static ShellApprovalAnalysis WithCommandCandidates(" \
    "=> approval is { IsMessy: true, Candidates.Count: 0, CommandCandidates.Count: > 0 }" \
    "&& context.RunScope.InteractiveApproval is InteractiveApprovalCapability.Available"
)
actor_mutations+=("Tools/ToolAccessPolicy.cs{$split_use_start..$split_use_end}")

read -r reusable_start reusable_end < <(
  find_span \
    "$tool_policy_file" \
    "private static bool HasReusableShellPhrase(" \
    "&& candidate.Unresolved == ShellUnresolvedPart.None" \
    "&& candidate.Unresolved == ShellUnresolvedPart.None"
)
actor_mutations+=("Tools/ToolAccessPolicy.cs{$reusable_start..$reusable_end}")

read -r d1_safe_start d1_safe_end < <(
  find_span \
    "$reviewed_file" \
    "string? proposedProjectRoot = null," \
    "if (candidate.Unresolved == ShellUnresolvedPart.Command" \
    "|| candidate.Unresolved == ShellUnresolvedPart.Operand && !allowUnknownOperands"
)
actor_mutations+=("Tools/ReviewedSafeShellPolicy.cs{$d1_safe_start..$d1_safe_end}")

read -r d1_fact_start d1_fact_end < <(
  find_span \
    "$reviewed_file" \
    "private bool AllAuthoredPathsStayWithinRoots(" \
    "if (allowUnknownOperands" \
    "&& fact.State == ShellPolicyPathResolutionState.UnknownDynamic)"
)
actor_mutations+=("Tools/ReviewedSafeShellPolicy.cs{$d1_fact_start..$d1_fact_end}")

read -r d1_call_start d1_call_end < <(
  find_span \
    "$reviewed_file" \
    "internal bool ShortCircuits(" \
    "allowUnknownOperands: candidate.Unresolved == ShellUnresolvedPart.Operand)" \
    "allowUnknownOperands: candidate.Unresolved == ShellUnresolvedPart.Operand)"
)
actor_mutations+=("Tools/ReviewedSafeShellPolicy.cs{$d1_call_start..$d1_call_end}")

run_group \
  "stryker-config.json" \
  "$output_path/actors" \
  28 \
  "${actor_mutations[@]}"
