# mcp-permissions.tape — smoke the `netclaw mcp permissions` TUI.
#
# Validates that the page opens (header renders), shows the no-daemon
# error state, and exits cleanly on Ctrl+Q. This catches rendering
# regressions in McpToolPermissionsPage without requiring a live
# daemon or MCP servers — the Loading/error path exercises the
# BuildHeader + BuildContent + BuildFooter wiring added in #1351.

Output "/tmp/tape-mcp-permissions.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw mcp permissions"
Enter

# BuildHeader() always renders regardless of daemon state.
Wait+Screen@10s /MCP Permissions/
Sleep 300ms

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo MCP_PERMISSIONS_EXIT=$?"
Enter
Wait+Screen@5s /MCP_PERMISSIONS_EXIT=0/

Type "exit"
Enter
