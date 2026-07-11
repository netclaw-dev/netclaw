#!/usr/bin/env bash
# Verify that the latest stable image's inline model configuration survives an upgrade to a
# locally built image and migrates without losing operator-owned model metadata.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/docker/lib/smoke-lib.sh
. "$SCRIPT_DIR/lib/smoke-lib.sh"

LOCAL_IMAGE="${1:?usage: test-model-config-upgrade.sh <local-image> [stable-image]}"
STABLE_IMAGE="${2:-ghcr.io/netclaw-dev/netclaw:latest}"
RUN_ID="model-upgrade-$PPID-$$"
CONTAINER="netclaw-$RUN_ID"
ROOT="$(mktemp -d -t netclaw-model-upgrade.XXXXXX)"
HOME_DIR="$ROOT/home"
CONFIG_DIR="$HOME_DIR/config"
CONFIG="$CONFIG_DIR/netclaw.json"

cleanup() {
    docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
    docker run --rm --entrypoint chmod -v "$ROOT:/cleanup" "$LOCAL_IMAGE" \
        -R a+rwX /cleanup >/dev/null 2>&1 || true
    rm -rf "$ROOT"
}
trap cleanup EXIT

mkdir -p "$CONFIG_DIR"
chmod 0777 "$HOME_DIR" "$CONFIG_DIR"

cat >"$CONFIG" <<'JSON'
{
  "configVersion": 1,
  "Providers": {
    "vllm": {
      "Type": "openai-compatible",
      "Endpoint": "http://127.0.0.1:8000"
    }
  },
  "Models": {
    "Main": {
      "Provider": "vllm",
      "ModelId": "qwen-vl",
      "ContextWindow": 32768,
      "InputModalities": "Text, Image",
      "OutputModalities": "Text"
    },
    "Fallback": {
      "Provider": "vllm",
      "ModelId": "llama-text"
    }
  }
}
JSON
chmod 0666 "$CONFIG"

echo "==> Latest stable consumes the isolated legacy configuration: $STABLE_IMAGE"
docker run --rm --entrypoint /usr/local/bin/netclaw \
    -v "$HOME_DIR:/home/netclaw/.netclaw" "$STABLE_IMAGE" model list >/dev/null

legacy_hash="$(sha256sum "$CONFIG" | awk '{print $1}')"

echo "==> Locally built daemon starts against the same legacy configuration without rewriting it"
docker run -d --name "$CONTAINER" -v "$HOME_DIR:/home/netclaw/.netclaw" "$LOCAL_IMAGE" >/dev/null
netclaw_wait_healthy "$CONTAINER" 5199 60
[[ "$(sha256sum "$CONFIG" | awk '{print $1}')" == "$legacy_hash" ]] \
    || { echo "ERROR: startup rewrote the legacy configuration" >&2; exit 1; }
docker rm -f "$CONTAINER" >/dev/null

run_local_cli() {
    docker run --rm --entrypoint /usr/local/bin/netclaw \
        -v "$HOME_DIR:/home/netclaw/.netclaw" "$LOCAL_IMAGE" "$@"
}

echo "==> Explicit model mutation migrates, then A -> B -> A preserves A's overrides"
run_local_cli model set main vllm llama-text --context-window 65536 >/dev/null
run_local_cli model set main vllm qwen-vl >/dev/null

jq -e '
  .Models.Main == null and
  .Models.Roles.Main as $active |
  .Models.Definitions[$active] |
  .Provider == "vllm" and
  .ModelId == "qwen-vl" and
  .ContextWindow == 32768 and
  .InputModalities == "Text, Image" and
  .OutputModalities == "Text"
' "$CONFIG" >/dev/null

test -f "$CONFIG.legacy-models.bak"
jq -e '.Models.Main.ModelId == "qwen-vl"' "$CONFIG.legacy-models.bak" >/dev/null

echo "✓ stable legacy config starts unchanged, migrates explicitly, and preserves model metadata"
