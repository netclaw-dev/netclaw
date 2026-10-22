#!/usr/bin/env bash
set -euo pipefail

# Focused gate for the text answer guard (child-approval-events-and-parent-pause, design D7 as the PR 2 review changed
# it). A text answer names no prompt. When a child prompt is among two or more prompts that the sender can answer, it
# resolves none; text that answers no prompt keeps the option-unavailable reply. With parent prompts only, the session
# resolves the latest prompt and the channel lookup the earliest. Two invocations: the session guard in
# SessionToolWorkflow.TryResolveTextResponse (Netclaw.Actors) and the channel lookup in PendingApprovalLookup
# (Netclaw.Channels). TextAnswerGuardMutationTests, TextAnswerParseOrderMutationTests, and PendingApprovalLookupTests
# must kill every mutant in both spans.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_root="${1:-$repo_root/artifacts/stryker/text-answer-guard}"
if [[ "$output_root" != /* ]]; then
  output_root="$repo_root/$output_root"
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

# Each row: project directory, relative source path, marker name, expected killed mutants, report name.
runs=(
  "Netclaw.Actors Sessions/SessionToolWorkflow.Approvals.cs text-answer-guard 9 session"
  "Netclaw.Channels PendingApprovalLookup.cs text-answer-lookup 6 channel"
)

failed=0
for row in "${runs[@]}"; do
  read -r project relative name count report_name <<< "$row"
  source_file="$repo_root/src/$project/$relative"
  read -r span_start span_end first last < <(select_span "$source_file" "$name")
  output_path="$output_root/$report_name"
  (
    cd "$test_project"
    dotnet stryker \
      --config-file stryker-config.json \
      --project "$project.csproj" \
      --mutate "$relative{$span_start..$span_end}" \
      --output "$output_path" \
      --skip-version-check
  )

  report="$output_path/reports/mutation-report.json"
  jq -e --arg source "$source_file" --argjson first "$first" --argjson last "$last" --argjson count "$count" '
    [(.files[$source].mutants // [])[]
      | select(.status != "Ignored" and .status != "CompileError")
      | select(.location.start.line >= $first and .location.start.line <= $last)] as $mutants
    | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
    and ([.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length) == $count
  ' "$report" > /dev/null || {
    echo "Expected $count killed $name mutants in $source_file." >&2
    jq -r '.files[].mutants[] | "\(.status) line \(.location.start.line): \(.mutatorName) \(.replacement // "")"' \
      "$report" >&2 || true
    failed=1
  }
done

exit "$failed"
