#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Sessions/ActiveToolBatchTracker.cs"
approval_source_file="$repo_root/src/Netclaw.Actors/Sessions/SessionToolWorkflow.Approvals.cs"
inspection_source_file="$repo_root/src/Netclaw.Actors/Sessions/SessionModelContext.cs"
history_source_file="$repo_root/src/Netclaw.Actors/Sessions/SessionState.cs"
stop_source_file="$repo_root/src/Netclaw.Actors/Sessions/Handlers/SessionExecutionLifetime.cs"
input_source_file="$repo_root/src/Netclaw.Actors/Sessions/Handlers/SessionInputFlow.cs"
refused_source_file="$repo_root/src/Netclaw.Actors/Sessions/Handlers/TurnStateTracker.cs"
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

read -r inspection_span_start inspection_span_end inspection_line inspection_column inspection_end_column < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $marker = "generation == active";
    $start = index($_, $marker);
    die "The inspection generation guard marker is missing or duplicated.\n"
      if $start < 0 || index($_, $marker, $start + 1) >= 0;
    $line = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $column = $start - rindex(substr($_, 0, $start), "\n");
    print "$start ", $start + length($marker), " $line $column ", $column + length($marker), "\n";
  ' "$inspection_source_file"
)

read -r history_span_start history_span_end history_line history_column history_end_column < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $marker = "userAlreadyInHistory || evt.ChildContinuation is not null";
    $start = index($_, $marker);
    die "The terminal history choice marker is missing or duplicated.\n"
      if $start < 0 || index($_, $marker, $start + 1) >= 0;
    $line = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $column = $start - rindex(substr($_, 0, $start), "\n");
    print "$start ", $start + length($marker), " $line $column ", $column + length($marker), "\n";
  ' "$history_source_file"
)

read -r stop_span_start stop_span_end stop_line stop_column stop_end_column < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $marker = "message.CallId != _modelCallId";
    $start = index($_, $marker);
    die "The model stop identity marker is missing or duplicated.\n"
      if $start < 0 || index($_, $marker, $start + 1) >= 0;
    $line = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $column = $start - rindex(substr($_, 0, $start), "\n");
    print "$start ", $start + length($marker), " $line $column ", $column + length($marker), "\n";
  ' "$stop_source_file"
)

read -r reap_span_start reap_span_end reap_line reap_column reap_end_column < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $method = "public bool ObserveReapReply(long epoch)";
    $next_method = "public PassivationWork ReapCommitted";
    $marker = "reap.AwaitingCommit = true";
    $method_start = index($_, $method);
    die "The reap reply method marker is missing or duplicated.\n"
      if $method_start < 0 || index($_, $method, $method_start + 1) >= 0;
    $method_end = index($_, $next_method, $method_start);
    die "The reap commit method marker is missing or duplicated.\n"
      if $method_end < 0 || index($_, $next_method, $method_end + 1) >= 0;
    $start = index($_, $marker, $method_start);
    $duplicate = $start < 0 ? -1 : index($_, $marker, $start + 1);
    die "The reap commit guard marker is missing or duplicated.\n"
      if $start < 0 || $start >= $method_end
        || ($duplicate >= 0 && $duplicate < $method_end);
    $start += length("reap.AwaitingCommit = ");
    $line = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $column = $start - rindex(substr($_, 0, $start), "\n");
    print "$start ", $start + length("true"), " $line $column ", $column + length("true"), "\n";
  ' "$stop_source_file"
)

read -r input_span_start input_span_end input_line input_column input_end_column < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $marker = "!item.IsReplay";
    $start = index($_, $marker);
    die "The fresh-turn replay predicate is missing or duplicated.\n"
      if $start < 0 || index($_, $marker, $start + 1) >= 0;
    $line = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $column = $start - rindex(substr($_, 0, $start), "\n");
    print "$start ", $start + length($marker), " $line $column ", $column + length($marker), "\n";
  ' "$input_source_file"
)

