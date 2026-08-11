# approvals.tape — smoke the `netclaw approvals` TUI.
#
# Validates that the page revokes the highlighted row and exits cleanly.
# This covers the selection regression from #1703 without a live daemon.

Output "/tmp/tape-approvals.gif"

# ─── Seed approvals ──────────────────────────────────────────────────
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Wait+Screen@5s /TAPE\$/

Type "first=alpha second=bravo exact='tool in mode' jq -n '{version:2,audiences:{personal:{shell_execute:[{verb:env.first},{verb:env.second}]},public:{custom_tool:[{verb:env.exact}]}}}' > $NETCLAW_HOME/config/tool-approvals.json"
Enter
Wait+Screen@5s /TAPE\$/

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw approvals"
Enter

# PanelNode title always renders regardless of daemon state.
Wait+Screen@10s /Approvals Manager/
Wait+Screen@5s /Bash legacy-exact "alpha" anywhere/
Wait+Screen@5s /Bash legacy-exact "bravo" anywhere/
Wait+Screen@5s /NonShell exact "tool in mode" anywhere/

# ─── Revoke the second row ───────────────────────────────────────────
Down
Wait+Screen@5s /Bash legacy-exact "bravo" anywhere/
Type "r"
Wait+Screen@5s /Revoke 'Bash legacy-exact "bravo" anywhere'/
Enter
Wait+Screen@5s /Removed 'Bash legacy-exact "bravo" anywhere'/

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q

# Wait for the alternate screen to close before the shell receives input.
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo APPROVALS_EXIT=$?"
Enter
Wait+Screen@5s /APPROVALS_EXIT=0/

Type "exit"
Enter
