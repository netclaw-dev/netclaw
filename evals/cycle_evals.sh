#!/usr/bin/env bash
# Opt-in cycle cases use the common daemon, trial runner, database, and archive.

start_cycle_fixture() {
    if [[ "$EVAL_PROVIDER_TYPE" != openai-compatible || "$EVAL_PROVIDER_ENDPOINT" != */v1 ]]; then
        echo "ERROR: cycle cases require an OpenAI-compatible API base that ends in /v1." >&2
        exit 2
    fi
    if [[ "$EVAL_PROVIDER_API_KEY" == ENC:* || -n "$EVAL_DATA_PROTECTION_KEYS" ]]; then
        echo "ERROR: the cycle relay requires a plain upstream API key, if authentication is needed." >&2
        exit 2
    fi
    case "$FILTER_CASE" in
        ""|tool_cycle_correction|tool_cycle_terminal|tool_cycle_compaction|tool_cycle_changed_result|tool_cycle_metadata_repair) ;;
        *) echo "ERROR: unknown cycle case: $FILTER_CASE" >&2; exit 2 ;;
    esac
    command -v python3 >/dev/null
    command -v sqlite3 >/dev/null
    EVAL_CONTEXT_WINDOW="${EVAL_CONTEXT_WINDOW:-65536}"
    export CYCLE_EVAL_UPSTREAM="$EVAL_PROVIDER_ENDPOINT" CYCLE_EVAL_CONTEXT_WINDOW="$EVAL_CONTEXT_WINDOW"
    export NETCLAW_EVAL_MODEL_ID="$EVAL_MODEL_ID" EVAL_HOME
    export NETCLAW_EVAL_PROVIDER_API_KEY="$EVAL_PROVIDER_API_KEY"
    coproc CYCLE_FIXTURE { exec python3 "$REPO_ROOT/evals/cycle_evals.py" serve; }
    CYCLE_FIXTURE_PID_SAVED=$CYCLE_FIXTURE_PID
    read -r -t 15 CYCLE_FIXTURE_PORT <&"${CYCLE_FIXTURE[0]}"
    [[ "$CYCLE_FIXTURE_PORT" =~ ^[0-9]+$ ]]
    EVAL_PROVIDER_ENDPOINT="http://127.0.0.1:$CYCLE_FIXTURE_PORT/v1"
    EVAL_PROVIDER_API_KEY=""
    EVAL_DATA_PROTECTION_KEYS=""
    # Only the isolated eval config changes. Production limits remain intact.
    jq '.Session.Tuning.KeepRecentMessages=0 | .Session.Tuning.KeepRecentToolResults=1
        | .Session.Tuning.TitleGenerationInterval=0' \
        "${NETCLAW_EVAL_CONFIG_FILE:-$EVAL_ASSET_ROOT/evals/fixtures/config/netclaw.json}" \
        > "$TMPDIR_EVAL/cycle-config.json"
    export NETCLAW_EVAL_CONFIG_FILE="$TMPDIR_EVAL/cycle-config.json"
    THRESHOLD=1
}

setup_cycle_case() {
    local case_name="$1"
    CYCLE_CASE="$case_name"
    CYCLE_PROMPT=$(curl -fsS --connect-timeout 5 --max-time 10 -H 'Content-Type: application/json' \
        -d "{\"case\":\"$case_name\"}" "http://127.0.0.1:$CYCLE_FIXTURE_PORT/control/cycle" | jq -er .prompt)
}

setup_tool_cycle_correction() { setup_cycle_case correction; }
setup_tool_cycle_terminal() { setup_cycle_case terminal; }
setup_tool_cycle_compaction() { setup_cycle_case compaction; }
setup_tool_cycle_changed_result() { setup_cycle_case changed_result; }
setup_tool_cycle_metadata_repair() { setup_cycle_case metadata_repair; }

cycle_inconclusive() {
    python3 "$REPO_ROOT/evals/cycle_evals.py" inconclusive --case "$CYCLE_CASE" --reason "$1" > "$2"
    return 1
}

assert_cycle_case() {
    local snapshot report actor_log headless_log
    snapshot="${STDOUT_FILE%.txt}_cycle-snapshot.json"
    report="${STDOUT_FILE%.txt}_cycle-verdict.txt"
    if ! curl -fsS --connect-timeout 5 --max-time 10 -H 'Content-Type: application/json' -d '{}' \
        "http://127.0.0.1:$CYCLE_FIXTURE_PORT/control/snapshot" > "$snapshot"; then
        cycle_inconclusive snapshot_unavailable "$report"
        return 1
    fi
    # A .txt copy puts the synthetic trace into the common stdout archive.
    cp "$snapshot" "${STDOUT_FILE%.txt}_cycle-snapshot.txt"
    if ! stdout_json_envelope_valid; then
        cycle_inconclusive invalid_final_cli_json "$report"
        return 1
    fi
    if ! actor_log=$(stdout_json_session_actor_log_path); then
        cycle_inconclusive actor_log_unavailable "$report"
        return 1
    fi
    if ! headless_log=$(stdout_json_headless_log_path); then
        cycle_inconclusive headless_log_unavailable "$report"
        return 1
    fi
    python3 "$REPO_ROOT/evals/cycle_evals.py" check --snapshot "$snapshot" \
        --output "$STDOUT_FILE" --actor-log "$actor_log" --headless-log "$headless_log" \
        > "$report" 2> "${report%.txt}-errors.txt"
}

assert_tool_cycle_correction() { assert_cycle_case; }
assert_tool_cycle_terminal() { assert_cycle_case; }
assert_tool_cycle_compaction() { assert_cycle_case; }
assert_tool_cycle_changed_result() { assert_cycle_case; }
assert_tool_cycle_metadata_repair() { assert_cycle_case; }

run_cycle_cases() {
    print_category "Tool cycles"
    run_case --json tool_cycle_correction "real model recovers after the runtime cycle correction" '{{CYCLE_PROMPT}}'
    run_case --json tool_cycle_terminal "real model reports a truthful runtime text-only stop" '{{CYCLE_PROMPT}}'
    run_case --json tool_cycle_compaction "cycle state survives normal compaction before model recovery" '{{CYCLE_PROMPT}}'
    run_case --json tool_cycle_changed_result "changed results permit the third execution" '{{CYCLE_PROMPT}}'
    run_case --json tool_cycle_metadata_repair "valid metadata repair permits execution" '{{CYCLE_PROMPT}}'
    end_category
}
