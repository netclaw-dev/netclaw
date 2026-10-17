#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Daemon/Security/LoopbackAuthenticationHandler.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/loopback-host-origin}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

read -r enforce_start enforce_end enforce_first enforce_last \
  rule_start rule_end rule_first rule_last < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    sub span {
      my ($text, $start_marker, $end_marker) = @_;
      my $start = index($text, $start_marker);
      die "The marker \"$start_marker\" is missing or duplicated.\n"
        if $start < 0 || index($text, $start_marker, $start + 1) >= 0;
      my $end = defined $end_marker ? index($text, $end_marker, $start) : length($text);
      die "The marker \"$end_marker\" is missing or duplicated.\n"
        if defined $end_marker && ($end < 0 || index($text, $end_marker, $end + 1) >= 0);
      my $first = 1 + (substr($text, 0, $start) =~ tr/\n//);
      my $last = 1 + (substr($text, 0, $end) =~ tr/\n//);
      return "$start $end $first $last";
    }
    print span($_, "var refusal = LoopbackHostOriginPolicy.FindRefusal(", "var claims = new[]"), " ",
          span($_, "public static string? FindRefusal", undef), "\n";
  ' "$source_file"
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Daemon.csproj \
    --mutate "Security/LoopbackAuthenticationHandler.cs{$enforce_start..$enforce_end}" \
    --mutate "Security/LoopbackAuthenticationHandler.cs{$rule_start..$rule_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
expected_count=33
tested_count="$(
  jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report"
)"
killed_count="$(jq '[.files[].mutants[] | select(.status == "Killed")] | length' "$report")"
span_mutants="$(
  jq --arg source "$source_file" \
    --argjson enforce_first "$enforce_first" --argjson enforce_last "$enforce_last" \
    --argjson rule_first "$rule_first" --argjson rule_last "$rule_last" \
    '[(.files[$source].mutants // [])[] | select(.status != "Ignored" and .status != "CompileError")
      | select((.location.start.line >= $enforce_first and .location.start.line < $enforce_last)
               or .location.start.line >= $rule_first)] | length' \
    "$report"
)"

if [[ "$tested_count" -ne "$expected_count" || "$killed_count" -ne "$expected_count" \
      || "$span_mutants" -ne "$expected_count" ]]; then
  echo "Expected $expected_count killed loopback Host and Origin mutants." >&2
  echo "Found $killed_count killed from $tested_count tested; span=$span_mutants." >&2
  exit 1
fi

# The strict name test sits in Netclaw.Configuration, below both the bind validator and the
# policy. A mutant that widens it widens request admission, so each mutant must die.
predicate_output="$output_path/predicate"
(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Configuration.csproj \
    --mutate "LoopbackHostName.cs" \
    --output "$predicate_output" \
    --skip-version-check
)

predicate_report="$predicate_output/reports/mutation-report.json"
predicate_expected=8
predicate_tested="$(
  jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$predicate_report"
)"
predicate_killed="$(jq '[.files[].mutants[] | select(.status == "Killed")] | length' "$predicate_report")"
if [[ "$predicate_tested" -ne "$predicate_expected" || "$predicate_killed" -ne "$predicate_expected" ]]; then
  echo "Expected $predicate_expected killed strict loopback name mutants." >&2
  echo "Found $predicate_killed killed from $predicate_tested tested." >&2
  exit 1
fi
