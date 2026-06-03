#!/usr/bin/env bash
# Container daemon-lifecycle regression test for #1279.
#
# Verifies that the official image keeps a SINGLE supervised netclawd — that
# entrypoint.sh (PID 1) is the only thing that ever starts the daemon — and that
# config changes apply the way the rework intends:
#
#   Phase A — in-process config reload that actually takes effect:
#     Writing netclaw.json with a NEW bind port drives the daemon's
#     ConfigWatcherService to perform a coordinated in-process restart. The new
#     port must be serving and the old one gone (the Daemon-section change took
#     effect), while the process stays alive (SAME pid), keeps the lock, and
#     remains the entrypoint's child — no second daemon is spawned.
#
#   Phase B — `netclaw daemon start` under the supervisor:
#     The CLI must defer to the supervisor and refuse to spawn a detached
#     netclawd (the original #1279 bug), leaving exactly one daemon.
#
#   Phase C — a bad Daemon config fails loudly and recovers:
#     A semantically-invalid Daemon section (reverse-proxy bound to loopback) must
#     make the daemon abort startup (the supervisor observes the exit and
#     crash-loops) rather than silently keep serving stale config; fixing the
#     config on disk must let the supervisor's next restart recover.
#
#   Phase D — PID 1 reaps orphaned subprocesses (#1287):
#     An orphaned process (its parent exits, so it reparents to PID 1) must be reaped
#     once it dies rather than lingering as a <defunct> zombie — proving tini runs as
#     PID 1 and reaps, which entrypoint.sh alone (it only `wait`s its direct child)
#     would not do.
#
# Process tree (#1287): tini (PID 1) -> entrypoint.sh supervisor -> netclawd. netclawd
# is therefore a child of the supervisor, NOT a direct child of PID 1 — see
# daemon_supervision() for how the supervised-child invariant is checked.
#
# Usage:
#   scripts/docker/test-daemon-lifecycle.sh <image-ref>
#   scripts/docker/test-daemon-lifecycle.sh netclawd-pr:pr-1279
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/docker/lib/smoke-lib.sh
. "$SCRIPT_DIR/lib/smoke-lib.sh"

IMAGE="${1:?usage: test-daemon-lifecycle.sh <image-ref>}"
CONTAINER="netclaw-lifecycle-1279"
DEFAULT_PORT=5199   # DaemonConfig default; the daemon binds this on first boot
NEW_PORT=5200       # Phase A re-binds here via a config-file write
PIDFILE=/home/netclaw/.netclaw/netclaw.pid
CONFIG=/home/netclaw/.netclaw/config/netclaw.json

cleanup() { docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; }
trap cleanup EXIT

fail() {
    echo "ERROR: $*" >&2
    echo "---- container logs ----" >&2
    docker logs "$CONTAINER" >&2 2>&1 || true
    exit 1
}

# Count of supervised netclawd processes (0 on none, no stderr noise).
daemon_count() { docker exec "$CONTAINER" sh -c 'pgrep -x netclawd | wc -l' | tr -d '[:space:]'; }
# PID of the (first) netclawd, empty if none.
daemon_pid()   { docker exec "$CONTAINER" sh -c 'pgrep -x netclawd | head -n1' | tr -d '[:space:]'; }
# Supervision chain of the (first) netclawd. Proves it is the supervised child of the
# entrypoint.sh process, which is itself a child of PID 1 (tini, #1287) — NOT a detached
# daemon from a `docker exec` session (which reparents straight to PID 1). With tini
# inserted as PID 1, netclawd's direct parent is the entrypoint.sh supervisor (PID > 1),
# so a bare "PPID == 1" check no longer expresses the invariant. Echoes "ok" on success,
# "no-daemon" when none is running, or a diagnostic otherwise; always exits 0 so it never
# trips the caller's `set -e` before the descriptive `fail` + log dump.
daemon_supervision() {
    docker exec "$CONTAINER" sh -c '
        dpid=$(pgrep -x netclawd | head -n1)
        [ -n "$dpid" ] || { echo "no-daemon"; exit 0; }
        sup=$(ps -o ppid= -p "$dpid" 2>/dev/null | tr -d "[:space:]")
        supargs=$(ps -o args= -p "$sup" 2>/dev/null)
        supppid=$(ps -o ppid= -p "$sup" 2>/dev/null | tr -d "[:space:]")
        case "$supargs" in
            *entrypoint.sh*) ;;
            *) echo "parent-not-supervisor(pid=$sup args=[$supargs])"; exit 0 ;;
        esac
        [ "$supppid" = "1" ] || { echo "supervisor-not-pid1-child(ppid=$supppid)"; exit 0; }
        echo "ok"
    '
}
# PID-file generation (line 2 = ISO start time); the daemon rewrites it on each restart.
daemon_generation() { docker exec "$CONTAINER" sh -c "sed -n 2p $PIDFILE 2>/dev/null" | tr -d '[:space:]'; }
# Number of times the supervisor has observed the daemon exit (proves a real exit
# vs an in-process restart, which keeps the process alive).
entrypoint_exit_count() { docker logs "$CONTAINER" 2>&1 | grep -c '\[entrypoint\] netclawd exited' || true; }

