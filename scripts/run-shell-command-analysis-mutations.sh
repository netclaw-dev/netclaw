#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
part="all"
output_root="$repo_root/artifacts/stryker/shell-command-analysis"
if [[ "${1:-}" == "--part" ]]; then
  part="${2:?Set the shell mutation part to a, b, or all.}"
  output_root="${3:-$output_root}"
elif [[ "${1:-}" == "--verify-b" ]]; then
  part="verify-b"
  output_root="${2:?Set the retained shell mutation output directory.}"
elif [[ "${1:-}" == "--collect" ]]; then
  part="collect"
else
  output_root="${1:-$output_root}"
fi
if [[ "$output_root" != /* ]]; then
  output_root="$repo_root/$output_root"
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

  local worker_args=()
  if [[ "$target_output" == */security-b ]]; then
    worker_args=(--concurrency 2)
  fi
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
      "${worker_args[@]}" \
      --output "$target_output" \
      --skip-version-check
  )

  verify_group "$target_output" "$expected_count"
}

verify_group() {
  local target_output="$1"
  local expected_count="$2"
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
    "|| HasProvedDataOperands(command, isTestBuiltin: true));"
)
security_mutations+=("ShellCommandAnalysis.cs{$data_start..$data_end}")

read -r data_use_start data_use_end < <(
  find_span \
    "$analysis_file" \
    "private ShellUnresolvedPart ClassifyUnresolvedPart(" \
    "if (HasOnlyDataOperands(command))" \
    ": ShellUnresolvedPart.None;"
)
security_mutations+=("ShellCommandAnalysis.cs{$data_use_start..$data_use_end}")

read -r test_verb_start test_verb_end < <(
  find_span \
    "$analysis_file" \
    "private bool HasTestBuiltinVerb(" \
    "=> Environment.Grammar == ShellGrammar.Bash" \
    "&& ShellVerbPolicyData.BashTestBuiltins.Contains(verb);"
)
security_mutations+=("ShellCommandAnalysis.cs{$test_verb_start..$test_verb_end}")

read -r name_safe_start name_safe_end < <(
  find_span \
    "$analysis_file" \
    "private static bool HasBoundedNameSafeValue(" \
    "=> argument.Value switch" \
    "&& !value.Contains('[', StringComparison.Ordinal);"
)
security_mutations+=("ShellCommandAnalysis.cs{$name_safe_start..$name_safe_end}")

read -r proved_start proved_end < <(
  find_span \
    "$analysis_file" \
    "internal static bool HasProvedDataOperands(" \
    "=> command.Arguments.All(argument => isTestBuiltin" \
    "|| HasGlobFreeAuthoredValue(argument));"
)
security_mutations+=("ShellCommandAnalysis.cs{$proved_start..$proved_end}")

read -r expansion_start expansion_end < <(
  find_span \
    "$analysis_file" \
    "private bool HasUnboundedPathnameExpansion(" \
    "=> Environment.Grammar == ShellGrammar.Bash" \
    "=> value is not null && value.IndexOfAny(['*', '?', '[']) < 0;"
)
security_mutations+=("ShellCommandAnalysis.cs{$expansion_start..$expansion_end}")

# A variable word with no path scope is an unknown operand (D1).
read -r unscoped_start unscoped_end < <(
  find_span \
    "$analysis_file" \
    "private static bool IsUnscopedVariableWord(AnalyzedArgument argument)" \
    "=> argument.Argument.Kind == ArgKind.EnvVar" \
    "&& argument.Value is not ShellValueDomain.IntegerRange;"
)
security_mutations+=("ShellCommandAnalysis.cs{$unscoped_start..$unscoped_end}")

read -r unscoped_use_start unscoped_use_end < <(
  find_span \
    "$analysis_file" \
    "private static bool HasUnresolvedOperand(" \
    "(HasUnsupportedArgumentDomain(argument) || IsUnscopedVariableWord(argument))" \
    "(HasUnsupportedArgumentDomain(argument) || IsUnscopedVariableWord(argument))"
)
security_mutations+=("ShellCommandAnalysis.cs{$unscoped_use_start..$unscoped_use_end}")

verb_data_file="$repo_root/src/Netclaw.Security/ShellVerbPolicyData.cs"
read -r data_verb_start data_verb_end < <(
  find_span \
    "$verb_data_file" \
    "internal static bool IsDataCommand(" \
    "=> SingleTokenSideEffectVerbs.Contains(verb)" \
    "|| BashControlTransferBuiltins.Contains(verb));"
)
security_mutations+=("ShellVerbPolicyData.cs{$data_verb_start..$data_verb_end}")

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

