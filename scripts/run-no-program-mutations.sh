#!/usr/bin/env bash
# Focused mutation gate for the owner decision of October 2026: a shell command
# that runs no program gets no prompt. Only a plain file target qualifies (a
# Bash special device such as /dev/tcp does not), and an input redirect of such
# a command must pass the file_read rules of the audience.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
locator_only=false
locator_self_test=false
if [[ "${1:-}" == "--locators-only" ]]; then
  locator_only=true
  shift
elif [[ "${1:-}" == "--locator-self-test" ]]; then
  locator_self_test=true
  shift
fi
output_path="${1:-$repo_root/artifacts/stryker/no-program}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Prints the source span and its exact line and column bounds. The method end
# marker bounds both target searches to one method.
find_span() {
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -e '
    my ($file, $context_marker, $first, $last, $method_end_marker) = @ARGV;
    local $/;
    open(my $fh, "<", $file) or die "Cannot read $file\n";
    my $text = <$fh>;
    my $context = index($text, $context_marker);
    die "A method marker is missing or duplicated: $context_marker\n"
      if $context < 0 || index($text, $context_marker, $context + 1) >= 0;
    my $method_end = index($text, $method_end_marker, $context + length($context_marker));
    die "A method end marker is missing after its method: $method_end_marker\n"
      if $method_end < 0;
    my $count_in_method = sub {
      my ($marker, $from, $to) = @_;
      my $count = 0;
      my $at = $from;
      while (($at = index($text, $marker, $at)) >= 0 && $at + length($marker) <= $to) {
        $count++;
        $at += length($marker);
      }
      return $count;
    };
    die "A start marker is missing or duplicated in its method: $first\n"
      if $count_in_method->($first, $context, $method_end) != 1;
    die "An end marker is missing or duplicated in its method: $last\n"
      if $count_in_method->($last, $context, $method_end) != 1;
    my $start = index($text, $first, $context);
    die "A start marker is outside its method or missing: $first\n"
      if $start < 0 || $start >= $method_end;
    my $end_start = index($text, $last, $start);
    die "An end marker is outside its method or missing: $last\n"
      if $end_start < 0 || $end_start >= $method_end;
    my $end = $end_start + length($last);
    die "The range markers are out of order.\n" if $end <= $start;
    my $start_prefix = substr($text, 0, $start);
    my $end_prefix = substr($text, 0, $end);
    my $start_line = 1 + ($start_prefix =~ tr/\n//);
    my $end_line = 1 + ($end_prefix =~ tr/\n//);
    my $start_column = $start - (rindex($start_prefix, "\n") + 1) + 1;
    my $end_column = $end - (rindex($end_prefix, "\n") + 1) + 1;
    print "$start $end $start_line $start_column $end_line $end_column\n";
  ' "$1" "$2" "$3" "$4" "$5"
}

if [[ "$locator_self_test" == true ]]; then
  fixture="$(mktemp)"
  trap 'rm -f "$fixture"' EXIT
  cat >"$fixture" <<'EOF'
class Probe
{
    void TargetMethod()
    {
        StartMarker();
        EndMarker();
    }

    void NextMethod()
    {
        StartMarker();
        EndMarker();
    }
}
EOF
  find_span "$fixture" "void TargetMethod()" "StartMarker();" "EndMarker();" "void NextMethod()" >/dev/null
  find_span "$fixture" "void TargetMethod()" "StartMarker();" "StartMarker();" "void NextMethod()" >/dev/null
  if find_span "$fixture" "void TargetMethod()" "MissingMarker();" "EndMarker();" "void NextMethod()" >/dev/null 2>&1; then
    echo "The locator accepted a missing target marker." >&2
    exit 1
  fi

  cat >"$fixture" <<'EOF'
class Probe
{
    void TargetMethod()
    {
        StartMarker();
        StartMarker();
        EndMarker();
    }

    void NextMethod() { }
}
EOF
  if find_span "$fixture" "void TargetMethod()" "StartMarker();" "EndMarker();" "void NextMethod()" >/dev/null 2>&1; then
    echo "The locator accepted a duplicate start marker." >&2
    exit 1
  fi

  cat >"$fixture" <<'EOF'
class Probe
{
    void TargetMethod()
    {
        StartMarker();
        EndMarker();
        EndMarker();
    }

    void NextMethod() { }
}
EOF
  if find_span "$fixture" "void TargetMethod()" "StartMarker();" "EndMarker();" "void NextMethod()" >/dev/null 2>&1; then
    echo "The locator accepted a duplicate end marker." >&2
    exit 1
  fi
  echo "Locator self-test passed: one marker, same-marker span, missing marker, and duplicate in-method markers."
  exit 0
fi

# Runs Stryker on listed spans. It checks only mutants inside those spans.
run_gate() {
  local project="$1" output="$2" expected_killed="$3" expected_ignored="$4" name="$5"
  shift 5
  local mutations=()
  local targets=()
  local target
  while [[ "$#" -gt 0 ]]; do
    local mutation="$1" target="$2"
    shift 2
    mutations+=(--mutate "$mutation")
    targets+=("$target")
  done
  mkdir -p "$output"
  local console_log="$output/console.log"
  (
    cd "$test_project"
    dotnet stryker \
      --config-file stryker-config.json \
      --project "$project" \
      "${mutations[@]}" \
      --output "$output" \
      --skip-version-check
  ) 2>&1 | tee "$console_log"

  local report="$output/reports/mutation-report.json"
  local tested=0 killed=0 ignored=0 compile_errors=0 selected_target_count=0
  local target_result target_file start_line start_column end_line end_column target_count target_killed target_ignored target_compile_errors target_other_ignored
  for target in "${targets[@]}"; do
    IFS='|' read -r target_file start_line start_column end_line end_column <<<"$target"
    target_result="$(jq -r \
      --arg file "$target_file" \
      --argjson start_line "$start_line" \
      --argjson start_column "$start_column" \
      --argjson end_line "$end_line" \
      --argjson end_column "$end_column" \
      '[.files[$file].mutants[]? | select(
        ((.location.start.line > $start_line)
          or (.location.start.line == $start_line and .location.start.column >= $start_column))
        and ((.location.end.line < $end_line)
          or (.location.end.line == $end_line and .location.end.column <= $end_column)))] as $target
       | [$target | length,
          ($target | map(select(.status == "Killed")) | length),
          ($target | map(select(.status == "Ignored")) | length),
          ($target | map(select(.status == "CompileError")) | length),
          ($target | map(select(.status == "Ignored" and
            (.mutatorName != "Block removal mutation" or
             .statusReason != "Removed by block already covered filter"))) | length)] | @tsv' \
      "$report")"
    read -r target_count target_killed target_ignored target_compile_errors target_other_ignored <<<"$target_result"
    target_count="${target_count:-0}"
    target_killed="${target_killed:-0}"
    target_ignored="${target_ignored:-0}"
    target_compile_errors="${target_compile_errors:-0}"
    target_other_ignored="${target_other_ignored:-0}"
    if [[ "$target_count" -ne "$((expected_killed + expected_ignored))" \
      || "$target_killed" -ne "$expected_killed" \
      || "$target_ignored" -ne "$expected_ignored" \
      || "$target_other_ignored" -ne 0 \
      || "$target_compile_errors" -ne 0 ]]; then
      echo "$name target $target_file:$start_line:$start_column-$end_line:$end_column: found $target_killed killed, $target_ignored ignored, $target_compile_errors compile errors, and $target_count selected mutants." >&2
      exit 1
    fi
    tested=$((tested + target_count))
    killed=$((killed + target_killed))
    ignored=$((ignored + target_ignored))
    compile_errors=$((compile_errors + target_compile_errors))
    selected_target_count=$((selected_target_count + 1))
  done
  if [[ "$selected_target_count" -ne "${#targets[@]}" \
    || "$tested" -ne "$((expected_killed + expected_ignored))" \
    || "$killed" -ne "$expected_killed" \
    || "$ignored" -ne "$expected_ignored" \
    || "$compile_errors" -ne 0 ]]; then
    echo "Expected $expected_killed killed and $expected_ignored ignored $name mutants across ${#targets[@]} target spans. Found $killed killed, $ignored ignored, $compile_errors compile errors, and $tested selected mutants." >&2
    exit 1
  fi
}

