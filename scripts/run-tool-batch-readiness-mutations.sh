#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Sessions/ActiveToolBatchTracker.cs"
approval_source_file="$repo_root/src/Netclaw.Actors/Sessions/SessionToolWorkflow.Approvals.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/tool-batch-readiness}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

read -r span_start span_end first_line last_line < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "private bool CanComplete => ExecutionTaskCompleted";
    $end_marker = "private bool ExecutionTaskCompleted";
    $start = index($_, $start_marker);
    die "The tool-batch readiness start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The tool-batch readiness end marker is missing or duplicated.\n"
      if $end < 0 || index($_, $end_marker, $end + 1) >= 0;
    $first = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $last = 1 + (substr($_, 0, $end) =~ tr/\n//);
    print "$start $end $first $last\n";
  ' "$source_file"
)

read -r approval_span_start approval_span_end approval_line < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $marker = "!ApprovalButtonValueCodec.CanApprove(principal, senderId, response.SenderId.Value)";
    $start = index($_, $marker);
    die "The requester guard marker is missing or duplicated.\n"
      if $start < 0 || index($_, $marker, $start + 1) >= 0;
    $line = 1 + (substr($_, 0, $start) =~ tr/\n//);
    print "$start ", $start + length($marker), " $line\n";
  ' "$approval_source_file"
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Actors.csproj \
    --mutate "Sessions/ActiveToolBatchTracker.cs{$span_start..$span_end}" \
    --mutate "Sessions/SessionToolWorkflow.Approvals.cs{$approval_span_start..$approval_span_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
expected_count=1
jq -e --arg source "$source_file" \
  --argjson first "$first_line" --argjson last "$last_line" \
  --argjson count "$expected_count" '
  [(.files[$source].mutants // [])[]
    | select(.status != "Ignored")
    | select(.location.start.line >= $first and .location.start.line < $last)] as $mutants
  | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
' "$report" > /dev/null || {
  echo "Expected $expected_count killed tool-batch readiness mutants in the selected span." >&2
  exit 1
}

expected_requester_count=1
jq -e --arg source "$approval_source_file" \
  --argjson line "$approval_line" --argjson count "$expected_requester_count" '
  [(.files[$source].mutants // [])[]
    | select(.status != "Ignored")
    | select(.location.start.line == $line)] as $mutants
  | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
' "$report" > /dev/null || {
  echo "Expected $expected_requester_count killed requester guard mutants on the selected line." >&2
  exit 1
}

jq -e --argjson count "$((expected_count + expected_requester_count))" '
  [.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")]
  | length == $count
' "$report" > /dev/null || {
  echo "Expected exactly $((expected_count + expected_requester_count)) tested session reliability mutants." >&2
  exit 1
}
