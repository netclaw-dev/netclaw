#!/usr/bin/env bash
set -euo pipefail

# Focused gate for the requester check that owns child status and cancel.
# Each mutant drops or inverts one requester field. TurnContextRequesterTests
# must kill every mutant: a stranger must not own a child, and a later turn of
# the same requester must keep it.
# The gate also selects the guard of a child result notice in
# SessionState.ApplyChildNotice: a run gets one notice, only an ended run gets
# one, and a notice carries the recorded authority of the run owner.
# ChildNoticeReducerTests must kill every mutant in that guard.
# The gate also selects the requester loop of SessionExecutionLifetime.ValidateRestartResume: a restart resume
# takes the leading inputs of one requester and leaves the rest waiting.
# RestartResumePartitionTests must kill every mutant in that loop.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Channels/TurnContext.cs"
notice_file="$repo_root/src/Netclaw.Actors/Sessions/SessionState.ChildRuns.cs"
resume_file="$repo_root/src/Netclaw.Actors/Sessions/Handlers/SessionExecutionLifetime.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/child-owner}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

read -r span_start span_end first_line last_line < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "internal bool HasSameRequester(TurnContext right)";
    $end_marker = "&& RequesterPrincipal == right.RequesterPrincipal;";
    $start = index($_, $start_marker);
    die "The requester check marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The requester check end marker is missing or duplicated.\n"
      if $end < 0 || index($_, $end_marker, $end + 1) >= 0;
    $end += length($end_marker);
    $first = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $last = 1 + (substr($_, 0, $end) =~ tr/\n//);
    print "$start $end $first $last\n";
  ' "$source_file"
)

# The notice guard: one notice for an ended run, with the authority of the run owner.
read -r notice_start notice_end notice_first notice_last < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "var run = ChildRuns.GetValueOrDefault(notice.RunId);";
    $end_marker = "Only a successful child run can carry a reviewed working context.\");";
    $start = index($_, $start_marker);
    die "The notice guard marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The notice guard end marker is missing or duplicated.\n"
      if $end < 0 || index($_, $end_marker, $end + 1) >= 0;
    $end += length($end_marker);
    $first = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $last = 1 + (substr($_, 0, $end) =~ tr/\n//);
    print "$start $end $first $last\n";
  ' "$notice_file"
)

# The restart resume partition: the loop stops at the first input of another requester and counts the leading inputs.
read -r resume_start resume_end resume_first resume_last < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "if (!first.HasSameRequester(candidate))";
    $end_marker = "count++;";
    $start = index($_, $start_marker);
    die "The restart resume marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The restart resume end marker is missing.\n" if $end < 0;
    $end += length($end_marker);
    $first = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $last = 1 + (substr($_, 0, $end) =~ tr/\n//);
    print "$start $end $first $last\n";
  ' "$resume_file"
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Actors.csproj \
    --mutate "Channels/TurnContext.cs{$span_start..$span_end}" \
    --mutate "Sessions/SessionState.ChildRuns.cs{$notice_start..$notice_end}" \
    --mutate "Sessions/Handlers/SessionExecutionLifetime.cs{$resume_start..$resume_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
expected_count=11
expected_notice_count=21
expected_resume_count=4
check_span() {
  local file="$1" first="$2" last="$3" count="$4" name="$5"
  jq -e --arg source "$file" \
    --argjson first "$first" --argjson last "$last" --argjson count "$count" '
    [(.files[$source].mutants // [])[]
      | select(.status != "Ignored" and .status != "CompileError")
      | select(.location.start.line >= $first and .location.start.line <= $last)] as $mutants
    | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
  ' "$report" > /dev/null || {
    echo "Expected $count killed $name mutants in the selected span." >&2
    jq -r --arg source "$file" \
      '(.files[$source].mutants // [])[] | "\(.status) line \(.location.start.line): \(.mutatorName) \(.replacement // "")"' \
      "$report" >&2 || true
    exit 1
  }
}

check_span "$source_file" "$first_line" "$last_line" "$expected_count" "requester check"
check_span "$notice_file" "$notice_first" "$notice_last" "$expected_notice_count" "notice guard"
check_span "$resume_file" "$resume_first" "$resume_last" "$expected_resume_count" "restart resume partition"

total=$((expected_count + expected_notice_count + expected_resume_count))
jq -e --argjson count "$total" '
  [.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")]
  | length == $count
' "$report" > /dev/null || {
  echo "Expected exactly $total tested child owner mutants." >&2
  exit 1
}
