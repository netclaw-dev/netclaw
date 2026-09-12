#!/usr/bin/env bash
# approvals.tape post-tape assertion.

set -euo pipefail

approvals_path="${NETCLAW_HOME}/config/tool-approvals.json"

jq -e '
  .version == 3
  and (.audiences.personal.shell_execute | length) == 1
  and .audiences.personal.shell_execute[0].shell == "Bash"
  and .audiences.personal.shell_execute[0].match == "LegacyExact"
  and .audiences.personal.shell_execute[0].verb == "alpha"
  and (.audiences.public.custom_tool | length) == 1
  and .audiences.public.custom_tool[0].verb == "tool in mode"
  and (.audiences.public.custom_tool[0] | has("shell") | not)
' "$approvals_path" >/dev/null

echo "approvals: the highlighted approval was revoked"
