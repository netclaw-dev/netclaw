# config-exposure.tape — edit Exposure Mode from netclaw config.
#
# Exposure Mode is a post-install Security & Access leaf, not an init-wizard
# step. This tape exercises the configured route:
#   netclaw config -> Security & Access -> Exposure Mode
# and verifies the reverse-proxy branch writes the existing Daemon config shape.

Output "/tmp/tape-config-exposure.gif"

# ─── Seed minimal installed config ───────────────────────────────────
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "jq -n '{configVersion:1, Daemon:{Port:5299, DisableSelfUpdate:true}}' > $NETCLAW_HOME/config/netclaw.json"
Enter

# ─── Launch config dashboard ─────────────────────────────────────────
Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/

# Root dashboard order: Inference Providers, Models, Channels, Inbound Webhooks,
# Skill Sources, Search, Browser Automation, Telemetry & Alerting, Security & Access.
Down 8
Enter
Wait+Screen@10s /Security & Access/

# Security & Access order: Security Posture, Enabled Features, Audience Profiles,
# Exposure Mode.
Down 3
Enter
Wait+Screen@10s /How will this Netclaw daemon be accessed/

# Select Reverse Proxy (second option).
Down
Enter

# Accept default reverse-proxy bind address.
Wait+Screen@10s /Reverse proxy: bind address/
Enter

# Enter one trusted proxy CIDR.
Wait+Screen@10s /Reverse proxy: trusted proxies/
Type "10.0.0.0/24"
Enter

# Confirm notice and save.
Wait+Screen@10s /Reverse proxy configured/
Wait+Screen@5s /http:\/\/0\.0\.0\.0:[0-9]+/
Enter
Wait+Screen@10s /Reverse Proxy exposure mode saved/

# Saved-state back behavior: Esc returns to the mode list before parent page.
Escape
Wait+Screen@10s /How will this Netclaw daemon be accessed/
Escape
Wait+Screen@10s /Security & Access/
Ctrl+Q

Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_EXPOSURE_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_EXPOSURE_EXIT=0/

Type "exit"
Enter
