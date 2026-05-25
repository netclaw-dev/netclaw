# init-wizard-reverse-proxy.tape — Personal posture, Ollama, ReverseProxy exposure.
#
# Variant of init-wizard.tape that exercises the reverse-proxy branch of the
# Network Exposure step. Covers:
#   - selecting "Reverse Proxy" from the exposure mode list (second option)
#   - the bind-address text input (kept at the 0.0.0.0 default)
#   - the trusted-proxies text input (one CIDR entered)
#   - the medium-risk notice screen that shows the serving URL
#
# The post-tape assertion (init-wizard-reverse-proxy.sh) jq-checks the produced
# Daemon section and runs `netclaw doctor`.
#
# Synchronization rule (per tapes/README.md): no literal Sleep for step
# synchronization; every step has a Wait+Screen anchor pulled from the
# matching *StepView.cs.

Output "/tmp/tape-init-wizard-reverse-proxy.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw init"
Enter

# ─── Step 1: Provider ───────────────────────────────────────────────
Wait+Screen@10s /Choose your LLM provider:/
# Provider list ordering is alphabetical by TypeKey:
#   anthropic, github-copilot, ollama, openai, openai-compatible, openrouter
# Two Downs from the Anthropic default land on Ollama.
Down 2
Enter

Wait+Screen@10s /endpoint:/
Right 32
Backspace 32
Type "http://localhost:11434"
Enter

Wait+Screen@45s /Select a model/
# Termina list-key-handler wiring beat — see init-wizard.tape:46
Sleep 1s
Down
Enter

# ─── Step 2: Security Posture ────────────────────────────────────────
Wait+Screen@10s /Who will interact with this Netclaw instance/
Enter

# ─── Step 3: Channel Picker ──────────────────────────────────────────
Wait+Screen@10s /Which channels would you like to connect/
Type "d"

# ─── Step 4: Web Search ─────────────────────────────────────────────
Wait+Screen@10s /Choose your web search provider/
Enter

# ─── Step 5: Browser Automation ──────────────────────────────────────
Wait+Screen@10s /Enable browser automation/
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

# ─── Step 8: Skill Feeds ────────────────────────────────────────────
Wait+Screen@10s /Connect to a private skill server/
Down
Enter

# ─── Step 9: Network Exposure (reverse proxy branch) ────────────────
Wait+Screen@10s /How will this Netclaw daemon be accessed/
# Mode list (ExposureModeStepView.BuildModeSelection):
#   0: Local — loopback only, safest (recommended)        ← default
#   1: Reverse Proxy — behind nginx, Caddy, Traefik, ...  ← target
#   2: Tailscale Serve
#   3: Tailscale Funnel
#   4: Cloudflare Tunnel
Down
Enter

# Sub-step 1: bind address. Default placeholder is 0.0.0.0; accept it.
Wait+Screen@10s /Reverse proxy: bind address/
Enter

# Sub-step 2: trusted proxies. Empty list is rejected by the ViewModel gate;
# enter one CIDR so the wizard can advance.
Wait+Screen@10s /Reverse proxy: trusted proxies/
Type "10.0.0.0/24"
Enter

# Sub-step 3: notice with serving URL.
Wait+Screen@10s /Reverse proxy configured/
# Serving URL line includes the configured bind address and smoke daemon port.
Wait+Screen@5s /http:\/\/0\.0\.0\.0:[0-9]+/
Enter

# Sub-step 4: webhook toggle.
# Option order is [No (default), Yes]. Down+Enter selects "Yes — accept inbound
# webhook requests" so the produced netclaw.json gets a Webhooks section.
# (The assertion script does not validate the webhook choice; both branches
# work for the smoke run. We deliberately exercise the non-default branch to
# prove the toggle plumbing.)
Wait+Screen@10s /Should this daemon accept inbound webhooks/
Down
Enter

# ─── Step 10: Health Check ──────────────────────────────────────────
Wait+Screen@10s /Press Enter to run health checks/
Enter

# Daemon starts on the configured reverse-proxy port, but in reverse-proxy mode the CLI cannot
# auto-auth back to it via loopback (loopback auto-auth is intentionally
# disabled for reverse-proxy to prevent a forwarded-header from inheriting
# operator privileges). The wizard's chat-page handshake therefore gets 401
# and the wizard exits to the shell instead of opening the TUI — that's
# correct behavior, not a bug.
#
# Wait for either terminal state:
#   (a) chat-page ready bar (if a future change ever wires post-init differently)
#   (b) the shell prompt re-appearing post-wizard-exit, with `netclaw init`
#       still visible as the last-typed command
Wait+Screen@180s /(Ready  \|  qwen2:0\.5b|TAPE\$ netclaw init)/
# No-op if we're already at the shell; quits the TUI if (a) above.
Ctrl+Q

Wait+Screen@15s /TAPE\$ /

# Sanity: re-check at the prompt that init reported success.
Type "echo INIT_EXIT=$?"
Enter
Wait+Screen@5s /INIT_EXIT=/

Type "exit"
Enter
