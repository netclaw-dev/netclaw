#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/reminder-report-once}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

source_file="src/Netclaw.Actors/Sessions/Handlers/DeliveryRetryHandler.cs"

# Resolve exact boundaries. An absent or duplicate marker must fail before Stryker starts.
spans="$(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    @targets = (
      ["if (_recordedTurnNumber != turnNumber)\n            return new ReminderLookup(ReminderLookupKind.UnknownTurn, null);", 1],
      ["if (_recordedTurnNumber != turnNumber || _recordedTurnReminder is null || _recordedTurnReported)\n            return false;", 6],
      ["_recordedTurnReported = true;\n        return true;", 2],
      ["_recordedTurnReported = false;", 1],
      ["_retryPending = false;\n    }\n\n    /// <summary>\n    /// Mark the given turn", 1],
      ["_retryPending = false;\n    }\n\n    /// <summary>\n    /// Which reminder fire", 1],
      ["_retryPending = true;", 1],
      ["_retryPending ? _recordedTurnReminder : null", 2]
    );
    for $target (@targets) {
      ($marker, $count) = @$target;
      $start = index($_, $marker);
      die "A report-once boundary is missing or duplicated.\n"
        if $start < 0 || index($_, $marker, $start + 1) >= 0;
      $first = 1 + (substr($_, 0, $start) =~ tr/\n/\n/);
      $last = $first + ($marker =~ tr/\n/\n/);
      print "$start ", $start + length($marker), " $first $last $count\n";
    }
  ' "$repo_root/$source_file"
)"

mutate_args=()
expected_total=0
while read -r span_start span_end first last count; do
  mutate_args+=(--mutate "${source_file#src/Netclaw.Actors/}{$span_start..$span_end}")
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
while read -r span_start span_end first last count; do
  jq -e --arg source "$repo_root/$source_file" \
    --argjson first "$first" --argjson last "$last" --argjson count "$count" '
    [(.files[$source].mutants // [])[] | select(.status != "Ignored" and .status != "CompileError")
      | select(.location.start.line >= $first and .location.start.line <= $last)] as $mutants
    | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
  ' "$report" > /dev/null || {
    echo "Expected $count killed report-once mutants in $source_file at lines $first-$last." >&2
    exit 1
  }
done <<< "$spans"

# Unrelated compiler errors can precede the source filter. Target errors still fail above.
jq -e --argjson count "$expected_total" '
  [.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")]
  | length == $count
' "$report" > /dev/null || {
  echo "Expected exactly $expected_total report-once mutants." >&2
  exit 1
}
