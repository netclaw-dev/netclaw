#!/usr/bin/env bash
set -euo pipefail

# Focused gate for the same-requester rule of buffered input. A turn has one
# authority. A drain after a turn adopts the authority of the first buffered
# input, a drain inside a turn keeps the turn's authority, and only inputs of
# that one requester join the batch. SessionInputFlowRequesterTests must kill
# every mutant in the two selected spans of SessionInputFlow.DrainBufferedBatch
# and SessionInputFlow.JoinsTurn.
# The gate also selects the automation check of
# SessionToolWorkflow.PlanNewInputAbandonment: automated input waits behind an
# open approval, and input from a person closes it. NewInputAbandonmentPlanTests
# must kill every mutant in that check.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Sessions/Handlers/SessionInputFlow.cs"
recovery_file="$repo_root/src/Netclaw.Actors/Sessions/SessionToolWorkflow.Recovery.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/input-one-authority}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Prints "start end first_line last_line" for one marker that must occur once.
select_span() {
  local file="${2:-$source_file}"
  MARKER="$1" perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $marker = $ENV{MARKER};
    $start = index($_, $marker);
    die "The marker \"$marker\" is missing or duplicated.\n"
      if $start < 0 || index($_, $marker, $start + 1) >= 0;
    $end = $start + length($marker);
    $first = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $last = 1 + (substr($_, 0, $end) =~ tr/\n//);
    print "$start $end $first $last\n";
  ' "$file"
}

# The choice of the turn owner: adopt the first input after a turn, keep the turn inside it.
owner_marker='var adoptsAuthority = !continuesTurn && !_buffer[0].IsReplay;
        var owner = adoptsAuthority ? BufferedAuthority(_buffer[0].Message) : _context;'
# The join rule: a replay joins its own turn; another input joins only for the same requester.
join_marker='=> input.IsReplay || (owner is not null && BufferedAuthority(input.Message).HasSameRequester(owner));'

read -r owner_start owner_end owner_first owner_last < <(select_span "$owner_marker")
read -r join_start join_end join_first join_last < <(select_span "$join_marker")

# The automation check: the call site in PlanNewInputAbandonment and the principal set.
wait_marker='if (IsAutomation(inputPrincipal))'
automation_marker='=> principal is PrincipalClassification.VerifiedAutomation or PrincipalClassification.SystemProcess;'
read -r wait_start wait_end wait_first wait_last < <(select_span "$wait_marker" "$recovery_file")
read -r automation_start automation_end automation_first automation_last < <(select_span "$automation_marker" "$recovery_file")

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Actors.csproj \
    --mutate "Sessions/Handlers/SessionInputFlow.cs{$owner_start..$owner_end}" \
    --mutate "Sessions/Handlers/SessionInputFlow.cs{$join_start..$join_end}" \
    --mutate "Sessions/SessionToolWorkflow.Recovery.cs{$wait_start..$wait_end}" \
    --mutate "Sessions/SessionToolWorkflow.Recovery.cs{$automation_start..$automation_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
expected_owner_count=5
expected_join_count=3
expected_wait_count=1
expected_automation_count=1

# A compile error does not count as a tested mutant. The "or" to "and" pattern mutant of the principal set
# does not compile (Stryker reports CompileError), so the fixed counts below exclude it.
check_span() {
  local first="$1" last="$2" count="$3" name="$4" file="${5:-$source_file}"
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

check_span "$owner_first" "$owner_last" "$expected_owner_count" "turn owner"
check_span "$join_first" "$join_last" "$expected_join_count" "same-requester join"
check_span "$wait_first" "$wait_last" "$expected_wait_count" "automation wait" "$recovery_file"
check_span "$automation_first" "$automation_last" "$expected_automation_count" "automation principal" "$recovery_file"

total=$((expected_owner_count + expected_join_count + expected_wait_count + expected_automation_count))
jq -e --argjson count "$total" '
  [.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")]
  | length == $count
' "$report" > /dev/null || {
  echo "Expected exactly $total tested input authority mutants." >&2
  exit 1
}
