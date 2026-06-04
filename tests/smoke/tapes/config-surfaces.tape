# config-surfaces.tape — exercise Task 1.5 config areas.
#
# Covers:
#   - Workspaces Directory successful save path
#   - Inbound Webhooks timeout editing and fail-closed enabled/no-route path
#   - Browser Automation guidance-only path without shelling out from the TUI
#
# Post-tape assertion validates the persisted config semantically.

Output "/tmp/tape-config-surfaces.gif"

# ─── Seed minimal installed config ──────────────────────────────────────────
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "jq -n '{configVersion:1}' > $NETCLAW_HOME/config/netclaw.json"
Enter

# ─── Launch config dashboard and save Workspaces Directory ─────────────────
Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/

# Workspaces Directory is after Security & Access.
Down 9
Enter
Wait+Screen@10s /Workspaces Directory/
Wait+Screen@5s /workspace-scoped prompts/
Type "/tmp/netclaw-smoke-config-surfaces-workspaces"
Enter
Wait+Screen@10s /Workspaces Directory saved/
Escape
Wait+Screen@10s /Settings Areas/
Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "WORKSPACES_DIR=/tmp/netclaw-smoke-config-surfaces-workspaces jq -e '.Workspaces.Directory == env.WORKSPACES_DIR' $NETCLAW_HOME/config/netclaw.json"
Enter
Wait+Screen@5s /true/

# ─── Inbound Webhooks timeout save and enabled/no-route guard ───────────────
Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/
Down 3
Enter
Wait+Screen@10s /Inbound Webhooks/
Wait+Screen@5s /Route authoring/
Down
Type "45"
Enter
Wait+Screen@10s /Inbound Webhooks settings saved/
Up
Space
Wait+Screen@10s /cannot be enabled until at least one valid route exists/
Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "jq -e '.Webhooks.Enabled == false and .Webhooks.ExecutionTimeoutSeconds == 45' $NETCLAW_HOME/config/netclaw.json"
Enter
Wait+Screen@5s /true/

# ─── Browser Automation guidance-only path ─────────────────────────────────
Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/
Down 6
Enter
Wait+Screen@10s /Browser Automation/
Wait+Screen@10s /Manual install guidance/
Wait+Screen@10s /MCP permissions/
Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_SURFACES_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_SURFACES_EXIT=0/

Type "exit"
Enter