read -r digest_start digest_end < <(
  find_span \
    "$matcher_file" \
    "private static bool TryCreateAssignmentDigest(" \
    "if (IsScopeFreeDataCommand(occurrence, shell, verb))" \
    "return true;"
)
security_mutations+=("IToolApprovalMatcher.cs{$digest_start..$digest_end}")

# F2: a data command with no redirect and proved data operands has no path
# scope, so an unknown directory keeps its normal candidate. A mutant that drops
# a condition gives another command (cat, a redirect, an unproved operand) the
# call directory as a wrong scope, so it must die.
read -r scope_free_start scope_free_end < <(
  find_span \
    "$matcher_file" \
    "private static bool IsScopeFreeDataCommand(" \
    "=> shell == ApprovalShell.Bash" \
    "isTestBuiltin: ShellVerbPolicyData.BashTestBuiltins.Contains(verb));"
)
security_mutations+=("IToolApprovalMatcher.cs{$scope_free_start..$scope_free_end}")

read -r messy_start messy_end < <(
  find_span \
    "$matcher_file" \
    "private bool IsMessy(ShellCommandAnalysis analysis, LinkRule hostLinks)" \
    "if (!analysis.IsResolved" \
    "return true;"
)
security_mutations+=("IToolApprovalMatcher.cs{$messy_start..$messy_end}")

# #2364: an option value can name a path for the program. A value that can
# leave the working directory gets the scope of a path word with the same
# text, so a folder or repository grant cannot cover it. A mutant that drops
# the scope, skips a value, or calls an outside value inside must die.
read -r option_use_start option_use_end < <(
  find_span \
    "$matcher_file" \
    "private static IReadOnlyList<string?>? ResolveCommandDirectories(" \
    "for (var index = 1; index < occurrence.Arguments.Count; index++)" \
    "directories)))"
)
security_mutations+=("IToolApprovalMatcher.cs{$option_use_start..$option_use_end}")

read -r option_value_start option_value_end < <(
  find_span \
    "$matcher_file" \
    "private static IReadOnlyList<Arg>? ResolveOptionValuePathWords(" \
    "var isGlob = value.Argument.Kind == ArgKind.Glob;" \
    "return words;"
)
security_mutations+=("IToolApprovalMatcher.cs{$option_value_start..$option_value_end}")

read -r option_location_start option_location_end < <(
  find_span \
    "$matcher_file" \
    "private static bool TryCreateLocation(" \
    "return CanonicalPath.IsHostPathStyle(pathStyle)" \
    "&& CanonicalPath.TryCreate(text, cwd.Value, pathStyle, out location);"
)
security_mutations+=("IToolApprovalMatcher.cs{$option_location_start..$option_location_end}")

read -r option_stays_start option_stays_end < <(
  find_span \
    "$matcher_file" \
    "private static bool StaysInWorkingDirectory(" \
    "=> FileSystemAuthority.EvaluateMembership(" \
    "[new PathBoundary.Folder(cwd, LinkRule.BelowRoot)]) is PathDecision.Allowed;"
)
security_mutations+=("IToolApprovalMatcher.cs{$option_stays_start..$option_stays_end}")

# ShellSyntaxTree 0.4.0-beta.17 facts. Decision D5 (option A): a glob word gets
# the decision of each literal path that its segments can match.
path_policy_file="$repo_root/src/Netclaw.Security/ToolPathPolicy.cs"
glob_file="$repo_root/src/Netclaw.Security/ShellGlobScope.cs"
read -r glob_deny_start glob_deny_end < <(
  find_span \
    "$path_policy_file" \
    "private static bool MatchIsDenied(" \
    "var match = ShellGlobScope.MatchPathToward(glob, target);" \
    "|| IsShellDenied(shell, match));"
)
security_mutations+=("ToolPathPolicy.cs{$glob_deny_start..$glob_deny_end}")

read -r credential_start credential_end < <(
  find_span \
    "$path_policy_file" \
    "private IEnumerable<string> DefaultCredentialStorePaths()" \
    "var home = Environment.HomeDirectory;" \
    '"config", "secrets.json"))'
)
security_mutations+=("ToolPathPolicy.cs{$credential_start..$credential_end}")

