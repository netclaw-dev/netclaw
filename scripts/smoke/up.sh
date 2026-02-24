#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="${COMPOSE_FILE:-$ROOT_DIR/docker-compose.smoke.yml}"
PROJECT_NAME="${PROJECT_NAME:-netclaw-smoke}"

docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" up -d --build

cat <<EOF
Smoke sandbox is starting.

Next steps:
  1) Wait for model initialization to complete.
  2) Run scripts/smoke/check.sh

Optional model override:
  SMOKE_OLLAMA_MODEL=qwen2:0.5b scripts/smoke/up.sh
EOF