read -r refused_span_start refused_span_end refused_first_line refused_last_line < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "private RefusedRepeatAction CountRefusedRepeat(bool repeats)";
    $end_marker = "private void ObserveGroup";
    $start = index($_, $start_marker);
    die "The refused repeat count start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The refused repeat count end marker is missing or duplicated.\n"
      if $end < 0 || index($_, $end_marker, $end + 1) >= 0;
    $first = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $last = 1 + (substr($_, 0, $end) =~ tr/\n//);
    print "$start $end $first $last\n";
  ' "$refused_source_file"
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Actors.csproj \
    --mutate "Sessions/ActiveToolBatchTracker.cs{$span_start..$span_end}" \
    --mutate "Sessions/SessionToolWorkflow.Approvals.cs{$approval_span_start..$approval_span_end}" \
    --mutate "Sessions/SessionModelContext.cs{$inspection_span_start..$inspection_span_end}" \
    --mutate "Sessions/SessionState.cs{$history_span_start..$history_span_end}" \
    --mutate "Sessions/Handlers/SessionExecutionLifetime.cs{$stop_span_start..$stop_span_end}" \
    --mutate "Sessions/Handlers/SessionExecutionLifetime.cs{$reap_span_start..$reap_span_end}" \
    --mutate "Sessions/Handlers/SessionInputFlow.cs{$input_span_start..$input_span_end}" \
    --mutate "Sessions/Handlers/TurnStateTracker.cs{$refused_span_start..$refused_span_end}" \
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

expected_inspection_count=1
jq -e --arg source "$inspection_source_file" \
  --argjson line "$inspection_line" \
  --argjson first "$inspection_column" --argjson last "$inspection_end_column" \
  --argjson count "$expected_inspection_count" '
  [(.files[$source].mutants // [])[]
    | select(.status != "Ignored")
    | select(.location.start.line == $line)
    | select(.location.end.line == $line)
    | select(.location.start.column >= $first and .location.end.column <= $last)] as $mutants
  | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
' "$report" > /dev/null || {
  echo "Expected $expected_inspection_count killed inspection generation mutants on the selected line." >&2
  exit 1
}

expected_history_count=2
jq -e --arg source "$history_source_file" \
  --argjson line "$history_line" \
  --argjson first "$history_column" --argjson last "$history_end_column" \
  --argjson count "$expected_history_count" '
  [(.files[$source].mutants // [])[]
    | select(.status != "Ignored")
    | select(.location.start.line == $line)
    | select(.location.end.line == $line)
    | select(.location.start.column >= $first and .location.end.column <= $last)] as $mutants
  | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
' "$report" > /dev/null || {
  echo "Expected $expected_history_count killed terminal history mutants on the selected expression." >&2
  exit 1
}

expected_stop_count=1
jq -e --arg source "$stop_source_file" \
  --argjson line "$stop_line" \
  --argjson first "$stop_column" --argjson last "$stop_end_column" \
  --argjson count "$expected_stop_count" '
  [(.files[$source].mutants // [])[]
    | select(.status != "Ignored")
    | select(.location.start.line == $line)
    | select(.location.end.line == $line)
    | select(.location.start.column >= $first and .location.end.column <= $last)] as $mutants
  | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
' "$report" > /dev/null || {
  echo "Expected $expected_stop_count killed model stop identity mutant on the selected expression." >&2
  exit 1
}

expected_reap_count=1
jq -e --arg source "$stop_source_file" \
  --argjson line "$reap_line" \
  --argjson first "$reap_column" --argjson last "$reap_end_column" \
  --argjson count "$expected_reap_count" '
  [(.files[$source].mutants // [])[]
    | select(.status != "Ignored")
    | select(.location.start.line == $line)
    | select(.location.end.line == $line)
    | select(.location.start.column >= $first and .location.end.column <= $last)] as $mutants
  | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
' "$report" > /dev/null || {
  echo "Expected $expected_reap_count killed reap commit mutant on the selected expression." >&2
  exit 1
}

expected_input_count=1
jq -e --arg source "$input_source_file" \
  --argjson line "$input_line" \
  --argjson first "$input_column" --argjson last "$input_end_column" \
  --argjson count "$expected_input_count" '
  [(.files[$source].mutants // [])[]
    | select(.status != "Ignored")
    | select(.location.start.line == $line)
    | select(.location.end.line == $line)
    | select(.location.start.column >= $first and .location.end.column <= $last)] as $mutants
  | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
' "$report" > /dev/null || {
  echo "Expected $expected_input_count killed fresh-turn replay mutant on the selected expression." >&2
  exit 1
}

expected_refused_count=11
jq -e --arg source "$refused_source_file" \
  --argjson first "$refused_first_line" --argjson last "$refused_last_line" \
  --argjson count "$expected_refused_count" '
  [(.files[$source].mutants // [])[]
    | select(.status != "Ignored")
    | select(.location.start.line >= $first and .location.start.line < $last)] as $mutants
  | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
' "$report" > /dev/null || {
  echo "Expected $expected_refused_count killed refused repeat count mutants in the selected method." >&2
  exit 1
}

total_count=$((expected_count + expected_requester_count + expected_inspection_count + expected_history_count + expected_stop_count + expected_reap_count + expected_input_count + expected_refused_count))
jq -e --argjson count "$total_count" '
  [.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")]
  | length == $count
' "$report" > /dev/null || {
  echo "Expected exactly $total_count tested session reliability mutants." >&2
  exit 1
}
