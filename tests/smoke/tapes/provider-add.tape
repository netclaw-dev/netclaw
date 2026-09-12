# provider-add.tape — drive the `netclaw provider` TUI through the
# add flow introduced in PR #997, including the new "Name your
# provider" step that lets the user override the generated default
# (`my-openai-compatible`, `my-anthropic`, …) before any credential entry.
#
# Flow shape: when no providers are configured yet, the provider list
# shows every known provider TYPE as a "(not configured)" row plus
# the "+ Add new provider..." sentinel at the bottom. Activating a
# type row goes straight to the AddName step for that type. The
# sentinel only routes through the explicit AddSelectType step and
# is only useful when every type is already configured (and we want
# to add another instance of one).
#
# Synchronization: every step waits on a stable substring from
# src/Netclaw.Cli/Tui/ProviderManagerPage.cs.

Output "/tmp/tape-provider-add.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw provider"
Enter

# List view: every type as unconfigured + the sentinel. Anchor on
# "OpenAI-compatible" rather
# than the sentinel so we know the type rows have rendered.
Wait+Screen@10s /OpenAI-compatible/

# The OpenAI-compatible row is index five in the provider type list.
Down 5
Enter

# ─── Step 1: Name your provider (NEW in #997) ────────────────────────
Wait+Screen@10s /Name your provider/
# Default pre-filled is "my-openai-compatible" (20 chars). Cursor is at end
# (explicit ConsoleKey.End synth in BuildAddNameView). Backspace
# clears all 20 chars; 32 backspaces is safely over.
Backspace 32
Type "smoke-add-provider"
Enter

# ─── Step 2: Endpoint ────────────────────────────────────────────────
Wait+Screen@10s /Endpoint/
# Default is "http://localhost:11434" (22 chars).
Right 32
Backspace 32
Type "__SMOKE_LLM_ENDPOINT__"
Enter

# ─── Step 3: Probe + persisted provider confirmation ─────────────────
# The deterministic provider probe resolves without model loading. The provider persists before
# the success screen renders, so wait directly on the persisted-provider
# confirmation (skips the transient "Validating..." frame).
Wait+Screen@45s /Provider 'smoke-add-provider' added/
Enter

# ─── Back at provider list ───────────────────────────────────────────
# The persisted provider row is the durable post-add proof. Avoid anchoring
# on the status footer: fill-height list changes can legitimately move or
# truncate footer text on small/OS-specific VHS viewports.
Wait+Screen@10s /smoke-add-provider/

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
