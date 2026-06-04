#!/usr/bin/env bash
# Regression test for the non-root CLI launcher (ADR-004).
#
# The daemon runs as the unprivileged `netclaw` user, but `docker exec` /
# `kubectl exec` default to the image USER (root) — and `netclaw init`, the
# documented first-run setup, is invoked exactly that way. The netclaw CLI is a
# .NET single-file binary that self-extracts into a per-$HOME dir the runtime
# locks to the invoking user (mode 700), and CLI config writes land under
# NETCLAW_HOME. Run as root, both become root-owned and the non-root daemon can
# no longer use them — the agent's own `shell_execute netclaw ...` then fails
# with "Failed to create directory ... Error code: 13" (EACCES, exit 160).
#
# /usr/local/bin/netclaw is therefore a self-dropping launcher that re-execs as
# the netclaw user when invoked as root. This test reproduces the original
# breakage path — a root `docker exec -- netclaw <cmd>` — and asserts:
#
#   Phase A — a root-context CLI invocation succeeds and drops to netclaw:
#     `docker exec` (default user = root) running `netclaw --version` must exit 0,
#     print a version (NOT the EACCES bundle-extraction error), and emit the
#     launcher's drop breadcrumb on stderr (proving it re-execed as netclaw).
#
#   Phase B — it leaves NOTHING root-owned under the netclaw home:
#     After root-context CLI runs (incl. the offline `doctor`, which touches
#     config), no path under /home/netclaw/.net or /home/netclaw/.netclaw may be
#     owned by uid 0. Without the launcher the extraction dir is created
#     root:root and this fails — the regression guard.
#
#   Phase C — the non-root path still execs directly:
#     `docker exec -u netclaw` (already the runtime user) must run the CLI without
#     a second drop, exit 0, and keep the extraction dir netclaw-owned.
#
#   Phase D — the daemon stays healthy throughout.
#
# Usage:
#   scripts/docker/test-nonroot-cli.sh <image-ref>
#   scripts/docker/test-nonroot-cli.sh netclawd-pr:pr-1234
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/docker/lib/smoke-lib.sh
. "$SCRIPT_DIR/lib/smoke-lib.sh"

IMAGE="${1:?usage: test-nonroot-cli.sh <image-ref>}"
CONTAINER="netclaw-nonroot-cli"
PORT=5199
RUNTIME_UID=1654
NET_DIR=/home/netclaw/.net          # .NET single-file extraction base ($HOME/.net)
HOME_DIRS="/home/netclaw/.net /home/netclaw/.netclaw"

cleanup() { docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; }
trap cleanup EXIT

fail() {
    echo "ERROR: $*" >&2
    echo "---- container logs ----" >&2
    docker logs "$CONTAINER" >&2 2>&1 || true
    exit 1
}

# Any path under the netclaw home owned by uid 0 means a root-context CLI run was
# NOT dropped to the netclaw user (extraction dir / config created root-owned).
assert_no_root_owned() {  # $1 = phase label
    local hits
    hits="$(docker exec "$CONTAINER" find $HOME_DIRS -user 0 -not -path '*/lost+found*' 2>/dev/null || true)"
    if [[ -n "$hits" ]]; then
        echo "$hits" | sed 's/^/    root-owned: /' >&2
        fail "$1: root-owned paths under the netclaw home — a root CLI invocation was not dropped to the netclaw user"
    fi
}

echo "==> Starting supervised daemon from image: $IMAGE"
cleanup
# shellcheck disable=SC2046  # intentional word-splitting of the -e args
docker run -d --name "$CONTAINER" $(netclaw_smoke_env_args) "$IMAGE" >/dev/null
netclaw_wait_healthy "$CONTAINER" "$PORT" 60 || fail "daemon never became healthy on :$PORT"
echo "    daemon healthy on :$PORT"

# Sanity: the daemon really is non-root, and its own extraction dir is netclaw-owned.
daemon_uid="$(docker exec "$CONTAINER" sh -c 'pid=$(pgrep -o -x netclawd); stat -c %u /proc/$pid')"
[[ "$daemon_uid" == "$RUNTIME_UID" ]] || fail "netclawd runs as uid $daemon_uid, expected $RUNTIME_UID (test premise invalid)"

# ── Phase A: root-context `docker exec -- netclaw` succeeds and drops to netclaw ──
echo "==> Phase A: root 'docker exec -- netclaw --version' succeeds + drops to netclaw"
out=""; rc=0
out="$(docker exec "$CONTAINER" netclaw --version 2>/dev/null)" || rc=$?
echo "    stdout: $out (rc=$rc)"
[[ "$rc" -eq 0 ]]            || fail "root 'netclaw --version' exited $rc (the bundle-extraction EACCES regression?)"
echo "$out" | grep -qi 'netclaw' || fail "root 'netclaw --version' did not print a version: $out"

# stderr must carry the launcher's drop breadcrumb -> proves it re-execed as netclaw.
err="$(docker exec "$CONTAINER" netclaw --version 2>&1 1>/dev/null)" || true
echo "    stderr: $err"
echo "$err" | grep -qi "dropping to the 'netclaw' user" \
    || fail "launcher did not report dropping to the netclaw user — is /usr/local/bin/netclaw still the self-dropping launcher?"

# Belt-and-suspenders: the failure mode's signature must never appear.
if echo "$out $err" | grep -qiE 'Failed to create directory|Error code: 13|Failure processing application bundle'; then
    fail "bundle-extraction failure signature present — root CLI was not dropped"
fi

# ── Phase B: nothing root-owned, and the extraction dir is netclaw-owned ─────
echo "==> Phase B: root CLI runs leave nothing root-owned under the netclaw home"
docker exec "$CONTAINER" netclaw doctor >/dev/null 2>&1 || true   # offline; touches config as 'root' exec
assert_no_root_owned "Phase B"

net_owner="$(docker exec "$CONTAINER" sh -c "stat -c '%u:%g' $NET_DIR/netclaw 2>/dev/null" | tr -d '[:space:]')"
echo "    $NET_DIR/netclaw owner: ${net_owner:-<absent>}"
[[ "$net_owner" == "$RUNTIME_UID:$RUNTIME_UID" ]] \
    || fail "$NET_DIR/netclaw owner is '${net_owner:-<absent>}', expected $RUNTIME_UID:$RUNTIME_UID"

# ── Phase C: when already the netclaw user, the launcher execs directly ──────
echo "==> Phase C: 'docker exec -u netclaw -- netclaw --version' (no double drop)"
rc=0
cout="$(docker exec -u netclaw "$CONTAINER" netclaw --version 2>/dev/null)" || rc=$?
[[ "$rc" -eq 0 ]] || fail "'netclaw --version' as the netclaw user exited $rc"
cerr="$(docker exec -u netclaw "$CONTAINER" netclaw --version 2>&1 1>/dev/null)" || true
echo "$cerr" | grep -qi "dropping to the 'netclaw' user" \
    && fail "launcher dropped privileges even though it was already the netclaw user (should exec directly)"
assert_no_root_owned "Phase C"

# ── Phase D: daemon still healthy after all the exec traffic ─────────────────
echo "==> Phase D: daemon still healthy"
docker exec "$CONTAINER" curl -fsS "http://127.0.0.1:$PORT/api/health/ready" >/dev/null 2>&1 \
    || fail "daemon not healthy on :$PORT after CLI exec traffic"

echo "✓ ADR-004: root-context 'docker exec -- netclaw' drops to the netclaw user; nothing left root-owned; non-root path execs directly; daemon healthy"
