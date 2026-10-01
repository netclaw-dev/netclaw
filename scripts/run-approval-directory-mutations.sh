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
    # Folder grants: one containment rule and one link walker serve Bash and
    # PowerShell. The two shell-specific containment copies are gone.
    (
        "src/Netclaw.Security/Authorization/Filesystem/FileSystemAuthority.cs",
        "!folder.Root.Contains(path)",
        1,
    ),
    (
        "src/Netclaw.Security/Authorization/Filesystem/FileSystemAuthority.cs",
        "LinkRule.BelowRoot => CrossesLink(folder.LinkAnchor.Value, path.Value, includeAnchor: false)",
        1,
    ),
    (
        "src/Netclaw.Security/Authorization/Filesystem/FileSystemAuthority.cs",
        "crossesLink ? PathDecision.CrossesLink : PathDecision.Allowed",
        2,
    ),
    (
        "src/Netclaw.Security/ApprovalPatternMatching.cs",
        "return RepositoryIdentity.TryResolve(candidateDirectory, cwd, out var repository)\n"
        "                   && ToolApprovalEntryComparer.Equals(repository!.CommonDirectory, entry.Repository)\n"
        "                ? ShellApprovalScopeResult.Match\n"
        "                : ShellApprovalScopeResult.OutsideDirectory;",
        3,
    ),
    (
        "src/Netclaw.Security/Authorization/Filesystem/RepositoryIdentity.cs",
        "!PathUtility.AreEquivalentPaths(resolved[0].CommonDirectory, identity.CommonDirectory)",
        1,
    ),
    (
        "src/Netclaw.Security/Authorization/Filesystem/RepositoryIdentity.cs",
        "!PathUtility.AreEquivalentPaths(reverse, dotGit)",
        1,
    ),
    # A ".." that leaves a link makes the shell scope unresolved.
    (
        "src/Netclaw.Security/IToolApprovalMatcher.cs",
        "if (HasParentSegmentAfterLink(occurrence, clauseWorkingDirectory, pathStyle))\n"
        "            return null;",
        1,
    ),
    (
        "src/Netclaw.Security/Authorization/Filesystem/FileSystemAuthority.cs",
        "CrossesLink(parents.Peek(), parent, includeAnchor: false)",
        2,
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
        source.relative_to(repo_root / "src/Netclaw.Security").as_posix(),
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
jq -e '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length == 12' \
  "$report" > /dev/null || {
  echo "Expected exactly 12 approval directory mutants." >&2
  exit 1
}

# The approval actor is the final authority boundary before persistence. The
# grant builder decides the folder of each stored grant: the directory where
# the occurrence runs, never the session directory after a cd.
actor_root="$repo_root/src/Netclaw.Actors"
actor_output="$output_path/actor"
actor_spans="$(
  python3 - "$actor_root" <<'PY'
from pathlib import Path
import sys

root = Path(sys.argv[1])
targets = [
    (
        "Tools/ToolApprovalActor.cs",
        "!candidateResolved",
        1,
    ),
    (
        "Tools/ToolApprovalActor.cs",
        "!ToolApprovalEntryComparer.Equals(scope!.CommonDirectory, repository.CommonDirectory)",
        1,
    ),
    (
        "Tools/ToolApprovalActor.cs",
        "!PathUtility.AreEquivalentPaths(\n"
        "                scope.WorktreeRoot, grant.RepositoryWorktree)",
        1,
    ),
    (
        "Authorization/Consent/GrantBuilder.cs",
        "candidate.Directory ?? workingDirectory",
        3,
    ),
]

for relative_path, marker, expected_count in targets:
    source = root / relative_path
    text = source.read_text(encoding="utf-8")
    start = text.find(marker)
    if start < 0 or text.find(marker, start + 1) >= 0:
        raise SystemExit("An approval persistence boundary is missing or duplicated.")
    line = text.count("\n", 0, start) + 1
    print(relative_path, source, start, start + len(marker), line, expected_count, sep="\t")
PY
)"

actor_mutate_args=()
while IFS=$'\t' read -r source_name _source_file span_start span_end _line _count; do
  actor_mutate_args+=(--mutate "$source_name{$span_start..$span_end}")
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
while IFS=$'\t' read -r _source_name source_file _span_start _span_end line count; do
  # A whole-condition negation that Stryker cannot compile shares the first
  # target line. It is not a tested mutant; the count still requires each
  # selected negation to compile and die.
  jq -e --arg source "$source_file" --argjson line "$line" --argjson count "$count" '
    [.files[$source].mutants[] | select(.status != "Ignored" and .status != "CompileError")
      | select(.location.start.line == $line)] as $mutants
    | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
  ' "$actor_report" > /dev/null || {
    echo "Expected $count killed approval persistence mutants at line $line." >&2
    exit 1
  }
done <<< "$actor_spans"

jq -e '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length == 6' \
  "$actor_report" > /dev/null || {
  echo "Expected exactly six approval persistence mutants." >&2
  exit 1
}
