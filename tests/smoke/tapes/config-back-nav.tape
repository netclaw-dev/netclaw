# config-back-nav.tape — regression guard for the embedded-manager back-out bug.
#
# `netclaw config` → Inference Providers → Esc must return to the dashboard
# (NOT quit the whole config app). Same for Models. Before the fix, the routed
# Provider/Model managers called Shutdown() alongside the queued /config
# navigation, which cancelled the run loop before the nav was processed — so
# backing out of either manager exited the entire config TUI instead of
# returning to the Settings Areas dashboard. The "Settings Areas" anchors after
# each Esc are the regression detectors: if the app quit, they would time out
# against the restored shell prompt.
#
# Nav-only tape: no config is mutated, so there is no semantic post-assertion
# (config-back-nav.sh is a no-op; the Wait+Screen anchors are the test).

Output "/tmp/tape-config-back-nav.gif"

# ─── Seed minimal installed config so the dashboard renders ─────────────────
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "jq -n '{configVersion:1}' > $NETCLAW_HOME/config/netclaw.json"
Enter

# ─── Inference Providers → Esc → back to dashboard ──────────────────────────
Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/
# "Inference Providers" is the first row, selected by default.
Enter
Wait+Screen@10s /Provider Manager/
Escape
# Regression: must land back on the dashboard, not the shell prompt.
Wait+Screen@10s /Settings Areas/

# ─── Models → Esc → back to dashboard ───────────────────────────────────────
Down
Enter
Wait+Screen@10s /Model Manager/
Escape
Wait+Screen@10s /Settings Areas/

# ─── Exit cleanly ───────────────────────────────────────────────────────────
Ctrl+Q
# Alt-screen restore guard (CSI ?1049l): give vhs a beat to catch the restored main buffer.
Sleep 1s
Wait+Screen@10s /TAPE\$/
