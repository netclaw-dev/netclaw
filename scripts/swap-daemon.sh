#!/usr/bin/env bash
# swap-daemon.sh — build, publish, swap the daemon binary for local testing
# Usage: ./scripts/swap-daemon.sh        (swap in worktree build)
#        ./scripts/swap-daemon.sh --restore  (restore original binary)
set -euo pipefail

DAEMON_DIR="$HOME/.netclaw/bin"
DAEMON_BIN="$DAEMON_DIR/netclawd"
BACKUP_BIN="$DAEMON_DIR/netclawd.original"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PUBLISH_DIR="/tmp/netclaw-swap-daemon"

if [[ "${1:-}" == "--restore" ]]; then
    if [[ ! -f "$BACKUP_BIN" ]]; then
        echo "No backup found at $BACKUP_BIN — nothing to restore"
        exit 1
    fi
    netclaw daemon stop 2>/dev/null || true
    cp "$BACKUP_BIN" "$DAEMON_BIN"
    rm "$BACKUP_BIN"
    netclaw daemon start
    echo "Restored original daemon"
    exit 0
fi

# Backup original if not already backed up
if [[ ! -f "$BACKUP_BIN" ]]; then
    cp "$DAEMON_BIN" "$BACKUP_BIN"
    echo "Backed up original daemon ($(du -h "$BACKUP_BIN" | cut -f1))"
fi

# Publish self-contained from the repo root
echo "Publishing self-contained daemon from $REPO_DIR..."
rm -rf "$PUBLISH_DIR"
dotnet publish "$REPO_DIR/src/Netclaw.Daemon/" \
    -c Debug \
    -r linux-x64 \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$PUBLISH_DIR" \
    --verbosity quiet

PUBLISHED="$PUBLISH_DIR/netclawd"
if [[ ! -f "$PUBLISHED" ]]; then
    echo "ERROR: Published binary not found at $PUBLISHED"
    exit 1
fi

PUB_SIZE=$(du -h "$PUBLISHED" | cut -f1)
ORIG_SIZE=$(du -h "$BACKUP_BIN" | cut -f1)
echo "Published: $PUB_SIZE (original: $ORIG_SIZE)"

if [[ $(stat -c%s "$PUBLISHED") -lt 1000000 ]]; then
    echo "ERROR: Published binary is too small ($(stat -c%s "$PUBLISHED") bytes) — likely just the host stub, not self-contained"
    exit 1
fi

# Swap
netclaw daemon stop 2>/dev/null || true
cp "$PUBLISHED" "$DAEMON_BIN"
netclaw daemon start

echo "Daemon swapped and started. Check logs with:"
echo "  tail -f ~/.netclaw/logs/daemon-$(date +%Y-%m-%d).log"
echo ""
echo "Restore with: $0 --restore"
