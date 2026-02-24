#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="${COMPOSE_FILE:-$ROOT_DIR/docker-compose.smoke.yml}"
PROJECT_NAME="${PROJECT_NAME:-netclaw-smoke}"

args=(down --remove-orphans)
if [[ "${SMOKE_REMOVE_VOLUMES:-0}" == "1" ]]; then
  args+=(--volumes)
fi

docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" "${args[@]}"

if [[ "${SMOKE_REMOVE_VOLUMES:-0}" == "1" ]]; then
  echo "Smoke sandbox stopped and volumes removed."
else
  echo "Smoke sandbox stopped. Set SMOKE_REMOVE_VOLUMES=1 to remove volumes."
fi
