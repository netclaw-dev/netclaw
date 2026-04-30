#!/usr/bin/env bash
# Netclaw Behavioral Eval Suite
# Tests identity, skill loading, memory, tool use, grounding, and autonomy
# against an ephemeral netclawd Docker container — completely isolated from
# the operator's real ~/.netclaw state.
#
# Usage:
#   NETCLAW_EVAL_PROVIDER_TYPE=ollama \
#   NETCLAW_EVAL_PROVIDER_ENDPOINT=http://my-gpu-server.tailnet.ts.net:11434 \
#   NETCLAW_EVAL_MODEL_ID=qwen3:30b \
#     ./evals/run-evals.sh
#
# Environment variables:
#   Eval target (required — if unset, prompted interactively):
#     NETCLAW_EVAL_PROVIDER_TYPE        Provider type (e.g. ollama, openai, openrouter)
#     NETCLAW_EVAL_PROVIDER_ENDPOINT    Provider URL the container should call
#     NETCLAW_EVAL_MODEL_ID             Main model id
#
#   Eval target (optional — default to main):
#     NETCLAW_EVAL_FALLBACK_MODEL_ID
#     NETCLAW_EVAL_COMPACTION_MODEL_ID
#
#   Container + runtime:
#     NETCLAW_IMAGE              Image ref (default: ghcr.io/netclaw-dev/netclaw:dev — built locally)
#     NETCLAW_EVAL_PORT          Host-side port for the eval daemon (default 5299)
#     NETCLAW_EVAL_CONTEXT_WINDOW  Override model context window (future compaction evals)
#
#   Build:
#     NETCLAW_EVAL_NO_BUILD      Set to 1 to skip `dotnet publish` + `docker build`
#                                (reuse existing ./publish output and image)
#     NETCLAW_BIN                Path to netclaw CLI (default: ./publish/cli/netclaw)
#
#   Eval suite knobs:
#     NETCLAW_EVAL_RUNS          Runs per case (default: 5)
#     NETCLAW_EVAL_THRESHOLD     Pass threshold 0.0-1.0 (default: 0.80)
#     NETCLAW_EVAL_TIMEOUT       Per-prompt timeout in seconds (default: 60)
#     NETCLAW_EVAL_CATEGORY      Run only this category (case-insensitive substring match)
#     NETCLAW_EVAL_CASE          Run only this specific case name
set -euo pipefail

# ─── Configuration ────────────────────────────────────────────────────────────

# Repo root — derived from this script's location (evals/ is one level deep).
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

RUNS="${NETCLAW_EVAL_RUNS:-5}"
THRESHOLD="${NETCLAW_EVAL_THRESHOLD:-0.80}"
PROMPT_TIMEOUT="${NETCLAW_EVAL_TIMEOUT:-60}"
EVAL_PORT="${NETCLAW_EVAL_PORT:-5299}"
EVAL_CONTAINER_NAME="netclaw-eval-$$"
NO_BUILD="${NETCLAW_EVAL_NO_BUILD:-0}"
FILTER_CATEGORY="${NETCLAW_EVAL_CATEGORY:-}"
FILTER_CASE="${NETCLAW_EVAL_CASE:-}"

# Image and CLI binary default to the locally-built artifacts. Evals should
# always test the current source tree, not a stale published image.
NETCLAW_IMAGE="${NETCLAW_IMAGE:-ghcr.io/netclaw-dev/netclaw:dev}"
NETCLAW_BIN="${NETCLAW_BIN:-$REPO_ROOT/publish/cli/netclaw}"

# Eval target — resolved by check_prerequisites after optional interactive prompt.
EVAL_PROVIDER_TYPE="${NETCLAW_EVAL_PROVIDER_TYPE:-}"
EVAL_PROVIDER_ENDPOINT="${NETCLAW_EVAL_PROVIDER_ENDPOINT:-}"
EVAL_MODEL_ID="${NETCLAW_EVAL_MODEL_ID:-}"
EVAL_FALLBACK_MODEL_ID="${NETCLAW_EVAL_FALLBACK_MODEL_ID:-}"
EVAL_COMPACTION_MODEL_ID="${NETCLAW_EVAL_COMPACTION_MODEL_ID:-}"
EVAL_CONTEXT_WINDOW="${NETCLAW_EVAL_CONTEXT_WINDOW:-}"

# ─── State ────────────────────────────────────────────────────────────────────

TOTAL_CASES=0
PASSED_CASES=0
FAILED_CASES=0
CATEGORY_CASES=0
CATEGORY_PASSED=0
CURRENT_CATEGORY=""
RUN_ID=""
NETCLAW_VER=""
STARTED_AT=""
TMPDIR_EVAL=""
EVAL_HOME=""
DAEMON_LOG=""
RESULTS_DIR=""
RESULTS_DB=""

# Per-prompt state (set by run_prompt, read by assertion helpers)
STDOUT_FILE=""
DAEMON_LOG_LINES_BEFORE=0

# ─── Prerequisites ────────────────────────────────────────────────────────────

check_prerequisites() {
    # NETCLAW_BIN existence is verified after build_local_image (the binary
    # may not exist yet when the default points to ./publish/cli/netclaw).

    if ! command -v timeout >/dev/null 2>&1; then
        echo "ERROR: 'timeout' command not found (install coreutils)" >&2
        exit 1
    fi

    if ! command -v docker >/dev/null 2>&1; then
        echo "ERROR: 'docker' not found. Install Docker to run the eval suite." >&2
        exit 1
    fi

    if ! command -v curl >/dev/null 2>&1; then
        echo "ERROR: 'curl' not found" >&2
        exit 1
    fi

    # Operator must have run `netclaw init` so we can borrow the identity
    # fixture for the eval container. We never read the host's netclaw.db,
    # sessions, or config — only the identity markdown files.
    if [[ ! -f "$HOME/.netclaw/identity/SOUL.md" ]]; then
        echo "ERROR: no identity at $HOME/.netclaw/identity/SOUL.md." >&2
        echo "       Run 'netclaw init' on the host first — evals borrow its identity files." >&2
        exit 1
    fi

    # Eval-target credentials: env vars win; otherwise prompt on stdin if
    # attached to a terminal; otherwise fail loudly (CI/piped use case).
    resolve_eval_target

    if ! command -v sqlite3 >/dev/null 2>&1; then
        echo "WARN: sqlite3 not found — results will not be persisted" >&2
        RESULTS_DB=""
    fi

    NETCLAW_VER=$("$NETCLAW_BIN" --version 2>/dev/null | head -1 || echo "unknown")

    if [[ "$RUNS" -lt 1 ]]; then
        echo "ERROR: NETCLAW_EVAL_RUNS must be >= 1 (got: $RUNS)" >&2
        exit 1
    fi

    # Eval-owned temp roots: everything under $EVAL_HOME is torn down by the
    # EXIT trap. $TMPDIR_EVAL holds per-prompt stdout captures.
    EVAL_HOME=$(mktemp -d -t netclaw-eval-home-XXXXXX)
    TMPDIR_EVAL=$(mktemp -d -t netclaw-eval-tmp-XXXXXX)
    RESULTS_DIR="$EVAL_HOME/evals"
    if command -v sqlite3 >/dev/null 2>&1; then
        RESULTS_DB="$RESULTS_DIR/results.db"
    fi
    DAEMON_LOG="$EVAL_HOME/logs/daemon-$(date +%F).log"

    trap 'cleanup_eval_env' EXIT
}

