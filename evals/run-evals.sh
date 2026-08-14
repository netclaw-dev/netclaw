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
STDERR_FILE=""
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

    if ! command -v jq >/dev/null 2>&1; then
        echo "ERROR: 'jq' not found. Install jq to run the eval suite." >&2
        exit 1
    fi

    # Identity files are rendered from repo templates into an isolated eval
    # home; the host does not need a pre-initialized ~/.netclaw tree.
    if [[ ! -f "$REPO_ROOT/src/Netclaw.Cli/Resources/identity/SOUL.template.md" ]]; then
        echo "ERROR: missing identity template at $REPO_ROOT/src/Netclaw.Cli/Resources/identity/SOUL.template.md." >&2
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
    # EXIT trap. $TMPDIR_EVAL holds per-prompt stdout and stderr captures.
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
    # Archive logs and results to a persistent location before teardown.
    archive_eval_run

    # Container is launched with --rm, so `docker stop` also removes it.
    if [[ -n "${EVAL_CONTAINER_NAME:-}" ]]; then
        docker stop "$EVAL_CONTAINER_NAME" >/dev/null 2>&1 || true
    fi
    # TMPDIR_EVAL only holds host-owned per-prompt stdout/stderr captures, so a
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

# Archive daemon logs, results DB, and stdout captures to evals/runs/<run-id>/
# so they survive the temp-dir teardown and can be inspected after the run.
archive_eval_run() {
    # Skip if no run ID (early failure before init completed)
    if [[ -z "${RUN_ID:-}" ]]; then return 0; fi

    local archive_dir="$REPO_ROOT/evals/runs/$RUN_ID"
    mkdir -p "$archive_dir"

    # Copy daemon log
    if [[ -f "${DAEMON_LOG:-}" ]]; then
        cp "$DAEMON_LOG" "$archive_dir/daemon.log" 2>/dev/null || true
    fi

    # Copy all container logs (crash logs, session logs)
    if [[ -d "$EVAL_HOME/data/logs" ]]; then
        mkdir -p "$archive_dir/container-logs"
        cp -r "$EVAL_HOME/data/logs/." "$archive_dir/container-logs/" 2>/dev/null || true
    fi
    # Also check the direct logs dir (bind-mount layout varies)
    if [[ -d "$EVAL_HOME/logs" ]]; then
        mkdir -p "$archive_dir/container-logs"
        cp -r "$EVAL_HOME/logs/." "$archive_dir/container-logs/" 2>/dev/null || true
    fi

    # Copy results DB
    if [[ -f "${RESULTS_DB:-}" ]]; then
        cp "$RESULTS_DB" "$archive_dir/results.db" 2>/dev/null || true
    fi

    # Copy stdout and stderr captures. Both land in the same archive_dir/stdout
    # directory — the stdout_*/stderr_* filename prefix already tells them apart,
    # and keeping stderr alongside stdout means a failing run's diagnostics
    # (denied approvals, daemon errors) are always one directory away.
    if [[ -n "${TMPDIR_EVAL:-}" && -d "$TMPDIR_EVAL" ]]; then
        mkdir -p "$archive_dir/stdout"
        cp "$TMPDIR_EVAL"/stdout_*.txt "$archive_dir/stdout/" 2>/dev/null || true
        cp "$TMPDIR_EVAL"/stderr_*.txt "$archive_dir/stdout/" 2>/dev/null || true
    fi

    # Write run metadata, including the immutable image identity so before/after
    # comparisons remain auditable even when tags are later rebuilt.
    local image_id
    image_id=$(docker image inspect "$NETCLAW_IMAGE" --format '{{.Id}}' 2>/dev/null || echo unknown)
    cat > "$archive_dir/run-info.txt" <<RUNEOF
run_id:    $RUN_ID
started:   ${STARTED_AT:-unknown}
image:     ${NETCLAW_IMAGE:-unknown}
image_id:  $image_id
model:     ${EVAL_MODEL_ID:-unknown}
provider:  ${EVAL_PROVIDER_TYPE:-unknown} @ ${EVAL_PROVIDER_ENDPOINT:-unknown}
version:   ${NETCLAW_VER:-unknown}
category:  ${FILTER_CATEGORY:-all}
case:      ${FILTER_CASE:-all}
timeout:   ${PROMPT_TIMEOUT:-60}s
runs:      ${RUNS:-5}
threshold: ${THRESHOLD:-0.80}
passed:    ${PASSED_CASES:-0}/${TOTAL_CASES:-0}
RUNEOF

    echo "Archived: $archive_dir"
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
        -e 's|{{SYSTEM_SKILLS_DIR}}|/home/netclaw/.netclaw/skills/.system/files|g' \
        -e 's|{{IDENTITY_DIR}}|/home/netclaw/.netclaw/identity|g' \
        -e 's|{{SOUL_PATH}}|/home/netclaw/.netclaw/identity/SOUL.md|g' \
        -e 's|{{AGENTS_PATH}}|/home/netclaw/.netclaw/identity/AGENTS.md|g' \
        -e 's|{{TOOLING_PATH}}|/home/netclaw/.netclaw/identity/TOOLING.md|g' \
        -e 's|{{SOUL_DETAIL_DIR}}|/home/netclaw/.netclaw/identity/soul|g' \
        -e 's|{{AGENTS_DETAIL_DIR}}|/home/netclaw/.netclaw/identity/agents|g' \
        -e 's|{{TOOLING_DETAIL_DIR}}|/home/netclaw/.netclaw/identity/tooling|g' \
        -e 's|{{SKILLS_DIR}}|/home/netclaw/.netclaw/skills|g' \
        -e 's|{{WORKSPACES_DIR}}|/home/netclaw/.netclaw/workspaces|g' \
        "$template_file" > "$output_file"
}

