# config-channels.tape - edit Channels from netclaw config.
#
# Exercises:
#   netclaw config -> Channels -> configured Slack management menu
#   -> channel permission edit -> add channel -> allowed users -> save.
# Verifies configured Slack does not re-prompt for credentials during re-entry.

Output "/tmp/tape-config-channels.gif"

# Seed Slack config so `netclaw config` can render the channel management flow.
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "c1=C01 c2=C02 u1=U01; jq -n --arg c1 $c1 --arg c2 $c2 --arg u1 $u1 --arg aud team '{configVersion:1,Slack:{Enabled:true,SocketMode:true,AllowedChannelIds:[$c1,$c2],AllowedUserIds:[$u1],AllowDirectMessages:false,ChannelAudiences:{($c1):$aud,($c2):$aud}}}' > $NETCLAW_HOME/config/netclaw.json"
Enter
Type "bot=xoxb-test app=xapp-test; jq -n --arg bot $bot --arg app $app '{configVersion:1,Slack:{BotToken:$bot,AppToken:$app}}' > $NETCLAW_HOME/config/secrets.json"
Enter

Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/

# Channels is row 3 in the dashboard list.
Down 2
Enter
Wait+Screen@10s /Which channels would you like to connect/
Wait+Screen@10s /Slack/
Wait+Screen@10s /2 channels, 1 user/

# Re-enter configured Slack. This should open management, not token prompts.
Enter
Wait+Screen@10s /Slack is configured/
Wait+Screen@10s /Manage channels and permissions/

# Edit the first channel audience from Team to Public.
Enter
Wait+Screen@10s /Channels & Permissions/
Wait+Screen@10s /C01/
Right
Wait+Screen@10s /Public/

# Add a new channel without touching credentials.
Type "a"
Wait+Screen@10s /Add Channel/
Type "C09"
Enter
Wait+Screen@10s /Added C09/
Wait+Screen@10s /C09/

# Update allowed users from the management menu.
Escape
Wait+Screen@10s /What would you like to do/
Down 2
Enter
Wait+Screen@10s /User IDs/
Right 32
Backspace 32
Type "U09"
Enter
Wait+Screen@10s /Allowed users staged/

# Rotate credentials using typed input, not paste.
Down 2
Enter
Wait+Screen@10s /Slack > Credentials/
Type "xoxb-smoke-typed"
Tab
Type "xapp-smoke-typed"
Enter
Wait+Screen@10s /Credential changes staged/

# Return to picker and save.
Escape
Wait+Screen@10s /Which channels would you like to connect/
Wait+Screen@10s /3 channels/

Type "d"
Wait+Screen@10s /Channel settings saved/
Enter
Wait+Screen@10s /Settings Areas/

Ctrl+Q

Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_CHANNELS_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_CHANNELS_EXIT=0/

Type "exit"
Enter
