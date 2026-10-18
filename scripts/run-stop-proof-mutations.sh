#!/usr/bin/env bash
set -euo pipefail

# Mutation gate for #2527 rule 2: the proof that `DaemonManager.StopAsync` needs before it signals a PID.
# Two Stryker runs, because the proof spans two projects.
#
#   Netclaw.Cli, Daemon/DaemonManager.cs
#     stale-pid:begin/end   the PID-file branch of TryGetRunningPid (a gone or non-daemon process)
#     stop-notice:begin/end the notice and the reading of its answer (200 with the PID and home, 409, other)
#     stop-proof:begin/end  the decision: an accepted notice, or an unconfirmed notice plus an environment match
#   Netclaw.Configuration, HomeIdentity.cs
#     home-match:begin/end  the link walk, the home comparison, and the environment rule
#
# Each marker pair must occur once. The tests are StopProofMutationTests (and the endpoint tests of
# ShutdownNoticeIdentityMutationTests, which share the home comparison). Every tested mutant must die.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/stop-proof}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Prints "<start> <end> <first line> <last line>" for one marker pair, or fails.
span_of() {
  local file="$1" label="$2"
  LABEL="$label" perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $label = $ENV{LABEL};
    $start_marker = "// $label:begin";
    $end_marker = "// $label:end";
    $start = index($_, $start_marker);
    die "The $label begin marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end = index($_, $end_marker, $start);
    die "The $label end marker is missing or duplicated.\n"
      if $end < 0 || index($_, $end_marker, $end + 1) >= 0;
    $first = 1 + (substr($_, 0, $start) =~ tr/\n//);
    $last = 1 + (substr($_, 0, $end) =~ tr/\n//);
    print "$start $end $first $last\n";
  ' "$file"
}

# run_gate <project> <source file> <relative path in project> <minimum mutants> <report dir>
#          <detected statuses: a jq test> <label>...
# <detected statuses> names the Stryker statuses that count as a dead mutant.
run_gate() {
  local project="$1" source_file="$2" relative="$3" minimum="$4" report_dir="$5" detected_filter="$6"
  shift 6
  local mutate_args=() lines='[]' label spans start end first last
  for label in "$@"; do
    # Fail before Stryker starts when a marker is missing or duplicated. The `|| return` is needed:
    # `set -e` does not apply inside a function that the caller runs in an `||` list.
    spans="$(span_of "$source_file" "$label")" || return 1
    read -r start end first last <<<"$spans"
    mutate_args+=(--mutate "$relative{$start..$end}")
    lines="$(jq -c --argjson f "$first" --argjson l "$last" '. + [[$f, $l]]' <<<"$lines")"
  done

  (
    cd "$test_project"
    dotnet stryker \
      --config-file stryker-config.json \
      --project "$project" \
      "${mutate_args[@]}" \
      --output "$report_dir" \
      --skip-version-check
  )

  local report="$report_dir/reports/mutation-report.json"
  local tested killed in_span
  tested="$(
    jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report"
  )"
  killed="$(jq "[.files[].mutants[] | select($detected_filter)] | length" "$report")"
  in_span="$(
    jq --arg source "$source_file" --argjson spans "$lines" \
      '[(.files[$source].mutants // [])[]
        | select(.status != "Ignored" and .status != "CompileError")
        | .location.start.line as $line
        | select(any($spans[]; $line >= .[0] and $line < .[1]))] | length' \
      "$report"
  )"

  if [[ "$tested" -lt "$minimum" || "$killed" -ne "$tested" || "$in_span" -ne "$tested" ]]; then
    echo "Expected at least $minimum mutants in $relative, and all killed." >&2
    echo "Found $killed killed from $tested tested; in the marked spans=$in_span." >&2
    return 1
  fi
}

# A mutant that removes the 40-link bound of HomeIdentity.Canonical makes a link cycle spin until Stryker
# stops the test run. Stryker reports that as Timeout, and a spin is a detected defect, so the home-match
# span counts Timeout as dead, as run-exact-verb-chain-mutations.sh does. The DaemonManager spans count
# Killed only.
failed=0
run_gate Netclaw.Cli.csproj \
  "$repo_root/src/Netclaw.Cli/Daemon/DaemonManager.cs" "Daemon/DaemonManager.cs" 32 \
  "$output_path/cli" '.status == "Killed"' stale-pid stop-notice stop-proof || failed=1
run_gate Netclaw.Configuration.csproj \
  "$repo_root/src/Netclaw.Configuration/HomeIdentity.cs" "HomeIdentity.cs" 45 \
  "$output_path/configuration" '.status == "Killed" or .status == "Timeout"' home-match || failed=1
exit "$failed"
