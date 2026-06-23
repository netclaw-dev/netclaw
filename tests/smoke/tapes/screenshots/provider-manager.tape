# provider-manager.tape (screenshot) — drive the `netclaw provider` TUI and
# capture the empty provider list as a PNG.
#
# Capture-only tape: NO post-tape assertion. The screenshots mode in
# run-smoke.sh compares the emitted PNG byte-for-byte against the committed
# baseline at tests/smoke/screenshots/<frame>.approved.png.
#
# Frames captured:
#   shot-provider-manager-empty — the provider list with no providers
#                                 configured (every type as "(not
#                                 configured)" + "+ Add new provider...")
#
# The "Name your provider" add step is deliberately NOT screenshotted: it
# is a text-input step, and Termina blinks the input caret on its own
# timer — the app-drawn caret cell is not byte-stable across runs.
#
# Navigation + anchors mirror tapes/provider-add.tape; see that file and
# src/Netclaw.Cli/Tui/ProviderManagerPage.cs for the synchronization
# rationale. Wait+Screen anchors on the list content, then Sleep 1s lets
# the full TUI frame flush before the screenshot is captured.
#
# Prepended preamble: tapes/screenshot-preamble.tape (determinism-pinned).

Output "/tmp/tape-shot-provider-manager.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw provider"
Enter

# ─── Frame 1: Empty provider list ────────────────────────────────────
# With no providers configured the list shows every known type as a
# "(not configured)" row plus the "+ Add new provider..." sentinel.
# Anchor on "Ollama" (a guaranteed type row) so we know the list rendered.
# Sleep lets VHS flush the full frame; the SECOND Wait+Screen is an anti-blank
# re-assert immediately before capture — if a Termina startup full-refresh
# blanked the screen during the Sleep, this blocks until the list repaints.
# (Defence in depth: run-smoke.sh also re-runs the tape if a blank slips through.)
Wait+Screen@10s /Ollama/
Sleep 1s
Wait+Screen@5s /Ollama/
Screenshot "/tmp/shot-provider-manager-empty.png"

# Frame captured; quit the TUI back to the shell.
Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "exit"
Enter
