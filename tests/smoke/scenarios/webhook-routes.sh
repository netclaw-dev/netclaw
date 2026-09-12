#!/usr/bin/env bash
# webhook-routes.sh — goal: prove one webhook route lifecycle end to end
# through the real CLI, the real daemon, and the anonymous delivery endpoint.
#
# The CLI has no local route write path: `webhooks set` and `webhooks delete`
# go to the daemon, which owns route mutations. This scenario checks the whole
# loop against a running daemon:
#   set          -> exit 0 and the daemon writes the route file
#   signed POST  -> 202 accepted, with no daemon restart
#   bad-sig POST -> 401, so verification fails closed
#   delete       -> exit 0, and a later POST answers 404, again with no restart
#
# The two delivery results after a mutation are the hot-reload proof: the
# catalog re-reads the route directory on each delivery, so a route the actor
# wrote (or removed) serves immediately.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

command -v jq >/dev/null 2>&1 || die "jq is required for webhook-routes.sh"
command -v openssl >/dev/null 2>&1 || die "openssl is required for webhook-routes.sh"

ROUTE_NAME="smoke-e2e-route"
ROUTE_SECRET="smoke-e2e-secret"
ROUTE_BODY='{"smoke":"e2e"}'
ROUTE_PROMPT="Report the smoke webhook payload in one word."
NETCLAW_JSON="${NETCLAW_HOME}/config/netclaw.json"
ROUTE_FILE="${NETCLAW_HOME}/config/webhooks/${ROUTE_NAME}.json"

trap stop_daemon EXIT

log "Seeding provider + model ($SMOKE_MODEL)..."
seed_provider_model

# Inbound webhooks are off by default, and the delivery endpoint answers 404
# for every route while the feature is off. Turn it on before the daemon reads
# its configuration.
log "Enabling inbound webhooks in netclaw.json..."
[[ -f "$NETCLAW_JSON" ]] || die "expected config file at $NETCLAW_JSON"
jq '.Webhooks = { "Enabled": true }' "$NETCLAW_JSON" >"${NETCLAW_JSON}.tmp" \
  || die "jq could not enable Webhooks in $NETCLAW_JSON"
mv "${NETCLAW_JSON}.tmp" "$NETCLAW_JSON"

log "Starting daemon..."
start_daemon || die "daemon did not start"
wait_for_health || die "daemon health endpoint not ready"

# post_delivery <signature> — POST one JSON delivery to the anonymous endpoint
# and print the HTTP status code.
post_delivery() {
  local signature="$1"
  run_timed "$STEP_TIMEOUT_SECONDS" \
    curl -sS -o /dev/null -w '%{http_code}' \
      -X POST "${DAEMON_BASE_URL}/api/webhooks/${ROUTE_NAME}" \
      -H 'Content-Type: application/json' \
      -H "X-Webhook-Signature: ${signature}" \
      --data-binary "$ROUTE_BODY" 2>/dev/null || true
}

# The Hmac verifier expects HMAC-SHA256 over the raw body, hex-encoded in lower
# case, in the default X-Webhook-Signature header, with no prefix.
valid_signature="$(printf '%s' "$ROUTE_BODY" \
  | openssl dgst -sha256 -hmac "$ROUTE_SECRET" -r | awk '{print $1}')"
[[ -n "$valid_signature" ]] || die "openssl produced no HMAC signature"

# ── set: the CLI writes through the daemon ──

log "Creating route '$ROUTE_NAME' through the CLI..."
set_status=0
set_output="$(nc webhooks set "$ROUTE_NAME" \
  --prompt "$ROUTE_PROMPT" \
  --secret "$ROUTE_SECRET" \
  --verification-kind hmac 2>&1)" || set_status=$?
echo "$set_output"
if [[ "$set_status" -eq 0 && "$set_output" == *"[OK] Created webhook route '${ROUTE_NAME}'."* ]]; then
  pass "webhooks set: CLI reported the route as created"
else
  die "webhooks set: expected exit 0 and a created line, got exit $set_status"
fi

