# mcp-permissions.tape (screenshot) — capture `netclaw mcp permissions` with
# the smoke MCP server (smoke-math) connected and its tools indexed.
#
# Capture-only tape: NO post-tape assertion. The screenshots mode in
# run-smoke.sh compares each emitted PNG byte-for-byte against the committed
# baseline at tests/smoke/screenshots/<frame>.approved.png.
#
# Frames captured:
#   mcp-permissions-server-list — ServerList state: smoke-math appears as
#                                 "Connected, 4 tools" below the header.
#   mcp-permissions-tool-grid  — ToolGrid state: header rows (Audience,
#                                Server enabled, Server default) and all
#                                four tool rows (add, echo, record-tasks, process-info)
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

# Register the deterministic smoke MCP server (add, echo, record-tasks, process-info).
# --grant-all means every tool is auto-approved for all audiences.
Type "netclaw mcp add --transport stdio --grant-all smoke-math -- __NETCLAW_SMOKE_MCP_SERVER__"
Enter
Wait+Screen@10s /TAPE\$/

# Start the daemon (detaches immediately; MCP handshake follows async).
Type "netclaw daemon start"
Enter
Wait+Screen@20s /TAPE\$/

# Poll until the daemon reports smoke-math as fully connected with 4 tools.
# Replaces the former fixed Sleep 5s: we wait for the actual CLI signal
# rather than guessing. `netclaw mcp list` queries the daemon's cached
# server state; "connected (4 tools)" means the stdio handshake and tool
# indexing are complete. The Wait+Screen@60s is a safety net for a hard hang.
Type "until netclaw mcp list 2>/dev/null | grep -q 'connected (4 tools)'; do sleep 2; done"
Enter
Wait+Screen@60s /TAPE\$/
Show

# ─── Launch the TUI ──────────────────────────────────────────────────
Type "netclaw mcp permissions"
Enter

# ─── Frame 1: ServerList ─────────────────────────────────────────────
# smoke-math should appear as "Connected, 4 tools". Do NOT anchor on
# "smoke-math" or "4 tools" alone — both already sit in the shell
# scrollback before the TUI ever paints:
#   - "smoke-math" appears in the setup output above ("Added MCP server
#     'smoke-math' (stdio)" / "...adjust approvals for 'smoke-math'.").
#   - "4 tools" appears in this tape's own typed readiness-loop command,
#     still visible on screen: `... grep -q 'connected (4 tools)' ...`.
# Immediately after Enter, before the TUI switches to the alternate
# screen buffer, Wait+Screen can match that leftover transcript text and
# return instantly, capturing the raw shell instead of the rendered TUI
# (README.md rule 5: anchor on the *next view*, not text that predates
# it). Anchor on TUI-only chrome instead: "MCP Permissions" is the page
# title from McpToolPermissionsPage.BuildHeader (proves the alt screen
# painted), and "Connected, 4 tools" is the exact rendered server-row
# text from McpToolPermissionsPage.BuildServerList — "{Name}  ({Status},
# {ToolCount} tools)" with capital-C Status and a comma, which the
# transcript's lowercase, comma-less "connected (4 tools)" never
# produces. Neither anchor occurs anywhere in the shell transcript.
Wait+Screen@15s /MCP Permissions/
Wait+Screen@5s /Connected, 4 tools/
# Sleep 3s: let the server-list frame fully settle before capturing. The
# first match of the anchors above can be a transient render; the
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
Wait+Screen@10s /4 tools/
# Sleep 5s: extended settle guard (was 2s). Daemon MCP state updates can
# arrive at any point; 5s provides substantially more headroom under CI
# load where the re-index cycle can be slow.
Sleep 5s
Enter

# ─── Frame 2: ToolGrid ───────────────────────────────────────────────
# All header rows (Server, Audience, Server enabled, Server default) plus
# all tool rows (add, echo, record-tasks, process-info) must be visible simultaneously.
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

# ─── Exercise staged save with the default Enter action ─────────────
# Change the audience, verify the dirty state appears, open confirmation,
# and accept the visible default with Enter. This is the native regression
# path for the confirmation flow that previously accepted only Y/N/Esc.
Down
Space
Wait+Screen@5s /unsaved/
Enter
Wait+Screen@5s /Enter\/Y.*Save/
Enter
Wait+Screen@10s /Connected, 4 tools/


# ─── Exit TUI ────────────────────────────────────────────────────────
# Sleep 2s BEFORE Ctrl+Q — settle guard AFTER the Screenshot. VHS's
# `Screenshot` does not write the PNG synchronously at this line: it hands the
# request to the render loop, which flushes the *current* framebuffer on a
# later tick. Ctrl+Q tears the TUI off the alternate screen buffer and restores
# the shell transcript on the main buffer. With no gap here, that teardown can
# win the race and VHS captures the restored transcript instead of the tool
# grid — the exact failure in run 28682629818 (frame 1 passed on the alt
# screen, frame 2 captured the post-Ctrl+Q main buffer). This is the ONLY
# Screenshot in the tape immediately followed by a screen-destroying key, which
# is why only this frame regressed. The sleep holds the tool grid on screen
# until the deferred capture lands. Proven with a minimal `less` alt-screen
# repro: Screenshot→immediate-q captures the main buffer; Screenshot→Sleep→q
# captures the alt screen. Do NOT remove without re-checking that race.
Sleep 2s
Ctrl+Q
Wait+Screen@10s /TAPE\$/

# Stop the daemon so the tape-level cleanup does not race the next tape.
Type "netclaw daemon stop"
Enter
Wait+Screen@10s /TAPE\$/

Type "exit"
Enter
