#!/usr/bin/env bash
# Test real background job lifetime and authority at queued process launch.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/run-evals.sh"

if [[ "${1:-}" == "--runtime-only" ]]; then
    export NETCLAW_EVAL_MODEL_ID=background-fixture
    EVAL_MODEL_ID=background-fixture
    EVAL_PROVIDER_TYPE=openai-compatible
    EVAL_PROVIDER_ENDPOINT=http://127.0.0.1:1/v1
elif [[ $# != 0 ]]; then
    echo "Usage: $0 [--runtime-only]" >&2
    exit 2
fi
case "$FILTER_CASE" in
    ""|queued_grant_revoked) ;;
    child_run_held_parent|child_run_partial_cancel|child_run_cli_acceptance)
        [[ "${1:-}" != --runtime-only && "$RUNS" == 1 ]] || {
            echo "ERROR: child cases require a real model and RUNS=1 per fresh invocation." >&2
            exit 2
        }
        [[ -f "${NETCLAW_CHILD_OBSERVER:-}" ]] || {
            echo "ERROR: set NETCLAW_CHILD_OBSERVER to the compiled canonical observer." >&2
            exit 2
        } ;;
    child_run_routed_skill|child_run_approval_once|child_run_owner_recovery|child_run_approval_cancel)
        [[ "${1:-}" == --runtime-only && "$RUNS" == 1 && -f "${NETCLAW_CHILD_OBSERVER:-}" ]] || {
            echo "ERROR: process cases require --runtime-only, RUNS=1, and the compiled process observer." >&2
            exit 2
        }
        EVAL_MODEL_ID=background-process-fixture
        export NETCLAW_EVAL_MODEL_ID="$EVAL_MODEL_ID"
        ;;
    tool_background_job_lifecycle)
        if [[ "${1:-}" == --runtime-only ]]; then
            echo "ERROR: the lifecycle case requires a real model." >&2
            exit 2
        fi ;;
    *) echo "ERROR: unknown background eval case: $FILTER_CASE" >&2; exit 2 ;;
esac
if [[ "$EVAL_PROVIDER_TYPE" != openai-compatible || "$EVAL_PROVIDER_ENDPOINT" != */v1 ]]; then
    echo "ERROR: set an OpenAI-compatible provider with an API base that ends in /v1." >&2
    exit 2
fi
if [[ "$EVAL_PROVIDER_API_KEY" == ENC:* || -n "$EVAL_DATA_PROTECTION_KEYS" ]]; then
    echo "ERROR: the eval relay requires a plain upstream API key, when authentication is needed." >&2
    exit 2
fi
command -v python3 >/dev/null
check_prerequisites
build_local_image

export BACKGROUND_EVAL_UPSTREAM="$EVAL_PROVIDER_ENDPOINT"
export NETCLAW_EVAL_MODEL_ID="$EVAL_MODEL_ID"
fixture_module=background_evals.py
if [[ "$FILTER_CASE" == child_run_* ]]; then
    fixture_module=child_run_evals.py
fi
case "$FILTER_CASE" in
    child_run_routed_skill|child_run_approval_once|child_run_owner_recovery|child_run_approval_cancel)
        fixture_module=background_process_fixture.py
        ;;
esac
export TMPDIR_EVAL EVAL_HOME EVAL_CONTAINER_NAME RUNS
coproc BACKGROUND_FIXTURE { exec python3 "$REPO_ROOT/evals/$fixture_module" serve; }
fixture_pid=$BACKGROUND_FIXTURE_PID
trap 'eval_exit=$?; cleanup_status=0; cleanup_eval_env || cleanup_status=$?; kill "$fixture_pid" 2>/dev/null || true; wait "$fixture_pid" 2>/dev/null || true; if [[ "$eval_exit" == 0 ]]; then eval_exit=$cleanup_status; fi; exit "$eval_exit"' EXIT
read -r -t 15 fixture_port <&"${BACKGROUND_FIXTURE[0]}"
[[ "$fixture_port" =~ ^[0-9]+$ ]]
EVAL_PROVIDER_ENDPOINT="http://127.0.0.1:$fixture_port/v1"
# Only the relay receives the upstream key. The daemon calls the loopback fixture.
EVAL_PROVIDER_API_KEY=""
EVAL_DATA_PROTECTION_KEYS=""
export NETCLAW_EVAL_CONFIG_FILE="$REPO_ROOT/evals/fixtures/background-jobs/netclaw.json"
export NETCLAW_EVAL_APPROVALS_FILE="$REPO_ROOT/evals/fixtures/background-jobs/tool-approvals.json"
mkdir -p "$EVAL_HOME/data/evals/markers"
for executable in hold_job queued_marker; do
    cp "$REPO_ROOT/evals/fixtures/background-jobs/job.py" "$EVAL_HOME/data/evals/$executable"
    chmod a+rx "$EVAL_HOME/data/evals/$executable"
done
if [[ "$fixture_module" == background_process_fixture.py ]]; then
    python3 "$REPO_ROOT/evals/$fixture_module" prepare
fi
RUN_ID="background-$(date -u +%Y%m%dT%H%M%SZ)-$$"
STARTED_AT=$(date -u +%FT%TZ)
FILTER_CATEGORY="Background launch"
THRESHOLD=1
NETCLAW_VER=$("$NETCLAW_BIN" --version)
start_eval_daemon
export EVAL_HOME EVAL_PORT EVAL_CONTAINER_NAME NETCLAW_BIN NETCLAW_IMAGE TMPDIR_EVAL RUNS PROMPT_TIMEOUT
result=0
python3 "$REPO_ROOT/evals/$fixture_module" run --port "$fixture_port" "$@" || result=$?
report="$TMPDIR_EVAL/stdout_background-results.txt"
if [[ -s "$report" ]]; then
    TOTAL_CASES=$(jq '[.runtime[], .model[]] | length' "$report")
    PASSED_CASES=$(jq '[.runtime[], .model[]] | map(select(.passed)) | length' "$report")
fi
exit "$result"
