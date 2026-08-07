# config-mention-thread.tape - toggle per-channel MentionRequiredInThread on the
# EditAudience leaf in `netclaw config`.
#
# Exercises:
#   netclaw config -> Channels -> configured Slack -> Manage channels and permissions
#   -> Enter a channel -> EditAudience leaf -> Space toggles "Mention required in
#   thread" Off -> On -> Enter applies (autosave).

Output "/tmp/tape-config-mention-thread.gif"

# Seed Slack config so `netclaw config` can render the channel management flow.
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "c1=C01 u1=U01; jq -n --arg c1 $c1 --arg u1 $u1 --arg aud team '{configVersion:1,Slack:{Enabled:true,SocketMode:true,AllowedChannelIds:[$c1],AllowedUserIds:[$u1],AllowDirectMessages:false,ChannelAudiences:{($c1):$aud}}}' > $NETCLAW_HOME/config/netclaw.json"
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

# Re-enter configured Slack -> management menu.
Enter
Wait+Screen@10s /Slack is configured/
Wait+Screen@10s /Manage channels and permissions/

# Open Manage channels and permissions -> channel list.
Enter
Wait+Screen@10s /Channels & Permissions/
Wait+Screen@10s /C01/

# Enter the focused channel's detail leaf. The mention rule starts Off.
Enter
Wait+Screen@10s /Who is this channel for/
Wait+Screen@10s /Mention required in thread: Off/

# Space toggles the mention rule On.
Space
Wait+Screen@10s /Mention required in thread: On/

# Enter applies and autosaves, returning to the channel list.
Enter
Wait+Screen@10s /audience and saved/

# Quit the config TUI.
Ctrl+Q

# Alt-screen restore guard (CSI ?1049l): give vhs a beat to catch the restored main buffer.
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_MENTION_THREAD_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_MENTION_THREAD_EXIT=0/

Type "exit"
Enter
