#!/usr/bin/env bash
# Opt-in pair that ablates only the optional retained-output child hint.

start_coordination_spill_fixture() {
    local relay="${1:-background_evals.py}"
    if [[ "$EVAL_PROVIDER_TYPE" != openai-compatible || "$EVAL_PROVIDER_ENDPOINT" != */v1 ]]; then
        echo "ERROR: coordination relay cases require an OpenAI-compatible API base that ends in /v1." >&2
        exit 2
    fi
    if [[ "$EVAL_PROVIDER_API_KEY" == ENC:* || -n "$EVAL_DATA_PROTECTION_KEYS" ]]; then
        echo "ERROR: the relay requires a plain upstream API key, if authentication is needed." >&2
        exit 2
    fi
    case "$FILTER_CASE" in
        ""|coordination_spill_hint_present|coordination_spill_hint_absent|coordination_productive_long_child) ;;
        *) echo "ERROR: unknown coordination relay case: $FILTER_CASE" >&2; exit 2 ;;
    esac
    command -v python3 >/dev/null
    export BACKGROUND_EVAL_UPSTREAM="$EVAL_PROVIDER_ENDPOINT"
    export NETCLAW_EVAL_MODEL_ID="$EVAL_MODEL_ID"
    export NETCLAW_EVAL_PROVIDER_API_KEY="$EVAL_PROVIDER_API_KEY"
    coproc SPILL_FIXTURE { exec python3 "$REPO_ROOT/evals/$relay" serve; }
    SPILL_FIXTURE_PID_SAVED=$SPILL_FIXTURE_PID
    read -r -t 15 SPILL_FIXTURE_PORT <&"${SPILL_FIXTURE[0]}"
    [[ "$SPILL_FIXTURE_PORT" =~ ^[0-9]+$ ]]
    EVAL_PROVIDER_ENDPOINT="http://127.0.0.1:$SPILL_FIXTURE_PORT/v1"
    EVAL_PROVIDER_API_KEY=""
    EVAL_DATA_PROTECTION_KEYS=""
    if [[ "$relay" == background_evals.py ]]; then
        curl -fsS --connect-timeout 5 --max-time 10 -H 'Content-Type: application/json' -d '{"mode":"model"}' \
            "http://127.0.0.1:$SPILL_FIXTURE_PORT/control/mode" >/dev/null
    fi
}

start_coordination_progress_fixture() {
    export EVAL_HOME PROGRESS_EVAL_UPSTREAM="$EVAL_PROVIDER_ENDPOINT"
    start_coordination_spill_fixture coordination_progress_evals.py
}

setup_coordination_productive_long_child() {
    curl -fsS --connect-timeout 5 --max-time 10 -H 'Content-Type: application/json' -d '{}' \
        "http://127.0.0.1:$SPILL_FIXTURE_PORT/control/progress" >/dev/null
}

assert_coordination_productive_long_child() {
    local snapshot="${STDOUT_FILE%.txt}_progress-snapshot.txt"
    local report="${STDOUT_FILE%.txt}_progress-verdict.txt"
    keep_assertion_report "$report"
    if ! curl -fsS --connect-timeout 5 --max-time 10 -H 'Content-Type: application/json' -d '{}' \
        "http://127.0.0.1:$SPILL_FIXTURE_PORT/control/snapshot" > "$snapshot"; then
        printf '%s\n' '{"passed":false,"status":"inconclusive","reason":"progress_snapshot_unavailable"}' > "$report"
        return 1
    fi
    python3 "$REPO_ROOT/evals/coordination_progress_evals.py" check \
        --snapshot "$snapshot" --output "$STDOUT_FILE" > "$report"
}

run_coordination_progress_case() {
    print_category "Coordination Child Progress"
    local prompt
    prompt=$(python3 "$REPO_ROOT/evals/coordination_progress_evals.py" prompt) || return 2
    run_case --json coordination_productive_long_child \
        "parent retains a productive child beyond its own estimate" "$prompt"
    end_category
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
