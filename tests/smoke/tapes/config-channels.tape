# config-channels.tape - open Channels from netclaw config.
#
# Exercises:
#   netclaw config -> Channels -> Slack details -> back to dashboard
# and verifies the read-only channel summary page can render existing config.

Output "/tmp/tape-config-channels.gif"

# Seed channel config so `netclaw config` can render useful summaries.
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "c1=C01 c2=C02 u1=U01 mm=town-square; jq -n --arg c1 $c1 --arg c2 $c2 --arg u1 $u1 --arg mm $mm '{configVersion:1,Slack:{Enabled:true,AllowedChannelIds:[$c1,$c2],AllowedUserIds:[$u1]},Mattermost:{Enabled:true,DefaultChannelId:$mm}}' > $NETCLAW_HOME/config/netclaw.json"
Enter
Type "bot=xoxb-test app=xapp-test mm_token=mattermost-test; jq -n --arg bot $bot --arg app $app --arg mm_token $mm_token '{configVersion:1,Slack:{BotToken:$bot,AppToken:$app},Mattermost:{BotToken:$mm_token}}' > $NETCLAW_HOME/config/secrets.json"
Enter

Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/

# Channels is row 3 in the dashboard list.
Down 2
Enter
Wait+Screen@10s /Chat Channels/
Wait+Screen@10s /Slack/
Wait+Screen@10s /2 channels, 1 user/

Enter
Wait+Screen@10s /Slack Channels/
Wait+Screen@10s /Bot token/
Wait+Screen@10s /Allowed channels[[:space:]]+2 configured/

Escape
Wait+Screen@10s /Chat Channels/
Escape
Wait+Screen@10s /Settings Areas/
Ctrl+Q

Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_CHANNELS_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_CHANNELS_EXIT=0/

Type "exit"
Enter