# The CLI never writes a route file, so the file on disk is the daemon's work.
if [[ -f "$ROUTE_FILE" ]]; then
  pass "webhooks set: the daemon wrote $ROUTE_FILE"
else
  die "webhooks set: expected the daemon to write $ROUTE_FILE"
fi

log "Verifying 'webhooks list' shows the route..."
list_output="$(nc webhooks list 2>/dev/null || true)"
echo "$list_output"
if [[ "$list_output" == *"$ROUTE_NAME"* ]]; then
  pass "webhooks list: includes $ROUTE_NAME"
else
  fail "webhooks list: expected $ROUTE_NAME"
fi

log "Verifying 'webhooks show' reports the route endpoint..."
show_output="$(nc webhooks show "$ROUTE_NAME" 2>/dev/null || true)"
echo "$show_output"
if [[ "$show_output" == *"/api/webhooks/${ROUTE_NAME}"* ]]; then
  pass "webhooks show: reports endpoint /api/webhooks/${ROUTE_NAME}"
else
  fail "webhooks show: expected endpoint /api/webhooks/${ROUTE_NAME}"
fi

# ── delivery: the new route serves without a daemon restart ──

log "Posting a correctly signed delivery (expect HTTP 202)..."
accepted_status="$(post_delivery "$valid_signature")"
log "HTTP status for the signed delivery: $accepted_status"
if [[ "$accepted_status" == "202" ]]; then
  pass "delivery: signed POST accepted with 202 and no daemon restart"
else
  fail "delivery: expected 202 for the signed POST, got $accepted_status"
fi

log "Posting a wrongly signed delivery (expect HTTP 401)..."
rejected_status="$(post_delivery "0000000000000000000000000000000000000000000000000000000000000000")"
log "HTTP status for the wrongly signed delivery: $rejected_status"
if [[ "$rejected_status" == "401" ]]; then
  pass "delivery: wrong signature rejected with 401"
else
  fail "delivery: expected 401 for the wrong signature, got $rejected_status"
fi

# ── delete: the route stops serving without a daemon restart ──

log "Deleting route '$ROUTE_NAME' through the CLI..."
delete_status=0
delete_output="$(nc webhooks delete "$ROUTE_NAME" --force 2>&1)" || delete_status=$?
echo "$delete_output"
if [[ "$delete_status" -eq 0 && "$delete_output" == *"[OK] Deleted webhook route '${ROUTE_NAME}'."* ]]; then
  pass "webhooks delete: CLI reported the route as deleted"
else
  die "webhooks delete: expected exit 0 and a deleted line, got exit $delete_status"
fi

if [[ -f "$ROUTE_FILE" ]]; then
  fail "webhooks delete: route file still present at $ROUTE_FILE"
else
  pass "webhooks delete: the daemon removed $ROUTE_FILE"
fi

log "Posting a signed delivery to the deleted route (expect HTTP 404)..."
deleted_status="$(post_delivery "$valid_signature")"
log "HTTP status after delete: $deleted_status"
if [[ "$deleted_status" == "404" ]]; then
  pass "delivery: deleted route answers 404 with no daemon restart"
else
  fail "delivery: expected 404 after delete, got $deleted_status"
fi

# ── daemon-down contract: a route write requires the daemon ──

log "Stopping the daemon to check the CLI write contract..."
stop_daemon

down_status=0
down_output="$(nc webhooks set "$ROUTE_NAME" \
  --prompt "$ROUTE_PROMPT" \
  --secret "$ROUTE_SECRET" \
  --verification-kind hmac 2>&1)" || down_status=$?
echo "$down_output"
if [[ "$down_status" -eq 1 && "$down_output" == *"daemon is not reachable"* ]]; then
  pass "webhooks set: fails with exit 1 and a not-reachable message when the daemon is down"
else
  fail "webhooks set: expected exit 1 and a not-reachable message, got exit $down_status"
fi

if [[ -f "$ROUTE_FILE" ]]; then
  fail "webhooks set: wrote $ROUTE_FILE without the daemon"
else
  pass "webhooks set: wrote no route file without the daemon"
fi

summarize
exit $?
