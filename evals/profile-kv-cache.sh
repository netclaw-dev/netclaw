#!/usr/bin/env bash
# KV Cache Profile Script
# Runs multi-turn conversations against a local netclaw daemon (Docker container)
# and profiles cache hit rates per turn using --json output.
#
# Usage:
#   ./evals/profile-kv-cache.sh
#
# Environment:
#   NETCLAW_EVAL_PROVIDER_TYPE        (default: openai-compatible)
#   NETCLAW_EVAL_PROVIDER_ENDPOINT    (default: https://llm.testlab.petabridge.net)
#   NETCLAW_EVAL_MODEL_ID             (default: Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf)
#   NETCLAW_EVAL_PORT                 (default: 5599)
#   NETCLAW_EVAL_TIMEOUT              (default: 120s per prompt)
#   NETCLAW_EVAL_NO_BUILD             Set to 1 to skip docker build
#   NETCLAW_IMAGE                     Image ref (default: ghcr.io/netclaw-dev/netclaw:latest)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

PROVIDER_TYPE="${NETCLAW_EVAL_PROVIDER_TYPE:-openai-compatible}"
PROVIDER_ENDPOINT="${NETCLAW_EVAL_PROVIDER_ENDPOINT:-https://llm.testlab.petabridge.net}"
MODEL_ID="${NETCLAW_EVAL_MODEL_ID:-Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf}"
EVAL_PORT="${NETCLAW_EVAL_PORT:-5599}"
PROMPT_TIMEOUT="${NETCLAW_EVAL_TIMEOUT:-120}"
CONTAINER_NAME="netclaw-cache-profile-$$"
IMAGE="${NETCLAW_EVAL_IMAGE:-ghcr.io/netclaw-dev/netclaw:latest}"
NETCLAW_BIN="${NETCLAW_BIN:-$REPO_ROOT/publish/cli/netclaw}"

RESULTS_DIR="$REPO_ROOT/evals/cache-profile"
mkdir -p "$RESULTS_DIR"
TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
RESULTS_FILE="$RESULTS_DIR/results-${TIMESTAMP}.json"
SUMMARY_FILE="$RESULTS_DIR/summary-${TIMESTAMP}.json"

# ─── Cleanup ──────────────────────────────────────────────────────────────────

EVAL_HOME=""
cleanup() {
    echo ""
    echo "→ Stopping container..."
    docker stop "$CONTAINER_NAME" >/dev/null 2>&1 || true
    if [[ -n "$EVAL_HOME" && -d "$EVAL_HOME" ]]; then
        rm -rf "$EVAL_HOME" 2>/dev/null || \
            docker run --rm -v "$EVAL_HOME:/target" alpine:latest \
                sh -c 'rm -rf /target/..?* /target/.[!.]* /target/*' >/dev/null 2>&1 || true
        rmdir "$EVAL_HOME" 2>/dev/null || true
    fi
}
trap cleanup EXIT

# ─── CLI Binary ───────────────────────────────────────────────────────────────

if [[ ! -x "$NETCLAW_BIN" ]]; then
    echo "ERROR: CLI binary not found at $NETCLAW_BIN" >&2
    # Try PATH fallback
    if command -v netclaw >/dev/null 2>&1; then
        NETCLAW_BIN="$(command -v netclaw)"
        echo "  Using: $NETCLAW_BIN"
    else
        echo "  Ensure netclaw is built or installed." >&2
        exit 1
    fi
fi
echo "CLI: $NETCLAW_BIN ($( "$NETCLAW_BIN" --version 2>&1 | head -1 ))"

# ─── Start Container ─────────────────────────────────────────────────────────

EVAL_HOME=$(mktemp -d -t netclaw-cache-profile-XXXXXX)
mkdir -p "$EVAL_HOME/identity" "$EVAL_HOME/logs"

if [[ -d "$HOME/.netclaw/identity" ]]; then
    cp -r "$HOME/.netclaw/identity/." "$EVAL_HOME/identity/"
else
    echo "WARN: No identity at ~/.netclaw/identity" >&2
fi

