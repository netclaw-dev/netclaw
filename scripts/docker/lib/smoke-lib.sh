#!/usr/bin/env bash
# Shared helpers for the Docker image smoke tests so the minimal-config contract
# and the health-poll live in ONE place instead of being copy-pasted across
# .github/workflows/validate_docker_image.yml and test-daemon-lifecycle.sh (#1303).
#
# Source it: . "$(dirname "$0")/lib/smoke-lib.sh"  (script)
#            source "$GITHUB_WORKSPACE/scripts/docker/lib/smoke-lib.sh"  (workflow step)

# Minimal provider/model config as `docker run` -e args (one per line; safe to
# word-split — no values contain spaces). Ollama needs no API key and the endpoint
# is never called during startup/health, so an unreachable one is fine. The bind
# port is intentionally NOT set here: it defaults to 5199, and tests that exercise a
# config-file port change need the file to be authoritative (env overrides file).
netclaw_smoke_env_args() {
    printf '%s\n' \
        -e NETCLAW_Providers__validate__Type=ollama \
        -e NETCLAW_Providers__validate__Endpoint=http://127.0.0.1:11434 \
        -e NETCLAW_Models__Main__Provider=validate \
        -e NETCLAW_Models__Main__ModelId=qwen2:0.5b
}

# Poll /api/health/ready inside a container until healthy.
# Usage: netclaw_wait_healthy <container> <port> <timeout-seconds>
# Returns: 0 healthy, 1 timed out, 2 container exited.
netclaw_wait_healthy() {
    local container="$1" port="$2" timeout="$3" i
    for ((i = 0; i < timeout; i++)); do
        if docker exec "$container" curl -fsS "http://127.0.0.1:$port/api/health/ready" >/dev/null 2>&1; then
            return 0
        fi
        [[ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null || echo false)" == "true" ]] \
            || return 2
        sleep 1
    done
    return 1
}
