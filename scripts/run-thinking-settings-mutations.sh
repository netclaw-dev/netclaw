#!/usr/bin/env bash
# Focused mutation gate for model-output-and-thinking-settings (task 6.5).
#
# Protected claims (reliability boundary):
# - ThinkingDialectMap: a dialect sends only its expressible levels, rejects any other level and
#   never maps it to a nearby one, writes the documented wire fields, and offers a level only when
#   the dialect and the model capability both allow it.
# - OutputLimitResolver: a call-site value beats the model value, which beats the default; the
#   default is min(32000, model max, window - prompt - margin) with a 1024 floor and a warning flag.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_root="${1:-$repo_root/artifacts/stryker/thinking-settings}"
if [[ "$output_root" != /* ]]; then
  output_root="$repo_root/$output_root"
fi

# Prints "start end first_line last_line" of the text between two markers. A missing end marker
# means the end of the file.
span() {
  START_MARKER="$2" END_MARKER="$3" perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    my ($s, $e) = ($ENV{START_MARKER}, $ENV{END_MARKER});
    my $start = index($_, $s);
    die "The marker \"$s\" is missing or duplicated.\n" if $start < 0 || index($_, $s, $start + 1) >= 0;
    my $end = $e eq "" ? length($_) : index($_, $e, $start);
    die "The marker \"$e\" is missing or duplicated.\n" if $e ne "" && ($end < 0 || index($_, $e, $end + 1) >= 0);
    printf "%d %d %d %d\n", $start, $end, 1 + (substr($_, 0, $start) =~ tr/\n//), 1 + (substr($_, 0, $end) =~ tr/\n//);
  ' "$1"
}

# Runs one Stryker invocation over one or more marker spans of one file. Every tested mutant must be
# killed, every one must sit in a span, and the count must equal the expected count.
# Arguments: name project source_file relative_path expected start1 end1 [start2 end2 ...]
run_gate() {
  local name="$1" project="$2" source_file="$3" relative="$4" expected="$5"
  shift 5
  local output_path="$output_root/$name"
  local mutate_args=() line_filter="false"
  while (( $# >= 2 )); do
    local span_start span_end first_line last_line
    read -r span_start span_end first_line last_line < <(span "$source_file" "$1" "$2")
    mutate_args+=(--mutate "$relative{$span_start..$span_end}")
    line_filter+=" or (.location.start.line >= $first_line and .location.start.line <= $last_line)"
    shift 2
  done

  (
    cd "$test_project"
    dotnet stryker \
      --config-file stryker-config.json \
      --project "$project" \
      "${mutate_args[@]}" \
      --output "$output_path" \
      --skip-version-check
  )

  local report="$output_path/reports/mutation-report.json"
  local tested killed span_mutants
  tested="$(jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report")"
  killed="$(jq '[.files[].mutants[] | select(.status == "Killed")] | length' "$report")"
  span_mutants="$(
    jq --arg source "$source_file" \
      "[(.files[\$source].mutants // [])[] | select(.status != \"Ignored\" and .status != \"CompileError\")
        | select($line_filter)] | length" "$report"
  )"
  echo "$name: $killed killed from $tested tested; span=$span_mutants; expected=$expected."
  if [[ "$tested" -ne "$expected" || "$killed" -ne "$expected" || "$span_mutants" -ne "$expected" ]]; then
    echo "Expected $expected killed $name mutants." >&2
    jq -r '.files[].mutants[] | select(.status != "Killed" and .status != "Ignored" and .status != "CompileError")
      | "\(.status) line \(.location.start.line): \(.mutatorName) -> \(.replacement)"' "$report" >&2
    return 1
  fi
}

failed=0
# ExpressibleLevels is outside the spans: Stryker turns its collection-expression mutants into
# compile errors, so the table tests in ThinkingDialectMapTests guard it. The rejection message of
# Apply is outside the spans, because the tests check only that it names the level and the offered
# list. The unreachable default arms that only throw for an unknown enum value are outside the
# spans too.
run_gate dialect-map Netclaw.Providers.csproj \
  "$repo_root/src/Netclaw.Providers/ThinkingDialectMap.cs" "ThinkingDialectMap.cs" \
  47 \
  "public static OfferedThinkingLevels OfferedLevels" \
  "_ => throw new ArgumentOutOfRangeException(nameof(capability)" \
  "public static void ApplySuppression" \
  "/// <summary>True when the suppression intent writes" \
  "public static void Apply(ThinkingDialect dialect" \
  "throw new NotSupportedException(" \
  "        switch (dialect)
        {
            case ThinkingDialect.Native:" \
  "throw new ArgumentOutOfRangeException(nameof(dialect), dialect, \"Unknown thinking dialect.\");" || failed=1
# The floor-warning flag (the `if (headroom < value)` block) is outside the spans: `<` and `<=` are
# equivalent there, and the flag only selects a log level.
run_gate output-limit Netclaw.Daemon.csproj \
  "$repo_root/src/Netclaw.Daemon/Configuration/ModelRequestSettingsChatClient.cs" \
  "Configuration/ModelRequestSettingsChatClient.cs" \
  17 \
  "public static OutputLimit Resolve(" "if (headroom < value)" \
  "value = (int)Math.Min(value, Math.Max(FloorLimit, headroom));" "" || failed=1
# The router reuses a pipeline only for the same provider (without case), model ID, and settings.
# EstimatePromptTokens and ApplyThinking stay outside the gate: they are private members of the
# model client that only the wire rows reach, and those rows need the whole provider wire harness.
run_gate pipeline-key Netclaw.Daemon.csproj \
  "$repo_root/src/Netclaw.Daemon/Configuration/ChatClientRouter.cs" \
  "Configuration/ChatClientRouter.cs" \
  10 \
  "internal RoleBasedFailoverRouter(Func<ModelReference, IChatClient> create" \
  "public IReadOnlyList<IChatClient> Route(ChatRoutingContext context)" || failed=1
exit "$failed"
