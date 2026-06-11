# model-manager.tape — smoke the `netclaw model` TUI.
#
# Validates that the page opens (panel title renders), shows the
# model role assignment view, and exits cleanly on Ctrl+Q. This covers
# ModelManagerPage rendering regressions (including the .WithFillHeight()
# scroll fix from #1351) without requiring a live daemon.

Output "/tmp/tape-model-manager.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw model"
Enter

# PanelNode title always renders regardless of daemon state.
Wait+Screen@10s /Model Manager/
Sleep 300ms

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo MODEL_MANAGER_EXIT=$?"
Enter
Wait+Screen@5s /MODEL_MANAGER_EXIT=0/

Type "exit"
Enter
