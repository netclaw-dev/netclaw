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

Output /tmp/tape-init-wizard.gif

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw init"
Enter

# ─── Step 1: Provider ───────────────────────────────────────────────
Wait+Screen /Choose your LLM provider:/
# Anthropic is highlighted by default; move down to Ollama.
# Provider list order is roughly: Anthropic, OpenAI, Ollama, ... — Ollama is index 2.
Down
Down
Enter

# Endpoint input (default http://localhost:11434, but in compose ollama is at http://ollama:11434).
Wait+Screen /endpoint:/
# Replace default with the compose-internal hostname.
Ctrl+U
Type "http://ollama:11434"
Enter

# Wait for connection probe + model discovery.
Wait+Screen /Connected! Found/
Wait+Screen /Select a model/
# qwen2:0.5b is the only model the smoke stack pulls; first item.
Enter

# ─── Step 2: Security Posture ────────────────────────────────────────
Wait+Screen /Who will interact with this Netclaw instance/
# Personal is the first / default-highlighted option.
Enter

# ─── Step 3: Channel Picker ──────────────────────────────────────────
Wait+Screen /Which channels would you like to connect/
# 'd' = done without selecting any channels (skip channel setup).
Type "d"

# ─── Step 4: Web Search ─────────────────────────────────────────────
Wait+Screen /Choose your web search provider/
# DuckDuckGo is the first / default option.
Enter

# ─── Step 5: Browser Automation ──────────────────────────────────────
Wait+Screen /Enable browser automation/
# "No" is the first / default option.
Enter

# ─── Step 6: Identity ───────────────────────────────────────────────
Wait+Screen /Agent name:/
Enter

Wait+Screen /Communication style:/
Enter

Wait+Screen /Your name:/
Type "SmokeTester"
Enter

Wait+Screen /Your timezone:/
Enter

Wait+Screen /Projects directory:/
Enter

Wait+Screen /Notification webhook URL/
Enter

# ─── Step 7: External Skills ─────────────────────────────────────────
# This step is conditional on detected external skills. In the smoke
# container there's no Claude Code, so this prompt may be skipped
# entirely — the tape treats both branches as acceptable by waiting
# on whichever appears next.
#
# Using Wait+Screen with alternation: if the external-skills picker
# shows up, accept it; otherwise we'll already be past it.
Wait+Screen /Add a custom skill directory|External skill directories detected|Configured feeds:|Connect to a private skill server/

# If we landed on the "External skill directories detected" page,
# accept and move on. The "Add a custom skill directory" prompt
# follows; press Enter to skip.
# (If we landed elsewhere already, this Enter is a no-op for that view.)
Enter

# Custom skill directory — leave blank.
Wait+Screen /Add a custom skill directory|Connect to a private skill server/
Enter

# ─── Step 8: Skill Feeds ────────────────────────────────────────────
Wait+Screen /Connect to a private skill server/
# "No — skip" is the second option.
Down
Enter

# ─── Step 9: Network Exposure ───────────────────────────────────────
Wait+Screen /How will this Netclaw daemon be accessed/
# "Local — loopback only" is the first / default option.
Enter

Wait+Screen /Should this daemon accept inbound webhooks/
# "No" — second option.
Down
Enter

# ─── Step 10: Health Check ──────────────────────────────────────────
Wait+Screen /Press Enter to run health checks/
Enter

# Health checks contact ollama, validate config, run doctor probes.
# Wait for either a clear success indicator or the wizard to finish
# and return to the shell prompt.
Wait+Screen /TAPE\$ $/

# Sanity: re-check at the prompt that init reported success.
Type "echo INIT_EXIT=$?"
Enter
Wait+Screen /INIT_EXIT=0/

Type "exit"
Enter