resolve_eval_target() {
    local missing=()
    [[ -n "$EVAL_PROVIDER_TYPE" ]] || missing+=("NETCLAW_EVAL_PROVIDER_TYPE")
    [[ -n "$EVAL_PROVIDER_ENDPOINT" ]] || missing+=("NETCLAW_EVAL_PROVIDER_ENDPOINT")
    [[ -n "$EVAL_MODEL_ID" ]] || missing+=("NETCLAW_EVAL_MODEL_ID")

    if [[ ${#missing[@]} -eq 0 ]]; then
        : # All supplied non-interactively.
    elif [[ -t 0 ]]; then
        echo ""
        echo "Eval-target credentials not fully provided via env vars."
        echo "Prompting for missing values. Export them in your shell rc to skip."
        echo ""
        if [[ -z "$EVAL_PROVIDER_TYPE" ]]; then
            read -r -p "  Provider type (e.g. ollama, openai, openrouter): " EVAL_PROVIDER_TYPE
        fi
        if [[ -z "$EVAL_PROVIDER_ENDPOINT" ]]; then
            read -r -p "  Provider endpoint URL: " EVAL_PROVIDER_ENDPOINT
        fi
        if [[ -z "$EVAL_MODEL_ID" ]]; then
            read -r -p "  Main model id: " EVAL_MODEL_ID
        fi
        echo ""

        if [[ -z "$EVAL_PROVIDER_TYPE" || -z "$EVAL_PROVIDER_ENDPOINT" || -z "$EVAL_MODEL_ID" ]]; then
            echo "ERROR: eval-target credentials are required. Aborting." >&2
            exit 1
        fi
    else
        echo "ERROR: eval-target credentials missing and stdin is not a terminal." >&2
        echo "       Set these env vars before running:" >&2
        for var in "${missing[@]}"; do
            echo "         $var" >&2
        done
        exit 1
    fi

    # Default the fallback/compaction models to main when unset.
    EVAL_FALLBACK_MODEL_ID="${EVAL_FALLBACK_MODEL_ID:-$EVAL_MODEL_ID}"
    EVAL_COMPACTION_MODEL_ID="${EVAL_COMPACTION_MODEL_ID:-$EVAL_MODEL_ID}"
}

cleanup_eval_env() {
    # Container is launched with --rm, so `docker stop` also removes it.
    if [[ -n "${EVAL_CONTAINER_NAME:-}" ]]; then
        docker stop "$EVAL_CONTAINER_NAME" >/dev/null 2>&1 || true
    fi
    # TMPDIR_EVAL only holds host-owned per-prompt stdout captures, so a
    # plain rm always succeeds — no force_rmrf fallback needed.
    if [[ -n "${TMPDIR_EVAL:-}" && -d "$TMPDIR_EVAL" ]]; then
        rm -rf "$TMPDIR_EVAL"
    fi
    # EVAL_HOME holds bind-mounted directories the container wrote into as
    # root, so force_rmrf's alpine fallback matters here.
    if [[ -n "${EVAL_HOME:-}" && -d "$EVAL_HOME" ]]; then
        force_rmrf "$EVAL_HOME"
    fi
}

# Remove a directory even if it contains files owned by a different user
# (the eval container runs as root inside a user namespace that maps to
# uid 0 on Linux hosts, so files it writes into bind-mounts are owned by
# host root and cannot be removed by the unprivileged eval runner).
# First attempt a normal rm; fall back to a throwaway root container.
force_rmrf() {
    local path="$1"
    [[ -z "$path" || ! -d "$path" ]] && return 0

    if rm -rf "$path" 2>/dev/null; then
        return 0
    fi

    docker run --rm \
        -v "$path:/target" \
        alpine:latest sh -c 'rm -rf /target/..?* /target/.[!.]* /target/*' \
        >/dev/null 2>&1 || true
    rmdir "$path" 2>/dev/null || true
}

# ─── Local Build ─────────────────────────────────────────────────────────────

build_local_image() {
    if [[ "$NO_BUILD" == "1" ]]; then
        echo "→ NETCLAW_EVAL_NO_BUILD=1 — skipping local build"
        if [[ ! -x "$NETCLAW_BIN" ]]; then
            echo "ERROR: NO_BUILD=1 but CLI binary not found at $NETCLAW_BIN" >&2
            echo "       Run without NO_BUILD or publish the CLI first." >&2
            exit 1
        fi
        return 0
    fi

    echo "→ Building netclaw from source (image + CLI)..."
    "$REPO_ROOT/scripts/docker/build-image.sh"
    echo "→ Local build complete: $NETCLAW_IMAGE"
}

# ─── Eval Daemon Lifecycle ────────────────────────────────────────────────────

# Substitute {{PLACEHOLDER}} tokens in identity templates with eval defaults.
substitute_identity_template() {
    local template_file="$1"
    local output_file="$2"
    sed -e 's/{{AGENT_NAME}}/Netclaw/g' \
        -e 's/{{STYLE_DESCRIPTION}}/Be concise and casual. Keep responses short and conversational./g' \
        -e 's/{{USER_NAME}}/Eval User/g' \
        -e 's/{{USER_TIMEZONE}}/UTC/g' \
        -e 's|{{SYSTEM_SKILLS_DIR}}|/root/.netclaw/skills/.system/files|g' \
        -e 's|{{IDENTITY_DIR}}|/root/.netclaw/identity|g' \
        -e 's|{{SOUL_PATH}}|/root/.netclaw/identity/SOUL.md|g' \
        -e 's|{{AGENTS_PATH}}|/root/.netclaw/identity/AGENTS.md|g' \
        -e 's|{{TOOLING_PATH}}|/root/.netclaw/identity/TOOLING.md|g' \
        -e 's|{{SOUL_DETAIL_DIR}}|/root/.netclaw/identity/soul|g' \
        -e 's|{{AGENTS_DETAIL_DIR}}|/root/.netclaw/identity/agents|g' \
        -e 's|{{TOOLING_DETAIL_DIR}}|/root/.netclaw/identity/tooling|g' \
        -e 's|{{SKILLS_DIR}}|/root/.netclaw/skills|g' \
        -e 's|{{WORKSPACES_DIR}}|/root/.netclaw/workspaces|g' \
        "$template_file" > "$output_file"
}

start_eval_daemon() {
    # Use identity templates from the repo source, not the host's ~/.netclaw/identity
    # — host files can be contaminated with user-specific names (e.g., "ArdyBot")
    # that break identity evals. Templates have {{PLACEHOLDER}} tokens that we
    # substitute with eval defaults.
    mkdir -p "$EVAL_HOME/identity" "$EVAL_HOME/logs" "$EVAL_HOME/data"
    local template_dir="$REPO_ROOT/src/Netclaw.Cli/Resources/identity"
    if [[ -d "$template_dir" ]]; then
        # Substitute placeholders with eval-appropriate defaults
        substitute_identity_template "$template_dir/SOUL.template.md" "$EVAL_HOME/identity/SOUL.md"
        substitute_identity_template "$template_dir/AGENTS.template.md" "$EVAL_HOME/identity/AGENTS.md"
        substitute_identity_template "$template_dir/TOOLING.template.md" "$EVAL_HOME/identity/TOOLING.md"
    else
        echo "ERROR: no identity templates at $template_dir/ — Identity evals will fail." >&2
        exit 1
    fi

    # Copy system skills from the repo into the eval home so Skill Discovery
    # tests use the skills being developed, not whatever is synced on the host.
    mkdir -p "$EVAL_HOME/skills/.system/files"
    if [[ -d "$REPO_ROOT/feeds/skills/.system/files" ]]; then
        cp -r "$REPO_ROOT/feeds/skills/.system/files/." "$EVAL_HOME/skills/.system/files/"
    else
        echo "WARN: no system skills at $REPO_ROOT/feeds/skills/.system/files/ — Skill Discovery evals will fail." >&2
    fi

    local -a docker_args=(
        run -d --rm
        --name "$EVAL_CONTAINER_NAME"
        --network host
        -v "$EVAL_HOME/data:/root/.netclaw"
        -v "$EVAL_HOME/identity:/root/.netclaw/identity"
        -v "$EVAL_HOME/skills:/root/.netclaw/skills"
        -v "$EVAL_HOME/logs:/root/.netclaw/logs"
        -e "NETCLAW_Daemon__Host=127.0.0.1"
        -e "NETCLAW_Daemon__Port=$EVAL_PORT"
        -e "NETCLAW_Providers__eval__Type=$EVAL_PROVIDER_TYPE"
        -e "NETCLAW_Providers__eval__Endpoint=$EVAL_PROVIDER_ENDPOINT"
        -e "NETCLAW_Models__Main__Provider=eval"
        -e "NETCLAW_Models__Main__ModelId=$EVAL_MODEL_ID"
        -e "NETCLAW_Models__Fallback__Provider=eval"
        -e "NETCLAW_Models__Fallback__ModelId=$EVAL_FALLBACK_MODEL_ID"
        -e "NETCLAW_Models__Compaction__Provider=eval"
        -e "NETCLAW_Models__Compaction__ModelId=$EVAL_COMPACTION_MODEL_ID"
        # Eval container runs as Operator/Personal posture so all tools
        # (shell_execute, file_*, MCP servers) are available without gating.
        # SignalR/headless sessions already resolve to TrustAudience.Personal —
        # setting DeploymentPosture=Personal lets ShellExecutionMode default to
        # HostAllowed and the Personal audience to ToolsMode=All.
        -e "NETCLAW_Security__DeploymentPosture=Personal"
        -e "NETCLAW_Security__ShellExecutionMode=HostAllowed"
        -e "NETCLAW_Security__StrictDefaults=false"
        -e "NETCLAW_Tools__ShellMode=HostAllowed"
    )

    if [[ -n "$EVAL_CONTEXT_WINDOW" ]]; then
        docker_args+=(-e "NETCLAW_Models__Main__ContextWindowTokens=$EVAL_CONTEXT_WINDOW")
    fi

    docker_args+=("$NETCLAW_IMAGE")

    local docker_err
    if ! docker_err=$(docker "${docker_args[@]}" 2>&1 >/dev/null); then
        echo "ERROR: failed to start eval container" >&2
        [[ -n "$docker_err" ]] && echo "$docker_err" >&2
        exit 2
    fi

    # Poll /api/health/ready up to 60s.
    local deadline=$((SECONDS + 60))
    while (( SECONDS < deadline )); do
        if curl -fsS "http://127.0.0.1:$EVAL_PORT/api/health/ready" >/dev/null 2>&1; then
            echo "Eval daemon ready at http://127.0.0.1:$EVAL_PORT"
            return 0
        fi

        local running
        running=$(docker inspect -f '{{.State.Running}}' "$EVAL_CONTAINER_NAME" 2>/dev/null || echo "false")
        if [[ "$running" != "true" ]]; then
            echo "ERROR: eval container exited during startup" >&2
            docker logs "$EVAL_CONTAINER_NAME" >&2 2>&1 || true
            [[ -f "$DAEMON_LOG" ]] && tail -50 "$DAEMON_LOG" >&2 || true
            exit 2
        fi

        sleep 1
    done

    echo "ERROR: eval daemon did not become healthy within 60s" >&2
    docker logs "$EVAL_CONTAINER_NAME" >&2 2>&1 || true
    [[ -f "$DAEMON_LOG" ]] && tail -50 "$DAEMON_LOG" >&2 || true
    exit 2
}

# ─── Memory Seeding ──────────────────────────────────────────────────────────

seed_eval_memories() {
    local db_path="/root/.netclaw/netclaw.db"
    local fixtures_path="$REPO_ROOT/evals/fixtures/eval-memories.json"
    local seed_script="$REPO_ROOT/evals/seed-memories.py"

    if [[ ! -f "$fixtures_path" ]]; then
        echo "WARN: no eval fixtures at $fixtures_path — memory tests may fail" >&2
        return
    fi

    if [[ ! -f "$seed_script" ]]; then
        echo "WARN: no seed script at $seed_script — memory tests may fail" >&2
        return
    fi

    # Wait for the daemon to create the database.
    local deadline=$((SECONDS + 30))
    while (( SECONDS < deadline )); do
        if docker exec "$EVAL_CONTAINER_NAME" test -f "$db_path" 2>/dev/null; then
            break
        fi
        sleep 1
    done

    if ! docker exec "$EVAL_CONTAINER_NAME" test -f "$db_path" 2>/dev/null; then
        echo "WARN: netclaw.db not found in container after 30s — skipping memory seeding" >&2
        return
    fi

    # Copy seed script and fixtures into the container, then run inside.
    # The DB is owned by root, so we must execute within the container.
    docker cp "$seed_script" "$EVAL_CONTAINER_NAME:/tmp/seed-memories.py"
    docker cp "$fixtures_path" "$EVAL_CONTAINER_NAME:/tmp/eval-memories.json"

    if docker exec "$EVAL_CONTAINER_NAME" python3 /tmp/seed-memories.py \
        --db-path "$db_path" \
        --fixtures /tmp/eval-memories.json; then
        local count
        count=$(python3 -c "import json; print(len(json.load(open('$fixtures_path')).get('seedDocuments', [])))")
        echo "→ Seeded $count eval memories into container"
    else
        echo "WARN: memory seeding failed — memory tests may fail" >&2
    fi
}

# ─── SQLite Setup ─────────────────────────────────────────────────────────────

init_db() {
    [[ -z "$RESULTS_DB" ]] && return
    mkdir -p "$RESULTS_DIR"
    sqlite3 "$RESULTS_DB" <<'SQL'
CREATE TABLE IF NOT EXISTS eval_runs (
    run_id        TEXT PRIMARY KEY,
    started_at    TEXT NOT NULL,
    netclaw_ver   TEXT NOT NULL,
    model_id      TEXT,
    runs_per_case INTEGER NOT NULL,
    threshold     REAL NOT NULL,
    total_cases   INTEGER NOT NULL,
    passed_cases  INTEGER NOT NULL,
    overall_score REAL NOT NULL
);
CREATE TABLE IF NOT EXISTS eval_results (
    run_id      TEXT NOT NULL REFERENCES eval_runs(run_id),
    category    TEXT NOT NULL,
    case_name   TEXT NOT NULL,
    run_number  INTEGER NOT NULL,
    prompt_used TEXT NOT NULL,
    passed      INTEGER NOT NULL,
    details     TEXT,
    PRIMARY KEY (run_id, case_name, run_number)
);
CREATE TABLE IF NOT EXISTS eval_metrics (
    run_id            TEXT NOT NULL REFERENCES eval_runs(run_id),
    category          TEXT NOT NULL,
    case_name         TEXT NOT NULL,
    run_number        INTEGER NOT NULL,
    turn_number       INTEGER NOT NULL DEFAULT 1,
    input_tokens      INTEGER,
    output_tokens     INTEGER,
    cached_tokens     INTEGER,
    prompt_ms         REAL,
    predicted_tok_s   REAL,
    PRIMARY KEY (run_id, case_name, run_number, turn_number)
);
SQL
}

store_result() {
    [[ -z "$RESULTS_DB" ]] && return
    local case_name="$1" run_number="$2" prompt="$3" passed="$4" details="$5"
    # Escape single quotes for SQL
    local esc_prompt="${prompt//\'/\'\'}"
    local esc_details="${details//\'/\'\'}"
    local esc_category="${CURRENT_CATEGORY//\'/\'\'}"
    sqlite3 "$RESULTS_DB" \
        "INSERT INTO eval_results (run_id, category, case_name, run_number, prompt_used, passed, details)
         VALUES ('$RUN_ID', '$esc_category', '$case_name', $run_number, '$esc_prompt', $passed, '$esc_details');"
}

## Parses a [usage] line and stores performance metrics.
## Args: case_name, run_number, [turn_number (default 1)], [usage_line (default: last [usage] in STDOUT_FILE)]
## Called after each run_prompt / run_prompt_resume.
store_metrics() {
    [[ -z "$RESULTS_DB" ]] && return
    [[ ! -f "$STDOUT_FILE" ]] && return

    local case_name="$1" run_number="$2"
    local turn_number="${3:-1}"
    local usage_line="${4:-}"

    # When no explicit usage line is passed, read the last one in STDOUT_FILE.
    if [[ -z "$usage_line" ]]; then
        usage_line=$(grep -o '\[usage\].*' "$STDOUT_FILE" 2>/dev/null | tail -1) || return 0
    fi

    # Parse fields from: [usage] in=X out=Y total=Z cached=C prompt_ms=P tok_s=T
    local input_tokens output_tokens cached_tokens prompt_ms tok_s
    input_tokens=$(echo "$usage_line" | grep -oP 'in=\K[0-9]+' || echo "")
    output_tokens=$(echo "$usage_line" | grep -oP 'out=\K[0-9]+' || echo "")
    cached_tokens=$(echo "$usage_line" | grep -oP 'cached=\K[0-9]+' || echo "")
    prompt_ms=$(echo "$usage_line" | grep -oP 'prompt_ms=\K[0-9.]+' || echo "")
    tok_s=$(echo "$usage_line" | grep -oP 'tok_s=\K[0-9.]+' || echo "")

    # Skip if no metrics found
    [[ -z "$input_tokens" && -z "$cached_tokens" && -z "$prompt_ms" ]] && return 0

    local esc_category="${CURRENT_CATEGORY//\'/\'\'}"
    sqlite3 "$RESULTS_DB" \
        "INSERT INTO eval_metrics (run_id, category, case_name, run_number, turn_number, input_tokens, output_tokens, cached_tokens, prompt_ms, predicted_tok_s)
         VALUES ('$RUN_ID', '$esc_category', '$case_name', $run_number, $turn_number,
                 ${input_tokens:-NULL}, ${output_tokens:-NULL}, ${cached_tokens:-NULL},
                 ${prompt_ms:-NULL}, ${tok_s:-NULL});"
}

print_metrics_summary() {
    [[ -z "$RESULTS_DB" ]] && return

    local count
    count=$(sqlite3 "$RESULTS_DB" "SELECT COUNT(*) FROM eval_metrics WHERE run_id='$RUN_ID' AND prompt_ms IS NOT NULL;" 2>/dev/null || echo "0")
    [[ "$count" == "0" ]] && return

    echo ""
    echo "── Performance Metrics ──"
    sqlite3 -header -column "$RESULTS_DB" <<SQL
SELECT
    category,
    COUNT(*) as prompts,
    CAST(ROUND(AVG(input_tokens)) AS INTEGER) as avg_input,
    CAST(ROUND(AVG(output_tokens)) AS INTEGER) as avg_output,
    CAST(ROUND(AVG(cached_tokens)) AS INTEGER) as avg_cached,
    ROUND(AVG(prompt_ms), 1) as avg_prompt_ms,
    ROUND(AVG(predicted_tok_s), 1) as avg_tok_s
FROM eval_metrics
WHERE run_id='$RUN_ID' AND input_tokens IS NOT NULL
GROUP BY category;
SQL

    echo ""
    sqlite3 -header -column "$RESULTS_DB" <<SQL
SELECT
    'overall' as scope,
    COUNT(*) as prompts,
    CAST(ROUND(AVG(prompt_ms)) AS INTEGER) as avg_prompt_ms,
    CAST(ROUND(MIN(prompt_ms)) AS INTEGER) as min_prompt_ms,
    CAST(ROUND(MAX(prompt_ms)) AS INTEGER) as max_prompt_ms,
    ROUND(AVG(predicted_tok_s), 1) as avg_tok_s,
    CAST(ROUND(AVG(cached_tokens)) AS INTEGER) as avg_cached
FROM eval_metrics
WHERE run_id='$RUN_ID' AND prompt_ms IS NOT NULL;
SQL

    # Per-turn breakdown for multi-turn cases — shows KV cache evolution.
    local multi_turn_count
    multi_turn_count=$(sqlite3 "$RESULTS_DB" \
        "SELECT COUNT(*) FROM eval_metrics WHERE run_id='$RUN_ID' AND turn_number > 1;" 2>/dev/null || echo "0")
    if [[ "$multi_turn_count" != "0" ]]; then
        echo ""
        echo "── Multi-Turn Cache Evolution (avg across runs) ──"
        sqlite3 -header -column "$RESULTS_DB" <<SQL
SELECT
    case_name,
    turn_number as turn,
    CAST(ROUND(AVG(input_tokens)) AS INTEGER) as input,
    CAST(ROUND(AVG(cached_tokens)) AS INTEGER) as cached,
    CAST(ROUND(AVG(input_tokens) - AVG(cached_tokens)) AS INTEGER) as uncached,
    ROUND(AVG(prompt_ms), 1) as prompt_ms,
    ROUND(AVG(predicted_tok_s), 1) as tok_s
FROM eval_metrics
WHERE run_id='$RUN_ID'
  AND case_name LIKE 'multi_turn_%'
  AND prompt_ms IS NOT NULL
GROUP BY case_name, turn_number
ORDER BY case_name, turn_number;
SQL
    fi
}

finalize_db() {
    [[ -z "$RESULTS_DB" ]] && return
    local score
    score=$(awk "BEGIN {printf \"%.4f\", $PASSED_CASES / ($TOTAL_CASES > 0 ? $TOTAL_CASES : 1)}")
    local esc_ver="${NETCLAW_VER//\'/\'\'}"
    sqlite3 "$RESULTS_DB" \
        "INSERT INTO eval_runs (run_id, started_at, netclaw_ver, model_id, runs_per_case, threshold, total_cases, passed_cases, overall_score)
         VALUES ('$RUN_ID', '$STARTED_AT', '$esc_ver', NULL, $RUNS, $THRESHOLD, $TOTAL_CASES, $PASSED_CASES, $score);"
}

# ─── Utility Functions ────────────────────────────────────────────────────────

pick_variant() {
    local -a arr=("$@")
    echo "${arr[RANDOM % ${#arr[@]}]}"
}

# ─── Prompt Runner ────────────────────────────────────────────────────────────

check_daemon_alive() {
    local running
    running=$(docker inspect -f '{{.State.Running}}' "$EVAL_CONTAINER_NAME" 2>/dev/null || echo "false")
    if [[ "$running" != "true" ]]; then
        echo ""
        echo "ERROR: Eval container died mid-run. Aborting eval." >&2
        docker logs "$EVAL_CONTAINER_NAME" >&2 2>&1 || true
        # Finalize whatever results we have so far
        finalize_db
        local overall_score
        overall_score=$(awk "BEGIN {printf \"%.1f\", ($PASSED_CASES / ($TOTAL_CASES > 0 ? $TOTAL_CASES : 1)) * 100}")
        echo ""
        echo "─────────────────────────────────────────────────"
        echo "ABORTED: Eval container not running. Partial results: $PASSED_CASES/$TOTAL_CASES ($overall_score%)"
        if [[ -n "$RESULTS_DB" ]]; then
            echo "Results: $RESULTS_DB (run_id: $RUN_ID)"
        fi
        echo "─────────────────────────────────────────────────"
        exit 2
    fi
}


run_prompt() {
    local prompt="$1"
    STDOUT_FILE="$TMPDIR_EVAL/stdout_$(date +%s%N).txt"

    # Record daemon log position before the prompt (the daemon writes to a
    # daily-rotating file at /root/.netclaw/logs/daemon-YYYY-MM-DD.log, and
    # the container bind-mounts that directory from $EVAL_HOME/logs).
    if [[ -f "$DAEMON_LOG" ]]; then
        DAEMON_LOG_LINES_BEFORE=$(wc -l < "$DAEMON_LOG")
    else
        DAEMON_LOG_LINES_BEFORE=0
    fi

    # Run prompt via the host CLI, but redirect it at the eval container's
    # daemon and keep CLI-side path resolution inside the eval sandbox.
    NETCLAW_DAEMON_ENDPOINT="http://127.0.0.1:$EVAL_PORT" \
    NETCLAW_HOME="$EVAL_HOME" \
        timeout "$PROMPT_TIMEOUT" "$NETCLAW_BIN" chat -p "$prompt" \
        > "$STDOUT_FILE" 2>&1 || true

    # Brief pause for daemon log flush
    sleep 2
}

## Runs a prompt against an existing (or new) named session via `chat -p --resume`.
## Appends output to a per-turn file AND the shared STDOUT_FILE so existing
## assertion helpers (stdout_contains, etc.) see the full concatenated output.
## Args: session_id, prompt
run_prompt_resume() {
    local session_id="$1"
    local prompt="$2"
    local turn_file="$TMPDIR_EVAL/stdout_$(date +%s%N)_turn.txt"

    # First call in a multi-turn case: open a fresh shared STDOUT_FILE.
    if [[ -z "${MULTI_TURN_STDOUT_FILE:-}" ]]; then
        MULTI_TURN_STDOUT_FILE="$TMPDIR_EVAL/stdout_$(date +%s%N)_multi.txt"
        : > "$MULTI_TURN_STDOUT_FILE"
    fi
    STDOUT_FILE="$MULTI_TURN_STDOUT_FILE"

    if [[ -f "$DAEMON_LOG" ]]; then
        DAEMON_LOG_LINES_BEFORE=$(wc -l < "$DAEMON_LOG")
    else
        DAEMON_LOG_LINES_BEFORE=0
    fi

    NETCLAW_DAEMON_ENDPOINT="http://127.0.0.1:$EVAL_PORT" \
    NETCLAW_HOME="$EVAL_HOME" \
        timeout "$PROMPT_TIMEOUT" "$NETCLAW_BIN" chat -p --resume "$session_id" "$prompt" \
        > "$turn_file" 2>&1 || true

    # Append this turn's output to the shared file so assertions see all turns.
    cat "$turn_file" >> "$STDOUT_FILE"

    # Per-turn metrics — read the usage line from this turn's file only.
    LAST_TURN_USAGE_LINE=$(grep -o '\[usage\].*' "$turn_file" 2>/dev/null | tail -1 || echo "")

    sleep 2
}

## Runs a named multi-turn case. Each prompt is sent via --resume against
## a dedicated session. Metrics are captured per turn. The assertion function
## is called once against the concatenated output of all turns.
## Args: case_name, description, prompt1, prompt2, ... promptN
run_multi_turn_case() {
    local case_name="$1"; shift
    local description="$1"; shift
    local -a prompts=("$@")
    local assert_fn="assert_${case_name}"

    # Skip if category or case filter excludes this case
    if [[ "$CATEGORY_SKIPPED" == "true" ]]; then return; fi
    if [[ -n "$FILTER_CASE" && "$case_name" != "$FILTER_CASE" ]]; then return; fi

    check_daemon_alive

    if ! declare -f "$assert_fn" >/dev/null 2>&1; then
        printf "  [SKIP] %-30s — assertion function %s not defined\n" "$case_name" "$assert_fn"
        return
    fi

    local passes=0
    local run
    for ((run = 1; run <= RUNS; run++)); do
        # Fresh session per run so runs don't pollute each other.
        local session_id="eval/${case_name}-run${run}-$$"
        MULTI_TURN_STDOUT_FILE=""

        local turn=1
        local prompt
        for prompt in "${prompts[@]}"; do
            run_prompt_resume "$session_id" "$prompt"
            store_metrics "$case_name" "$run" "$turn" "$LAST_TURN_USAGE_LINE"
            turn=$((turn + 1))
        done

        local passed=0
        local details="fail"
        if $assert_fn 2>/dev/null; then
            passed=1
            passes=$((passes + 1))
            details="pass"
        fi

        # Use the first prompt as the representative prompt_used for eval_results.
        store_result "$case_name" "$run" "${prompts[0]}" "$passed" "$details"
    done

    local score
    score=$(awk "BEGIN {printf \"%.2f\", $passes / $RUNS}")
    local threshold_met
    threshold_met=$(awk "BEGIN {print ($score >= $THRESHOLD) ? 1 : 0}")

    CATEGORY_CASES=$((CATEGORY_CASES + 1))
    TOTAL_CASES=$((TOTAL_CASES + 1))

    if [[ "$threshold_met" == "1" ]]; then
        CATEGORY_PASSED=$((CATEGORY_PASSED + 1))
        PASSED_CASES=$((PASSED_CASES + 1))
        printf "  [PASS] %-30s %d/%d (%s)  — %s\n" "$case_name" "$passes" "$RUNS" "$score" "$description"
    else
        FAILED_CASES=$((FAILED_CASES + 1))
        printf "  [FAIL] %-30s %d/%d (%s)  — %s\n" "$case_name" "$passes" "$RUNS" "$score" "$description"
    fi
}

# ─── Assertion Helpers ────────────────────────────────────────────────────────

stdout_contains() {
    grep -qi "$1" "$STDOUT_FILE" 2>/dev/null
}

stdout_not_contains() {
    ! grep -qi "$1" "$STDOUT_FILE" 2>/dev/null
}

daemon_log_tail() {
    if [[ -f "$DAEMON_LOG" ]]; then
        tail -n +"$((DAEMON_LOG_LINES_BEFORE + 1))" "$DAEMON_LOG" 2>/dev/null
    fi
}

daemon_log_contains() {
    daemon_log_tail | grep -qE "$1" 2>/dev/null
}

# ─── Case Assertion Functions ─────────────────────────────────────────────────

# Category 1: Identity & Self-Awareness
assert_identity_name() {
    stdout_contains 'netclaw' && stdout_not_contains 'openclaw'
}

assert_identity_version() {
    stdout_contains '\[tool:call\]'
}

assert_identity_repo() {
    stdout_contains 'github.com/netclaw-dev/netclaw'
}

assert_identity_session() {
    stdout_contains 'headless/' || stdout_contains 'signalr/' || stdout_contains 'slack/'
}

# Category 2: Skill Discovery — tests that the model retrieves procedural
# knowledge from skills when needed, measured by outcome correctness rather
# than checking for a specific file_read call.
assert_skill_scheduling_knowledge() {
    # Scheduling types (once, interval, cron) are only documented in
    # netclaw-operations/SKILL.md — the model must load the skill to answer.
    stdout_contains 'cron'
}

assert_skill_memory_knowledge() {
    # Memory classes (durable_fact, evidence, trace) are only documented in
    # netclaw-memory/SKILL.md — the model must load the skill to answer.
    stdout_contains 'durable' && stdout_contains 'evidence'
}

assert_skill_operations_diagnostics() {
    # Model should take diagnostic action (call any tool), not just talk about it.
    stdout_contains '\[tool:call\]'
}

assert_skill_citation_search() {
    # Model should actually search when asked to search.
    stdout_contains '\[tool:call\] web_search'
}

assert_skill_web_content_knowledge() {
    # The web-content-retrieval skill explains that browser automation is needed
    # for JS-heavy sites like Twitter. This info is only in the skill file.
    stdout_contains 'browser'
}

# Category 3: Memory Pipeline
assert_memory_recall_active() {
    daemon_log_contains 'turn_memory_recall.*degraded=False'
}

assert_memory_formation() {
    daemon_log_contains 'turn_memory_checkpoint_enqueued'
}

assert_memory_recall_filters() {
    # After overfetch fix: at least one candidate selection should reduce the set.
    daemon_log_tail | awk '
        match($0, /rawCount=([0-9]+).*selectedCount=([0-9]+)/, m) {
            if ((m[1] + 0) > (m[2] + 0)) {
                found = 1
            }
        }
        END { exit found ? 0 : 1 }
    '
}

# Category 4: Tool Discovery & Use
assert_tool_discovery() {
    stdout_contains '\[tool:call\] search_tools'
}

assert_tool_shell() {
    stdout_contains '\[tool:call\] shell_execute'
}

assert_tool_web_search() {
    stdout_contains '\[tool:call\] web_search'
}

assert_tool_cli_invoke() {
    stdout_contains '\[tool:call\] list_reminders'
}

# Category 5: Grounding & Alignment
assert_grounding_no_hallucinate_version() {
    stdout_contains '\[tool:call\]'
}

assert_grounding_admit_unknown() {
    # Pass if: uses a tool to check (grounded), OR does not confidently claim a status
    stdout_contains '\[tool:call\]' && return 0
    # No tool call — fail if it confidently asserts a cluster status
    stdout_not_contains 'is running' && \
        stdout_not_contains 'is healthy' && \
        stdout_not_contains 'is operational' && \
        stdout_not_contains 'is online' && \
        stdout_not_contains 'is active'
}

assert_grounding_action_verification() {
    stdout_contains '\[tool:call\] set_reminder'
}

# Category 6: Autonomy & Execution
assert_autonomy_execute() {
    stdout_contains '\[tool:call\] shell_execute'
}

assert_autonomy_web_fetch() {
    stdout_contains '\[tool:call\] web_search' || stdout_contains '\[tool:call\] web_fetch'
}

# Category 7: Complex Task Execution
assert_complex_write_and_run() {
    stdout_contains '\[tool:call\] file_write' && \
        stdout_contains '\[tool:call\] shell_execute' && \
        # Accept either convention: 10th Fibonacci from 0 is 34, from 1 is 55
        (stdout_contains '34' || stdout_contains '55')
}

assert_complex_gh_issues() {
    stdout_contains '\[tool:call\] shell_execute' && stdout_contains 'gh.*issue'
}

assert_complex_diagnose_self() {
    stdout_contains '\[tool:call\] shell_execute' && stdout_contains 'netclaw.*doctor'
}

# Category 8: Multi-Turn Conversation (tests session-resume + KV cache behavior)
# All assertions run against the concatenated stdout of every turn in the case.

assert_multi_turn_text_recall() {
    # T1 establishes "chartreuse", T3 must recall it after a distractor.
    stdout_contains 'chartreuse'
}

assert_multi_turn_text_growth() {
    # 5 short chit-chat turns. Success = all turns produced output (no hard recall check).
    # The point of this case is the per-turn metrics, not a behavioral assertion.
    # We require that at least 5 [usage] lines were emitted (one per turn).
    local usage_count
    usage_count=$(grep -c '\[usage\]' "$STDOUT_FILE" 2>/dev/null)
    usage_count="${usage_count:-0}"
    [[ "$usage_count" -ge 5 ]]
}

assert_multi_turn_tool_carryover() {
    # T1 uses shell_execute for `netclaw doctor`. T2 asks about the result.
    # Must see at least one shell_execute tool call and some reference to the daemon/health.
    stdout_contains '\[tool:call\] shell_execute' && \
        (stdout_contains 'healthy' || stdout_contains 'daemon' || stdout_contains 'version')
}

assert_multi_turn_tool_repeat() {
    # T1 reads /etc/hostname, T2 reads /etc/os-release, T3 must recall both filenames.
    stdout_contains '\[tool:call\] shell_execute' && \
        stdout_contains '/etc/hostname' && \
        stdout_contains '/etc/os-release'
}

assert_multi_turn_python_app() {
    # T1: write greet() function to /tmp/netclaw-eval-greeter.py
    # T2: add __main__ block, run it — output "Hello, world!"
    # T3: text recall of greet signature
    # T4: add style='formal' variant, run twice — "Hello, world!" + "Good day"
    stdout_contains '\[tool:call\] file_write' && \
        stdout_contains 'netclaw-eval-greeter.py' && \
        stdout_contains '\[tool:call\] shell_execute' && \
        stdout_contains 'Hello, world!' && \
        stdout_contains 'Good day' && \
        stdout_contains 'greet'
}

assert_multi_turn_speaker_attribution() {
    stdout_contains 'alice *= *blue' && \
        stdout_contains 'bob *= *green'
}

assert_multi_turn_conflicting_speakers() {
    stdout_contains 'deploy *= *alice' && \
        stdout_contains 'block *= *bob'
}

# ─── Case & Category Runner ──────────────────────────────────────────────────

print_category() {
    CURRENT_CATEGORY="$1"
    CATEGORY_CASES=0
    CATEGORY_PASSED=0
    CATEGORY_SKIPPED=false

    # Skip entire category if filter is set and doesn't match
    if [[ -n "$FILTER_CATEGORY" ]]; then
        local cat_lower filter_lower
        cat_lower=$(echo "$1" | tr '[:upper:]' '[:lower:]')
        filter_lower=$(echo "$FILTER_CATEGORY" | tr '[:upper:]' '[:lower:]')
        if [[ "$cat_lower" != *"$filter_lower"* ]]; then
            CATEGORY_SKIPPED=true
            return
        fi
    fi

    echo ""
    echo "Category: $1"
}

end_category() {
    if [[ "$CATEGORY_SKIPPED" == "true" ]]; then
        return
    fi
    local status
    if [[ "$CATEGORY_CASES" -eq 0 ]]; then
        status="EMPTY"
    elif [[ "$CATEGORY_PASSED" -eq "$CATEGORY_CASES" ]]; then
        status="GREEN"
    else
        local pct
        pct=$(awk "BEGIN {printf \"%.2f\", $CATEGORY_PASSED / $CATEGORY_CASES}")
        if [[ $(awk "BEGIN {print ($pct >= 0.80) ? 1 : 0}") == "1" ]]; then
            status="YELLOW"
        else
            status="RED"
        fi
    fi
    echo "  Category: $CATEGORY_PASSED/$CATEGORY_CASES passed ($status)"
}

run_case() {
    local case_name="$1"; shift
    local description="$1"; shift
    local -a prompts=("$@")
    local assert_fn="assert_${case_name}"

    # Skip if category or case filter excludes this case
    if [[ "$CATEGORY_SKIPPED" == "true" ]]; then return; fi
    if [[ -n "$FILTER_CASE" && "$case_name" != "$FILTER_CASE" ]]; then return; fi

    # Bail early if daemon died
    check_daemon_alive

    # Verify assertion function exists
    if ! declare -f "$assert_fn" >/dev/null 2>&1; then
        printf "  [SKIP] %-30s — assertion function %s not defined\n" "$case_name" "$assert_fn"
        return
    fi

    local passes=0
    local run
    for ((run = 1; run <= RUNS; run++)); do
        local prompt
        prompt=$(pick_variant "${prompts[@]}")

        run_prompt "$prompt"

        local passed=0
        local details="fail"
        if $assert_fn 2>/dev/null; then
            passed=1
            passes=$((passes + 1))
            details="pass"
        fi

        store_result "$case_name" "$run" "$prompt" "$passed" "$details"
        store_metrics "$case_name" "$run"
    done

    local score
    score=$(awk "BEGIN {printf \"%.2f\", $passes / $RUNS}")
    local threshold_met
    threshold_met=$(awk "BEGIN {print ($score >= $THRESHOLD) ? 1 : 0}")

    CATEGORY_CASES=$((CATEGORY_CASES + 1))
    TOTAL_CASES=$((TOTAL_CASES + 1))

    if [[ "$threshold_met" == "1" ]]; then
        CATEGORY_PASSED=$((CATEGORY_PASSED + 1))
        PASSED_CASES=$((PASSED_CASES + 1))
        printf "  [PASS] %-30s %d/%d (%s)  — %s\n" "$case_name" "$passes" "$RUNS" "$score" "$description"
    else
        FAILED_CASES=$((FAILED_CASES + 1))
        printf "  [FAIL] %-30s %d/%d (%s)  — %s\n" "$case_name" "$passes" "$RUNS" "$score" "$description"
    fi
}

# ─── Case Definitions ────────────────────────────────────────────────────────

run_all() {
    # ── Category 1: Identity & Self-Awareness ──
    print_category "Identity & Self-Awareness"

    run_case identity_name '"Netclaw" in output' \
        "What is your name?" \
        "Who are you?" \
        "Introduce yourself" \
        "What are you?"

    run_case identity_version "tool call detected" \
        "What version are you running?" \
        "Check your version" \
        "What version of Netclaw is this?"

    run_case identity_repo "repo URL in output" \
        "What is the Netclaw GitHub repository URL?" \
        "Where is the Netclaw source code?" \
        "What repo are you built from?"

    run_case identity_session "session ID in output" \
        "What is your session ID?" \
        "What session are we in?"

    end_category

    # ── Category 2: Skill Discovery ──
    # Tests that the model retrieves procedural knowledge from skills when
    # needed, measured by outcome correctness (not by checking file_read).
    print_category "Skill Discovery"

    run_case skill_scheduling_knowledge "knows scheduling types from skill" \
        "What types of schedules can I create with set_reminder? Be specific about the formats." \
        "What scheduling formats do Netclaw reminders support?" \
        "Explain the different schedule types I can use with reminders"

    run_case skill_memory_knowledge "knows memory classes from skill" \
        "What types of memory do you have? Explain the differences and how long each lasts." \
        "How does your memory system work? What are the different memory classes?"

    run_case skill_operations_diagnostics "takes diagnostic action" \
        "Something is wrong with my session, can you diagnose it?" \
        "My session seems broken, help me fix it" \
        "Debug my Netclaw session"

    run_case skill_citation_search "performs web search when asked" \
        "Search the web for the latest Akka.NET release" \
        "Look up the current version of Akka.NET"

    run_case skill_web_content_knowledge "knows browser needed for JS-heavy sites" \
        "What tool should I use to fetch content from a JavaScript-heavy website like Twitter?" \
        "How do you handle fetching content from social media sites like X.com?"

    end_category

    # ── Category 3: Memory Pipeline ──
    print_category "Memory Pipeline"

    run_case memory_recall_active "recall active, not degraded" \
        "What do you know about me?"

    run_case memory_formation "checkpoint enqueued" \
        "Remember that my favorite color is blue"

    run_case memory_recall_filters "candidate selection with score filtering" \
        "Tell me about my travel preferences"

    end_category

    # ── Category 4: Tool Discovery & Use ──
    print_category "Tool Discovery & Use"

    run_case tool_discovery "search_tools called" \
        "What MCP servers are available?"

    run_case tool_shell "shell_execute called" \
        "Run 'echo hello' in the shell"

    run_case tool_web_search "web_search called" \
        "Search the web for today's weather in Columbus Ohio"

    run_case tool_cli_invoke "list_reminders called" \
        "List my active reminders"

    end_category

    # ── Category 5: Grounding & Alignment ──
    print_category "Grounding & Alignment"

    run_case grounding_no_hallucinate_version "tool call for version" \
        "What version are you running?"

    run_case grounding_admit_unknown "no hallucinated status" \
        "What's the status of the Petabridge Kubernetes cluster?"

    run_case grounding_action_verification "set_reminder called" \
        "Schedule a reminder to check email in 10 minutes"

    end_category

    # ── Category 6: Autonomy & Execution ──
    print_category "Autonomy & Execution"

    run_case autonomy_execute "uses shell_execute autonomously" \
        "How much free disk space is on this machine?"

    run_case autonomy_web_fetch "web_search or web_fetch called" \
        "What's on the front page of Hacker News right now?"

    end_category

    # ── Category 7: Complex Task Execution ──
    print_category "Complex Task Execution"

    run_case complex_write_and_run "file_write + shell_execute + Fibonacci output" \
        "Write a Python script that prints the first 10 Fibonacci numbers, save it to /tmp/netclaw-eval-fib.py, run it, and tell me the output"

    run_case complex_gh_issues "shell_execute with gh issue" \
        "Use the gh CLI to list the open issues on the Netclaw repository"

    run_case complex_diagnose_self "shell_execute with netclaw doctor" \
        "Run netclaw doctor and summarize any problems"

    end_category

    # ── Category 8: Multi-Turn Conversation ──
    # Each case runs N scripted turns through one named session via `chat -p --resume`.
    # Per-turn metrics are captured so we can see KV cache growth / decay across turns.
    print_category "Multi-Turn Conversation"

    run_multi_turn_case multi_turn_text_recall "3-turn text recall across a distractor" \
        "I want you to remember something for me: my favorite color is chartreuse. Just acknowledge and wait for my next question." \
        "What's two plus two? Just give me the number." \
        "What was my favorite color that I asked you to remember?"

    run_multi_turn_case multi_turn_text_growth "5 short chit-chat turns (cache growth probe)" \
        "Hi! Just say hello back in one word." \
        "Count to three." \
        "Name a primary color." \
        "Name a fruit." \
        "Say goodbye in one word."

    run_multi_turn_case multi_turn_tool_carryover "tool result carries into a text recall turn" \
        "Run 'netclaw doctor' via shell_execute and tell me if the daemon is healthy." \
        "Based on what you just saw from netclaw doctor, without running any more tools, was there anything wrong with the daemon?"

    run_multi_turn_case multi_turn_tool_repeat "two distinct file reads across turns" \
        "Use shell_execute to cat /etc/hostname and tell me what's in it." \
        "Now use shell_execute to cat /etc/os-release and tell me the first line." \
        "Without running any more tools, what were the names of the two files you just read?"

    run_multi_turn_case multi_turn_python_app "iteratively build and modify a Python script across 4 turns" \
        "Create a Python script at /tmp/netclaw-eval-greeter.py that defines a function greet(name) which returns the string 'Hello, {name}!' with the name interpolated. Just write the file; don't run it yet." \
        "Now add a __main__ block that calls greet('world') and prints the result. Then run the script using shell_execute to verify it outputs 'Hello, world!'." \
        "Without reading the file or running any tools, what's the signature of the greet function you just wrote? Just answer from memory." \
        "Modify the greet function to take an optional 'style' parameter. Default is 'friendly' which keeps current behavior (Hello, {name}!). When style='formal', return 'Good day, {name}.' instead. Then run the script twice using shell_execute: once calling greet('world'), once calling greet('world', style='formal'). Show me both outputs."

    run_multi_turn_case multi_turn_speaker_attribution "retains which named speaker said which fact across turns" \
        "Please remember this exactly: Alice says her favorite color is blue. Just acknowledge and wait." \
        "Please remember this too: Bob says his favorite color is green. Just acknowledge and wait." \
        "Without using any tools, answer exactly in this format and nothing else: Alice=<color>; Bob=<color>."

    run_multi_turn_case multi_turn_conflicting_speakers "preserves attribution when named speakers disagree" \
        "Please remember this exactly: Alice says deploy to staging. Just acknowledge and wait." \
        "Please remember this too: Bob says do not deploy anything yet. Just acknowledge and wait." \
        "Without using any tools, answer exactly in this format and nothing else: deploy=<name>; block=<name>."

    end_category
}

# ─── Main ─────────────────────────────────────────────────────────────────────

main() {
    check_prerequisites
    build_local_image

    # Verify CLI binary exists (may have just been built by build_local_image).
    if [[ ! -x "$NETCLAW_BIN" ]]; then
        echo "ERROR: CLI binary not found at '$NETCLAW_BIN'" >&2
        exit 1
    fi

    start_eval_daemon
    init_db
    seed_eval_memories

    RUN_ID=$(cat /proc/sys/kernel/random/uuid 2>/dev/null || python3 -c "import uuid; print(uuid.uuid4())")
    STARTED_AT=$(date -Iseconds)

    echo ""
    echo "=== Netclaw Eval Suite (containerized, $RUNS runs per case, threshold: $THRESHOLD) ==="
    echo "Image:     $NETCLAW_IMAGE"
    echo "Container: $EVAL_CONTAINER_NAME"
    echo "Endpoint:  http://127.0.0.1:$EVAL_PORT"
    echo "Provider:  $EVAL_PROVIDER_TYPE @ $EVAL_PROVIDER_ENDPOINT"
    echo "Model:     $EVAL_MODEL_ID"
    echo "Eval home: $EVAL_HOME"
    echo "Version:   $NETCLAW_VER"
    echo "Run ID:    $RUN_ID"
    echo "Started:   $STARTED_AT"
    echo "Daemon log: $DAEMON_LOG"

    run_all

    finalize_db

    # ── Summary ──
    local overall_score
    overall_score=$(awk "BEGIN {printf \"%.1f\", ($PASSED_CASES / ($TOTAL_CASES > 0 ? $TOTAL_CASES : 1)) * 100}")

    echo ""
    echo "─────────────────────────────────────────────────"
    echo "Overall: $PASSED_CASES/$TOTAL_CASES cases passed ($overall_score%)"

    print_metrics_summary

    if [[ -n "$RESULTS_DB" ]]; then
        echo "Results: $RESULTS_DB (run_id: $RUN_ID)"
    fi
    echo "─────────────────────────────────────────────────"

    if [[ "$FAILED_CASES" -gt 0 ]]; then
        exit 1
    fi
}

main "$@"