# ShellSyntaxTree 0.4.0-beta.19 decodes ANSI-C words. A proved value gets the
# protected list and the default credential store text hints.
read -r proved_value_start proved_value_end < <(
  find_span \
    "$path_policy_file" \
    "private static bool IsProvedValueDenied(" \
    "=> IsShellDenied(shell, value)" \
    "StringComparison.OrdinalIgnoreCase));"
)
security_mutations+=("ToolPathPolicy.cs{$proved_value_start..$proved_value_end}")

read -r glob_fact_start glob_fact_end < <(
  find_span \
    "$glob_file" \
    "internal static ShellValueDomain.PathPattern? AsGlobPattern(" \
    "=> domain is ShellValueDomain.PathPattern" \
    ": null;"
)
security_mutations+=("ShellGlobScope.cs{$glob_fact_start..$glob_fact_end}")

read -r segment_start segment_end < <(
  find_span \
    "$glob_file" \
    "internal static bool SegmentMayMatch(" \
    "if (!segment.IsPattern)" \
    "ignoreCase: true);"
)
security_mutations+=("ShellGlobScope.cs{$segment_start..$segment_end}")

read -r toward_start toward_end < <(
  find_span \
    "$glob_file" \
    "private static string? MatchPathToward(" \
    "const char separator = '/';" \
    "return reach == 0 ? null : prefix + string.Join(separator, relative[..reach]);"
)
security_mutations+=("ShellGlobScope.cs{$toward_start..$toward_end}")

read -r bracket_start bracket_end < <(
  find_span \
    "$glob_file" \
    "private static string ToSimpleExpression(" \
    "=> text.Contains('[', StringComparison.Ordinal)" \
    '? "*" : text;'
)
security_mutations+=("ShellGlobScope.cs{$bracket_start..$bracket_end}")

# The complete result is the reusable-scope boundary. The alias gate covers
# target checks and the two work counters separately.
read -r walk_start walk_end < <(
  find_span \
    "$glob_file" \
    "internal static ShellGlobReach ResolveReach(" \
    "return complete" \
    ": new ShellGlobReach.Unresolved(scopePaths, targetReach);"
)
security_mutations+=("ShellGlobScope.cs{$walk_start..$walk_end}")

read -r glob_part_start glob_part_end < <(
  find_span \
    "$analysis_file" \
    "var globMayAddOption = false;" \
    "var globMayAddOption = false;" \
    "globMayAddOption |= pattern.Glob!.MayStartWithDash;"
)
security_mutations+=("ShellCommandAnalysis.cs{$glob_part_start..$glob_part_end}")

# A bound value gets the hard-deny decision of its literal twin.
read -r effective_use_start effective_use_end < <(
  find_span \
    "$policy_file" \
    "private ShellCommandDecision EvaluateStructuralAnalysis(" \
    "if (Environment.Grammar == ShellGrammar.Bash)" \
    "if (!decision.Allowed)"
)
security_mutations+=("ShellCommandPolicy.cs{$effective_use_start..$effective_use_end}")

read -r effective_start effective_end < <(
  find_span \
    "$policy_file" \
    "private ShellCommandDecision EvaluateEffectiveValues(" \
    "var choices = new List<IReadOnlyList<string>>();" \
    "return ShellCommandDecision.Allow();"
)
security_mutations+=("ShellCommandPolicy.cs{$effective_start..$effective_end}")

read -r proved_start proved_end < <(
  find_span \
    "$policy_file" \
    "private static IReadOnlyList<string>? ProvedValues(" \
    "foreach (var argument in occurrence.Arguments)" \
    "_ => null"
)
security_mutations+=("ShellCommandPolicy.cs{$proved_start..$proved_end}")

read -r combine_start combine_end < <(
  find_span \
    "$policy_file" \
    "private static IEnumerable<IReadOnlyList<string>> Combine(" \
    "IEnumerable<IReadOnlyList<string>> combinations = [[]];" \
    "return combinations;"
)
security_mutations+=("ShellCommandPolicy.cs{$combine_start..$combine_end}")

# Fixed text on stdin is data only for a receiver that is not a shell, and
# only when the heredoc does not expand or the here string has a proved value.
read -r stdin_arm_start stdin_arm_end < <(
  find_span \
    "$analysis_file" \
    "private static bool HasUnresolvedRedirect(" \
    "HereDocumentRedirectAnalysis heredoc =>" \
    "!HasFixedTextStdin(occurrence, hereString),"
)
security_mutations+=("ShellCommandAnalysis.cs{$stdin_arm_start..$stdin_arm_end}")

