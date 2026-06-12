# config-surfaces.tape — exercise Task 1.5 config areas.
#
# Covers:
#   - Inbound Webhooks timeout editing and enable-first advisory (no routes yet)
#   - Browser Automation guidance-only path without shelling out from the TUI
#
# The Workspaces Directory save path moved to config-workspaces-picker.tape: that
# screen is now a Termina FilePickerNode, whose terminal scroll region the vhs
# emulator does not reset on exit, so a second netclaw launch in the same recording
# never renders. Each directory-picker flow therefore gets its own single-launch tape.
#
# Post-tape assertion validates the persisted config semantically.

Output "/tmp/tape-config-surfaces.gif"

# ─── Seed minimal installed config ──────────────────────────────────────────
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "jq -n '{configVersion:1}' > $NETCLAW_HOME/config/netclaw.json"
Enter

# ─── Inbound Webhooks timeout save and enable-first advisory ────────────────
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
Wait+Screen@10s /Add at least one route/
Ctrl+Q
# Alt-screen restore guard (CSI ?1049l): give vhs a beat to catch the restored main buffer.
Sleep 1s
Wait+Screen@10s /TAPE\$/

# Enabling with no routes is the intended setup order: the toggle persists
# Enabled=true and the advisory just points at `netclaw webhooks set`.
Type "jq -e '.Webhooks.Enabled == true and .Webhooks.ExecutionTimeoutSeconds == 45' $NETCLAW_HOME/config/netclaw.json"
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
# Alt-screen restore guard (CSI ?1049l): give vhs a beat to catch the restored main buffer.
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_SURFACES_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_SURFACES_EXIT=0/

Type "exit"
Enter
