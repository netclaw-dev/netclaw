# mcp-permissions.tape (screenshot) — capture `netclaw mcp permissions` with
# the smoke MCP server (smoke-math) connected and its tools indexed.
#
# Capture-only tape: NO post-tape assertion. The screenshots mode in
# run-smoke.sh compares each emitted PNG byte-for-byte against the committed
# baseline at tests/smoke/screenshots/<frame>.approved.png.
#
# Frames captured:
#   mcp-permissions-server-list — ServerList state: smoke-math appears as
#                                 "Connected, 3 tools" below the header.
#   mcp-permissions-tool-grid  — ToolGrid state: header rows (Audience,
#                                Server enabled, Server default) and all
#                                three tool rows (add, echo, record-tasks)
#                                simultaneously visible. This is the direct
#                                regression check for issue #1424 — the
#                                scroll container must not overwrite the header.
#
# Why these frames matter: the Loading/no-daemon frame only confirmed the
# header layout compiles. These frames confirm the layout holds under real
# data: a connected server, its tool rows rendered at the correct vertical
# offset, and all metadata rows above them untouched.
#
# Prepended preamble: tapes/screenshot-preamble.tape (determinism-pinned).
# __NETCLAW_SMOKE_MCP_SERVER__ is substituted by run-native-tape.sh.

Output "/tmp/tape-shot-mcp-permissions.gif"

# ─── Setup: seed provider + register the smoke MCP server ────────────
# The smoke Ollama instance is already running at 127.0.0.1:11434 in
# screenshots mode. We seed a minimal provider so the daemon starts cleanly.
Hide
Type "netclaw provider add smoke-llm ollama --endpoint http://127.0.0.1:11434"
Enter
Wait+Screen@10s /TAPE\$/

Type "netclaw model set main smoke-llm qwen2:0.5b"
Enter
Wait+Screen@10s /TAPE\$/

# Register the deterministic smoke MCP server (add, echo, record-tasks).
# --grant-all means every tool is auto-approved for all audiences.
Type "netclaw mcp add --transport stdio --grant-all smoke-math -- __NETCLAW_SMOKE_MCP_SERVER__"
Enter
Wait+Screen@10s /TAPE\$/

# Start the daemon (detaches immediately; MCP handshake follows async).
Type "netclaw daemon start"
Enter
Wait+Screen@20s /TAPE\$/

# Allow time for the daemon to spawn the stdio MCP process and index tools.
Sleep 5s
Show

# ─── Launch the TUI ──────────────────────────────────────────────────
Type "netclaw mcp permissions"
Enter

# ─── Frame 1: ServerList ─────────────────────────────────────────────
# smoke-math should appear as "Connected, 3 tools".
Wait+Screen@15s /smoke-math/
Wait+Screen@5s /3 tools/
Screenshot "/tmp/shot-mcp-permissions-server-list.png"

# ─── Navigate into the ToolGrid ──────────────────────────────────────
# Sleep 1s here is an intentional settle guard: the daemon can push a
# server-state update (tool re-index, reconnect) immediately after the
# server-list frame stabilises. Navigating into the tool grid during such
# an update causes Enter to hit the TUI mid-transition, producing a blank
# screen rather than the tool grid. The sleep lets any in-flight update
# complete before Enter is delivered.
Sleep 1s
Enter

# ─── Frame 2: ToolGrid ───────────────────────────────────────────────
# All header rows (Server, Audience, Server enabled, Server default) plus
# all tool rows (add, echo, record-tasks) must be visible simultaneously.
# If the #1424 regression reappears, tool rows will overwrite the header.
# Timeout is 15s to match the server-list anchor; /Server default:/ (with
# colon) matches the rendered "Server default: [Auto]" line, which is
# unique to the tool-grid view and absent from the server-list scrollback.
Wait+Screen@15s /record-tasks/
Wait+Screen@10s /Server default:/
Screenshot "/tmp/shot-mcp-permissions-tool-grid.png"

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
Wait+Screen@10s /TAPE\$/

# Stop the daemon so the tape-level cleanup does not race the next tape.
Type "netclaw daemon stop"
Enter
Wait+Screen@10s /TAPE\$/

Type "exit"
Enter