# Wrap the shared poll so a container-exit (rc 2) becomes a descriptive failure.
wait_healthy() {  # $1 = port, $2 = timeout-seconds
    local rc=0
    netclaw_wait_healthy "$CONTAINER" "$1" "$2" || rc=$?
    if [[ "$rc" -eq 2 ]]; then
        fail "container exited while waiting for health on :$1"
    fi
    return "$rc"
}
port_serving() { docker exec "$CONTAINER" curl -fsS "http://127.0.0.1:$1/api/health/ready" >/dev/null 2>&1; }

# Write netclaw.json into the container (stdin heredoc; -i keeps stdin open in CI).
write_config() { docker exec -i "$CONTAINER" sh -c "cat > $CONFIG"; }

echo "==> Starting supervised daemon from image: $IMAGE"
cleanup
# Minimal provider/model config from the shared lib (deliberately no Daemon.Port — see
# netclaw_smoke_env_args; Phase A needs the file's port to win over env).
# shellcheck disable=SC2046  # intentional word-splitting of the -e args
docker run -d --name "$CONTAINER" $(netclaw_smoke_env_args) "$IMAGE" >/dev/null

wait_healthy "$DEFAULT_PORT" 60 || fail "supervised daemon never became healthy on :$DEFAULT_PORT"

count="$(daemon_count)"; pid="$(daemon_pid)"; sup="$(daemon_supervision)"
echo "    initial: count=$count pid=$pid supervision=$sup (port :$DEFAULT_PORT)"
[[ "$count" == "1" ]] || fail "expected exactly 1 netclawd at startup, found $count"
[[ "$sup" == "ok" ]]  || fail "netclawd is not properly supervised at startup: $sup"

# ── Phase A: a config write reloads in-process AND the change takes effect ──
echo "==> Phase A: config write re-binds the daemon in-process (:$DEFAULT_PORT -> :$NEW_PORT)"
gen_before="$(daemon_generation)"
[[ -n "$gen_before" ]] || fail "daemon PID file has no start-time generation (line 2) at $PIDFILE"

# A Daemon-section change the watcher used to SKIP (#1279). Changing the bind port is
# the externally-observable proof that the reload actually re-read and re-bound config.
write_config <<JSON
{ "Daemon": { "Host": "127.0.0.1", "Port": $NEW_PORT, "ExposureMode": "local" } }
JSON

