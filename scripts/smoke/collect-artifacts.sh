#!/usr/bin/env bash
# Collect failure artifacts from the native smoke harness — per-scenario
# NETCLAW_HOME logs + config, the smoke LLM log and request record, tape GIFs/PNGs, and
# harness stdout into a destination directory.
#
# Usage:
#   scripts/smoke/collect-artifacts.sh <dest-dir> [run-root]
#
#   <dest-dir>   where artifacts are gathered (created if missing)
#   [run-root]   the run-scoped temp root produced by run-smoke.sh
#                (default: $RUN_ROOT, then $SMOKE_RUN_ROOT)

set -euo pipefail

DEST_DIR="${1:-${SMOKE_LOG_DIR:-./smoke-logs}}"
RUN_ROOT="${2:-${RUN_ROOT:-${SMOKE_RUN_ROOT:-}}}"

mkdir -p "$DEST_DIR"

echo "==> Collecting native smoke artifacts to: $DEST_DIR"

# Tape GIFs / PNGs (vhs writes these to /tmp).
for f in /tmp/tape-*.gif /tmp/tape-*.png; do
  [[ -e "$f" ]] || continue
  cp -v "$f" "$DEST_DIR/" 2>/dev/null || true
done

if [[ -n "$RUN_ROOT" && -d "$RUN_ROOT" ]]; then
  # Smoke LLM diagnostics contain bounded metadata only.
  for f in "$RUN_ROOT/smoke-llm.log" "$RUN_ROOT/smoke-llm-requests.jsonl"; do
    [[ -f "$f" ]] && cp -v "$f" "$DEST_DIR/" 2>/dev/null || true
  done

  # Harness stdout, if the run captured it.
  for f in "$RUN_ROOT"/*.log; do
    [[ -e "$f" ]] || continue
    cp -v "$f" "$DEST_DIR/" 2>/dev/null || true
  done

  # Per-scenario / per-tape NETCLAW_HOME directories live under
  # $RUN_ROOT/home/<name>. Copy each one's logs + config.
  if [[ -d "$RUN_ROOT/home" ]]; then
    for home in "$RUN_ROOT"/home/*; do
      [[ -d "$home" ]] || continue
      name="$(basename "$home")"
      out="$DEST_DIR/home-${name}"
      mkdir -p "$out"
      if [[ -d "$home/logs" ]]; then
        cp -r "$home/logs" "$out/logs" 2>/dev/null || true
      fi
      if [[ -f "$home/config/netclaw.json" ]]; then
        cp "$home/config/netclaw.json" "$out/netclaw.json" 2>/dev/null || true
      fi
      ls -laR "$home" > "$out/listing.txt" 2>&1 || true
    done
  fi
else
  echo "    (no run root supplied; skipped NETCLAW_HOME and smoke-server diagnostics)" >&2
fi

echo "==> Artifacts gathered: $(ls "$DEST_DIR" 2>/dev/null | tr '\n' ' ')"