read -r stdin_start stdin_end < <(
  find_span \
    "$analysis_file" \
    "private static bool HasFixedTextStdin(" \
    "=> IsStandardInputSource(redirect.Source)" \
    "&& MayNameScriptShell(argument.Value));"
)
security_mutations+=("ShellCommandAnalysis.cs{$stdin_start..$stdin_end}")

read -r shell_value_start shell_value_end < <(
  find_span \
    "$analysis_file" \
    "private static bool MayNameScriptShell(" \
    "=> value switch" \
    ".Any(ShellVerbPolicyData.IsScriptShellProgram);"
)
security_mutations+=("ShellCommandAnalysis.cs{$shell_value_start..$shell_value_end}")

read -r literal_start literal_end < <(
  find_span \
    "$analysis_file" \
    "private static bool HasLiteralHereDocument(" \
    "&& hereDocument.ExpansionMode == HereDocumentExpansionMode.Literal" \
    "&& hereDocument.ExpansionMode == HereDocumentExpansionMode.Literal"
)
security_mutations+=("ShellCommandAnalysis.cs{$literal_start..$literal_end}")

verb_policy_file="$repo_root/src/Netclaw.Security/ShellVerbPolicyData.cs"
read -r shell_name_start shell_name_end < <(
  find_span \
    "$verb_policy_file" \
    "internal static bool IsScriptShellProgram(" \
    "var program = LegacyShellTextScan.TrimShellPunctuation(word);" \
    "return ScriptShellNames.Contains(name);"
)
security_mutations+=("ShellVerbPolicyData.cs{$shell_name_start..$shell_name_end}")

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
    "=> approval is { IsMessy: true, Candidates.Count: 0, CommandCandidates.Count: > 0 }"
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
    "private bool IsReviewedDiagnostic(" \
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

if [[ "${#security_mutations[@]}" -ne 53 || "${#actor_mutations[@]}" -ne 9 ]]; then
  echo "The shell analysis target set changed. Review its partition before Stryker runs." >&2
  exit 1
fi

# These partitions use one list of source spans.
# The workflow runs each partition in a separate job.
# The two groups keep the Security mutants outside one long group with Actors.
security_a_mutations=("${security_mutations[@]:0:27}")
security_b_mutations=("${security_mutations[@]:27}")
# These narrow spans retain the contained-alias authority boundaries in the
# existing Security-B invocation. A missing or duplicated marker stops setup.
alias_spans="$(
  python3 - "$repo_root" <<'ALIAS_SPANS'
from pathlib import Path
import sys

root = Path(sys.argv[1]) / "src/Netclaw.Security"
targets = targets = targets = [
    ('ShellGlobScope.cs', '!coveringDirectory.Contains(knownPath)', 1, '*'),
    ('ShellGlobScope.cs', 'matches && isDirectory && index < glob.SegmentDepth - 1', 5, '*'),
    ('ShellGlobScope.cs', 'if (walked > MaximumWalkWork)', 3, '*'),
    ('ShellGlobScope.cs', 'if (++walked > MaximumWalkWork)', 4, '*'),
    ('ShellGlobScope.cs', 'directory.Ancestors.Contains(target) || target.Contains(directory.Path)', 2, '*'),
    ('ShellGlobScope.cs', 'scopes.Add(isDirectory\n                                    ? target\n                                    : CanonicalPath.TryCreateHost(Path.GetDirectoryName(path), null, out parent) ? parent : target);', 4, '*'),
    ('ShellGlobScope.cs', 'index < glob.SegmentDepth - 1 || hasTrailingSlash', 4, '*'),
    ('ShellGlobScope.cs', 'requiresDirectory && targetExists && !isDirectory', 4, '*'),
    ('ShellGlobScope.cs', 'targetExists && Directory.Exists(path)', 1, '*'),
    ('ShellGlobScope.cs', 'if (matches)\n                                        targets.Add(new ShellGlobTargetReach(knownPath, index + 1));', 3, '*'),
    ('ShellGlobScope.cs', '!targetExists\n                                    || !CanonicalPath.TryCreateHost(path, null, out target)', 3, '*'),
    ('ShellGlobScope.cs', 'FileSystemAuthority.EvaluateMembership(target,\n                                        [new PathBoundary.Folder(linkBoundary, LinkRule.BelowRoot)]) is not PathDecision.Allowed', 1, '*'),
    ('ShellGlobScope.cs', 'if (!matches)\n                                    continue;', 2, '*'),
    ('ToolPathPolicy.cs', 'IsProvedValueDenied(shell, known.Target.Value)', 1, '*'),
    ('ToolPathPolicy.cs', 'match is not null\n                    && (string.Equals(match, target, StringComparison.OrdinalIgnoreCase) || IsShellDenied(shell, match))', 4, '*'),
    ('ShellGlobScope.cs', 'reach == 0 ? null : prefix + string.Join(separator, relative[..reach])', 1, 'Conditional (false) mutation'),
]
for name, marker, count, mutator in targets:
    text = (root / name).read_text(encoding="utf-8")
    if text.count(marker) != 1:
        raise SystemExit("A contained-alias mutation boundary is missing or duplicated.")
    start = text.index(marker)
    first = text.count("\n", 0, start) + 1
    last = text.count("\n", 0, start + len(marker)) + 1
    print(f"{name}{{{start}..{start + len(marker)}}}", root / name, first, last, count, mutator, sep="\t")