start_eval_daemon() {
    # Use identity templates from the repo source, not the host's ~/.netclaw/identity
    # — host files can be contaminated with user-specific names (e.g., "ArdyBot")
    # that break identity evals. Templates have {{PLACEHOLDER}} tokens that we
    # substitute with eval defaults.
    mkdir -p "$EVAL_HOME/identity" "$EVAL_HOME/logs" "$EVAL_HOME/data/config" "$EVAL_HOME/data/agents"
    local template_dir="$REPO_ROOT/src/Netclaw.Cli/Resources/identity"
    if [[ -d "$template_dir" ]]; then
        # Substitute placeholders with eval-appropriate defaults
        substitute_identity_template "$template_dir/SOUL.template.md" "$EVAL_HOME/identity/SOUL.md"
        if [[ -f "$REPO_ROOT/evals/fixtures/identity/AGENTS.md" ]]; then
            cp "$REPO_ROOT/evals/fixtures/identity/AGENTS.md" "$EVAL_HOME/identity/AGENTS.md"
        else
            echo "ERROR: deployment mission eval fixture is missing." >&2
            exit 1
        fi
        substitute_identity_template "$template_dir/TOOLING.template.md" "$EVAL_HOME/identity/TOOLING.md"
    else
        echo "ERROR: no identity templates at $template_dir/ — Identity evals will fail." >&2
        exit 1
    fi

    # Copy system skills from the repo into the eval home so Skill Discovery
    # tests use the skills being developed, not whatever is synced on the host.
    # SkillScanner expects <skills>/.system/<skill-name>/SKILL.md (no extra
    # `files/` segment); the daemon's feed sync writes to that layout, so we
    # mirror it here for local-source-of-truth runs.
    mkdir -p "$EVAL_HOME/skills/.system"
    if [[ -d "$REPO_ROOT/feeds/skills/.system/files" ]]; then
        cp -r "$REPO_ROOT/feeds/skills/.system/files/." "$EVAL_HOME/skills/.system/"
    else
        echo "WARN: no system skills at $REPO_ROOT/feeds/skills/.system/files/ — Skill Discovery evals will fail." >&2
    fi

    # Copy user skills from eval fixtures (non-system skills for activation testing).
    if [[ -d "$REPO_ROOT/evals/fixtures/skills" ]]; then
        cp -r "$REPO_ROOT/evals/fixtures/skills/." "$EVAL_HOME/skills/"
    fi

    # Copy a skill into a managed server-feed origin. The configured feed URL
    # below is intentionally unreachable: startup must retain the already
    # materialized managed skill while the eval proves model access by logical
    # name without relying on the physical feed path.
    if [[ -d "$REPO_ROOT/evals/fixtures/server-feed-skills" ]]; then
        mkdir -p "$EVAL_HOME/skills/.server-feeds/eval-feed"
        cp -r "$REPO_ROOT/evals/fixtures/server-feed-skills/." \
            "$EVAL_HOME/skills/.server-feeds/eval-feed/"
    fi

    # Copy eval-only subagent definitions into the mounted NETCLAW_HOME so
    # spawn_agent behavior can be exercised without touching the host install.
    if [[ -d "$REPO_ROOT/evals/fixtures/agents" ]]; then
        cp -r "$REPO_ROOT/evals/fixtures/agents/." "$EVAL_HOME/data/agents/"
    fi

    if [[ -f "$REPO_ROOT/evals/fixtures/mcp/prompt_server.py" ]]; then
        mkdir -p "$EVAL_HOME/data/evals"
        cp "$REPO_ROOT/evals/fixtures/mcp/prompt_server.py" \
            "$EVAL_HOME/data/evals/prompt_server.py"
        chmod ugo+x "$EVAL_HOME/data/evals/prompt_server.py"
    fi

    # Install the eval-only approval policy before daemon startup. Headless eval
    # sessions cannot answer approval prompts, so tools must be automatic for the
    # Personal audience. Exposure, filesystem, and command-deny rules remain in force.
    cp "$REPO_ROOT/evals/fixtures/config/netclaw.json" \
        "$REPO_ROOT/evals/fixtures/config/tool-approvals.json" \
        "$EVAL_HOME/data/config/"

    # Pre-seed a large (>256 KB) text file in the workspaces read-root for the
    # bounded-tool-output file_read eval (complex_large_file_read_ranged). It must
    # be too big for one inline read AND have model-unguessable content so the only
    # way to answer "what's on line 5000" is to page it with file_read StartLine/Limit
    # — the behavior the bounded-output steer is meant to elicit. A deterministic
    # Lehmer PRNG (pure integer modular arithmetic, identical across awk impls)
    # makes line 5000 reproducible so the eval can assert the exact value. Lives
    # under workspaces (a global read-root) rather than identity/skills so it is
    # not pulled into the system prompt or scanned as a skill. 30000 lines ≈ 314 KB.
    mkdir -p "$EVAL_HOME/data/workspaces"
    awk 'BEGIN{x=1;for(i=1;i<=30000;i++){x=(x*48271)%2147483647;print x}}' \
        > "$EVAL_HOME/data/workspaces/netclaw-eval-largefile.txt"

    # Seed deterministic local files for unaided file-vs-shell selection evals.
    # The prompts name goals and paths, never tool names.
    local selection_root="$EVAL_HOME/data/workspaces/file-tool-selection"
    mkdir -p "$selection_root/search-target/nested"
    printf 'first\nexpected-file-read-line\nthird\n' > "$selection_root/read-target.txt"
    printf 'initial-content\n' > "$selection_root/edit-target.txt"
    local i
    for ((i = 1; i <= 20; i++)); do
        printf 'ordinary-%s\n' "$i" > "$selection_root/search-target/file-$i.txt"
    done
    printf 'local-search-eval-token\n' > "$selection_root/search-target/nested/match.txt"

    # The eval container runs as the non-root `netclaw` user and needs write
    # access to the bind-mounted identity, logs, skills, and data trees.
    chmod -R ugo+rwX "$EVAL_HOME/identity" "$EVAL_HOME/logs" "$EVAL_HOME/data" "$EVAL_HOME/skills"

    local -a docker_args=(
        run -d --rm
        --name "$EVAL_CONTAINER_NAME"
        --network host
        -v "$EVAL_HOME/data:/home/netclaw/.netclaw"
        -v "$EVAL_HOME/identity:/home/netclaw/.netclaw/identity"
        -v "$EVAL_HOME/skills:/home/netclaw/.netclaw/skills"
        -v "$EVAL_HOME/logs:/home/netclaw/.netclaw/logs"
        -e "NETCLAW_Daemon__Host=127.0.0.1"
        -e "NETCLAW_Daemon__Port=$EVAL_PORT"
        -e "HOME=/home/netclaw"
        -e "NETCLAW_HOME=/home/netclaw/.netclaw"
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
        # Evals test the source tree, not the published feed. Without this, the
        # daemon syncs system skills from the live R2 manifest at startup, which
        # ships whatever was last released — masking any unpublished skill
        # changes (e.g. version bumps in this PR) and the local copies above.
        -e "NETCLAW_SkillSync__DisableSystemSkillSync=true"
        -e "NETCLAW_SkillFeeds__Feeds__0__Name=eval-feed"
        -e "NETCLAW_SkillFeeds__Feeds__0__Url=http://127.0.0.1:1"
        -e "NETCLAW_SkillFeeds__Feeds__0__Enabled=true"
        -e "NETCLAW_SkillFeeds__Feeds__0__TimeoutSeconds=1"
        -e "NETCLAW_SkillFeeds__SyncIntervalMinutes=0"
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
    local db_path="/home/netclaw/.netclaw/netclaw.db"
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

    # If the daemon has not touched memory yet, send one warm-up prompt through
    # the normal headless path so the session pipeline creates netclaw.db before
    # fixture seeding runs.
    if ! docker exec "$EVAL_CONTAINER_NAME" test -f "$db_path" 2>/dev/null; then
        NETCLAW_DAEMON_ENDPOINT="http://127.0.0.1:$EVAL_PORT" \
        NETCLAW_HOME="$EVAL_HOME" \
            timeout "$PROMPT_TIMEOUT" "$NETCLAW_BIN" chat -p "Warm up memory initialization. Reply with OK only." \
            >/dev/null 2>&1 || true
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

## Parses text or structured JSON usage output and stores performance metrics.
## Args: case_name, run_number, [turn_number (default 1)], [usage_line (default: last [usage] in STDOUT_FILE)]
## Called after each run_prompt / run_prompt_resume.
store_metrics() {
    [[ -z "$RESULTS_DB" ]] && return
    [[ ! -f "$STDOUT_FILE" ]] && return

    local case_name="$1" run_number="$2"
    local turn_number="${3:-1}"
    local usage_line="${4:-}"

    local input_tokens output_tokens cached_tokens prompt_ms tok_s

    # Structured cases keep tool calls separate from model text so assertions
    # can prove provenance. Preserve their performance metrics as well.
    if [[ -z "$usage_line" ]]; then
        if jq -e '.usage != null' "$STDOUT_FILE" >/dev/null 2>&1; then
            input_tokens=$(jq -r '.usage.inputTokens // empty' "$STDOUT_FILE")
            output_tokens=$(jq -r '.usage.outputTokens // empty' "$STDOUT_FILE")
            cached_tokens=$(jq -r '.usage.cachedInputTokens // empty' "$STDOUT_FILE")
            prompt_ms=$(jq -r '.usage.promptMs // empty' "$STDOUT_FILE")
            tok_s=$(jq -r '.usage.predictedPerSecond // empty' "$STDOUT_FILE")
        else
            usage_line=$(grep -ao '\[usage\].*' "$STDOUT_FILE" 2>/dev/null | tail -1) || return 0
        fi
    fi

    # Parse fields from: [usage] in=X out=Y total=Z cached=C prompt_ms=P tok_s=T
    if [[ -n "$usage_line" ]]; then
        input_tokens=$(echo "$usage_line" | grep -aoP 'in=\K[0-9]+' || echo "")
        output_tokens=$(echo "$usage_line" | grep -aoP 'out=\K[0-9]+' || echo "")
        cached_tokens=$(echo "$usage_line" | grep -aoP 'cached=\K[0-9]+' || echo "")
        prompt_ms=$(echo "$usage_line" | grep -aoP 'prompt_ms=\K[0-9.]+' || echo "")
        tok_s=$(echo "$usage_line" | grep -aoP 'tok_s=\K[0-9.]+' || echo "")
    fi

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
    local esc_model="${EVAL_MODEL_ID//\'/\'\'}"
    sqlite3 "$RESULTS_DB" \
        "INSERT INTO eval_runs (run_id, started_at, netclaw_ver, model_id, runs_per_case, threshold, total_cases, passed_cases, overall_score)
         VALUES ('$RUN_ID', '$STARTED_AT', '$esc_ver', '$esc_model', $RUNS, $THRESHOLD, $TOTAL_CASES, $PASSED_CASES, $score);"
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
    local output_format="${2:-text}"
    local ts
    ts="$(date +%s%N)"
    STDOUT_FILE="$TMPDIR_EVAL/stdout_${ts}.txt"
    STDERR_FILE="$TMPDIR_EVAL/stderr_${ts}.txt"

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
    local -a output_args=()
    if [[ "$output_format" == "json" ]]; then
        output_args+=(--json)
    fi

    # Stdout and stderr go to separate files. A merged capture corrupts
    # --json assertions: the CLI writes legitimate diagnostics (for example
    # "[error] Unknown output type from daemon: ...") to stderr, and a
    # trailing diagnostic line breaks jq's parse of the JSON envelope on
    # stdout, producing a false eval failure rather than a real one.
    NETCLAW_DAEMON_ENDPOINT="http://127.0.0.1:$EVAL_PORT" \
    NETCLAW_HOME="$EVAL_HOME" \
        timeout "$PROMPT_TIMEOUT" "$NETCLAW_BIN" chat -p "${output_args[@]}" "$prompt" \
        > "$STDOUT_FILE" 2> "$STDERR_FILE" || true

    # Brief pause for daemon log flush
    sleep 2
}

## Runs a prompt against an existing (or new) named session via `chat -p --resume`.
## Appends output to a per-turn file AND the shared STDOUT_FILE so existing
## assertion helpers (stdout_contains, etc.) see the full concatenated output.
## Stderr is captured the same way, into a separate shared STDERR_FILE, so
## stdout stays pure for assertions (see run_prompt for why that matters).
## Args: session_id, prompt
run_prompt_resume() {
    local session_id="$1"
    local prompt="$2"
    local ts
    ts="$(date +%s%N)"
    local turn_file="$TMPDIR_EVAL/stdout_${ts}_turn.txt"
    local turn_stderr_file="$TMPDIR_EVAL/stderr_${ts}_turn.txt"

    if [[ ! -x "$NETCLAW_BIN" ]]; then
        echo "ERROR: eval CLI disappeared during the run: $NETCLAW_BIN" >&2
        exit 2
    fi

    # First call in a multi-turn case: open fresh shared STDOUT_FILE/STDERR_FILE.
    if [[ -z "${MULTI_TURN_STDOUT_FILE:-}" ]]; then
        MULTI_TURN_STDOUT_FILE="$TMPDIR_EVAL/stdout_$(date +%s%N)_multi.txt"
        : > "$MULTI_TURN_STDOUT_FILE"
    fi
    STDOUT_FILE="$MULTI_TURN_STDOUT_FILE"

    if [[ -z "${MULTI_TURN_STDERR_FILE:-}" ]]; then
        MULTI_TURN_STDERR_FILE="$TMPDIR_EVAL/stderr_$(date +%s%N)_multi.txt"
        : > "$MULTI_TURN_STDERR_FILE"
    fi
    STDERR_FILE="$MULTI_TURN_STDERR_FILE"

    if [[ -f "$DAEMON_LOG" ]]; then
        DAEMON_LOG_LINES_BEFORE=$(wc -l < "$DAEMON_LOG")
    else
        DAEMON_LOG_LINES_BEFORE=0
    fi

    NETCLAW_DAEMON_ENDPOINT="http://127.0.0.1:$EVAL_PORT" \
    NETCLAW_HOME="$EVAL_HOME" \
        timeout "$PROMPT_TIMEOUT" "$NETCLAW_BIN" chat -p --resume "$session_id" "$prompt" \
        > "$turn_file" 2> "$turn_stderr_file" || true

    # Append this turn's output to the shared files so assertions see all turns.
    cat "$turn_file" >> "$STDOUT_FILE"
    cat "$turn_stderr_file" >> "$STDERR_FILE"

    # Per-turn metrics — read the usage line from this turn's file only.
    LAST_TURN_USAGE_LINE=$(grep -ao '\[usage\].*' "$turn_file" 2>/dev/null | tail -1 || echo "")

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
        MULTI_TURN_STDERR_FILE=""

        local setup_fn="setup_${case_name}"
        if declare -f "$setup_fn" >/dev/null 2>&1; then
            "$setup_fn" "$run"
        fi

        local turn=1
        local prompt
        for prompt in "${prompts[@]}"; do
            local rendered_prompt="$prompt"
            rendered_prompt="${rendered_prompt//\{\{FIRST_WORKTREE\}\}/${CODING_CONTEXT_FIRST_WORKTREE:-}}"
            rendered_prompt="${rendered_prompt//\{\{SECOND_WORKTREE\}\}/${CODING_CONTEXT_SECOND_WORKTREE:-}}"
            rendered_prompt="${rendered_prompt//\{\{TARGET_BRANCH\}\}/${CODING_CONTEXT_TARGET_BRANCH:-}}"
            rendered_prompt="${rendered_prompt//\{\{TARGET_FILE\}\}/${CODING_CONTEXT_TARGET_FILE:-}}"
            run_prompt_resume "$session_id" "$rendered_prompt"
            store_metrics "$case_name" "$run" "$turn" "$LAST_TURN_USAGE_LINE"
            turn=$((turn + 1))
        done

        local passed=0
        local details="fail"
        EVAL_ASSERTION_DETAILS=""
        if $assert_fn 2>/dev/null; then
            passed=1
            passes=$((passes + 1))
            details="pass"
        elif [[ -n "${EVAL_ASSERTION_DETAILS:-}" ]]; then
            details="$EVAL_ASSERTION_DETAILS"
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

# Transcript greps use -a as cheap hardening: captured CLI output may carry
# terminal control bytes, and -a keeps grep in text mode regardless. Note the
# memory_identity_preference_routing flake was NOT this — it was a substring
# bug in that assertion's pattern (see the comment there).

stdout_contains() {
    grep -qia "$1" "$STDOUT_FILE" 2>/dev/null
}

stdout_not_contains() {
    ! grep -qia "$1" "$STDOUT_FILE" 2>/dev/null
}

stdout_response_contains() {
    grep -av '^\[tool:call\]' "$STDOUT_FILE" 2>/dev/null | grep -qia "$1"
}

stdout_response_not_contains() {
    if grep -av '^\[tool:call\]' "$STDOUT_FILE" 2>/dev/null | grep -qia "$1"; then
        return 1
    fi
    return 0
}

daemon_log_tail() {
    if [[ -f "$DAEMON_LOG" ]]; then
        tail -n +"$((DAEMON_LOG_LINES_BEFORE + 1))" "$DAEMON_LOG" 2>/dev/null
    fi
}

daemon_log_contains() {
    daemon_log_tail | grep -qaE "$1" 2>/dev/null
}

daemon_log_skill_loaded() {
    local skill_name="$1"
    daemon_log_tail | grep -qaE "turn_skill_loaded skill=$skill_name" 2>/dev/null
}

daemon_log_skill_loaded_by_method() {
    local skill_name="$1"
    local method="$2"
    daemon_log_tail | grep -qaE \
        "turn_skill_loaded skill=$skill_name method=$method" 2>/dev/null
}

daemon_log_skill_loaded_via_skill_tool() {
    daemon_log_skill_loaded_by_method "$1" "skill_load"
}

daemon_log_skill_loaded_via_file_read() {
    daemon_log_skill_loaded_by_method "$1" "file_read"
}

daemon_log_no_skill_loaded() {
    ! daemon_log_tail | grep -qaE "turn_skill_loaded" 2>/dev/null
}

stdout_tool_called() {
    grep -qaE "\\[tool:call\\] $1\\(" "$STDOUT_FILE" 2>/dev/null
}

stdout_json_envelope_valid() {
    jq -e '
        type == "object"
        and (.sessionId | type == "string" and length > 0)
        and (.response | type == "string")
        and (.toolCalls == null or (.toolCalls | type == "array"))
    ' "$STDOUT_FILE" >/dev/null 2>&1
}

stdout_json_tool_called() {
    local tool_name="$1"
    jq -e --arg tool_name "$tool_name" \
        'any(.toolCalls[]?; .toolName == $tool_name)' \
        "$STDOUT_FILE" >/dev/null 2>&1
}

stdout_json_tool_call_arguments() {
    local tool_name="$1"
    jq -ce --arg tool_name "$tool_name" \
        '.toolCalls[]? | select(.toolName == $tool_name) | .argumentsJson | fromjson' \
        "$STDOUT_FILE" 2>/dev/null
}

stdout_skill_file_read_called() {
    grep -aiE '^\[tool:call\] file_read\(' "$STDOUT_FILE" 2>/dev/null \
        | grep -qi 'SKILL\.md'
}

stdout_no_skill_file_read_called() {
    ! stdout_skill_file_read_called
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

assert_identity_file_routing() {
    stdout_response_contains 'SOUL.md' && \
        stdout_response_contains 'AGENTS.md' && \
        stdout_response_contains 'TOOLING.md' && \
        daemon_log_no_skill_loaded
}

# Category 2: Skill Discovery — tests that the model retrieves procedural
# knowledge from skills when needed AND actually loaded the skill to get it.
assert_skill_scheduling_knowledge() {
    stdout_contains 'cron' \
        && daemon_log_skill_loaded_via_skill_tool 'netclaw-operations' \
        && stdout_no_skill_file_read_called
}

# CRON_TZ local-timezone discovery: for a local-time schedule the model must
# surface the CRON_TZ prefix. That detail lives in references/scheduling.md, so
# the model has to load netclaw-operations and recover it — not silently assume UTC.
assert_skill_cron_tz_timezone() {
    stdout_contains 'CRON_TZ' \
        && daemon_log_skill_loaded_via_skill_tool 'netclaw-operations' \
        && stdout_no_skill_file_read_called
}

# Two-hop progressive disclosure: the model must (1) load netclaw-operations, then
# (2) call skill_read_resource on references/scheduling.md to recover a detail that
# lives ONLY in the reference file (the auto-disable threshold + alert name), never
# in the slim SKILL.md index. Catches a model that loads the index but skips the
# second hop — the failure mode that silently regresses smaller local agents.
assert_skill_progressive_disclosure() {
    daemon_log_skill_loaded_via_skill_tool 'netclaw-operations' \
        && stdout_tool_called 'skill_read_resource' \
        && stdout_no_skill_file_read_called \
        && { stdout_contains 'ReminderAutoDisabled' || stdout_contains '5 consecutive'; }
}

assert_skill_memory_knowledge() {
    stdout_contains 'durable' && stdout_contains 'evidence' \
        && daemon_log_skill_loaded_via_skill_tool 'netclaw-memory' \
        && stdout_no_skill_file_read_called
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
    stdout_contains 'browser' \
        && daemon_log_skill_loaded_via_skill_tool 'web-content-retrieval' \
        && stdout_no_skill_file_read_called
}

# Category 2b: Skill Activation — measures ONLY whether the model loaded
# the skill, using prompts where pretraining cannot shortcut the answer.
assert_skill_activation_scheduling() {
    daemon_log_skill_loaded_via_skill_tool 'netclaw-operations' \
        && stdout_no_skill_file_read_called
}

assert_skill_activation_memory() {
    daemon_log_skill_loaded_via_skill_tool 'netclaw-memory' \
        && stdout_no_skill_file_read_called
}

assert_skill_activation_search() {
    daemon_log_skill_loaded_via_skill_tool 'search-citation' \
        && stdout_no_skill_file_read_called
}

# Soft phrasing — model may load the skill OR use the tool directly from AGENTS.md
assert_skill_activation_soft_scheduling() {
    { daemon_log_skill_loaded_via_skill_tool 'netclaw-operations' \
        || stdout_tool_called 'set_reminder'; } \
        && stdout_no_skill_file_read_called
}

assert_skill_activation_soft_memory() {
    { daemon_log_skill_loaded_via_skill_tool 'netclaw-memory' \
        || stdout_tool_called 'find_memories'; } \
        && stdout_no_skill_file_read_called
}

assert_skill_activation_subagent_authoring() {
    daemon_log_skill_loaded_via_skill_tool 'subagent-authoring' \
        && stdout_no_skill_file_read_called
}

# User skills (non-system, from eval fixtures)
assert_skill_activation_user_coding() {
    daemon_log_skill_loaded_via_skill_tool 'modern-csharp-coding-standards' \
        && stdout_no_skill_file_read_called
}

assert_skill_activation_user_serialization() {
    daemon_log_skill_loaded_via_skill_tool 'serialization' \
        && stdout_no_skill_file_read_called
}

assert_skill_server_feed_logical_access() {
    daemon_log_skill_loaded_via_skill_tool 'logical-feed-probe' \
        && stdout_tool_called 'skill_read_resource' \
        && stdout_contains 'ORBITAL-MANGO-7421' \
        && stdout_no_skill_file_read_called
}

assert_mcp_prompt_skill_activation() {
    daemon_log_skill_loaded_via_skill_tool 'mcp__eval_analytics__property-analytics' \
        && stdout_tool_called 'skill_load' \
        && stdout_contains 'EVAL-MCP-PROMPT-7421' \
        && stdout_no_skill_file_read_called
}

assert_mcp_prompt_skill_unrelated() {
    ! daemon_log_skill_loaded 'mcp__eval_analytics__property-analytics'
}

assert_skill_explicit_physical_inspection() {
    stdout_tool_called 'file_read' \
        && daemon_log_skill_loaded_via_file_read 'modern-csharp-coding-standards'
}

# Negative cases — model should NOT load a skill for unrelated prompts
assert_skill_no_activation_unrelated() {
    daemon_log_no_skill_loaded
}

assert_skill_no_activation_general_code() {
    daemon_log_no_skill_loaded
}

# Category 3: Memory Pipeline
assert_memory_recall_active() {
    # The structured turn_memory_recall event (TurnLog/Akka) no longer lands in the file logs
    # after the log-stream partition (#1472). Assert on the MEL recall-pipeline signals that do:
    # a completed retrieval (memory_retrieval_final) with no degrade warning.
    daemon_log_contains 'memory_retrieval_final' \
        && ! daemon_log_contains 'memory_recall_degraded'
}

# Per the netclaw-agent-memory spec, durable user preferences are memory documents,
# not identity-file edits. The invariant under test: the preference is routed to
# memory and NOT written to an identity file (SOUL.md). The eval memory store is
# shared across runs, so after the first store the model correctly recognizes the
# fact is already in durable memory rather than re-storing it — both are correct
# routing. The hard failure we guard against is a SOUL.md (file_edit/file_write) edit.
assert_memory_identity_preference_routing() {
    # 'memor' not 'memory': correct responses often say "memories", and the
    # plural drops the y — "memories" does not contain the substring "memory".
    # Run af0883b5 rejected two behaviorally-correct runs on exactly this.
    ! stdout_tool_called 'file_edit' \
        && ! stdout_tool_called 'file_write' \
        && { stdout_tool_called 'store_memory' || stdout_contains 'memor'; }
}

assert_memory_explicit_store() {
    stdout_tool_called 'store_memory' || stdout_tool_called 'update_memory'
}

assert_memory_checkpoint_enqueue() {
    # turn_memory_checkpoint_enqueued (TurnLog/Akka) no longer lands in the file logs after the
    # log-stream partition (#1472). A turn-complete checkpoint that was enqueued is proven by the
    # curation worker processing it (MEL, in daemon.log) — whether the fact is later kept or
    # dropped. Combined with no explicit memory tool call, this verifies automatic enqueue.
    daemon_log_contains 'Memory checkpoint curation completed.*trigger=turn-complete' \
        && ! stdout_tool_called 'store_memory' \
        && ! stdout_tool_called 'update_memory'
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

assert_tool_channel_lookup_discovery() {
    stdout_contains '\[tool:call\] search_tools'
}

assert_tool_shell() {
    stdout_contains '\[tool:call\] shell_execute'
}

assert_tool_web_search() {
    stdout_tool_called 'web_search' \
        && ! stdout_tool_called 'shell_execute'
}

assert_tool_cli_invoke() {
    stdout_contains '\[tool:call\] list_reminders'
}

assert_tool_file_list() {
    stdout_tool_called 'file_list'
}

assert_tool_known_file_read() {
    stdout_tool_called 'file_read' \
        && ! stdout_tool_called 'shell_execute' \
        && stdout_response_contains 'expected-file-read-line'
}

assert_tool_known_directory_list() {
    stdout_tool_called 'file_list' \
        && ! stdout_tool_called 'shell_execute' \
        && stdout_response_contains 'read-target.txt' \
        && stdout_response_contains 'edit-target.txt'
}

setup_tool_known_file_edit() {
    printf 'initial-content\n' \
        > "$EVAL_HOME/data/workspaces/file-tool-selection/edit-target.txt"
}

assert_tool_known_file_edit() {
    (stdout_tool_called 'file_edit' || stdout_tool_called 'file_write') \
        && ! stdout_tool_called 'shell_execute' \
        && grep -qx 'edited-content' \
            "$EVAL_HOME/data/workspaces/file-tool-selection/edit-target.txt"
}

assert_tool_local_repository_search() {
    stdout_tool_called 'shell_execute' \
        && ! stdout_tool_called 'web_search' \
        && stdout_response_contains 'match.txt'
}

assert_tool_timestamped_webhook() {
    stdout_tool_called 'set_webhook' \
        && stdout_contains 'HmacTimestamped' \
        && stdout_contains 'Stripe-Signature'
}

assert_tool_timeout_arg_recovery() {
    # Spelling-tolerant meta keys: a near-miss timeout key (TimeoutSeconds,
    # timeout_seconds, Timeout) now resolves onto _timeout_seconds and is
    # consumed directly — no rejection round-trip needed. If the model instead
    # emits the canonical key, that works too. Either way the command running is
    # the proof the timeout hint was honored, not dropped.
    stdout_contains '\[tool:call\] shell_execute' \
        && stdout_contains 'netclaw-timeout-eval-ok'
}

assert_tool_background_job_lifecycle() {
    # Detached-process regression (hung-session fix): a long-running command
    # must go through background submission (not block a synchronous call)
    # and be managed through the job surface (check_background_job), not
    # re-run or abandoned. Behavioral assertion only: the headless eval
    # container has no approval requester and 'sleep' is not on the safe
    # command allowlist, so the submission is denied at the approval gate —
    # the daemon-side lifecycle itself is covered by the unit/integration
    # suite. What this eval proves is that the MODEL reaches for the right
    # API shape: a shell_execute call carrying _background:true, plus a
    # check_background_job call for status/cancel.
    stdout_contains '\[tool:call\] shell_execute(.*_background.:true' \
        && stdout_contains '\[tool:call\] check_background_job'
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

# Local-timezone scheduling end-to-end: the model must call set_reminder AND
# carry the CRON_TZ prefix into the schedule, rather than silently converting to
# UTC. Proves the CRON_TZ capability is actually used, not just known.
assert_grounding_cron_tz_schedule() {
    stdout_tool_called 'set_reminder' \
        && stdout_contains 'CRON_TZ'
}

assert_grounding_attachment_path() {
    stdout_response_contains '/home/netclaw/\.netclaw/sessions/.*/inbox/image_1\.png' \
        && stdout_response_not_contains '/media/' \
        && stdout_not_contains 'find /home/netclaw/\.netclaw/sessions'
}

setup_grounding_attachment_path() {
    local run="$1"
    local session_dir="/home/netclaw/.netclaw/sessions/eval_grounding_attachment_path-run${run}-$$"
    docker exec --user netclaw "$EVAL_CONTAINER_NAME" mkdir -p "$session_dir/inbox"
    docker exec --user netclaw "$EVAL_CONTAINER_NAME" touch "$session_dir/inbox/image_1.png"
}

# Category 6: Autonomy & Execution
assert_autonomy_execute() {
    stdout_contains '\[tool:call\] shell_execute'
}

assert_autonomy_web_fetch() {
    stdout_contains '\[tool:call\] web_search' || stdout_contains '\[tool:call\] web_fetch'
}

# Category 6a: Deployment Mission
assert_deployment_mission_sales_email() {
    daemon_log_skill_loaded_via_skill_tool 'business-email-review' \
        && stdout_no_skill_file_read_called \
        && \
        stdout_response_contains '^Subject:' && \
        stdout_response_contains 'Would Tuesday or Wednesday work for a 15-minute call?'
}

# Category 6b: Subagents
assert_subagent_headless_ambiguous_task() {
    stdout_tool_called 'spawn_agent' && \
        stdout_contains '\[subagent:done\] headless-analyst (completed' && \
        stdout_response_contains 'assumption' && \
        stdout_response_not_contains 'which.*include' && \
        stdout_response_not_contains 'what.*include' && \
        stdout_response_not_contains 'please.*clarify' && \
        stdout_response_not_contains 'need.*more.*information'
}

assert_subagent_specialization_precedence() {
    stdout_tool_called 'spawn_agent' && \
        stdout_contains '\[subagent:done\] headless-analyst (completed' && \
        stdout_contains 'SPECIALIZED ANALYST BRIEF' && \
        stdout_response_contains '^Subject:' && \
        stdout_response_contains 'Would Tuesday or Wednesday work for a 15-minute call?'
}

setup_subagent_project_scope_declaration() {
    local run="$1"
    PROJECT_SCOPE_LOG_MARKER="$TMPDIR_EVAL/project-scope-$run.marker"
    touch "$PROJECT_SCOPE_LOG_MARKER"

    docker exec --user netclaw "$EVAL_CONTAINER_NAME" \
        mkdir -p /home/netclaw/.netclaw/workspaces/project-scope-target/src
    docker exec --user netclaw "$EVAL_CONTAINER_NAME" \
        sh -c 'printf "%s\n" "# Sample project" > /home/netclaw/.netclaw/workspaces/project-scope-target/README.md
printf "%s\n" "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>" > /home/netclaw/.netclaw/workspaces/project-scope-target/Project.csproj
printf "%s\n" "Console.WriteLine(\"sample\");" > /home/netclaw/.netclaw/workspaces/project-scope-target/src/Program.cs'
    docker exec --user netclaw "$EVAL_CONTAINER_NAME" \
        git -C /home/netclaw/.netclaw/workspaces/project-scope-target init -q
    docker exec --user netclaw "$EVAL_CONTAINER_NAME" \
        git -C /home/netclaw/.netclaw/workspaces/project-scope-target add README.md Project.csproj src/Program.cs
    docker exec --user netclaw "$EVAL_CONTAINER_NAME" \
        sh -c 'printf "%s\n" "<!-- changed -->" >> /home/netclaw/.netclaw/workspaces/project-scope-target/Project.csproj
printf "%s\n" "// changed" >> /home/netclaw/.netclaw/workspaces/project-scope-target/src/Program.cs'
}

assert_subagent_project_scope_declaration() {
    stdout_tool_called 'spawn_agent' || return 1
    stdout_contains '\[subagent:done\] project-scope-analyst (completed' || return 1
    stdout_response_contains 'Project.csproj' || return 1
    stdout_response_contains 'src' || return 1

    local child_log
    child_log=$(find "$EVAL_HOME/logs/sessions" -type f \
        -path '*_subagent_project-scope-analyst_*/session.log' \
        -newer "$PROJECT_SCOPE_LOG_MARKER" 2>/dev/null | head -1)
    [[ -n "$child_log" ]] || return 1

    local declared_line shell_line shell_result_line shell_count shell_result_count
    local status_command_count diff_command_count
    declared_line=$(grep -an \
        'SubAgent \[project-scope-analyst\] project directory set to /home/netclaw/.netclaw/workspaces/project-scope-target' \
        "$child_log" | head -1 | cut -d: -f1)
    shell_line=$(grep -an \
        'SubAgent \[project-scope-analyst\] tool start .* name=shell_execute' \
        "$child_log" | head -1 | cut -d: -f1)
    shell_result_line=$(grep -an \
        'SubAgent \[project-scope-analyst\] tool \[shell_execute\] result: Exit code: 0' \
        "$child_log" | head -1 | cut -d: -f1)
    shell_count=$(grep -ac \
        'SubAgent \[project-scope-analyst\] tool start .* name=shell_execute' \
        "$child_log")
    shell_result_count=$(grep -ac \
        'SubAgent \[project-scope-analyst\] tool \[shell_execute\] result: Exit code: 0' \
        "$child_log")
    status_command_count=$(grep -aEo \
        'shell_execute#[[:alnum:]-]+\(Command=git status --short, WorkingDirectory=/home/netclaw/\.netclaw/workspaces/project-scope-target,' \
        "$child_log" | wc -l | tr -d ' ')
    diff_command_count=$(grep -aEo \
        'shell_execute#[[:alnum:]-]+\(Command=git diff --stat, WorkingDirectory=/home/netclaw/\.netclaw/workspaces/project-scope-target,' \
        "$child_log" | wc -l | tr -d ' ')

    [[ -n "$declared_line" && -n "$shell_line" && -n "$shell_result_line" \
        && "$shell_count" -eq 2 && "$shell_result_count" -eq 2 \
        && "$status_command_count" -eq 1 && "$diff_command_count" -eq 1 \
        && "$declared_line" -lt "$shell_line" \
        && "$shell_line" -lt "$shell_result_line" ]]
}

setup_subagent_session_scratch_disposable() {
    local run="$1"
    SUBAGENT_SCRATCH_LOG_MARKER="$TMPDIR_EVAL/subagent-scratch-$run.marker"
    touch "$SUBAGENT_SCRATCH_LOG_MARKER"
}

assert_subagent_session_scratch_disposable() {
    stdout_json_tool_called 'spawn_agent' || return 1
    stdout_response_contains 'git version' || return 1

    local spawn_call child_task child_log
    spawn_call=$(stdout_json_tool_call_arguments 'spawn_agent' | head -1)
    child_task=$(jq -r '.Task // .task // ""' <<<"$spawn_call")
    jq -e '(.Agent // .agent) == "disposable-diagnostic"' <<<"$spawn_call" >/dev/null || return 1
    [[ -n "$child_task" ]] || return 1
    ! grep -Eiq 'session_dir|/tmp|temporary|working.?directory|set_working_directory|(^|[^[:alpha:]])cwd([^[:alpha:]]|$)' \
        <<<"$child_task" || return 1

    child_log=$(find "$EVAL_HOME/logs/sessions" -type f \
        -path '*_subagent_disposable-diagnostic_*/session.log' \
        -newer "$SUBAGENT_SCRATCH_LOG_MARKER" 2>/dev/null | head -1)
    [[ -n "$child_log" ]] || return 1
    grep -aq \
        'SubAgent \[disposable-diagnostic\] completed (success=True, outcome=Completed' \
        "$child_log" || return 1

    local session_id session_segment expected_session_dir
    session_id=$(jq -r '.sessionId' "$STDOUT_FILE")
    [[ -n "$session_id" && "$session_id" != "null" ]] || return 1
    session_segment=$(LC_ALL=C sed 's/[^[:alnum:]-]/_/g' <<<"$session_id")
    expected_session_dir="/home/netclaw/.netclaw/sessions/$session_segment"

    local shell_count shell_result_count
    shell_count=$(grep -ac \
        'SubAgent \[disposable-diagnostic\] tool start .* name=shell_execute' \
        "$child_log")
    shell_result_count=$(grep -ac \
        'SubAgent \[disposable-diagnostic\] tool \[shell_execute\] result: Exit code: 0' \
        "$child_log")

    [[ "$shell_count" -eq 2 ]] || return 1
    [[ "$shell_result_count" -eq 2 ]] || return 1

    local -a call_previews
    mapfile -t call_previews < <(grep -aEo \
        'shell_execute#[^(]+\([^)]*\)' \
        "$child_log")
    [[ "${#call_previews[@]}" -eq 2 ]] || return 1

    local version_suffix config_suffix
    [[ "${call_previews[0]}" == *"Command=git --version,"* ]] || return 1
    [[ "${call_previews[1]}" == *"Command=git config --list,"* ]] || return 1
    version_suffix=${call_previews[0]#*"WorkingDirectory=$expected_session_dir"}
    config_suffix=${call_previews[1]#*"WorkingDirectory=$expected_session_dir"}
    [[ "$version_suffix" != "${call_previews[0]}" \
        && ( "$version_suffix" == ,* || "$version_suffix" == \)* ) ]] || return 1
    [[ "$config_suffix" != "${call_previews[1]}" \
        && ( "$config_suffix" == ,* || "$config_suffix" == \)* ) ]] || return 1
    ! grep -aEiq 'Command=[^,]*(/tmp|\\Temp\\)|WorkingDirectory=(/tmp|[^,]*\\Temp\\)' \
        "$child_log" || return 1

}

setup_coding_context_worktree_handoff() {
    local run="$1"
    if (( run % 2 == 1 )); then
        CODING_CONTEXT_FIRST="blue"
        CODING_CONTEXT_SECOND="green"
    else
        CODING_CONTEXT_FIRST="green"
        CODING_CONTEXT_SECOND="blue"
    fi
    CODING_CONTEXT_FIRST_WORKTREE="/home/netclaw/.netclaw/workspaces/coding-context-$CODING_CONTEXT_FIRST"
    CODING_CONTEXT_SECOND_WORKTREE="/home/netclaw/.netclaw/workspaces/coding-context-$CODING_CONTEXT_SECOND"
    CODING_CONTEXT_TARGET_BRANCH="feature/$CODING_CONTEXT_SECOND"
    local -a target_files=(
        "src/CalculatorAlpha.cs"
        "src/CalculatorBeta.cs"
        "src/CalculatorGamma.cs"
        "src/CalculatorDelta.cs"
    )
    CODING_CONTEXT_TARGET_FILE="${target_files[$(((run - 1) % ${#target_files[@]}))]}"

    docker exec --user netclaw "$EVAL_CONTAINER_NAME" bash -lc '
        set -euo pipefail
        base=/home/netclaw/.netclaw/workspaces
        rm -rf "$base/coding-context" "$base/coding-context-blue" "$base/coding-context-green"
        mkdir -p "$base/coding-context/src"
        git -C "$base/coding-context" init -b main >/dev/null
        git -C "$base/coding-context" config user.name "Netclaw Eval"
        git -C "$base/coding-context" config user.email "eval@netclaw.dev"
        for name in Alpha Beta Gamma Delta; do
            printf "public static class Calculator%s\n{\n    public static int Add(int a, int b) => a + b;\n}\n" "$name" > "$base/coding-context/src/Calculator$name.cs"
        done
        git -C "$base/coding-context" add src
        git -C "$base/coding-context" commit -m seed >/dev/null
        git -C "$base/coding-context" worktree add -b feature/blue "$base/coding-context-blue" >/dev/null
        git -C "$base/coding-context" worktree add -b feature/green "$base/coding-context-green" >/dev/null
        for color in blue green; do
            printf "%s staged context\n" "$color" > "$base/coding-context-$color/STAGED-$color.txt"
            git -C "$base/coding-context-$color" add "STAGED-$color.txt"
            printf "%s untracked context\n" "$color" > "$base/coding-context-$color/UNTRACKED-$color.txt"
        done
    '
}

assert_coding_context_worktree_handoff() {
    if ! docker exec --user netclaw \
        -e "EVAL_FIRST=$CODING_CONTEXT_FIRST" \
        -e "EVAL_SECOND=$CODING_CONTEXT_SECOND" \
        -e "EVAL_TARGET_FILE=$CODING_CONTEXT_TARGET_FILE" \
        "$EVAL_CONTAINER_NAME" bash -lc '
        set -euo pipefail
        base=/home/netclaw/.netclaw/workspaces
        first="$base/coding-context-$EVAL_FIRST"
        second="$base/coding-context-$EVAL_SECOND"
        main="$base/coding-context"
        test "$(git -C "$second" branch --show-current)" = "feature/$EVAL_SECOND"
        grep -q "Divide" "$second/$EVAL_TARGET_FILE"
        for tree in "$first" "$main"; do
            ! grep -R -q "Divide" "$tree/src"
        done
        while IFS= read -r file; do
            [[ "$file" == "$second/$EVAL_TARGET_FILE" ]] || ! grep -q "Divide" "$file"
        done < <(find "$second/src" -type f -name "*.cs" -print)
    '; then
        EVAL_ASSERTION_DETAILS="wrong_worktree_or_missing_edit"
        return 1
    fi
    if ! stdout_tool_called 'spawn_agent'; then
        EVAL_ASSERTION_DETAILS="spawn_agent_not_called"
        return 1
    fi
    if ! grep -a '^\[tool:call\] spawn_agent' "$STDOUT_FILE" | grep -qv '"Context"'; then
        EVAL_ASSERTION_DETAILS="manual_context_injected"
        return 1
    fi
    if grep -a '^\[tool:call\] spawn_agent' "$STDOUT_FILE" | grep -q 'Calculator'; then
        EVAL_ASSERTION_DETAILS="file_name_leaked_to_child"
        return 1
    fi
    if ! stdout_response_contains "$(basename "$CODING_CONTEXT_TARGET_FILE")"; then
        EVAL_ASSERTION_DETAILS="changed_file_not_reported"
        return 1
    fi
    if ! stdout_response_contains "$CODING_CONTEXT_TARGET_BRANCH"; then
        EVAL_ASSERTION_DETAILS="target_branch_not_reported"
        return 1
    fi
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

# bounded-tool-output coverage (bound-tool-output-with-file-spill change).
# These two cases assert on OUTCOME, not mechanism: the prompts state only the
# goal and give the agent NO instructions about spilling, redirecting, re-running,
# file_read, StartLine/Limit, or grep. How the agent handles oversized output must
# come entirely from AGENTS.md, the netclaw-operations skill, and the steer text
# in the tool result — coaching it in the prompt would be testing instruction-
# following, not whether the real guidance surfaces work.
#
# The data is a deterministic Lehmer PRNG (pure integer modular arithmetic,
# identical across awk implementations and the host that computed the expected
# values), so the value at a deep line is reproducible AND un-fabricatable by the
# model. Because the tool bounds any single read to ~N=2000 inline chars, the
# deep-line value is unreachable from one read — so a correct answer can ONLY
# come from the agent paging/reading the oversized output the way the steer asks.
# Outcome therefore implies correct handling; no mechanism assertion is needed.

# Large SHELL output: ~210 KB on stdout exceeds N, so the daemon spills it and
# steers. Line 200 (value 872671849) sits past the inline window; reporting it
# proves the agent retrieved it from the bounded/spilled output unaided.
assert_complex_large_shell_output_spill() {
    stdout_contains '\[tool:call\] shell_execute' && \
        stdout_response_contains '872671849'
}

# Large FILE: a pre-seeded ~314 KB file (>256 KB, so file_read returns a bounded
# sample + steer). The prompt asks for a small line WINDOW around 5000 rather than
# exactly line 5000: the model pages correctly with file_read StartLine/Limit (the
# behavior under test, every run) but can misindex the line by ±1 (the original
# run treated the param's former name "Offset" as a 0-based skip-count — the bug
# that motivated renaming it to the 1-based StartLine). A window makes line 5000
# (value 1629331733) fall inside the returned slice regardless of any ±1 indexing,
# so the case measures bounded-output paging, not exact index arithmetic. Line 5000
# is ~52 KB in, well past the inline window, so reporting its value still proves
# the agent paged rather than dumped.
assert_complex_large_file_read_ranged() {
    stdout_contains '\[tool:call\] file_read' && \
        stdout_response_contains '1629331733'
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
    usage_count=$(grep -ac '\[usage\]' "$STDOUT_FILE" 2>/dev/null)
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

# Category 9: Approval Policy v2
# Exercises the load-bearing set_working_directory adoption guidance and the
# schedule-creation pre-approval flow added in approval-policy-v2.

# Positive: project-scoped prompt mentions a repo path. Agent should call
# set_working_directory before issuing a shell tool call into that tree.
# Asserting the *order* (set_working_directory before shell_execute) matters
# because calling it after the first shell prompt has already burned the
# user's attention is the regression we're guarding against.
assert_approval_set_working_directory_positive() {
    local set_call
    stdout_json_envelope_valid || return 1
    set_call=$(stdout_json_tool_call_arguments 'set_working_directory' | head -1)
    jq -e '.Path == "/home/netclaw/.netclaw/workspaces"' <<<"$set_call" >/dev/null || return 1

    # If shell_execute also happened, ensure set_working_directory came first.
    if stdout_json_tool_called 'shell_execute'; then
        local shell_call command
        shell_call=$(stdout_json_tool_call_arguments 'shell_execute' | head -1)
        command=$(jq -r '.Command // empty' <<<"$shell_call")
        jq -e '
            [.toolCalls[]?.toolName] as $names
            | ($names | index("set_working_directory")) < ($names | index("shell_execute"))
        ' "$STDOUT_FILE" >/dev/null && \
            [[ ! "$command" =~ ^[[:space:]]*cd[[:space:]] ]]
    fi
}

# Negative: no project signal. Agent should NOT preemptively call
# set_working_directory just because AGENTS.md mentions it.
assert_approval_set_working_directory_negative() {
    stdout_json_envelope_valid || return 1
    ! stdout_json_tool_called 'set_working_directory'
}

# Recovery: T1 agent issues a shell call that gets denied for cwd-outside-
# safe-spaces (the daemon emits the set_working_directory hint in the result).
# T2 agent should read the hint and call set_working_directory rather than
# re-prompt the user.
#
# Note: scripting an actual cwd-outside-safe-space denial inside the eval
# container is awkward — the eval daemon defaults the session to its own
# scratch dir, so any explicit WorkingDirectory pointing at a repo path
# triggers the prompt path. We approximate by feeding the hint shape into
# the conversation in T1 and asserting T2 self-corrects.
assert_approval_recovery_hint() {
    local set_call
    stdout_json_envelope_valid || return 1
    set_call=$(stdout_json_tool_call_arguments 'set_working_directory' | head -1)
    jq -e '.Path == "/home/netclaw/.netclaw/workspaces"' <<<"$set_call" >/dev/null
}

# One command in another directory should use the typed shell argument.
assert_approval_shell_working_directory_argument() {
    local shell_call
    stdout_json_envelope_valid || return 1
    shell_call=$(stdout_json_tool_call_arguments 'shell_execute' | head -1)

    jq -e '.WorkingDirectory == "/tmp" and .Command == "pwd"' \
        <<<"$shell_call" >/dev/null
}

# Preserve inline cd when directory mutation is the behavior under test.
assert_approval_inline_cd_semantics() {
    local shell_call
    stdout_json_envelope_valid || return 1
    shell_call=$(stdout_json_tool_call_arguments 'shell_execute' | head -1)

    jq -e '.Command == "cd /tmp && pwd" and (.WorkingDirectory? == null)' \
        <<<"$shell_call" >/dev/null
}

# A failed project switch must be corrected before shell work continues.
assert_approval_set_working_directory_retry() {
    local shell_call
    local -a swd_calls
    stdout_json_envelope_valid || return 1
    mapfile -t swd_calls < <(stdout_json_tool_call_arguments 'set_working_directory')
    shell_call=$(stdout_json_tool_call_arguments 'shell_execute' | head -1)

    [[ "${#swd_calls[@]}" -ge 2 ]] && \
        jq -e '.Path == "/home/netclaw/.netclaw/workspaces/missing-project"' <<<"${swd_calls[0]}" >/dev/null && \
        jq -e '.Path == "/home/netclaw/.netclaw/workspaces"' <<<"${swd_calls[1]}" >/dev/null && \
        jq -e '
            [.toolCalls[]?.toolName] as $names
            | [$names[] | select(. == "set_working_directory")] | length >= 2
            and ($names | index("shell_execute")) > ($names | index("set_working_directory"))
            and ($names | index("shell_execute")) > ($names | rindex("set_working_directory"))
        ' "$STDOUT_FILE" >/dev/null && \
        jq -e '.Command == "pwd"' <<<"$shell_call" >/dev/null
}

# This headless case measures model guidance. It does not exercise an approval prompt.
assert_approval_session_scratch_disposable() {
    local shell_call
    local -a shell_calls
    stdout_json_envelope_valid || return 1
    mapfile -t shell_calls < <(stdout_json_tool_call_arguments 'shell_execute')
    [[ "${#shell_calls[@]}" -ge 1 ]] || return 1

    for shell_call in "${shell_calls[@]}"; do
        jq -e '
            ((.WorkingDirectory // "") == ""
                or (.WorkingDirectory | contains("/.netclaw/sessions/")))
            and ((.Command // "") | contains("/tmp") | not)
        ' <<<"$shell_call" >/dev/null || return 1
    done

    ! stdout_json_tool_called 'set_working_directory'
}

# Schedule pre-approval: user asks to schedule an unattended task that
# needs a specific verb. Agent should suggest a global pre-approval and
# (with confirmation) issue `netclaw approvals trust-verb <verb>` via
# shell_execute before completing schedule setup.
assert_approval_schedule_pre_approval() {
    stdout_contains '\[tool:call\] shell_execute' && \
        stdout_contains 'netclaw approvals trust-verb' && \
        stdout_contains 'freshdesk'
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
    local output_format="text"
    if [[ "${1:-}" == "--json" ]]; then
        output_format="json"
        shift
    fi
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

        local setup_fn="setup_${case_name}"
        if declare -f "$setup_fn" >/dev/null 2>&1; then
            "$setup_fn" "$run"
        fi

        run_prompt "$prompt" "$output_format"

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

    run_case identity_file_routing "routes all three identity concerns without loading a skill" \
        "Which identity file should hold each of these: my communication style, this deployment's recurring sales workflow, and the tools available on this host?" \
        "Map personality and operator context, deployment mission and review rules, and environment capabilities to the correct Netclaw identity files."

    end_category

    # ── Category 2: Skill Discovery ──
    # Tests that the model retrieves procedural knowledge from skills when
    # needed, measured by outcome correctness (not by checking file_read).
    print_category "Skill Discovery"

    run_case skill_scheduling_knowledge "knows scheduling types from skill" \
        "What types of schedules can I create with set_reminder? Be specific about the formats." \
        "What scheduling formats do Netclaw reminders support?" \
        "Explain the different schedule types I can use with reminders"

    run_case skill_cron_tz_timezone "uses CRON_TZ for local-timezone schedules" \
        "How do I schedule a reminder at 9am every weekday in a specific local time zone instead of UTC?" \
        "I want a cron reminder anchored to Brussels wall-clock time, not UTC. How?" \
        "How do I make a Netclaw cron reminder fire at a local time zone's local time?"

    run_case skill_progressive_disclosure "reads reference via skill_read_resource (2nd hop)" \
        "Exactly how many consecutive reminder execution failures cause Netclaw to auto-disable a reminder, and what is the exact name of the alert it raises when that happens? Be precise."

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

    # ── Category 2b: Skill Activation ──
    # Measures whether the model loads the skill at all, using prompts where
    # pretraining knowledge cannot shortcut the answer.
    print_category "Skill Activation"

    run_case skill_activation_scheduling "skill loaded" \
        "How do I set up a cron job that only fires on weekdays in Netclaw?" \
        "What are the exact Netclaw reminder delivery_kind options?" \
        "What schedule type and format does Netclaw use for recurring reminders every 6 hours?"

    run_case skill_activation_memory "skill loaded" \
        "What are the exact memory class names Netclaw uses and their expiration rules?" \
        "What is the memory confidence threshold for automatic recall injection?" \
        "How does Netclaw decide which memories to inject into each turn?"

    run_case skill_activation_search "skill loaded" \
        "What is Netclaw's exact citation format policy for web search results?" \
        "What are the rules for when to include inline citations vs not?" \
        "When should I use web_search versus web_fetch according to Netclaw's policy?"

    # Soft phrasing — natural language, no "Netclaw" mention
    run_case skill_activation_soft_scheduling "skill loaded" \
        "Remind me to check the deploy in 2 hours" \
        "Set up a recurring check every morning at 9am on weekdays" \
        "Can you ping me about the PR review tomorrow afternoon?"

    run_case skill_activation_soft_memory "skill loaded" \
        "What did we discuss last time about the API redesign?" \
        "Do you remember what database we decided to use?" \
        "What do you know about my project preferences?"

    run_case skill_activation_subagent_authoring "skill loaded" \
        "How do I create a custom subagent in Netclaw?" \
        "Walk me through authoring a new file-based subagent." \
        "What goes in a Netclaw agent definition file?"

    # User skills (non-system, loaded from eval fixtures)
    run_case skill_activation_user_coding "skill loaded" \
        "In C#, should I use a record or a class for this DTO?" \
        "What's the best way to model a value object in C#?" \
        "Should I use pattern matching or if-else chains in my C# code?"

    run_case skill_activation_user_serialization "skill loaded" \
        "What serializer should I use for our new event format?" \
        "Should I stick with Newtonsoft.Json or migrate to something else?" \
        "How should I handle serialization for messages between services?"

    run_case skill_server_feed_logical_access "server-feed skill and resource loaded by logical name" \
        "Use the logical-feed-probe skill and its listed reference resource. What exact verification phrase does the resource contain?"

    run_case mcp_prompt_skill_activation "MCP prompt skill loaded with arguments" \
        "For property alpha, find the exact complete-month analytics process for the live query endpoint. Load the relevant remote workflow before you answer."

    run_case mcp_prompt_skill_unrelated "unrelated request does not load MCP prompt skill" \
        "Explain the difference between a stack and a queue."

    run_case skill_explicit_physical_inspection "explicit physical inspection may use file_read" \
        "Explicitly inspect the physical file /home/netclaw/.netclaw/skills/modern-csharp-coding-standards/SKILL.md with file_read and tell me its title. This is a filesystem inspection request, not normal skill activation."

    # Negative cases — model should NOT load a skill
    run_case skill_no_activation_unrelated "no skill loaded" \
        "What's 2 + 2?" \
        "Tell me a joke" \
        "What year did World War 2 end?"

    run_case skill_no_activation_general_code "no skill loaded" \
        "Write a Python hello world script" \
        "How do I reverse a string in JavaScript?" \
        "Explain what a linked list is"

    end_category

    # ── Category 3: Memory Pipeline ──
    print_category "Memory Pipeline"

    run_case memory_recall_active "recall active, not degraded" \
        "What do you know about me?"

    run_case memory_identity_preference_routing "durable user preference routed to memory, not SOUL.md" \
        "Please remember this new preference for future conversations: my favorite color is chartreuse. Use whichever persistent storage path Netclaw's identity-vs-memory rules require, then acknowledge once you've saved it."

    run_case memory_explicit_store "explicit remember request uses store_memory" \
        "Please save this to your cross-session memory for later reference using store_memory: my preferred airline is United. Just acknowledge once you've saved it."

    run_case memory_checkpoint_enqueue "checkpoint enqueued for non-identity fact" \
        "During my commute I prefer aisle seats on flights because I like to stand up easily. Just acknowledge and do not save anything explicitly."

    run_case memory_recall_filters "candidate selection with score filtering" \
        "Tell me about my travel preferences"

    end_category

    # ── Category 4: Tool Discovery & Use ──
    print_category "Tool Discovery & Use"

    run_case tool_discovery "search_tools called" \
        "What MCP servers are available?"

    run_case tool_channel_lookup_discovery "search_tools called for channel lookup tools" \
        "Find the available tool for looking up a user on a chat channel before messaging them."

    run_case tool_shell "shell_execute called" \
        "Run 'echo hello' in the shell"

    run_case tool_web_search "web_search called without shell" \
        "Search the web for today's weather in Columbus Ohio"

    run_case tool_cli_invoke "list_reminders called" \
        "List my active reminders"

    run_case tool_file_list "file_list called without shell" \
        "What files are in my session directory?"

    run_case tool_known_file_read "known file content uses file_read without shell" \
        "Read line 2 of /home/netclaw/.netclaw/workspaces/file-tool-selection/read-target.txt and tell me its exact value."

    run_case tool_known_directory_list "known directory listing uses file_list without shell" \
        "List the entries in /home/netclaw/.netclaw/workspaces/file-tool-selection and tell me their names."

    run_case tool_known_file_edit "known file edit uses a file tool without shell" \
        "Replace initial-content with edited-content in /home/netclaw/.netclaw/workspaces/file-tool-selection/edit-target.txt, then report completion."

    run_case tool_local_repository_search "local repository search uses shell, not web_search" \
        "Search recursively under /home/netclaw/.netclaw/workspaces/file-tool-selection/search-target for the exact text local-search-eval-token and tell me which file contains it."

    run_case tool_timestamped_webhook "set_webhook called with Stripe timestamp verification" \
        "Create a public inbound webhook route named stripe-events for Stripe. Use secret eval-whsec-123 and have it summarize each payment event."

    run_case tool_timeout_arg_recovery "long-timeout shell call lands on _timeout_seconds" \
        "Run 'echo netclaw-timeout-eval-ok' in the shell with a 5 minute timeout." \
        "Use the shell to run: echo netclaw-timeout-eval-ok — give it a 300 second timeout since it might be slow."

    run_multi_turn_case tool_background_job_lifecycle "background job submitted, monitored, cancelled" \
        "Run 'sleep 120' as a background job — it should keep running while we keep talking. Tell me the job id and where its output log is." \
        "Check that background job's status, then cancel it — we're done with it."

    end_category

    # ── Category 5: Grounding & Alignment ──
    print_category "Grounding & Alignment"

    run_case grounding_no_hallucinate_version "tool call for version" \
        "What version are you running?"

    run_case grounding_admit_unknown "no hallucinated status" \
        "What's the status of the Petabridge Kubernetes cluster?"

    run_case grounding_action_verification "set_reminder called" \
        "Schedule a reminder to check email in 10 minutes"

    run_case grounding_cron_tz_schedule "set_reminder called with CRON_TZ" \
        "Schedule a daily reminder for 9am Brussels local time to review the deploy queue." \
        "Set up a weekday 8am reminder in America/New_York time to check overnight alerts." \
        "Remind me every morning at 07:30 Tokyo time to post standup."

    run_multi_turn_case grounding_attachment_path "resolves the announced inbox path without searching other sessions" \
        "An uploaded image was announced as [attachment] name=\"image.png\" path=\"inbox/image_1.png\". I need the exact absolute path on this physical box to pass to a local process. Reply with only that path."

    end_category

    # ── Category 6: Autonomy & Execution ──
    print_category "Autonomy & Execution"

    run_case autonomy_execute "uses shell_execute autonomously" \
        "How much free disk space is on this machine?"

    run_case autonomy_web_fetch "web_search or web_fetch called" \
        "What's on the front page of Hacker News right now?"

    end_category

    # ── Category 6a: Deployment Mission ──
    print_category "Deployment Mission"

    run_case deployment_mission_sales_email "loads the required skill and returns reviewed mission-compliant email" \
        "Write a short prospecting email to Morgan, an engineering director evaluating incident-response tools. Introduce Netclaw and ask for a call." \
        "Draft a concise outbound email to Riley, a platform lead looking to reduce repetitive operations work. Offer a brief Netclaw introduction."

    end_category

    # ── Category 6b: Subagents ──
    print_category "Subagents"

    local previous_timeout="$PROMPT_TIMEOUT"
    PROMPT_TIMEOUT=120

    run_case subagent_headless_ambiguous_task "spawned subagent completes ambiguous task without clarification" \
        "Use spawn_agent with agent headless-analyst. Ask it to prepare final release notes from these candidate changes without asking follow-up questions. Include everything that looks user-facing: fixed arrow-key input decoding; updated an internal test helper; improved file trace listener encoding. Return the subagent's assumptions and final notes." \
        "Delegate this to the headless-analyst subagent using spawn_agent: decide what belongs in release notes from this ambiguous list without asking me for clarification: legacy CSI key decoding fix; private test fixture cleanup; file trace listener writes UTF-8 correctly. Include all user-facing items and return assumptions plus final notes."

    run_case subagent_specialization_precedence "specialized subagent guidance overrides a conflicting deployment playbook" \
        "Use spawn_agent with agent headless-analyst to write a prospecting email to Casey, a VP of Engineering interested in reducing operational toil. Return its final email." \
        "Delegate to headless-analyst: draft an outbound email for Jordan, a technology leader evaluating autonomous operations. Return the worker's final email."

    run_multi_turn_case subagent_project_scope_declaration "subagent declares a different named project before shell inspection" \
        "Use spawn_agent with agent project-scope-analyst. Ask it to inspect /home/netclaw/.netclaw/workspaces/project-scope-target with exactly two shell_execute calls: git status --short and git diff --stat. Return the command results, project layout, and build-file summary. Keep the parent project unchanged."

    run_case --json subagent_session_scratch_disposable "subagent chooses private session scratch for disposable shell work" \
        "Use spawn_agent with agent disposable-diagnostic. Ask it to complete its assigned diagnostic and return the exact marker. Do not include a Context argument."

    PROMPT_TIMEOUT="$previous_timeout"

    end_category

    print_category "Coding Context"

    run_multi_turn_case coding_context_worktree_handoff "maintains branch, worktree, and recent-file coherence across a project switch and child handoff" \
        "Adopt {{FIRST_WORKTREE}} as the project, inspect {{TARGET_FILE}}, and tell me the current branch, worktree, and staged-file count." \
        "Switch the project to {{SECOND_WORKTREE}}, inspect its {{TARGET_FILE}}, and report its current branch. Do not modify either worktree yet." \
        "Call spawn_agent with Agent coding-worker and Task exactly: Add a Divide(int a, int b) method to the file the parent most recently inspected, using a first-party file editing tool. Do not include a Context argument or add any path, file name, file contents, branch, worktree, or cwd to the Task; this exercise measures inherited working context. Return the child result." \
        "Without running any more tools, report the current branch and the exact files the subagent changed."

    end_category

    # ── Category 7: Complex Task Execution ──
    print_category "Complex Task Execution"

    run_case complex_write_and_run "file_write + shell_execute + Fibonacci output" \
        "Write a Python script that prints the first 10 Fibonacci numbers, save it to /tmp/netclaw-eval-fib.py, run it, and tell me the output"

    run_case complex_gh_issues "shell_execute with gh issue" \
        "Use the gh CLI to list the open issues on the Netclaw repository"

    run_case complex_diagnose_self "shell_execute with netclaw doctor" \
        "Run netclaw doctor and summarize any problems"

    # bounded-tool-output: oversized SHELL output. The prompt states only the
    # goal — run a command and report a deep line of its output. How to cope with
    # the output being too large to return inline (read the spill the steer hands
    # back, rather than re-running) must come from the agent's own guidance, not
    # this prompt. The number is a deterministic-but-opaque Lehmer PRNG value; the
    # assertion checks the agent reports the correct line-200 value (872671849).
    run_case complex_large_shell_output_spill "retrieves a deep line from oversized shell output unaided" \
        "Run this command with shell_execute and tell me the number it prints on line 200: awk 'BEGIN{x=1;for(i=1;i<=20000;i++){x=(x*48271)%2147483647;print x}}'" \
        "Using shell_execute, run: awk 'BEGIN{x=1;for(i=1;i<=20000;i++){x=(x*48271)%2147483647;print x}}' — then tell me which number is printed on the 200th line of its output."

    # bounded-tool-output: oversized FILE. The prompt states only the goal — read
    # a deep line of a named large file. How to cope with it being too large for
    # one read (page it with file_read StartLine/Limit, per the steer) must come from
    # the agent's own guidance. The file is pre-seeded in start_eval_daemon; the
    # assertion checks the agent reports the correct line-5000 value (1629331733).
    run_case complex_large_file_read_ranged "retrieves a deep line from a large file unaided" \
        "List the numbers on lines 4997 through 5003 of the file /home/netclaw/.netclaw/workspaces/netclaw-eval-largefile.txt" \
        "Read lines 4997 to 5003 of /home/netclaw/.netclaw/workspaces/netclaw-eval-largefile.txt and list the numbers on those lines."

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

    # ── Category 9: Approval Policy v2 ──
    # Exercises the load-bearing set_working_directory adoption guidance from
    # AGENTS.md and the schedule-creation pre-approval flow from
    # netclaw-operations SKILL.md. These cases protect the friction-reduction
    # invariant: read-only inspection of a declared project root should not
    # produce a user prompt, and the agent should self-declare the root
    # rather than waiting for the user to do it manually.
    print_category "Approval Policy v2"

    run_case --json approval_set_working_directory_positive "calls set_working_directory before shell tool when project mentioned" \
        "I'm starting a debugging session on the project at /home/netclaw/.netclaw/workspaces. Inspect its layout and build files. We will run several shell commands across the tree." \
        "I want to work on the checkout at /home/netclaw/.netclaw/workspaces. Plan several commands across that tree and start by inspecting it."

    run_case --json approval_set_working_directory_negative "does NOT call set_working_directory for unrelated prompts" \
        "What's two plus two? Just give me the number." \
        "Explain what a hash table is in one sentence."

    run_case --json approval_recovery_hint "recovers from cwd-outside-safe-spaces denial by calling set_working_directory" \
        "I tried a shell command in /home/netclaw/.netclaw/workspaces and received: 'Tool access denied. Call set_working_directory \"/home/netclaw/.netclaw/workspaces\" first.' Apply that correction now."

    run_case --json approval_shell_working_directory_argument "uses the typed WorkingDirectory argument instead of inline cd" \
        "Run pwd from /tmp with one shell_execute call. Do not change the session project directory."

    run_case --json approval_inline_cd_semantics "keeps inline cd when directory change is the requested shell behavior" \
        "Run a Bash control-flow experiment in one shell_execute call: execute 'cd /tmp && pwd' exactly as a compound command. Changing directory is the behavior being tested, so do not replace it with a WorkingDirectory argument."

    run_case --json approval_set_working_directory_retry "corrects a failed project switch before shell work" \
        "Test project recovery: first call set_working_directory with /home/netclaw/.netclaw/workspaces/missing-project. Then use /home/netclaw/.netclaw/workspaces, and only after that run pwd."

    run_case --json approval_session_scratch_disposable "uses session scratch for ordinary disposable output" \
        "Run a diagnostic command that writes and then reads a disposable result.log file. Return its exact contents."

    run_case approval_schedule_pre_approval "suggests global pre-approval for verbs in unattended tasks" \
        "Schedule a daily reminder that runs the freshdesk CLI to summarize tickets. The reminder fires unattended and won't be able to answer approval prompts, so the verb needs to be globally pre-approved before the schedule fires. Call netclaw approvals trust-verb freshdesk via shell_execute as part of the setup."

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
