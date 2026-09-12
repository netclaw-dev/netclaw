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
coproc BACKGROUND_FIXTURE { exec python3 "$REPO_ROOT/evals/background_evals.py" serve; }
fixture_pid=$BACKGROUND_FIXTURE_PID
trap 'cleanup_eval_env; kill "$fixture_pid" 2>/dev/null || true; wait "$fixture_pid" 2>/dev/null || true' EXIT
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
RUN_ID="background-$(date -u +%Y%m%dT%H%M%SZ)-$$"
STARTED_AT=$(date -u +%FT%TZ)
FILTER_CATEGORY="Background launch"
THRESHOLD=1
NETCLAW_VER=$("$NETCLAW_BIN" --version)
start_eval_daemon
export EVAL_HOME EVAL_PORT EVAL_CONTAINER_NAME NETCLAW_BIN TMPDIR_EVAL RUNS PROMPT_TIMEOUT
result=0
python3 "$REPO_ROOT/evals/background_evals.py" run --port "$fixture_port" "$@" || result=$?
report="$TMPDIR_EVAL/stdout_background-results.txt"
if [[ -s "$report" ]]; then
    TOTAL_CASES=$(jq '[.runtime[], .model[]] | length' "$report")
    PASSED_CASES=$(jq '[.runtime[], .model[]] | map(select(.passed)) | length' "$report")
fi
exit "$result"
