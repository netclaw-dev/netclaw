# init-wizard.tape — Personal-posture, OpenAI-compatible bootstrap walkthrough.
#
# This is the regression-catcher for the simplified `netclaw init` flow
# (simplify-netclaw-init): Provider → Identity → Security Posture →
# (Enabled Features, skipped for Personal) → Health Check. Channels, Search,
# Browser Automation, and Skill Sources moved to `netclaw config` and are no
# longer part of bootstrap. The post-tape assertion validates the produced
# config against the embedded JSON schema.
#
# Synchronization: every step waits on a stable substring from the step's
# view source (see src/Netclaw.Cli/Tui/Wizard/Steps/*StepView.cs). Do NOT
# introduce literal `Sleep` — tighten the regex if you hit a false positive.
#
# Outputs only a debug GIF; tapes are not authored for screenshots.

Output "/tmp/tape-init-wizard.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw init"
Enter

# ─── Step 1 of 4: Provider ──────────────────────────────────────────
Wait+Screen@10s /Choose your LLM provider:/
# The OpenAI-compatible row is index five in the provider type list.
Down 5
Enter

# Endpoint input.
Wait+Screen@10s /endpoint:/
# Clear the default value. VHS has no End/Home — push cursor right past
# the existing text, then Backspace enough to clear (default is 22 chars).
Right 32
Backspace 32
Type "__SMOKE_LLM_ENDPOINT__"
Enter

# Wait for connection probe + model discovery. The success message
# auto-advances to the model list quickly enough that vhs can miss it;
# wait directly for the model selection header instead.
Wait+Screen@45s /Select a model/
# The deterministic server returns the required smoke model first.
Enter

# ─── Step 2 of 4: Identity ──────────────────────────────────────────
# Identity now immediately follows Provider in the bootstrap flow.
Wait+Screen@10s /Agent name:/
Enter

Wait+Screen@10s /Communication style:/
Enter

Wait+Screen@10s /Your name:/
Type "SmokeTester"
Enter

Wait+Screen@10s /Your timezone:/
Enter

# Identity ends at timezone — workspaces directory and notification webhooks are
# post-install settings owned by `netclaw config`, no longer collected in init.

# ─── Step 3 of 4: Security Posture ──────────────────────────────────
Wait+Screen@10s /Who will interact with this Netclaw instance/
# Personal is the first / default-highlighted option. Personal skips the
# Enabled Features step and goes straight to the health check.
Enter

# ─── Step 4 of 4: Health Check ──────────────────────────────────────
# Health checks start automatically when the final step opens. No extra Enter.
# They contact the smoke provider, write config, validate, and start the daemon.
# On a clean bootstrap, once validation passes the wizard launches chat
# automatically — there is no Enter gate / post-flight summary to confirm.
# Anchor on a health item that renders during the run (it stays on screen
# through the daemon-start poll), then wait for the chat screen to take over.
Wait+Screen@60s /Configuration written/
Wait+Screen@60s /Ready  \|  netclaw-smoke-tool-model/
Ctrl+Q
