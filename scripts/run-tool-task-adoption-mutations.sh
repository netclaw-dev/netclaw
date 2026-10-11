#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/tool-task-adoption}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Resolve exact boundaries. An absent or duplicate marker must fail before Stryker starts.
spans="$(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    @targets = $ARGV =~ /LlmSessionActor/
      ? (["_log.Debug(\"Ignoring a stale local tool callback\");\n        return false;", 1, "false"])
      : $ARGV =~ /TurnStateTracker/
      ? (["previous.Outcome == group.Outcome", 1])
      : (
      ["left with { AdoptedSpeakerIds = right.AdoptedSpeakerIds } == right", 1],
      ["!PendingInputs.Take(evt.InputIds.Count).Select(static input => input.InputId).SequenceEqual(evt.InputIds)", 2]
    );
    for $target (@targets) {
      ($marker, $count) = @$target;
      $start = index($_, $marker);
      die "An adoption boundary is missing or duplicated.\n"
        if $start < 0 || index($_, $marker, $start + 1) >= 0;
      if (defined $target->[2]) {
        $start += rindex($marker, $target->[2]);
        $marker = $target->[2];
      }
      $first = 1 + (substr($_, 0, $start) =~ tr/\n/\n/);
      $last = $first + ($marker =~ tr/\n/\n/);
      ($file = $ARGV) =~ s{.*/src/Netclaw.Actors/}{};
      print "$file $start ", $start + length($marker), " $first $last $count\n";
    }
  ' "$repo_root/src/Netclaw.Actors/Sessions/SessionState.cs" \
    "$repo_root/src/Netclaw.Actors/Sessions/Handlers/TurnStateTracker.cs" \
    "$repo_root/src/Netclaw.Actors/Sessions/LlmSessionActor.cs"
)"

mutate_args=()
expected_total=0
while read -r file span_start span_end first last count; do
  mutate_args+=(--mutate "$file{$span_start..$span_end}")
  expected_total=$((expected_total + count))
done <<< "$spans"

# Stryker 5 accepts this filter in its JSON configuration only.
config_path="$(mktemp --suffix=.json)"
trap 'rm -f "$config_path"' EXIT
jq '."stryker-config"."test-case-filter" =
  "FullyQualifiedName~ToolTaskAdoptionMutationTests|FullyQualifiedName~ToolRecurrenceMutationTests|FullyQualifiedName~LateToolReplyMutationTests"' \
  "$test_project/stryker-config.json" > "$config_path"

(
  cd "$test_project"
  dotnet stryker \
    --config-file "$config_path" \
    "${mutate_args[@]}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
while read -r file span_start span_end first last count; do
  jq -e --arg source "$repo_root/src/Netclaw.Actors/$file" \
    --argjson first "$first" --argjson last "$last" --argjson count "$count" '
    (if $source | endswith("LlmSessionActor.cs") then ["Boolean mutation"]
     elif $count == 2 then ["Linq method mutation (Take() to Skip())",
       "LogicalNotExpression to un-LogicalNotExpression mutation"]
     else ["Equality mutation"] end) as $expected
    | [.files[$source].mutants[] | select(.status != "Ignored")
      | select(.location.start.line >= $first and .location.start.line <= $last)
      | select(.mutatorName as $name | $expected | index($name))] as $mutants
    | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
      and (($mutants | map(.mutatorName) | sort) == $expected)
  ' "$report" > /dev/null || {
    echo "Expected $count killed adoption mutants in $file at lines $first-$last." >&2
    exit 1
  }
done <<< "$spans"

# Unrelated compiler errors can precede the source filter. Target errors still fail above.
jq -e --argjson count "$expected_total" '
  [.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")]
  | length == $count
' "$report" > /dev/null || {
  echo "Expected exactly $expected_total adoption mutants." >&2
  exit 1
}
