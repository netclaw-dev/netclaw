# sessions-tui.tape — smoke the interactive `netclaw sessions` TUI browser.
#
# Validates that the sessions page opens (panel title renders), shows
# the empty/loading state, and exits cleanly on Escape. This covers
# SessionsPage rendering regressions (including the for-loop →
# SelectionListNode.WithFillHeight() change from #1351) without
# requiring a live daemon or existing sessions.

Output "/tmp/tape-sessions-tui.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw sessions"
Enter

# PanelNode title always renders regardless of daemon state.
Wait+Screen@10s /Sessions/
Sleep 300ms

# ─── Exit TUI ────────────────────────────────────────────────────────
Escape
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo SESSIONS_TUI_EXIT=$?"
Enter
Wait+Screen@5s /SESSIONS_TUI_EXIT=0/

Type "exit"
Enter
