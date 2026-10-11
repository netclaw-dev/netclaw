# config-posture.tape - edit Security Posture from netclaw config.
#
# Exercises:
#   netclaw config -> Security & Access -> Security Posture
# and verifies posture persistence plus automatic continuation into Enabled Features.

Output "/tmp/tape-config-posture.gif"

# Seed minimal installed config with Personal posture.
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "posture=Personal; jq -n --arg posture $posture '{configVersion:1,Security:{DeploymentPosture:$posture}}' > $NETCLAW_HOME/config/netclaw.json"
Enter

Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/
# Anchor on a Security & Access menu row description. The root dashboard and the
# Security & Access sub-screens also show the text "Security & Access", so that
# text does not prove that the menu is on screen and bound to input.
Down 8
Enter
Wait+Screen@10s /Deployment trust stance\./

# Open Security Posture, switch Personal -> Team, then land in Enabled Features.
Enter
Wait+Screen@10s /Current posture: Personal/
Down
Enter
Wait+Screen@10s /Toggle global runtime features/
Escape
Wait+Screen@10s /Deployment trust stance\./
Ctrl+Q

# Alt-screen restore guard (CSI ?1049l): give vhs a beat to catch the restored main buffer.
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_POSTURE_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_POSTURE_EXIT=0/

Type "exit"
Enter
