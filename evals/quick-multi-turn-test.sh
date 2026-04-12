#!/usr/bin/env bash
# Quick multi-turn verification test for chat -p --resume.
# Builds from source, starts an isolated container, runs a 2-turn conversation,
# and verifies context carryover + JSON output.
#
# Usage:
#   NETCLAW_EVAL_PROVIDER_TYPE=openai-compatible \
#   NETCLAW_EVAL_PROVIDER_ENDPOINT=https://llm.example.com \
#   NETCLAW_EVAL_MODEL_ID=my-model \
#     ./evals/quick-multi-turn-test.sh
#
# Set NETCLAW_EVAL_NO_BUILD=1 to skip build if image/binaries already exist.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

EVAL_PORT="${NETCLAW_EVAL_PORT:-5399}"
CONTAINER_NAME="netclaw-multi-turn-test-$$"
IMAGE="${NETCLAW_IMAGE:-ghcr.io/aaronontheweb/netclawd:dev}"
NETCLAW_BIN="$REPO_ROOT/publish/cli/netclaw"
NO_BUILD="${NETCLAW_EVAL_NO_BUILD:-0}"
PROMPT_TIMEOUT=90

# Provider config — required
PROVIDER_TYPE="${NETCLAW_EVAL_PROVIDER_TYPE:-}"
PROVIDER_ENDPOINT="${NETCLAW_EVAL_PROVIDER_ENDPOINT:-}"
MODEL_ID="${NETCLAW_EVAL_MODEL_ID:-}"

if [[ -z "$PROVIDER_TYPE" || -z "$PROVIDER_ENDPOINT" || -z "$MODEL_ID" ]]; then
    echo "ERROR: Provider configuration required." >&2
    echo "  Set NETCLAW_EVAL_PROVIDER_TYPE, NETCLAW_EVAL_PROVIDER_ENDPOINT, NETCLAW_EVAL_MODEL_ID" >&2
    exit 1
fi

# ─── Cleanup ──────────────────────────────────────────────────────────────────

EVAL_HOME=""
cleanup() {
    echo ""
    echo "→ Cleaning up..."
    docker stop "$CONTAINER_NAME" >/dev/null 2>&1 || true
    if [[ -n "$EVAL_HOME" && -d "$EVAL_HOME" ]]; then
        rm -rf "$EVAL_HOME" 2>/dev/null || \
            docker run --rm -v "$EVAL_HOME:/target" alpine:latest \
                sh -c 'rm -rf /target/..?* /target/.[!.]* /target/*' >/dev/null 2>&1 || true
        rmdir "$EVAL_HOME" 2>/dev/null || true
    fi
}
trap cleanup EXIT

# ─── Build ────────────────────────────────────────────────────────────────────

if [[ "$NO_BUILD" != "1" ]]; then
    echo "→ Building from source..."
    "$REPO_ROOT/scripts/docker/build-image.sh"
else
    echo "→ Skipping build (NO_BUILD=1)"
fi

if [[ ! -x "$NETCLAW_BIN" ]]; then
    echo "ERROR: CLI binary not found at $NETCLAW_BIN" >&2
    exit 1
fi

# ─── Start Container ─────────────────────────────────────────────────────────

EVAL_HOME=$(mktemp -d -t netclaw-mt-test-XXXXXX)
mkdir -p "$EVAL_HOME/identity" "$EVAL_HOME/logs"

if [[ -d "$HOME/.netclaw/identity" ]]; then
    cp -r "$HOME/.netclaw/identity/." "$EVAL_HOME/identity/"
else
    echo "WARN: No identity at ~/.netclaw/identity — container will use defaults"
fi

echo "→ Starting eval container on port $EVAL_PORT..."
docker run -d --rm \
    --name "$CONTAINER_NAME" \
    --network host \
    -v "$EVAL_HOME/identity:/root/.netclaw/identity" \
    -v "$EVAL_HOME/logs:/root/.netclaw/logs" \
    -e "NETCLAW_Daemon__Host=127.0.0.1" \
    -e "NETCLAW_Daemon__Port=$EVAL_PORT" \
    -e "NETCLAW_Providers__eval__Type=$PROVIDER_TYPE" \
    -e "NETCLAW_Providers__eval__Endpoint=$PROVIDER_ENDPOINT" \
    -e "NETCLAW_Models__Main__Provider=eval" \
    -e "NETCLAW_Models__Main__ModelId=$MODEL_ID" \
    -e "NETCLAW_Models__Fallback__Provider=eval" \
    -e "NETCLAW_Models__Fallback__ModelId=$MODEL_ID" \
    -e "NETCLAW_Models__Compaction__Provider=eval" \
    -e "NETCLAW_Models__Compaction__ModelId=$MODEL_ID" \
    "$IMAGE" >/dev/null

