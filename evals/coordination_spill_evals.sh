#!/usr/bin/env bash
# Opt-in pair that ablates only the optional retained-output child hint.

start_coordination_spill_fixture() {
    if [[ "$EVAL_PROVIDER_TYPE" != openai-compatible || "$EVAL_PROVIDER_ENDPOINT" != */v1 ]]; then
        echo "ERROR: spill hint cases require an OpenAI-compatible API base that ends in /v1." >&2
        exit 2
    fi
    if [[ "$EVAL_PROVIDER_API_KEY" == ENC:* || -n "$EVAL_DATA_PROTECTION_KEYS" ]]; then
        echo "ERROR: the relay requires a plain upstream API key, if authentication is needed." >&2
        exit 2
    fi
    case "$FILTER_CASE" in
        ""|coordination_spill_hint_present|coordination_spill_hint_absent) ;;
        *) echo "ERROR: unknown coordination spill hint case: $FILTER_CASE" >&2; exit 2 ;;
    esac
    command -v python3 >/dev/null
    export BACKGROUND_EVAL_UPSTREAM="$EVAL_PROVIDER_ENDPOINT"
    export NETCLAW_EVAL_MODEL_ID="$EVAL_MODEL_ID"
    export NETCLAW_EVAL_PROVIDER_API_KEY="$EVAL_PROVIDER_API_KEY"
    coproc SPILL_FIXTURE { exec python3 "$REPO_ROOT/evals/background_evals.py" serve; }
    SPILL_FIXTURE_PID_SAVED=$SPILL_FIXTURE_PID
    read -r -t 15 SPILL_FIXTURE_PORT <&"${SPILL_FIXTURE[0]}"
    [[ "$SPILL_FIXTURE_PORT" =~ ^[0-9]+$ ]]
    EVAL_PROVIDER_ENDPOINT="http://127.0.0.1:$SPILL_FIXTURE_PORT/v1"
    EVAL_PROVIDER_API_KEY=""
    EVAL_DATA_PROTECTION_KEYS=""
    curl -fsS -H 'Content-Type: application/json' -d '{"mode":"model"}' \
        "http://127.0.0.1:$SPILL_FIXTURE_PORT/control/mode" >/dev/null
    # Keep the configured pass threshold. Three runs require three passes.
    RUNS=3
}

run_coordination_spill_hint_cases() {
    print_category "Coordination Spill Hint"
    local previous_timeout="$PROMPT_TIMEOUT"
    PROMPT_TIMEOUT=300
    local prompt="Read /home/netclaw/.netclaw/workspaces/coordination-retained-inspection/records.txt. Find Record 400. Return one raw JSON object, without Markdown fences. The record field must be the integer record number. The verification field must be the exact phrase string from that record."
    run_case --json coordination_spill_hint_present \
        "same retained-output task with the optional child-inspection hint" "$prompt"
    run_case --json coordination_spill_hint_absent \
        "same retained-output task without the optional child-inspection hint" "$prompt"
    PROMPT_TIMEOUT="$previous_timeout"
    end_category
}
