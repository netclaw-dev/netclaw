#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT_DIR="${OUT_DIR:-$ROOT_DIR/artifacts/evals/memory}"
FIXTURES="${FIXTURES:-$ROOT_DIR/scripts/evals/fixtures/memory-cases.json}"
RUNS="${RUNS:-1}"
DB_PATH="${DB_PATH:-$HOME/.netclaw/netclaw.db}"
LOG_PATH="${LOG_PATH:-$HOME/.netclaw/logs/daemon-$(date +%F).log}"
PROMPT_TIMEOUT_SECONDS="${PROMPT_TIMEOUT_SECONDS:-180}"

mkdir -p "$OUT_DIR"

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required" >&2
  exit 1
fi

echo "[eval] repo: $ROOT_DIR"
echo "[eval] fixtures: $FIXTURES"
echo "[eval] output dir: $OUT_DIR"
echo "[eval] db: $DB_PATH"
echo "[eval] log: $LOG_PATH"

# Ensure latest local binaries pick up observability changes.
dotnet build "$ROOT_DIR/src/Netclaw.Daemon/Netclaw.Daemon.csproj" >/dev/null
dotnet build "$ROOT_DIR/src/Netclaw.Cli/Netclaw.Cli.csproj" >/dev/null

python3 "$ROOT_DIR/scripts/evals/memory-score.py" \
  --repo-root "$ROOT_DIR" \
  --fixtures "$FIXTURES" \
  --results "$OUT_DIR/eval-results.json" \
  --summary "$OUT_DIR/eval-summary.md" \
  --db-path "$DB_PATH" \
  --log-path "$LOG_PATH" \
  --runs "$RUNS" \
  --prompt-timeout-seconds "$PROMPT_TIMEOUT_SECONDS"

echo "[eval] wrote: $OUT_DIR/eval-results.json"
echo "[eval] wrote: $OUT_DIR/eval-summary.md"