echo "→ Starting container on port $EVAL_PORT..."
docker run -d --rm \
    --name "$CONTAINER_NAME" \
    --network host \
    -v "$EVAL_HOME/identity:/root/.netclaw/identity" \
    -v "$EVAL_HOME/logs:/root/.netclaw/logs" \
    -e "NETCLAW_Daemon__Host=127.0.0.1" \
    -e "NETCLAW_Daemon__Port=$EVAL_PORT" \
    -e "NETCLAW_Providers__profile__Type=$PROVIDER_TYPE" \
    -e "NETCLAW_Providers__profile__Endpoint=$PROVIDER_ENDPOINT" \
    -e "NETCLAW_Models__Main__Provider=profile" \
    -e "NETCLAW_Models__Main__ModelId=$MODEL_ID" \
    -e "NETCLAW_Models__Fallback__Provider=profile" \
    -e "NETCLAW_Models__Fallback__ModelId=$MODEL_ID" \
    -e "NETCLAW_Models__Compaction__Provider=profile" \
    -e "NETCLAW_Models__Compaction__ModelId=$MODEL_ID" \
    "$IMAGE" >/dev/null

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
        docker logs "$CONTAINER_NAME" 2>&1 | tail -30 || true
        exit 2
    fi
    sleep 1
done

if ! curl -fsS "http://127.0.0.1:$EVAL_PORT/api/health/ready" >/dev/null 2>&1; then
    echo "ERROR: Daemon did not become healthy within 60s" >&2
    docker logs "$CONTAINER_NAME" 2>&1 | tail -30 || true
    exit 2
fi

# ─── Profile Runner ──────────────────────────────────────────────────────────

chat() {
    local extra_args=("$@")
    NETCLAW_DAEMON_ENDPOINT="http://127.0.0.1:$EVAL_PORT" \
    NETCLAW_HOME="$EVAL_HOME" \
        timeout "$PROMPT_TIMEOUT" "$NETCLAW_BIN" "${extra_args[@]}" 2>/dev/null
}

