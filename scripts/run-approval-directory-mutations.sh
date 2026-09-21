#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/approval-directory}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Resolve each boundary separately. Source drift must fail before Stryker starts.
spans="$(
  python3 - "$repo_root" <<'PY'
from pathlib import Path
import sys

repo_root = Path(sys.argv[1])
targets = [
    (
        "src/Netclaw.Security/ApprovalPatternMatching.cs",
        "!IsWithinWindowsRoot(normalizedCandidate, normalizedRoot)",
        1,
    ),
    (
        "src/Netclaw.Security/ApprovalPatternMatching.cs",
        "if (!PathUtility.IsNormalizedWithinRoot(normalizedCandidate, entry.Directory))\n"
        "                return ShellApprovalScopeResult.OutsideDirectory;",
        1,
    ),
    (
        "src/Netclaw.Security/ApprovalPatternMatching.cs",
        "return PathUtility.ContainsSymlinkSegment(entry.Directory, effectiveDirectory)\n"
        "                ? ShellApprovalScopeResult.Symlink\n"
        "                : ShellApprovalScopeResult.Match;",
        2,
    ),
    (
        "src/Netclaw.Security/ApprovalPatternMatching.cs",
        "return GitRepositoryApprovalScope.TryResolveCandidate(candidateDirectory, cwd, out var scope)\n"
        "                   && ToolApprovalEntryComparer.Equals(scope!.CommonDirectory, entry.Repository)\n"
        "                ? ShellApprovalScopeResult.Match\n"
        "                : ShellApprovalScopeResult.OutsideDirectory;",
        3,
    ),
    (
        "src/Netclaw.Security/GitRepositoryApprovalScope.cs",
        "!PathUtility.AreEquivalentPaths(resolved[0].CommonDirectory, scope.CommonDirectory)",
        1,
    ),
    (
        "src/Netclaw.Security/GitRepositoryApprovalScope.cs",
        "!PathUtility.AreEquivalentPaths(reverse, dotGit)",
        1,
    ),
]

for relative_path, marker, expected_count in targets:
    source = repo_root / relative_path
    text = source.read_text(encoding="utf-8")
    start = text.find(marker)
    if start < 0 or text.find(marker, start + 1) >= 0:
        raise SystemExit("An approval boundary is missing or duplicated.")
    line = text.count("\n", 0, start) + 1
    print(
        source.name,
        source,
        start,
        start + len(marker),
        line,
        expected_count,
        sep="\t",
    )
PY
)"

mutate_args=()
while IFS=$'\t' read -r source_name _source_file span_start span_end _line _count; do
  mutate_args+=(--mutate "$source_name{$span_start..$span_end}")
done <<< "$spans"

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Security.csproj \
    "${mutate_args[@]}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
while IFS=$'\t' read -r _source_name source_file _span_start _span_end line count; do
  jq -e --arg source "$source_file" --argjson line "$line" --argjson count "$count" '
    [.files[$source].mutants[] | select(.status != "Ignored")
      | select(.location.start.line == $line)] as $mutants
    | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
  ' "$report" > /dev/null || {
    echo "Expected $count killed approval directory mutants at line $line." >&2
    exit 1
  }
done <<< "$spans"

# Each target above must die even if Stryker reports unrelated compiler errors.
jq -e '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length == 9' \
  "$report" > /dev/null || {
  echo "Expected exactly nine approval directory mutants." >&2
  exit 1
}

# The approval actor is the final authority boundary before persistence.
actor_source="$repo_root/src/Netclaw.Actors/Tools/ToolApprovalActor.cs"
actor_output="$output_path/actor"
actor_spans="$(
  python3 - "$actor_source" <<'PY'
from pathlib import Path
import sys

source = Path(sys.argv[1])
text = source.read_text(encoding="utf-8")
targets = [
    (
        "!candidateResolved",
        1,
    ),
    (
        "!ToolApprovalEntryComparer.Equals(scope!.CommonDirectory, grant.Repository)",
        1,
    ),
    (
        "!PathUtility.AreEquivalentPaths(\n"
        "                                scope.WorktreeRoot, grant.RepositoryWorktree)",
        1,
    ),
]

for marker, expected_count in targets:
    start = text.find(marker)
    if start < 0 or text.find(marker, start + 1) >= 0:
        raise SystemExit("An approval persistence boundary is missing or duplicated.")
    line = text.count("\n", 0, start) + 1
    print(start, start + len(marker), line, expected_count, sep="\t")
PY
)"

actor_mutate_args=()
while IFS=$'\t' read -r span_start span_end _line _count; do
  actor_mutate_args+=(--mutate "Tools/ToolApprovalActor.cs{$span_start..$span_end}")
done <<< "$actor_spans"

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Actors.csproj \
    "${actor_mutate_args[@]}" \
    --output "$actor_output" \
    --skip-version-check
)

actor_report="$actor_output/reports/mutation-report.json"
while IFS=$'\t' read -r _span_start _span_end line count; do
  jq -e --arg source "$actor_source" --argjson line "$line" --argjson count "$count" '
    [.files[$source].mutants[] | select(.status != "Ignored")
      | select(.location.start.line == $line)] as $mutants
    | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
  ' "$actor_report" > /dev/null || {
    echo "Expected $count killed approval persistence mutants at line $line." >&2
    exit 1
  }
done <<< "$actor_spans"

jq -e '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length == 3' \
  "$actor_report" > /dev/null || {
  echo "Expected exactly three approval persistence mutants." >&2
  exit 1
}
