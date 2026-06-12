# config-features.tape — edit Enabled Features from netclaw config.
#
# Exercises:
#   netclaw config -> Security & Access -> Enabled Features
# and verifies feature toggle persistence through the shared config editor save path.

Output "/tmp/tape-config-features.gif"

# ─── Seed minimal installed config ───────────────────────────────────
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "posture=Team; backend=duckduckgo; jq -n --arg posture $posture --arg backend $backend '{configVersion:1,Security:{DeploymentPosture:$posture},Memory:{Enabled:true},Search:{Enabled:false,Backend:$backend},SkillSync:{Enabled:true},Scheduling:{Enabled:false},SubAgents:{Enabled:true},Webhooks:{Enabled:false}}' > $NETCLAW_HOME/config/netclaw.json"
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
Down
Enter
Wait+Screen@10s /Toggle global runtime features/
Wait+Screen@5s /\[✓\] Memory/
Wait+Screen@5s /\[ \] Search/

# Enable Search; inline toggles save immediately.
Down
Space
Wait+Screen@5s /\[✓\] Search/
Escape
Wait+Screen@10s /Security & Access/
Ctrl+Q

# VHS's screen scraper needs a beat to pick up the restored main buffer after the
# TUI tears down the alternate screen (CSI ?1049l); without it /TAPE\$/ matches the
# stale pre-restore buffer and the next keystroke is eaten by the exiting TUI.
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_FEATURES_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_FEATURES_EXIT=0/

Type "exit"
Enter
