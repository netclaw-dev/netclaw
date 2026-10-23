#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
actors="$repo_root/src/Netclaw.Actors"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
collect_mode="${1:-}"
if [[ "$collect_mode" == "--collect" ]]; then
  output_path="$repo_root/artifacts/stryker/child-owner-and-report"
else
  output_path="${1:-$repo_root/artifacts/stryker/child-owner-and-report}"
fi
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

mutate_args=()
checks=()
collected_total=0
add_collected_spans() {
  local rows="$1"
  while IFS=$'\t' read -r relative file first last count name offsets; do
    [[ -n "$relative" ]] || continue
    local start="${offsets%%:*}" end="${offsets##*:}"
    mutate_args+=(--mutate "$relative{$start..$end}")
    checks+=("$file $first $last $count $name")
    collected_total=$((collected_total + count))
  done <<< "$rows"
}

owner_spans="$("$repo_root/scripts/run-child-owner-mutations.sh" --collect)"
control_spans="$("$repo_root/scripts/run-child-control-mutations.sh" --collect)"
report_spans="$("$repo_root/scripts/run-reminder-report-once-mutations.sh" --collect)"
close_spans="$("$repo_root/scripts/run-child-approval-close-mutations.sh" --collect)"
add_collected_spans "$owner_spans"
add_collected_spans "$control_spans"
add_collected_spans "$report_spans"
add_collected_spans "$close_spans"
if [[ "$collected_total" -ne 72 ]]; then
  echo "Expected the four collected child gates to total 72 mutants; found $collected_total." >&2
  exit 1
fi

# Keep this selector on the FindIndex decision. The test rows cover notices,
# owner input, other requesters, replay, and the next-turn boundary.
scan_file="$actors/Sessions/Handlers/SessionInputFlow.cs"
scan_marker='_buffer.FindIndex(static queued => queued.IsReplay || !BufferedAuthority(queued.Message).IsChildConsentNotice)'
read -r scan_start scan_end scan_first scan_last < <(
  MARKER="$scan_marker" perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $marker = $ENV{MARKER};
    $start = index($_, $marker);
    die "The continuing-turn scan marker is missing or duplicated.\n"
      if $start < 0 || index($_, $marker, $start + 1) >= 0;
    $end = $start + length($marker);
    $first = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $last = 1 + (substr($_, 0, $end) =~ tr/\n//);
    print "$start $end $first $last\n";
  ' "$scan_file"
)
scan_count=2
mutate_args+=(--mutate "Sessions/Handlers/SessionInputFlow.cs{$scan_start..$scan_end}")
checks+=("$scan_file $scan_first $scan_last $scan_count continuing-turn-scan")

expected_total=$((collected_total + scan_count))
if [[ "$collect_mode" == "--collect" ]]; then
  printf '%s\n' "${checks[@]}"
  printf 'existing=%s scan=%s total=%s\n' "$collected_total" "$scan_count" "$expected_total"
  exit 0
fi

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-child-owner-and-report.json \
    --project Netclaw.Actors.csproj \
    "${mutate_args[@]}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
for check in "${checks[@]}"; do
  read -r file first last count name <<< "$check"
  jq -e --arg source "$file" \
    --argjson first "$first" --argjson last "$last" --argjson count "$count" '
    [(.files[$source].mutants // [])[]
      | select(.status != "Ignored" and .status != "CompileError")
      | select(.location.start.line >= $first and .location.start.line <= $last)] as $mutants
    | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
  ' "$report" > /dev/null || {
    echo "Expected $count killed $name mutants in $file." >&2
    jq -r --arg source "$file" \
      '(.files[$source].mutants // [])[] | "\(.status) line \(.location.start.line): \(.mutatorName) \(.replacement // "")"' \
      "$report" >&2 || true
    exit 1
  }
done

jq -e --argjson count "$expected_total" '
  [.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] as $mutants
  | ($mutants | length) == $count and all($mutants[]; .status == "Killed")
' "$report" > /dev/null || {
  echo "Expected exactly $expected_total tested child owner and report mutants." >&2
  exit 1
}
