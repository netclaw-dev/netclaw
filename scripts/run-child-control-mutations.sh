#!/usr/bin/env bash
set -euo pipefail

# Focused gate for the child control check (#2524): TurnContext.CanControlChildRunOf
# and the reminder turn test that it uses. Each mutant drops or inverts one kept
# fact, the reminder test, or the choice between the relaxed rule and the six-fact
# rule. TurnContextChildControlTests must kill every mutant: a reminder turn and a
# person turn of one session manage each other's runs, and no turn reaches a run of
# another session.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Channels/TurnContext.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/child-control}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

read -r span_start span_end first_line last_line < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "internal bool CanControlChildRunOf(TurnContext owner)";
    $end_marker = "ReminderExecutionActor.ReminderSourceKind;";
    $start = index($_, $start_marker);
    die "The child control check marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The child control check end marker is missing or duplicated.\n"
      if $end < 0 || index($_, $end_marker, $end + 1) >= 0;
    $end += length($end_marker);
    $first = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $last = 1 + (substr($_, 0, $end) =~ tr/\n//);
    print "$start $end $first $last\n";
  ' "$source_file"
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Actors.csproj \
    --mutate "Channels/TurnContext.cs{$span_start..$span_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
expected_count=13
jq -e --arg source "$source_file" \
  --argjson first "$first_line" --argjson last "$last_line" \
  --argjson count "$expected_count" '
  [(.files[$source].mutants // [])[]
    | select(.status != "Ignored")
    | select(.location.start.line >= $first and .location.start.line <= $last)] as $mutants
  | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
' "$report" > /dev/null || {
  echo "Expected $expected_count killed child control mutants in the selected span." >&2
  jq -r --arg source "$source_file" \
    '(.files[$source].mutants // [])[] | "\(.status) line \(.location.start.line): \(.mutatorName) \(.replacement // "")"' \
    "$report" >&2 || true
  exit 1
}

jq -e --argjson count "$expected_count" '
  [.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")]
  | length == $count
' "$report" > /dev/null || {
  echo "Expected exactly $expected_count tested child control mutants." >&2
  exit 1
}
