# init-wizard.tape — Personal-posture, Ollama, full wizard walkthrough.
#
# This is the regression-catcher: the netclaw/knit interactive-wizard
# bug lived inside this flow. Every interactive prompt in the wizard
# is exercised here, and the post-tape assertion validates the
# produced config against the embedded JSON schema.
#
# Synchronization: every step waits on a stable substring from the
# step's view source (see src/Netclaw.Cli/Tui/Wizard/Steps/*StepView.cs).
# Do NOT introduce literal `Sleep` — tighten the regex if you hit a
# false positive.
#
# Outputs only a debug GIF; tapes are not authored for screenshots.

Output "/tmp/tape-init-wizard.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw init"
Enter

# ─── Step 1: Provider ───────────────────────────────────────────────
Wait+Screen@10s /Choose your LLM provider:/
# Provider list ordering is alphabetical by TypeKey:
#   anthropic, ollama, openai, openai-compatible, openrouter
# Display order matches: Anthropic (default), then Ollama (one Down).
Down
Enter

# Endpoint input (default http://localhost:11434, but in compose ollama is at http://ollama:11434).
Wait+Screen@10s /endpoint:/
# Clear the default value. VHS has no End/Home — push cursor right past
# the existing text (Right N is a no-op when already at end), then
# Backspace enough to clear. Default is "http://localhost:11434" (22 chars).
Right 32
Backspace 32
Type "http://ollama:11434"
Enter

# Wait for connection probe + model discovery. The "Connected! Found N models"
# success message auto-advances to the model list quickly enough that vhs
# can miss it; wait directly for the model selection header instead.
Wait+Screen@45s /Select a model/
# Models are alphabetical: all-minilm first, qwen2 second. Move to qwen2:0.5b.
Down
Enter

# ─── Step 2: Security Posture ────────────────────────────────────────
Wait+Screen@10s /Who will interact with this Netclaw instance/
# Personal is the first / default-highlighted option.
Enter

# ─── Step 3: Channel Picker ──────────────────────────────────────────
Wait+Screen@10s /Which channels would you like to connect/
# 'd' = done without selecting any channels (skip channel setup).
Type "d"

# ─── Step 4: Web Search ─────────────────────────────────────────────
Wait+Screen@10s /Choose your web search provider/
# DuckDuckGo is the first / default option.
Enter

# ─── Step 5: Browser Automation ──────────────────────────────────────
Wait+Screen@10s /Enable browser automation/
# "No" is the first / default option.
Enter

# ─── Step 6: Identity ───────────────────────────────────────────────
Wait+Screen@10s /Agent name:/
Enter

Wait+Screen@10s /Communication style:/
Enter

Wait+Screen@10s /Your name:/
Type "SmokeTester"
Enter

Wait+Screen@10s /Your timezone:/
Enter

Wait+Screen@10s /Projects directory:/
Enter

Wait+Screen@10s /Notification webhook URL/
Enter

# ─── Step 7: External Skills (skipped in smoke) ─────────────────────
# ExternalSkillsStep.IsApplicable returns false when _detectedSources.Count == 0.
# The smoke container has no Claude Code etc., so the wizard goes straight
# from Identity → SkillFeeds with no External Skills prompts. If that ever
# changes (smoke image starts seeding Claude Code), we'll need to handle
# the "External skill directories detected" multi-select + custom dir input.

# ─── Step 8: Skill Feeds ────────────────────────────────────────────
Wait+Screen@10s /Connect to a private skill server/
# Default is "Yes, connect"; we want "No — skip" (Down + Enter).
Down
Enter

# ─── Step 9: Network Exposure ───────────────────────────────────────
Wait+Screen@10s /How will this Netclaw daemon be accessed/
# "Local — loopback only" is the first / default option.
Enter

Wait+Screen@10s /Should this daemon accept inbound webhooks/
# "No" — second option.
Down
Enter

# ─── Step 10: Health Check ──────────────────────────────────────────
Wait+Screen@10s /Press Enter to run health checks/
Enter

# Health checks contact ollama, validate config, run doctor probes.
# On success the wizard auto-navigates to the chat page (the daemon is
# running by this point and the user lands inside the TUI). Wait for
# the chat status bar to show the configured model, then quit the TUI
# with Ctrl+Q to drop back to the shell. The post-tape assertion runs
# `netclaw doctor` against the config the wizard wrote, which is the
# real signal — vhs only proves we got this far.
Wait+Screen@120s /Ready  \|  qwen2:0.5b/
Ctrl+Q

Wait+Screen@10s /TAPE\$/

# Sanity: re-check at the prompt that init reported success.
Type "echo INIT_EXIT=$?"
Enter
Wait+Screen@5s /INIT_EXIT=0/

Type "exit"
Enter
