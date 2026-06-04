#!/bin/bash
# Process supervisor for netclawd inside Docker.
# Restarts the daemon on exit (e.g., config-update shutdown) while keeping
# the container alive so `docker exec` sessions survive.
# Forwards SIGTERM/SIGINT to the daemon for clean `docker stop`.
#
# Restarts use exponential backoff (2s -> 60s) when the daemon exits quickly.
# A crash loop — a misconfiguration that fails fast, or lock contention with a
# second netclawd started via `docker exec netclaw daemon start` — therefore
# backs off instead of spamming hundreds of restarts. The backoff resets once
# the daemon has run for a healthy stretch.
set -u

NETCLAW_USER=netclaw
NETCLAW_UID=1654
NETCLAW_GID=1654
NETCLAW_USER_HOME=/home/netclaw
NETCLAW_DATA_DIR="${NETCLAW_HOME:-$NETCLAW_USER_HOME/.netclaw}"

ensure_runtime_ownership() {
    local path="$1"

    if ! mkdir -p "$path"; then
        echo "[entrypoint] ERROR: failed to create $path before dropping privileges." >&2
        echo "[entrypoint] Check that the parent path exists and is writable by container root." >&2
        exit 1
    fi

    local owner
    owner="$(stat -c '%u:%g' "$path" 2>/dev/null || true)"
    if [[ "$owner" == "${NETCLAW_UID}:${NETCLAW_GID}" ]]; then
        return 0
    fi

    echo "[entrypoint] Taking ownership of $path for ${NETCLAW_USER} (${NETCLAW_UID}:${NETCLAW_GID})..."
    if chown -R "${NETCLAW_UID}:${NETCLAW_GID}" "$path"; then
        return 0
    fi

    if gosu "$NETCLAW_USER" test -w "$path"; then
        echo "[entrypoint] WARN: recursive chown failed for $path, but the top-level directory is writable by ${NETCLAW_USER}. Continuing." >&2
        return 0
    fi

    echo "[entrypoint] ERROR: failed to chown $path for ${NETCLAW_USER} (${NETCLAW_UID}:${NETCLAW_GID})." >&2
    echo "[entrypoint] If this is a read-only bind mount, make it writable or pre-create it with uid/gid ${NETCLAW_UID}:${NETCLAW_GID}." >&2
    exit 1
}

# Best-effort accessibility for /tools — a PATH directory the agent only ever
# READS and executes from, never writes. Unlike the data dir, /tools is commonly
# supplied as a read-only mount carrying a large, pre-provisioned, immutable
# toolset (e.g. a vendored .NET SDK). So this MUST NOT recursively chown it
# (pointlessly expensive over thousands of files, and a guaranteed failure on a
# read-only mount) and MUST NOT treat a read-only mount as fatal — doing so
# crash-loops the container for a perfectly valid configuration. We only confirm
# the runtime user can traverse and read it; if it happens to be writable we
# opportunistically fix the top-level owner so a fresh named volume is usable.
ensure_tools_accessible() {
    local path="$1"

    mkdir -p "$path" 2>/dev/null || true

    if gosu "$NETCLAW_USER" test -r "$path" && gosu "$NETCLAW_USER" test -x "$path"; then
        if [[ -w "$path" ]]; then
            local owner
            owner="$(stat -c '%u:%g' "$path" 2>/dev/null || true)"
            [[ "$owner" == "${NETCLAW_UID}:${NETCLAW_GID}" ]] || \
                chown "${NETCLAW_UID}:${NETCLAW_GID}" "$path" 2>/dev/null || true
        fi
        return 0
    fi

    echo "[entrypoint] WARN: $path is not readable/traversable by ${NETCLAW_USER} and is not writable (read-only mount?); continuing." >&2
    echo "[entrypoint] Tools mounted there must be world-readable/executable, or pre-owned by uid/gid ${NETCLAW_UID}:${NETCLAW_GID}." >&2
    return 0
}

if [[ "$(id -u)" == "0" ]]; then
    export HOME="$NETCLAW_USER_HOME"
    export NETCLAW_HOME="$NETCLAW_DATA_DIR"

    # The data dir must be writable by the runtime user — fatal if we cannot
    # deliver that. /tools only needs to be readable — never fatal (see above).
    ensure_runtime_ownership "$NETCLAW_DATA_DIR"
    ensure_tools_accessible /tools

    exec gosu "$NETCLAW_USER" "$0" "$@"
fi

PID=""
trap 'kill $PID 2>/dev/null; wait $PID 2>/dev/null; exit 0' SIGTERM SIGINT

MIN_BACKOFF=2
MAX_BACKOFF=60
HEALTHY_RUNTIME=30
backoff=$MIN_BACKOFF

while true; do
    started=$SECONDS
    /usr/local/bin/netclawd "$@" &
    PID=$!
    wait $PID
    EXIT_CODE=$?
    runtime=$(( SECONDS - started ))

    # A daemon that stayed up for a healthy stretch is not crash-looping —
    # reset the backoff so the next genuine restart is prompt.
    if [[ $runtime -ge $HEALTHY_RUNTIME ]]; then
        backoff=$MIN_BACKOFF
    fi

    echo "[entrypoint] netclawd exited (code=$EXIT_CODE) after ${runtime}s, restarting in ${backoff}s..."
    sleep "$backoff"

    # Rapid exit — escalate the backoff for the next attempt.
    if [[ $runtime -lt $HEALTHY_RUNTIME ]]; then
        backoff=$(( backoff * 2 ))
        [[ $backoff -gt $MAX_BACKOFF ]] && backoff=$MAX_BACKOFF
    fi
done
