#!/bin/bash
# Process supervisor for netclawd inside Docker.
# Restarts the daemon on exit (e.g., config-update shutdown) while keeping
# the container alive so `docker exec` sessions survive.
# Forwards SIGTERM/SIGINT to the daemon for clean `docker stop`.
set -u

trap 'kill $PID 2>/dev/null; wait $PID 2>/dev/null; exit 0' SIGTERM SIGINT

while true; do
    /usr/local/bin/netclawd "$@" &
    PID=$!
    wait $PID
    EXIT_CODE=$?
    echo "[entrypoint] netclawd exited (code=$EXIT_CODE), restarting in 2s..."
    sleep 2
done