# Wait for healthy
echo "→ Waiting for daemon health..."
deadline=$((SECONDS + 60))
while (( SECONDS < deadline )); do
    if curl -fsS "http://127.0.0.1:$EVAL_PORT/api/health/ready" >/dev/null 2>&1; then
        echo "→ Daemon ready"
        break
    fi
    running=$(docker inspect -f '{{.State.Running}}' "$CONTAINER_NAME" 2>/dev/null || echo "false")
    if [[ "$running" != "true" ]]; then
        echo "ERROR: Container exited during startup" >&2
        docker logs "$CONTAINER_NAME" 2>&1 || true
        exit 2
    fi
    sleep 1
done

if ! curl -fsS "http://127.0.0.1:$EVAL_PORT/api/health/ready" >/dev/null 2>&1; then
    echo "ERROR: Daemon did not become healthy within 60s" >&2
    docker logs "$CONTAINER_NAME" 2>&1 | tail -30 || true
    exit 2
fi

# ─── Tests ────────────────────────────────────────────────────────────────────

PASSED=0
FAILED=0
SESSION_ID="test/multi-turn-$$"

run_headless() {
    local extra_args=("$@")
    NETCLAW_DAEMON_ENDPOINT="http://127.0.0.1:$EVAL_PORT" \
    NETCLAW_HOME="$EVAL_HOME" \
        timeout "$PROMPT_TIMEOUT" "$NETCLAW_BIN" "${extra_args[@]}" 2>&1
}

assert_contains() {
    local label="$1" output="$2" pattern="$3"
    if echo "$output" | grep -qi "$pattern"; then
        echo "  ✓ $label"
        PASSED=$((PASSED + 1))
    else
        echo "  ✗ $label (expected '$pattern' in output)"
        echo "    Output: $(echo "$output" | head -3)"
        FAILED=$((FAILED + 1))
    fi
}

assert_json_field() {
    local label="$1" output="$2" field="$3"
    if echo "$output" | python3 -c "import json,sys; d=json.load(sys.stdin); assert '$field' in d" 2>/dev/null; then
        echo "  ✓ $label"
        PASSED=$((PASSED + 1))
    else
        echo "  ✗ $label (field '$field' not found in JSON)"
        echo "    Output: $(echo "$output" | head -1)"
        FAILED=$((FAILED + 1))
    fi
}

echo ""
echo "=== Multi-Turn Resume Test ==="
echo "Session: $SESSION_ID"
echo ""

# Test 1: Create a named session with a memorable fact
echo "Turn 1: Establishing context..."
turn1=$(run_headless chat -p --resume "$SESSION_ID" "My favorite color is chartreuse. Just acknowledge that and nothing else.")
echo "  Response: $(echo "$turn1" | head -1)"
assert_contains "Turn 1 produced output" "$turn1" "."

# Test 2: Resume the session and ask about the fact
echo "Turn 2: Verifying context carryover..."
turn2=$(run_headless chat -p --resume "$SESSION_ID" "What is my favorite color? Answer in one word.")
echo "  Response: $(echo "$turn2" | head -1)"
assert_contains "Turn 2 references chartreuse" "$turn2" "chartreuse"

# Test 3: JSON output mode
echo "JSON output test..."
json_out=$(run_headless chat -p --json "Say hello in one word.")
echo "  Output: $(echo "$json_out" | head -1)"
assert_json_field "JSON has sessionId" "$json_out" "sessionId"
assert_json_field "JSON has response" "$json_out" "response"

# Test 4: JSON output with --resume
echo "JSON + resume test..."
json_resume=$(run_headless chat -p --json --resume "test/json-resume-$$" "Say goodbye in one word.")
echo "  Output: $(echo "$json_resume" | head -1)"
assert_json_field "JSON+resume has sessionId" "$json_resume" "sessionId"

echo ""
echo "─────────────────────────────────────────────────"
echo "Results: $PASSED passed, $FAILED failed"
echo "─────────────────────────────────────────────────"

if [[ "$FAILED" -gt 0 ]]; then
    exit 1
fi
