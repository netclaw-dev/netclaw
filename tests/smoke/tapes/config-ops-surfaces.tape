# config-ops-surfaces.tape - exercise Task 1.6 config areas.
#
# Covers:
#   - Telemetry & Alerting telemetry + outbound webhook save path
#
# The Skill Sources "add a local folder" save path moved to config-skill-picker.tape:
# that screen is now a Termina FilePickerNode, whose terminal scroll region the vhs
# emulator does not reset on exit, so a second netclaw launch in the same recording
# never renders. Each directory-picker flow therefore gets its own single-launch tape.
#
# Post-tape assertion validates the persisted config semantically.

Output "/tmp/tape-config-ops-surfaces.gif"

# Seed minimal installed config.
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "jq -n '{configVersion:1}' > $NETCLAW_HOME/config/netclaw.json"
Enter

# Telemetry & Alerting: enable OTLP, set the endpoint, then add an outbound
# Slack webhook through the multi-webhook list editor.
Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/
Down 7
Enter
Wait+Screen@10s /Telemetry & Alerting/
Wait+Screen@5s /Delivery-policy tuning/

# Row 0: toggle telemetry on (autosaves).
Space
Wait+Screen@10s /Telemetry enabled state saved/

# Row 1: edit and save the OTLP endpoint.
Down
Type "http://127.0.0.1:4318"
Enter
Wait+Screen@10s /Telemetry & Alerting settings saved/

# Row 2 ("+ Add webhook"): open the add form, enter a Slack URL, and save.
Down
Wait+Screen@5s /Add webhook/
Enter
Wait+Screen@10s /Add outbound webhook/
Down
Type "https://hooks.slack.com/services/T000/B000/SECRET"
Wait+Screen@5s /Slack \(auto-detected/
Enter
Wait+Screen@10s /Saved/
Wait+Screen@5s /Outbound Webhooks/
Ctrl+Q
# Alt-screen restore guard (CSI ?1049l): give vhs a beat to catch the restored main buffer.
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "OTLP_ENDPOINT=http://127.0.0.1:4318 WEBHOOK_FORMAT=Slack jq -e '.Telemetry.Enabled == true and .Telemetry.Otlp.Endpoint == env.OTLP_ENDPOINT and .Notifications.Webhooks[0].Format == env.WEBHOOK_FORMAT' $NETCLAW_HOME/config/netclaw.json"
Enter
Wait+Screen@5s /true/

Type "echo CONFIG_OPS_SURFACES_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_OPS_SURFACES_EXIT=0/

Type "exit"
Enter
