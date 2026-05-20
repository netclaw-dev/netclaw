#!/usr/bin/env bash
# pairing.sh — full device pairing lifecycle (pair, exchange, auth, revoke).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

seed_and_start_daemon

SMOKE_DEVICE_NAME="smoke-pairing-$$"

log "Generating pairing code via netclaw daemon pair..."
pair_output="$(nc daemon pair 2>/dev/null || true)"
echo "$pair_output"

pairing_code="$(echo "$pair_output" | grep 'Pairing code:' | awk '{print $NF}')"
if [[ -z "$pairing_code" ]]; then
  die "daemon pair: expected pairing code in output"
fi
pass "daemon pair: extracted pairing code $pairing_code"

log "Exchanging pairing code for bearer token..."
exchange_response="$(run_timed "$STEP_TIMEOUT_SECONDS" \
  curl -fsS \
    -X POST "${DAEMON_BASE_URL}/api/pair/exchange" \
    -H 'Content-Type: application/json' \
    -d "{\"code\":\"$pairing_code\",\"deviceName\":\"$SMOKE_DEVICE_NAME\"}" 2>/dev/null || true)"
echo "$exchange_response"

device_token="$(echo "$exchange_response" | jq -r '.token')"
if [[ -z "$device_token" || "$device_token" == "null" ]]; then
  die "pair exchange: expected token field in response"
fi
pass "pair exchange: bearer token received"

log "Verifying device appears in daemon devices list..."
devices_output="$(nc daemon devices 2>/dev/null || true)"
echo "$devices_output"
if [[ "$devices_output" == *"$SMOKE_DEVICE_NAME"* ]]; then
  pass "daemon devices: includes $SMOKE_DEVICE_NAME"
else
  fail "daemon devices: expected $SMOKE_DEVICE_NAME"
fi

log "Verifying bearer token authenticates protected endpoint (/api/pair/devices)..."
auth_response="$(run_timed "$STEP_TIMEOUT_SECONDS" \
  curl -fsS \
    -H "Authorization: Bearer $device_token" \
    "${DAEMON_BASE_URL}/api/pair/devices" 2>/dev/null || true)"
echo "$auth_response"
if [[ "$auth_response" == *"$SMOKE_DEVICE_NAME"* ]]; then
  pass "auth: bearer token authenticates /api/pair/devices"
else
  fail "auth: expected $SMOKE_DEVICE_NAME in authenticated response"
fi

log "Revoking device $SMOKE_DEVICE_NAME..."
nc daemon devices revoke "$SMOKE_DEVICE_NAME"

log "Verifying revoked token is rejected (expect HTTP 401)..."
revoke_status="$(run_timed "$STEP_TIMEOUT_SECONDS" \
  curl -sS -o /dev/null -w '%{http_code}' \
    -H "Authorization: Bearer $device_token" \
    "${DAEMON_BASE_URL}/api/pair/devices" 2>/dev/null || true)"
log "HTTP status after revoke: $revoke_status"
if [[ "$revoke_status" == "401" ]]; then
  pass "revoke: revoked token rejected with HTTP 401"
else
  fail "revoke: expected 401 for revoked token, got $revoke_status"
fi

summarize
exit $?
