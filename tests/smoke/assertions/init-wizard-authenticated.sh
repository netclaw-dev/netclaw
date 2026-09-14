#!/usr/bin/env bash
# Validate the authenticated init flow and the runtime request boundary.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

: "${SMOKE_LLM_PROTECTED_ENDPOINT:?The protected smoke endpoint is required.}"
: "${SMOKE_LLM_PROTECTED_API_KEY:?The protected smoke API key is required.}"
: "${SMOKE_LLM_REQUEST_RECORD:?The smoke request record is required.}"

assert_fail=0

if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist after the wizard." >&2
  exit 1
fi

config_json="$(read_config_json)"
if ! printf '%s' "$config_json" | jq empty >/dev/null 2>&1; then
  echo "FAIL: ${CONFIG_PATH} is not valid JSON." >&2
  exit 1
fi

assert_field '.Providers["openai-compatible"].Type' 'openai-compatible' "$config_json" || :
assert_field '.Providers["openai-compatible"].Endpoint' "$SMOKE_LLM_PROTECTED_ENDPOINT" "$config_json" || :
assert_field '.Providers["openai-compatible"].AuthMethod' 'ApiKey' "$config_json" || :

if printf '%s' "$config_json" | grep -Fq "$SMOKE_LLM_PROTECTED_API_KEY"; then
  echo "FAIL: ${CONFIG_PATH} contains the API key as plaintext." >&2
  assert_fail=1
fi

secrets_path="${NETCLAW_HOME}/config/secrets.json"
if [[ ! -f "$secrets_path" ]]; then
  echo "FAIL: ${secrets_path} does not exist after the wizard." >&2
  assert_fail=1
else
  secrets_json="$(cat "$secrets_path")"
  if printf '%s' "$secrets_json" | grep -Fq "$SMOKE_LLM_PROTECTED_API_KEY"; then
    echo "FAIL: ${secrets_path} contains the API key as plaintext." >&2
    assert_fail=1
  elif ! printf '%s' "$secrets_json" | jq -e \
      '.Providers["openai-compatible"].ApiKey | type == "string" and startswith("ENC:")' \
      >/dev/null; then
    echo "FAIL: ${secrets_path} does not contain an encrypted API key." >&2
    assert_fail=1
  else
    echo "  ok  secrets.json contains an encrypted provider API key"
  fi
fi

if [[ ! -f "$SMOKE_LLM_REQUEST_RECORD" ]]; then
  echo "FAIL: ${SMOKE_LLM_REQUEST_RECORD} does not exist." >&2
  assert_fail=1
elif ! jq -se \
    --arg models "/test/protected/v1/models" \
    --arg chat "/test/protected/v1/chat/completions" \
    'any(.[]; .Route == $models and .BearerAuthorized == true)
     and any(.[]; .Route == $chat and .BearerAuthorized == true)' \
    "$SMOKE_LLM_REQUEST_RECORD" >/dev/null; then
  echo "FAIL: the protected model and chat routes did not accept a Bearer key." >&2
  assert_fail=1
else
  echo "  ok  the protected model and chat routes accepted the Bearer key"
fi

missing_status="$(curl -sS -o /dev/null -w '%{http_code}' \
  "${SMOKE_LLM_PROTECTED_ENDPOINT}/v1/models")"
wrong_status="$(curl -sS -o /dev/null -w '%{http_code}' \
  -H "Authorization: Bearer ${SMOKE_LLM_PROTECTED_API_KEY}-wrong" \
  "${SMOKE_LLM_PROTECTED_ENDPOINT}/v1/models")"
if [[ "$missing_status" != '401' || "$wrong_status" != '401' ]]; then
  echo "FAIL: the protected model route accepted an absent or incorrect Bearer key." >&2
  assert_fail=1
else
  echo "  ok  the protected model route rejected absent and incorrect Bearer keys"
fi

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "init-wizard-authenticated: assertions passed."
