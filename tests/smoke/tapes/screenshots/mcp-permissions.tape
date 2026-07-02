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

# Poll until the daemon reports smoke-math as fully connected with 3 tools.
# Replaces the former fixed Sleep 5s: we wait for the actual CLI signal
# rather than guessing. `netclaw mcp list` queries the daemon's cached
# server state; "connected (3 tools)" means the stdio handshake and tool
# indexing are complete. The Wait+Screen@60s is a safety net for a hard hang.
Type "until netclaw mcp list 2>/dev/null | grep -q 'connected (3 tools)'; do sleep 2; done"
Enter
Wait+Screen@60s /TAPE\$/
Show

# ─── Launch the TUI ──────────────────────────────────────────────────
Type "netclaw mcp permissions"
Enter

# ─── Frame 1: ServerList ─────────────────────────────────────────────
# smoke-math should appear as "Connected, 3 tools".
Wait+Screen@15s /smoke-math/
Wait+Screen@5s /3 tools/
# Sleep 3s: let the server-list frame fully settle before capturing. The
# first match of 'smoke-math'/'3 tools' can be a transient render; the
# daemon may push a state update (re-index, status refresh) immediately
# after the initial display. The sleep absorbs that window.
Sleep 3s
Screenshot "/tmp/shot-mcp-permissions-server-list.png"

# ─── Navigate into the ToolGrid ──────────────────────────────────────
# Re-anchor immediately before Enter: confirm smoke-math is STILL Connected
# after the settle sleep. If the daemon pushed a state update during the
# sleep (periodic re-index / reconnect), these waits will hold until the
# server list is stable again. Without this double-anchor the Enter can
# land on an empty server list (daemon cleared it mid-transition), causing
# the TUI to navigate to a blank or non-existent tool grid.
Wait+Screen@20s /smoke-math/
Wait+Screen@10s /3 tools/
# Sleep 5s: extended settle guard (was 2s). Daemon MCP state updates can
# arrive at any point; 5s provides substantially more headroom under CI
# load where the re-index cycle can be slow.
Sleep 5s
Enter

# ─── Frame 2: ToolGrid ───────────────────────────────────────────────
# All header rows (Server, Audience, Server enabled, Server default) plus
# all tool rows (add, echo, record-tasks) must be visible simultaneously.
# If the #1424 regression reappears, tool rows will overwrite the header.
# Timeout is 30s (was 15s) to give the tool grid more headroom to load
# under CI load. /Server default:/ (with colon) matches the rendered
# "Server default: [Auto]" line, unique to the tool-grid view.
Wait+Screen@30s /record-tasks/
Wait+Screen@10s /Server default:/
# Sleep 500ms: settle guard against a Spectre Console full-refresh repaint
# racing with VHS Screenshot. The Wait+Screen anchors fire on first match,
# but the TUI can immediately redraw (daemon event, animation tick) before
# Screenshot captures. The sleep absorbs that repaint window. See #1471.
Sleep 500ms
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