run_scenario() {
    local scenario_name="$1"; shift
    local description="$1"; shift
    local -a prompts=("$@")

    local session_id="cache-profile/${scenario_name}-$$"
    echo ""
    echo "═══════════════════════════════════════════════════════"
    echo "Scenario: $scenario_name"
    echo "Description: $description"
    echo "Session: $session_id"
    echo "Turns: ${#prompts[@]}"
    echo "───────────────────────────────────────────────────────"

    # Start JSON array for this scenario
    local scenario_json="["
    local turn=0
    local total_input=0
    local total_cached=0
    local total_output=0

    for prompt in "${prompts[@]}"; do
        turn=$((turn + 1))
        printf "\n  Turn %d: " "$turn"
        # Truncate prompt display
        local short_prompt="${prompt:0:80}"
        [[ ${#prompt} -gt 80 ]] && short_prompt="${short_prompt}..."
        printf "%s\n" "$short_prompt"

        local raw_output
        raw_output=$(chat chat -p --json --resume "$session_id" "$prompt") || true

        # Save raw JSON per turn
        echo "$raw_output" > "$RESULTS_DIR/${scenario_name}-turn${turn}.json"

        # Parse all usage fields in one shot
        local parsed
        parsed=$(echo "$raw_output" | python3 -c "
import json, sys
d = json.load(sys.stdin)
u = d.get('usage', {})
ct = u.get('cachedInputTokens', u.get('prompt_tokens_details', {}).get('cached_tokens', 0)) or 0
print(f\"{u.get('inputTokens',0)}|{u.get('outputTokens',0)}|{ct}|{u.get('promptMs',0)}|{d.get('ttftMs',0)}\")
" 2>/dev/null || echo "0|0|0|0|0")

        local input_tokens output_tokens cached_tokens prompt_ms ttft_ms
        IFS='|' read -r input_tokens output_tokens cached_tokens prompt_ms ttft_ms <<< "$parsed"

        # Calculate cache hit %
        local cache_pct="N/A"
        local uncached="—"
        if [[ "$input_tokens" -gt 0 ]] 2>/dev/null; then
            cache_pct=$(python3 -c "print(f'{${cached_tokens}/${input_tokens}*100:.1f}%')")
            uncached=$((input_tokens - cached_tokens))
        fi

        printf "    input=%s  cached=%s  uncached=%s  cache_hit=%s  prompt_ms=%s  ttft_ms=%s\n" \
            "$input_tokens" "$cached_tokens" "$uncached" "$cache_pct" "$prompt_ms" "$ttft_ms"

        # Accumulate totals
        total_input=$((total_input + input_tokens))
        total_cached=$((total_cached + cached_tokens))
        total_output=$((total_output + output_tokens))

        # Append to scenario JSON array
        [[ $turn -gt 1 ]] && scenario_json+=","
        scenario_json+="{\"turn\":$turn,\"input\":$input_tokens,\"output\":$output_tokens,\"cached\":$cached_tokens,\"prompt_ms\":$prompt_ms,\"ttft_ms\":$ttft_ms}"

        # Small pause between turns for stability
        sleep 2
    done

    scenario_json+="]"

    # Scenario summary
    local overall_cache="N/A"
    if [[ $total_input -gt 0 ]]; then
        overall_cache=$(python3 -c "print(f'{${total_cached}/${total_input}*100:.1f}%')")
    fi

    echo ""
    echo "  ── Scenario Summary ──"
    printf "  Total input tokens (all turns): %d\n" "$total_input"
    printf "  Total cached tokens:            %d\n" "$total_cached"
    printf "  Total output tokens:            %d\n" "$total_output"
    printf "  Overall cache hit rate:         %s\n" "$overall_cache"
    printf "  Effective tokens processed:     %d (input + output - cached)\n" $((total_input + total_output - total_cached))

    # Store scenario result as valid JSON
    python3 -c "
import json
result = {
    'scenario': '$scenario_name',
    'description': '$description',
    'turns': json.loads('$scenario_json'),
    'totals': {
        'total_input': $total_input,
        'total_cached': $total_cached,
        'total_output': $total_output,
        'effective_processed': $((total_input + total_output - total_cached))
    }
}
with open('$RESULTS_DIR/${scenario_name}.json', 'w') as f:
    json.dump(result, f, indent=2)
" 2>/dev/null || true
}

# ═══════════════════════════════════════════════════════════════════════════════
# Scenarios
# ═══════════════════════════════════════════════════════════════════════════════

echo ""
echo "╔═══════════════════════════════════════════════════════╗"
echo "║     Netclaw KV Cache Profile                         ║"
echo "║  Model: $MODEL_ID"
echo "║  Provider: $PROVIDER_ENDPOINT"
echo "╚═══════════════════════════════════════════════════════╝"

# Scenario 1: Simple chit-chat (baseline, no tools)
run_scenario simple_chitchat "Pure conversation, no tools — baseline cache behavior" \
    "Hi there! Just say hello back in one word." \
    "Count to three for me." \
    "Name a primary color." \
    "What's the capital of France?" \
    "Say goodbye in one word."

# Scenario 2: Memory planting + distractor + recall
run_scenario memory_recall "Plant a fact, distract, then recall it" \
    "I want you to remember something important for this conversation: my project codename is 'bluefin'. Just acknowledge and wait for my next question." \
    "What's the square root of 144? Just give me the number." \
    "What was the project codename I asked you to remember earlier?"

# Scenario 3: Tool calls every turn (stress test for cache busting)
run_scenario tool_every_turn "Tool call on every turn — does execution bust the cache?" \
    "Use shell_execute to run 'echo hello from turn 1' and tell me what it output." \
    "Now use shell_execute to run 'date +%Y-%m-%d' and tell me today's date." \
    "Use shell_execute to run 'uname -s' and tell me the OS." \
    "Use shell_execute to run 'whoami' and tell me who I am."

# Scenario 4: Tool calls then text-only (cache recovery test)
run_scenario tool_then_text "Tool calls first, then text-only — does cache recover?" \
    "Use shell_execute to run 'echo turn-one' and tell me what it said." \
    "Use shell_execute to run 'pwd' and tell me the working directory." \
    "Without running any more tools, what were the outputs of the two commands you just ran?" \
    "Tell me a fun fact about penguins. No tools needed."

# Scenario 5: Long-running session (progressive cache growth)
run_scenario long_session "Extended 8-turn conversation to see progressive cache behavior" \
    "Let's have a conversation about programming languages. Start by naming three popular languages." \
    "Of those three, which one do you think has the best type system? Pick one and explain briefly." \
    "What's a common anti-pattern in that language?" \
    "How would you fix that anti-pattern? Give a concrete example." \
    "Compare that language's approach to error handling with Rust's approach." \
    "What's one thing Rust does better overall?" \
    "What's one thing the original language does better?" \
    "Summarize our entire conversation in two sentences."

# Scenario 6: Context-heavy conversation (large responses that grow context)
run_scenario context_heavy "Conversation where responses are long, growing context fast" \
    "Explain the CAP theorem in detail, covering each component (Consistency, Availability, Partition Tolerance) and what happens when you can only pick two." \
    "Now explain how a distributed database like CockroachDB navigates the CAP theorem in practice." \
    "Compare that to how Redis handles it, especially with Redis Cluster." \
    "Given what we discussed, if I'm building a global e-commerce platform, which tradeoff would you recommend and why?"

# Scenario 7: Memory recall across many turns (Regression A canary).
# Live evidence from PR #1171 follow-up showed partial cache drops
# mid-session when memory-recall content changed between turns and
# something upstream rebuilt the leading prefix. With the
# SetSystemPrompt idempotency fix in place, the prefix must stay
# byte-stable and cache hit rate must extend monotonically across the
# six turns. A regression to "cache plateau at ~static prefix size on
# turn 3+" matches the pre-fix failure mode.
run_scenario memory_recall_mid_session "Multi-turn with shifting recall anchors — cache must extend, not plateau" \
    "Remember this for our chat: my primary project name is Aurora and uses Rust on Tokio. Confirm in one sentence." \
    "What's 17 times 23? Just the number." \
    "Remind me what programming language and runtime my Aurora project uses." \
    "Now switch gears: name three approaches for handling backpressure in async systems." \
    "Of those three, which one is most idiomatic for the runtime my Aurora project uses?" \
    "Summarize Aurora's stack and the recommended backpressure approach in two sentences."

# Scenario 8: Tool discovery mid-session (Regression B canary).
# Live evidence showed permanent cache=0 on every turn after a
# dynamic `load_tool` call. The session.log captured cache collapse
# from 99% hit rate to 0% across 5+ subsequent turns over 24 minutes.
# This scenario exercises the dynamic-tool registration path via
# search_tools + load_tool, then verifies subsequent turns still
# extend the cache prefix.
run_scenario tool_loaded_mid_session "Discover and load a tool mid-conversation — cache must recover next turn" \
    "Tell me a one-sentence fun fact about hummingbirds. No tools needed." \
    "Now I want you to use search_tools to find a tool that can list directory contents, then call load_tool on it (no need to execute the listed tool — just discover and load it)." \
    "Did you successfully load the tool? In one sentence, name the tool you loaded." \
    "Without invoking any more tools, what was the hummingbird fact you told me earlier?"

# ─── Write Combined Results ───────────────────────────────────────────────────

echo ""
echo "═══════════════════════════════════════════════════════"
echo "Writing results..."

# Combine all scenario JSON files into a single results array
python3 -c "
import json, glob, os
results = []
for f in sorted(glob.glob('$RESULTS_DIR/*.json')):
    bn = os.path.basename(f)
    # Skip the final results file itself if it exists from a previous run
    if bn == 'results.json':
        continue
    with open(f) as fh:
        try:
            data = json.load(fh)
            if 'scenario' in data:
                results.append(data)
        except json.JSONDecodeError:
            pass
with open('$RESULTS_FILE', 'w') as f:
    json.dump(results, f, indent=2)
print(f'  {len(results)} scenarios written to $RESULTS_FILE')
" 2>/dev/null || echo "  Results written (JSON formatting skipped)"

echo "  Raw turn JSON saved to $RESULTS_DIR/"
echo "  Results timestamp: $TIMESTAMP"
echo ""
echo "═══════════════════════════════════════════════════════"
echo "Profile complete. Results in $RESULTS_DIR/"
echo "═══════════════════════════════════════════════════════"
