#!/usr/bin/env bash
# Focused mutation gate for the local-control proof. A proof grants one host
# operation. When the purpose chain matches, only the operation byte check
# refuses a payload for another operation. The Data Protection purpose chain
# binds the OAuth state; deterministic tests prove that binding. The request
# gate refuses a request without a valid proof, and only a refused request
# counts against the per-source limit.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/local-control-proof}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Prints the character span from the first marker to the end of the last
# marker. A missing or duplicated marker fails before Stryker starts.
find_range() {
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -e '
    my ($first, $last, $file) = @ARGV;
    local $/;
    open(my $fh, "<", $file) or die "Cannot read $file\n";
    my $text = <$fh>;
    for my $marker ($first, $last) {
      my $at = index($text, $marker);
      die "A target is missing or duplicated: $marker\n"
        if $at < 0 || index($text, $marker, $at + 1) >= 0;
    }
    my $start = index($text, $first);
    my $end = index($text, $last) + length($last);
    die "The range markers are out of order.\n" if $end <= $start;
    print "$start $end\n";
  ' "$1" "$2" "$3"
}

# Runs Stryker on the given project spans and requires every tested mutant to be killed.
run_gate() {
  local project="$1" output="$2" expected="$3" name="$4"
  shift 4
  (
    cd "$test_project"
    dotnet stryker \
      --config-file stryker-config.json \
      --project "$project" \
      "$@" \
      --output "$output" \
      --skip-version-check
  )

  local report="$output/reports/mutation-report.json"
  local tested killed
  tested="$(jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report")"
  killed="$(jq '[.files[].mutants[] | select(.status == "Killed")] | length' "$report")"
  if [[ "$tested" -ne "$expected" || "$killed" -ne "$expected" ]]; then
    echo "Expected $expected killed $name mutants. Found $killed killed from $tested tested." >&2
    exit 1
  fi
}

validator_file="$repo_root/src/Netclaw.Daemon/Security/LocalControlPairingProofValidator.cs"
read -r op_start op_end < <(
  find_range 'if (payload.Operation != operation.Code)' 'if (payload.Operation != operation.Code)' "$validator_file")
gate_marker='return refusal is null ? (request, null) : (null, CountRefusal(httpContext, refusal));'
read -r gate_start gate_end < <(find_range "$gate_marker" "$gate_marker" "$validator_file")
run_gate Netclaw.Daemon.csproj "$output_path" 4 "operation check and request gate" \
  --mutate "Security/LocalControlPairingProofValidator.cs{$op_start..$op_end}" \
  --mutate "Security/LocalControlPairingProofValidator.cs{$gate_start..$gate_end}"
