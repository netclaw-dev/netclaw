#!/usr/bin/env bash
# plugin-public-repository.sh — prove a public Codex plugin through logical skill tools.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

PLUGIN_NAME="dotnet-skills-public"
PLUGIN_REPOSITORY="Aaronontheweb/dotnet-skills"
PLUGIN_COMMIT="13e26d39ed01d97ea592235d041304d289f4ba07"
PROOF_PROMPT="NETCLAW_SMOKE_SKILL_PLUGIN_PROOF"

seed_and_start_daemon

log "Install the public Codex plugin at an exact commit..."
install_output="$(nc plugin install "$PLUGIN_REPOSITORY" \
  --id "$PLUGIN_NAME" \
  --format codex \
  --commit "$PLUGIN_COMMIT" \
  --timeout-seconds 300 \
  --yes 2>&1)" || die "plugin install failed"
echo "$install_output"
if [[ "$install_output" == *"Installed plugin '${PLUGIN_NAME}' at commit ${PLUGIN_COMMIT}."* ]]; then
  pass "plugin install: the CLI installed the exact public commit"
else
  die "plugin install: the CLI did not report the exact public commit"
fi

log "Check the canonical skill in the daemon inventory..."
skill_output="$(nc skill list 2>&1)" || die "skill list failed"
echo "$skill_output"
if awk '$1 == "akka-net-best-practices" && $2 == "external" { found = 1 } END { exit !found }' <<<"$skill_output"; then
  pass "skill list: the daemon published the canonical external skill"
else
  die "skill list: the canonical external skill is absent"
fi

log "Load the skill and its exact bundled resource through the agent tools..."
proof_output="$(nc chat -p "$PROOF_PROMPT" 2>&1)" || die "logical skill proof failed"
if [[ "$proof_output" == *"Skill plugin proof passed."* ]]; then
  pass "skill tools: skill_load and skill_read_resource returned public plugin content"
else
  die "skill tools: the logical tool proof did not pass"
fi

log "Run the external skill sync again..."
sync_output="$(nc skill sync 2>&1)" || die "second plugin sync failed"
echo "$sync_output"
if [[ "$sync_output" == *"${PLUGIN_NAME}"* && "$sync_output" == *"failed=0"* && "$sync_output" == *"rejected=0"* ]]; then
  pass "skill sync: the installed public plugin stayed healthy"
else
  die "skill sync: the second pass did not report a healthy plugin"
fi

summarize
exit $?
