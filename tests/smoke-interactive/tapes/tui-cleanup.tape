# tui-cleanup.tape — regression coverage for the bug class fixed in
# PR #997, commit 21450c3d "fix(cli): buffer update-available notice;
# emit after mode handler exits".
#
# The underlying bug: any background task that writes to stderr/
# stdout while a Termina TUI owns the alt screen corrupts the layout
# (missing panel borders, duplicate rows under arrow navigation).
# `BackgroundUpdateCheckAsync` was the most visible offender — it
# fires on every CLI invocation and used to write its "Update
# available" / "warn: background update check failed" lines straight
# to stderr.
#
# The fix moves those writes into a buffered `_pendingNotice` field
# emitted by `EmitPendingNoticeIfReady()` AFTER the mode handler
# returns (TUI torn down). This tape exercises the conditions that
# used to trigger the corruption:
#   1) Launch the provider TUI (which Aaron was navigating when he
#      reproduced the bug on #997).
#   2) Wait long enough for BackgroundUpdateCheckAsync to plausibly
#      fire (network probe to the update endpoint).
#   3) Navigate with arrows; if the background task wrote to stderr
#      mid-render, the list rendering would drift and our screen
#      anchors would no longer match.
#   4) Exit the TUI and verify we land on a clean shell prompt with
#      no stray output between the TUI tear-down and the prompt.
#
# Note: the test cannot force `BackgroundUpdateCheckAsync` to find an
# update or emit a notice — that depends on the network response from
# the update endpoint. What it CAN do is keep the TUI up long enough
# that any background stderr write would manifest as visible
# corruption. The fix is observable as the absence of that
# corruption.

Output "/tmp/tape-tui-cleanup.gif"

# Seed a couple of providers so the list has multiple rows to
# arrow-navigate through — multiple rows is what triggered the
# "duplicate rows on arrow navigation" symptom in Aaron's report.
Type "netclaw provider add seed-a ollama --endpoint http://ollama:11434"
Enter
Wait+Screen@10s /Added provider 'seed-a'|TAPE\$/

Type "netclaw provider add seed-b ollama --endpoint http://ollama:11434"
Enter
Wait+Screen@10s /Added provider 'seed-b'|TAPE\$/

# ─── Launch provider TUI ────────────────────────────────────────────
Type "netclaw provider"
Enter

# Both seeded entries present.
Wait+Screen@10s /seed-a/
Wait+Screen@10s /seed-b/

# Arrow-navigate up and down through the list. Each step has its own
# anchor on the row content — if stderr writes corrupted the alt
# screen, the row text wouldn't reappear unchanged.
Down
Wait+Screen@5s /seed-b/
Down
Wait+Screen@5s /\+ Add new provider/
Up
Wait+Screen@5s /seed-b/
Up
Wait+Screen@5s /seed-a/

# ─── Exit and verify clean shell post-TUI ───────────────────────────
Ctrl+Q

# Anchor on the prompt. If the alt screen wasn't torn down cleanly,
# the prompt either wouldn't render or would be visually merged with
# TUI artefacts. The TAPE$ regex must match for this tape to pass.
Wait+Screen@10s /TAPE\$/

# Run a known echo and verify the output round-trips cleanly. A stray
# update-notice line emitted at the wrong time would interleave with
# this output; a buffered notice (the fix) renders BEFORE the prompt
# and leaves the round-trip uncorrupted.
Type "echo TUI_EXIT_SENTINEL"
Enter
Wait+Screen@5s /TUI_EXIT_SENTINEL/

# Also re-anchor on the prompt to ensure the line was processed and
# returned us to a working shell — not stuck in some half-state.
Type "echo POST_SENTINEL_$?"
Enter
Wait+Screen@5s /POST_SENTINEL_0/

Type "exit"
Enter
