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

# Detect whether the daemon runs under the user's systemd. When it does,
# `netclaw daemon stop` triggers the SIGTERM but systemd's Restart policy
# immediately spawns a replacement, racing with the `cp` and producing
# "Text file busy" or a silent "already running" — depending on timing.
# In that case we drive systemd directly so the unit is fully stopped
# (and stays stopped) until we explicitly start it again.
is_systemd_managed() {
    command -v systemctl >/dev/null 2>&1 \
        && systemctl --user is-active netclaw.service >/dev/null 2>&1
}

stop_daemon() {
    if is_systemd_managed; then
        echo "Stopping netclaw.service via systemd..."
        systemctl --user stop netclaw.service
    else
        netclaw daemon stop 2>/dev/null || true
    fi
}

start_daemon() {
    if command -v systemctl >/dev/null 2>&1 \
        && systemctl --user is-enabled netclaw.service >/dev/null 2>&1; then
        echo "Starting netclaw.service via systemd..."
        systemctl --user start netclaw.service
    else
        # Detach stdio. The CLI's Process.Start inherits the parent's stdio
        # handles into the daemon child, so without this redirect the script
        # hangs forever waiting for the daemon to close the inherited pipes.
        netclaw daemon start </dev/null >/dev/null 2>&1
    fi
}

if [[ "${1:-}" == "--restore" ]]; then
    if [[ ! -f "$BACKUP_BIN" ]]; then
        echo "No backup found at $BACKUP_BIN — nothing to restore"
        exit 1
    fi
    stop_daemon
    cp "$BACKUP_BIN" "$DAEMON_BIN"
    rm "$BACKUP_BIN"
    start_daemon
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
    --verbosity minimal

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
stop_daemon
cp "$PUBLISHED" "$DAEMON_BIN"
start_daemon

echo "Daemon swapped and started. Check logs with:"
echo "  tail -f ~/.netclaw/logs/daemon-$(date +%Y-%m-%d).log"
echo ""
echo "Restore with: $0 --restore"