reloaded=false
for _ in $(seq 1 30); do
    gen_now="$(daemon_generation)"
    if [[ -n "$gen_now" && "$gen_now" != "$gen_before" ]]; then reloaded=true; break; fi
    [[ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null || echo false)" == "true" ]] \
        || fail "container exited during config-reload restart"
    sleep 1
done
[[ "$reloaded" == "true" ]] || fail "config write did not trigger an in-process restart (generation unchanged)"

# The change took effect: the new port serves and the old one is gone.
wait_healthy "$NEW_PORT" 60   || fail "daemon not healthy on the new port :$NEW_PORT after reload (re-bind did not take effect)"
! port_serving "$DEFAULT_PORT" || fail "old port :$DEFAULT_PORT still serving — the bind change did not apply"

# ...and it was an in-process restart, not a respawn / duplicate.
count_a="$(daemon_count)"; pid_a="$(daemon_pid)"; sup_a="$(daemon_supervision)"
echo "    after reload: count=$count_a pid=$pid_a supervision=$sup_a (port :$NEW_PORT)"
[[ "$count_a" == "1" ]]  || fail "config reload produced $count_a daemons (expected 1 — duplicate!)"
[[ "$pid_a" == "$pid" ]] || fail "PID changed ($pid -> $pid_a): the process exited instead of restarting in-process"
[[ "$sup_a" == "ok" ]]   || fail "netclawd not properly supervised after reload: $sup_a"
[[ "$(entrypoint_exit_count)" == "0" ]] \
    || fail "entrypoint observed a daemon exit during an in-process reload (supervisor would respawn)"

# ── Phase B: `netclaw daemon start` must defer to the supervisor ────────────
echo "==> Phase B: 'netclaw daemon start' under supervisor"
# Capture output without letting a non-zero exit (e.g. a transient not-running blip,
# which returns exit 1) trip `set -e` before the assertion below runs.
out="$(docker exec "$CONTAINER" netclaw daemon start 2>&1)" || true
echo "    daemon start => $out"
echo "$out" | grep -qi "container supervisor" \
    || fail "'netclaw daemon start' did not defer to the supervisor: $out"

# Give any erroneously-spawned daemon time to race for the lock.
sleep 3

count_b="$(daemon_count)"; sup_b="$(daemon_supervision)"
echo "    after daemon start: count=$count_b supervision=$sup_b"
[[ "$count_b" == "1" ]] || fail "'netclaw daemon start' produced $count_b daemons (split-brain!)"
[[ "$sup_b" == "ok" ]]  || fail "netclawd not properly supervised after 'daemon start': $sup_b"
if docker logs "$CONTAINER" 2>&1 | grep -q "Another netclawd instance is already running (lock file held)"; then
    fail "lock-file contention detected in container logs (split-brain)"
fi

# ── Phase C: a bad Daemon config fails loudly, then recovers when fixed ──────
echo "==> Phase C: bad Daemon config fails loudly (and recovers)"
exits_before="$(entrypoint_exit_count)"
# reverse-proxy bound to loopback is rejected by ExposureModeValidationService at
# startup — the rebuilt host aborts and exits rather than silently serving stale config.
write_config <<'JSON'
{ "Daemon": { "Host": "127.0.0.1", "ExposureMode": "reverse-proxy" } }
JSON

failed_loud=false
for _ in $(seq 1 45); do
    [[ "$(entrypoint_exit_count)" -gt "$exits_before" ]] && { failed_loud=true; break; }
    sleep 1
done
[[ "$failed_loud" == "true" ]] \
    || fail "bad Daemon config did not fail loudly — the supervisor never observed an exit (silently served stale config?)"
echo "    bad config -> daemon aborted startup (supervisor observed the exit)"

# Recover: write a good config; the supervisor's next restart reads it from disk.
write_config <<JSON
{ "Daemon": { "Host": "127.0.0.1", "Port": $NEW_PORT, "ExposureMode": "local" } }
JSON
wait_healthy "$NEW_PORT" 90 || fail "daemon did not recover on :$NEW_PORT after the bad config was fixed"
count_c="$(daemon_count)"; sup_c="$(daemon_supervision)"
[[ "$count_c" == "1" ]] || fail "after recovery found $count_c daemons (expected 1)"
[[ "$sup_c" == "ok" ]]  || fail "after recovery netclawd not properly supervised: $sup_c"
echo "    recovered: count=$count_c supervision=$sup_c (port :$NEW_PORT)"

# ── Phase D: PID 1 reaps orphaned subprocesses (tini) ───────────────────────
echo "==> Phase D: orphaned subprocesses are reaped by PID 1 (tini)"
# Spawn a process whose parent shell exits immediately, so it reparents to PID 1 —
# exactly how netclawd's tool subprocesses orphan. entrypoint.sh alone only `wait`s its
# direct child, so without a reaping init (#1287) these would pile up as <defunct>.
# Capture the PID directly via `echo $!` (not `pgrep -x sleep`, which could latch onto a
# coincidental sleep such as the supervisor's backoff). The reparent comes from the
# parent `sh` exiting; `disown` is intentionally NOT used — it isn't a /bin/sh (dash)
# builtin on the Ubuntu base.
orphan="$(docker exec "$CONTAINER" sh -c 'sleep 300 >/dev/null 2>&1 & echo $!' | tr -d '[:space:]')"
[[ -n "$orphan" ]] || fail "could not create an orphan test process"

# Reparenting is async (it happens when the parent sh exits), so poll for PPID 1.
oppid=""
for _ in $(seq 1 10); do
    oppid="$(docker exec "$CONTAINER" sh -c "ps -o ppid= -p $orphan 2>/dev/null" | tr -d '[:space:]')"
    [[ "$oppid" == "1" ]] && break
    sleep 1
done
[[ "$oppid" == "1" ]] || fail "orphan (pid $orphan) PPID is '$oppid', expected 1 (did not reparent to PID 1)"

docker exec "$CONTAINER" kill "$orphan" >/dev/null 2>&1 || true
reaped=false
for _ in $(seq 1 10); do
    # Reaped == the PID is gone entirely; a non-reaping PID 1 leaves it <defunct> (state Z),
    # which `ps -p` would still report.
    docker exec "$CONTAINER" sh -c "ps -o stat= -p $orphan" >/dev/null 2>&1 || { reaped=true; break; }
    sleep 1
done
[[ "$reaped" == "true" ]] \
    || fail "orphan (pid $orphan) was not reaped — PID 1 left it as a zombie (tini not reaping?)"
echo "    orphan pid $orphan reaped by PID 1"

echo "✓ #1279/#1287: single supervised daemon; reload re-binds in-process; daemon start defers; bad config fails loud + recovers; orphans reaped"
