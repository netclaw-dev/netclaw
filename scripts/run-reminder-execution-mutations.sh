#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/reminder-execution}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Resolve exact boundaries. An absent or duplicate marker must fail before Stryker starts.
spans="$(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    if (/class ActiveExecutionTracker/) {
      @targets = (
          ["execution.ExecutionId == executionId", 1],
          ["_executing.Remove(reminderId);", 1]
        );
    } elsif (/class ReminderExecutionActor/) {
      @targets = (
          ["unrelatedTurn.SourceReminderId != expectedKey", 1]
        );
    } elsif (/class SessionInputFlow/) {
      @targets = (
          ["if (TryFindWaitingFire(evt, state, out var fire, out var waiting))\n            return new AdmissionDecision.Dropped(fire, waiting);", 1],
          ["if (IsBuffered(pending.InputId)\n                && TryReadFireKey(pending, out waiting)\n                && waiting.DefinitionId == fire.DefinitionId)\n                return true;", 5],
          ["if (!TryReadFireKey(incoming, out fire))\n            return false;", 2],
          ["return input.TurnContext.SourceKind == ReminderExecutionActor.ReminderSourceKind\n               && ReminderFireKey.TryParse(input.SourceMessageId, out key);", 2]
        );
    } elsif (/record struct ReminderFireKey/) {
      @targets = (
          ["if (text is null)\n            return false;", 2],
          ["if (separator <= 0 || text.IndexOf(\x27:\x27, StringComparison.Ordinal) != separator)\n            return false;", 6],
          ["if (!long.TryParse(text.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var fireTimeMs))\n            return false;", 3]
        );
    } else {
      @targets = (
          ["execution.ExecutionId != outcome.ExecutionId", 1],
          ["definition.Schedule.Type == ReminderScheduleType.OneShot\n        && tombstone.FireAtMs == definition.Schedule.FireAtMs;", 3],
          ["definition.Schedule.FireAtMs == envelope.DueTimeUtc.ToUnixTimeMilliseconds();", 1],
          ["if (SettledOccurrenceIsCurrent(definition, envelope))\n            return false;", 2],
          ["finally\n        {\n            _activeExecutions.TryRemove(outcome.Id, outcome.ExecutionId, out _);\n            replyTo.Tell(new ReminderExecutionAccepted(outcome.ExecutionId));\n        }", 1]
        );
    }
    for $target (@targets) {
      ($marker, $count) = @$target;
      $start = index($_, $marker);
      die "An execution boundary is missing or duplicated.\n"
        if $start < 0 || index($_, $marker, $start + 1) >= 0;
      if ($marker =~ /^finally/) {
        $start += index($marker, "replyTo.Tell");
        $marker = "replyTo.Tell(new ReminderExecutionAccepted(outcome.ExecutionId));";
      }
      $first = 1 + (substr($_, 0, $start) =~ tr/\n/\n/);
      $last = $first + ($marker =~ tr/\n/\n/);
      ($file = $ARGV) =~ s{.*/src/Netclaw.Actors/}{};
      print "$file $start ", $start + length($marker), " $first $last $count\n";
    }
  ' "$repo_root/src/Netclaw.Actors/Reminders/ReminderManagerActor.cs" \
    "$repo_root/src/Netclaw.Actors/Reminders/ActiveExecutionTracker.cs" \
    "$repo_root/src/Netclaw.Actors/Reminders/ReminderExecutionActor.cs" \
    "$repo_root/src/Netclaw.Actors/Sessions/Handlers/SessionInputFlow.cs" \
    "$repo_root/src/Netclaw.Actors/Reminders/ReminderFireKey.cs"
)"

mutate_args=()
expected_total=0
while read -r file span_start span_end first last count; do
  mutate_args+=(--mutate "$file{$span_start..$span_end}")
  expected_total=$((expected_total + count))
done <<< "$spans"

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    "${mutate_args[@]}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
while read -r file span_start span_end first last count; do
  jq -e --arg source "$repo_root/src/Netclaw.Actors/$file" \
    --argjson first "$first" --argjson last "$last" --argjson count "$count" '
    [(.files[$source].mutants // [])[] | select(.status != "Ignored" and .status != "CompileError")
      | select(.location.start.line >= $first and .location.start.line <= $last)] as $mutants
    | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
  ' "$report" > /dev/null || {
    echo "Expected $count killed execution mutants in $file at lines $first-$last." >&2
    exit 1
  }
done <<< "$spans"

# Unrelated compiler errors can precede the source filter. Target errors still fail above.
jq -e --argjson count "$expected_total" '
  [.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")]
  | length == $count
' "$report" > /dev/null || {
  echo "Expected exactly $expected_total execution mutants." >&2
  exit 1
}
