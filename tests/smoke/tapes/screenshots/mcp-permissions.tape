# mcp-permissions.tape (screenshot) — capture the Loading/no-daemon state of
# `netclaw mcp permissions`.
#
# Capture-only tape: NO post-tape assertion. The screenshots mode in
# run-smoke.sh compares the emitted PNG byte-for-byte against the committed
# baseline at tests/smoke/screenshots/<frame>.approved.png.
#
# Frames captured:
#   mcp-permissions-loading — no-daemon state showing the header panel, the
#                             "Could not reach daemon" error body, and the
#                             footer hint line all visible simultaneously.
#
# Why this frame matters: the render bug fixed in #1424 caused
# ScrollableContainerNode to overwrite the header rows when too many tools
# were present. The no-daemon state exercises BuildHeader + BuildContent +
# BuildFooter layout wiring without a live daemon, producing a fully
# deterministic frame. If the header is pushed off screen or replaced by
# content rows, this baseline diff will catch the regression.
#
# The frame is byte-stable because the page shows only static text: no
# version string, timestamp, spinner, or token counter.
#
# Prepended preamble: tapes/screenshot-preamble.tape (determinism-pinned).

Output "/tmp/tape-shot-mcp-permissions.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw mcp permissions"
Enter

# ─── Frame: Loading / no-daemon ─────────────────────────────────────
# BuildHeader() renders "MCP Permissions" regardless of daemon state.
# Wait for that anchor, then settle for one second before the shot.
Wait+Screen@10s /MCP Permissions/
Sleep 1s
Screenshot "/tmp/shot-mcp-permissions-loading.png"
Sleep 1s

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "exit"
Enter