analysis_file="$repo_root/src/Netclaw.Security/ShellCommandAnalysis.cs"
read -r resolve_start resolve_end resolve_start_line resolve_start_column resolve_end_line resolve_end_column < <(
  find_span "$analysis_file" \
    "internal static bool TryResolvePlainFileTarget(" \
    "if (!redirect.IsComplete" \
    "return true;" \
    "private static bool IsPlainFileTarget("
)
policy_file="$repo_root/src/Netclaw.Actors/Tools/ToolAccessPolicy.cs"
read -r read_start read_end read_start_line read_start_column read_end_line read_end_column < <(
  find_span "$policy_file" \
    "internal ToolAuthorizationDecision? ScreenNoProgramRedirects(" \
    "if (!candidate.RunsNoProgram && !candidate.UsesFileToolRedirects)" \
    '$"this audience may not use {FileToolName(read)}, and a redirect gets the same decision");' \
    "internal ShellApprovalAnalysis WithManagedTemporaryRedirects("
)
if [[ "$locator_only" == true ]]; then
  printf 'plain-target-resolver %s %s %s %s:%s-%s:%s\n' \
    "$analysis_file" "$resolve_start" "$resolve_end" \
    "$resolve_start_line" "$resolve_start_column" "$resolve_end_line" "$resolve_end_column"
  printf 'no-program-screen %s %s %s %s:%s-%s:%s\n' \
    "$policy_file" "$read_start" "$read_end" \
    "$read_start_line" "$read_start_column" "$read_end_line" "$read_end_column"
  exit 0
fi

# Direct contract tests kill all fifteen resolver mutants. Stryker deduplicates one block removal.
run_gate Netclaw.Security.csproj "$output_path/plain-target" 15 1 "plain file target" \
  "ShellCommandAnalysis.cs{$resolve_start..$resolve_end}" \
  "$analysis_file|$resolve_start_line|$resolve_start_column|$resolve_end_line|$resolve_end_column"
# One block-removal record has the exact Stryker reason "Removed by block already covered filter".
# All other mutations in this span must run and die in the focused Actor tests.
run_gate Netclaw.Actors.csproj "$output_path/redirect-checks" 18 1 "redirect check" \
  "Tools/ToolAccessPolicy.cs{$read_start..$read_end}" \
  "$policy_file|$read_start_line|$read_start_column|$read_end_line|$read_end_column"
