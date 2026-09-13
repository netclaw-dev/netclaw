# mcp-permissions.tape — smoke the `netclaw mcp permissions` TUI.
#
# Validates that the page opens (header renders), shows the no-daemon
# error state, and exits cleanly on Ctrl+Q. This catches rendering
# regressions in McpToolPermissionsPage without requiring a live
# daemon or MCP servers — the Loading/error path exercises the
# BuildHeader + BuildContent + BuildFooter wiring added in #1351.

Output "/tmp/tape-mcp-permissions.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw mcp permissions; status=$?; echo MCP_PERMISSIONS_EXIT=$status"
Enter

# BuildHeader() always renders regardless of daemon state.
Wait+Screen@10s /MCP Permissions/
Wait+Screen@5s /Could not reach daemon:/

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
Wait+Screen@5s /MCP_PERMISSIONS_EXIT=0/

Type "exit"
Enter
