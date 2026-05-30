# config-audience.tape - edit Audience Profiles from netclaw config.
#
# Exercises:
#   netclaw config -> Security & Access -> Audience Profiles -> Team
# and verifies curated per-audience tool toggles persist without exposing raw MCP editing.

Output "/tmp/tape-config-audience.gif"

# Seed minimal Team config; Audience Profiles should resolve from the system posture.
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "posture=Team; jq -n --arg posture $posture '{configVersion:1,Security:{DeploymentPosture:$posture}}' > $NETCLAW_HOME/config/netclaw.json"
Enter

Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/
Down 8
Enter
Wait+Screen@10s /Security & Access/

# Open Audience Profiles, edit Team, disable Web access.
Down 2
Enter
Wait+Screen@10s /System default posture: Team/
Wait+Screen@10s /\* Team/
Down
Enter
Wait+Screen@10s /Audience Profile: Team/
Down
Space
Wait+Screen@10s /\[ \] Web/
Down 4
Wait+Screen@10s /\[◀ Session only/
Down
Wait+Screen@10s /Common work files: images/
Escape
Wait+Screen@10s /System default posture: Team/
Escape
Wait+Screen@10s /Security & Access/
Ctrl+Q

Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_AUDIENCE_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_AUDIENCE_EXIT=0/

Type "exit"
Enter
