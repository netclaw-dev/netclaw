# provider-rename.tape — drive the `netclaw provider` TUI rename
# action introduced in PR #997 ([N] from the Details view).
#
# The tape seeds an existing provider via the non-interactive CLI
# (`netclaw provider add ...`) before launching the TUI so we are
# testing the rename flow itself, not the add flow. The expectation
# is that after the rename:
#   * the configured key in netclaw.json swaps from the old name to
#     the new one
#   * any Models.{Main,Fallback,Compaction}.Provider references are
#     cascaded (or, if absent, the rename succeeds without warning)
#
# See assertions/provider-rename.sh for the post-tape checks.

Output "/tmp/tape-provider-rename.gif"

# ─── Seed an existing provider ───────────────────────────────────────
# Non-interactive CLI add — bypasses the TUI entirely and writes
# straight to netclaw.json.
Type "netclaw provider add seed-ollama ollama --endpoint http://localhost:11434"
Enter
Wait+Screen@10s /Added provider 'seed-ollama'|TAPE\$/

# ─── Launch provider TUI ─────────────────────────────────────────────
Type "netclaw provider"
Enter

# Wait for the probe to complete. ActivateSelectedProvider() is a no-op
# while health == Probing, so we must wait for the ✓ checkmark (healthy)
# before pressing Enter. The list renders seed-ollama immediately with a
# spinner, but Enter would be silently ignored until the probe settles.
# 30s covers slow CI; Ollama is pre-loaded so it's typically < 5s.
Wait+Screen@30s /✓ seed-ollama/
Sleep 300ms

# The seeded provider is at index 0 and highlighted by default —
# Enter activates the Details view.
Enter

# ─── Details view ────────────────────────────────────────────────────
# Details view shows "Provider: seed-ollama" along with [N] Rename
# in the key hints. Anchor on the rename hint so we know we landed.
Wait+Screen@10s /Provider: seed-ollama/
Sleep 300ms

# Press N to start rename.
Type "N"

# ─── Rename input ────────────────────────────────────────────────────
Wait+Screen@10s /Rename 'seed-ollama'/
Sleep 300ms

# Cursor sits at the end of the pre-filled name (PR #997 moves it
# there explicitly — see commit 1493ddd1). 'seed-ollama' is 11 chars;
# 16 backspaces is safely over.
Backspace 16
Type "renamed-ollama"
Enter

# ─── Success status ──────────────────────────────────────────────────
# StatusMessage on the list view: "Renamed 'seed-ollama' to
# 'renamed-ollama'. Restart daemon for changes to take effect."
# The status message and new name both appear on the list view after
# the rename, so one anchor covering the new name is sufficient.
Wait+Screen@10s /renamed-ollama/
Sleep 300ms

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q

# VHS's screen scraper needs a beat to pick up the restored main buffer
# after the TUI tears down the alternate screen (CSI ?1049l).
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo PROVIDER_RENAME_EXIT=$?"
Enter
Wait+Screen@5s /PROVIDER_RENAME_EXIT=0/

Type "exit"
Enter
