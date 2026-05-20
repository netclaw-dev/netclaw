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
