#!/usr/bin/env bash
set -euo pipefail

# Focused gate for the child consent pause (child-approval-events-and-parent-pause, design D5). An open child
# consent request pauses the session. The model cannot start a child (a slash start of a person is accepted), and
# automated input and child result notices wait: in Ready (PlanNewInputAbandonment) and in the drain. A held input
# never joins a running turn and never starts one. A person input runs. An approval notice is identified by three
# facts with the principal, is never held, never joins a turn, and nothing joins a notice turn.
# ChildConsentPauseMutationTests and TurnContextChildConsentNoticeTests must kill every mutant in the five spans. The
# drain and the recovery share one hold rule, ChildConsentPause.Holds (durable-child-consent-wait, design D5).

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
actors="$repo_root/src/Netclaw.Actors"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/child-consent}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Prints "start end first_line last_line" for a marker pair that must occur once in the file.
select_span() {
  local file="$1" name="$2"
  NAME="$name" perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "// <$ENV{NAME}>";
    $end_marker = "// </$ENV{NAME}>";
    $start = index($_, $start_marker);
    die "The $ENV{NAME} start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The $ENV{NAME} end marker is missing or duplicated.\n"
      if $end < 0 || index($_, $end_marker, $end + 1) >= 0;
    $first = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $last = 1 + (substr($_, 0, $end) =~ tr/\n//);
    print "$start $end $first $last\n";
  ' "$file"
}

# Each row: relative source path, marker name, expected killed mutants.
spans=(
  "Sessions/SessionToolWorkflow.Children.cs child-consent-pause 4"
  "Sessions/SessionToolWorkflow.Recovery.cs child-consent-hold 3"
  "Sessions/Handlers/SessionInputFlow.cs child-consent-drain 19"
  "Sessions/ChildConsentPause.cs child-consent-predicate 3"
  "Channels/TurnContext.cs child-consent-notice 7"
)

mutate_args=()
checks=()
expected_total=0
for row in "${spans[@]}"; do
  read -r relative name count <<< "$row"
  read -r span_start span_end first last < <(select_span "$actors/$relative" "$name")
  mutate_args+=(--mutate "$relative{$span_start..$span_end}")
  checks+=("$actors/$relative $first $last $count $name")
  expected_total=$((expected_total + count))
done

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Actors.csproj \
    "${mutate_args[@]}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
failed=0
for check in "${checks[@]}"; do
  read -r source first last count name <<< "$check"
  jq -e --arg source "$source" --argjson first "$first" --argjson last "$last" --argjson count "$count" '
    [(.files[$source].mutants // [])[]
      | select(.status != "Ignored" and .status != "CompileError")
      | select(.location.start.line >= $first and .location.start.line <= $last)] as $mutants
    | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
  ' "$report" > /dev/null || {
    echo "Expected $count killed $name mutants in $source." >&2
    jq -r --arg source "$source" \
      '(.files[$source].mutants // [])[] | "\(.status) line \(.location.start.line): \(.mutatorName) \(.replacement // "")"' \
      "$report" >&2 || true
    failed=1
  }
done

jq -e --argjson count "$expected_total" '
  [.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length == $count
' "$report" > /dev/null || {
  echo "Expected exactly $expected_total tested child consent mutants." >&2
  failed=1
}

exit "$failed"