ALIAS_SPANS
)"
while IFS=$'\t' read -r alias_span _source _first _last _count _mutator; do
  security_b_mutations+=("$alias_span")
done <<< "$alias_spans"

verify_alias_targets() {
  local report="$1"
  local receipt="$(dirname "$report")/contained-alias-targets.ndjson"
  : > "$receipt"
  while IFS=$'\t' read -r _span source first last count mutator; do
    jq -c --arg source "$source" --argjson first "$first" --argjson last "$last" --arg mutator "$mutator" '
      [.files[$source].mutants[] | select(.status != "Ignored")
        | select($mutator == "*" or .mutatorName == $mutator)
        | select(.location.start.line >= $first and .location.start.line <= $last)]
      | {source: $source, first: $first, last: $last, mutants: .}
    ' "$report" >> "$receipt"
    jq -e --arg source "$source" --argjson first "$first" --argjson last "$last" --argjson count "$count" --arg mutator "$mutator" '
      [.files[$source].mutants[] | select(.status != "Ignored")
        | select($mutator == "*" or .mutatorName == $mutator)
        | select(.location.start.line >= $first and .location.start.line <= $last)] as $mutants
      | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
    ' "$report" > /dev/null || {
      echo "Expected $count killed contained-alias mutants in $source at lines $first-$last." >&2
      return 1
    }
  done <<< "$alias_spans"
}
security_a_expected=131
security_b_expected=198
if [[ $((security_a_expected + security_b_expected)) -ne 329 ]]; then
  echo "The Security partitions must total 329 mutants." >&2
  exit 1
fi

case "$part" in
  verify-b)
    verify_group "$output_root/security-b" "$security_b_expected"
    verify_alias_targets "$output_root/security-b/reports/mutation-report.json"
    ;;
  collect)
    printf 'security-a spans=%s\n' "${#security_a_mutations[@]}"
    printf '%s\n' "${security_a_mutations[@]}"
    printf 'security-b spans=%s\n' "${#security_b_mutations[@]}"
    printf '%s\n' "${security_b_mutations[@]}"
    printf 'actors spans=%s\n' "${#actor_mutations[@]}"
    printf 'security total expected=329; actors expected=27; combined expected=356\n'
    ;;
  all)
    run_group "stryker-shell-command-analysis.json" "$output_root/security-a" \
      "$security_a_expected" "${security_a_mutations[@]}"
    run_group "stryker-shell-command-analysis.json" "$output_root/security-b" \
      "$security_b_expected" "${security_b_mutations[@]}"
    verify_alias_targets "$output_root/security-b/reports/mutation-report.json"
    run_group "stryker-config.json" "$output_root/actors" 27 "${actor_mutations[@]}"
    ;;
  a)
    run_group "stryker-shell-command-analysis.json" "$output_root/security-a" \
      "$security_a_expected" "${security_a_mutations[@]}"
    run_group "stryker-config.json" "$output_root/actors" 27 "${actor_mutations[@]}"
    ;;
  b)
    run_group "stryker-shell-command-analysis.json" "$output_root/security-b" \
      "$security_b_expected" "${security_b_mutations[@]}"
    verify_alias_targets "$output_root/security-b/reports/mutation-report.json"
    ;;
  *)
    echo "Usage: $0 [output-path] | --part a|b|all [output-path] | --verify-b output-path | --collect" >&2
    exit 2
    ;;
esac
