#!/usr/bin/env bash
# approvals.tape post-tape assertion.

set -euo pipefail

approvals_path="${NETCLAW_HOME}/config/tool-approvals.json"

jq -e '
  .version == 2
  and (.audiences.personal.shell_execute | length) == 1
  and .audiences.personal.shell_execute[0].verb == "alpha"
' "$approvals_path" >/dev/null

echo "approvals: the highlighted approval was revoked"
