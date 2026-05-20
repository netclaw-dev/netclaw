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
# Serving URL line is rendered as "Daemon will listen on:    http://0.0.0.0:5199"
Wait+Screen@5s /http:\/\/0\.0\.0\.0:5199/
Enter

# Sub-step 4: webhook toggle. "No" = second option.
Wait+Screen@10s /Should this daemon accept inbound webhooks/
Down
Enter

# ─── Step 10: Health Check ──────────────────────────────────────────
Wait+Screen@10s /Press Enter to run health checks/
Enter

# Reverse-proxy mode binds to a non-loopback address; the daemon will refuse
# to start cleanly inside the smoke env (it tries to listen on 0.0.0.0:5199
# which may not be reachable / may already be bound). That's fine — the
# tape's job is to drive the wizard through the new sub-steps and produce
# a netclaw.json; the post-tape assertion is the real signal.
#
# The wizard may either:
#   (a) reach the chat page after the daemon starts (if 0.0.0.0:5199 is
#       bindable in the smoke env), or
#   (b) hang on the health-check screen if daemon startup fails.
#
# Wait for whichever marker appears first within a generous window, then quit.
Wait+Screen@120s /(Ready  \|  qwen2:0\.5b|Health checks)/
Ctrl+Q

Wait+Screen@10s /TAPE\$/

# Sanity: re-check at the prompt that init reported success.
Type "echo INIT_EXIT=$?"
Enter
Wait+Screen@5s /INIT_EXIT=/

Type "exit"
Enter
