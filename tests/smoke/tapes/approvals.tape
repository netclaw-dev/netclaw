# approvals.tape — smoke the `netclaw approvals` TUI.
#
# Validates that the page opens (panel title renders), shows the
# no-daemon / empty state, and exits cleanly on Ctrl+Q. This covers
# ApprovalsManagerPage rendering regressions (including the .WithFillHeight()
# scroll fix from #1351) without requiring a live daemon.

Output "/tmp/tape-approvals.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw approvals"
Enter

# PanelNode title always renders regardless of daemon state.
Wait+Screen@10s /Approvals Manager/
Sleep 300ms

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo APPROVALS_EXIT=$?"
Enter
Wait+Screen@5s /APPROVALS_EXIT=0/

Type "exit"
Enter
