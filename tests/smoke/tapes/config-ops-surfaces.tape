# config-ops-surfaces.tape - exercise Task 1.6 config areas.
#
# Covers:
#   - Skill Sources external directory save path
#   - Telemetry & Alerting telemetry + outbound webhook save path
#
# Post-tape assertion validates the persisted config semantically.

Output "/tmp/tape-config-ops-surfaces.gif"

# Seed minimal installed config and local skill directory.
Type "mkdir -p $NETCLAW_HOME/config /tmp/netclaw-smoke-config-ops-skills"
Enter
Type "jq -n '{configVersion:1}' > $NETCLAW_HOME/config/netclaw.json"
Enter

# Skill Sources: save an existing external skill directory.
Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/
Down 4
Enter
Wait+Screen@10s /Skill Sources/
Wait+Screen@5s /Places Netclaw loads skills from/
Enter
Wait+Screen@10s /Add a local skill folder/
Type "/tmp/netclaw-smoke-config-ops-skills"
Enter
Wait+Screen@10s /Allow symlinks inside this folder/
Enter
Wait+Screen@10s /Review local folder source/
Enter
Wait+Screen@10s /Added local skill folder/
Escape
Wait+Screen@10s /Local folders/
Escape
Wait+Screen@10s /Settings Areas/
Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "SKILL_DIR=/tmp/netclaw-smoke-config-ops-skills jq -e '.ExternalSkills.Sources[0].Path == env.SKILL_DIR' $NETCLAW_HOME/config/netclaw.json"
Enter
Wait+Screen@5s /true/

# Telemetry & Alerting: enable OTLP and configure an outbound Slack webhook.
Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/
Down 7
Enter
Wait+Screen@10s /Telemetry & Alerting/
Wait+Screen@5s /Delivery-policy tuning/
Space
Down
Type "http://127.0.0.1:4318"
Down
Type "https://hooks.slack.com/services/T000/B000/SECRET"
Enter
Wait+Screen@10s /Telemetry & Alerting settings saved/
Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "OTLP_ENDPOINT=http://127.0.0.1:4318 WEBHOOK_FORMAT=Slack jq -e '.Telemetry.Enabled == true and .Telemetry.Otlp.Endpoint == env.OTLP_ENDPOINT and .Notifications.Webhooks[0].Format == env.WEBHOOK_FORMAT' $NETCLAW_HOME/config/netclaw.json"
Enter
Wait+Screen@5s /true/

Type "echo CONFIG_OPS_SURFACES_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_OPS_SURFACES_EXIT=0/

Type "exit"
Enter
