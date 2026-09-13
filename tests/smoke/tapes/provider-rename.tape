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
Type "netclaw provider add seed-provider openai-compatible --endpoint __SMOKE_LLM_ENDPOINT__"
Enter
Wait+Screen@10s /Added provider 'seed-provider'|TAPE\$/

# ─── Launch provider TUI ─────────────────────────────────────────────
Type "netclaw provider"
Enter

# Wait for the probe to complete. ActivateSelectedProvider() is a no-op
# while health == Probing, so we must wait for the ✓ checkmark (healthy)
# before pressing Enter. The list renders seed-provider immediately with a
# spinner, but Enter would be silently ignored until the probe settles.
Wait+Screen@30s /✓ seed-provider/

# The seeded provider is at index 0 and highlighted by default —
# Enter activates the Details view.
Enter

# ─── Details view ────────────────────────────────────────────────────
# Details view shows "Provider: seed-provider" along with [N] Rename
# in the key hints. Anchor on the rename hint so we know we landed.
Wait+Screen@10s /Provider: seed-provider/

# Press N to start rename.
Type "N"

# ─── Rename input ────────────────────────────────────────────────────
Wait+Screen@10s /Rename 'seed-provider'/

# Cursor sits at the end of the pre-filled name (PR #997 moves it
# there explicitly — see commit 1493ddd1). 'seed-provider' is 13 chars;
# 16 backspaces is safely over.
Backspace 16
Type "renamed-provider"
Enter

# ─── Success status ──────────────────────────────────────────────────
# StatusMessage on the list view: "Renamed 'seed-provider' to
# 'renamed-provider'. Restart daemon for changes to take effect."
# The status message and new name both appear on the list view after
# the rename, so one anchor covering the new name is sufficient.
Wait+Screen@10s /renamed-provider/

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
