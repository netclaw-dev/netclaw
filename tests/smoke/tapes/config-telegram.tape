# config-telegram.tape - native Telegram configuration management flow.
#
# Exercises:
#   netclaw config -> Channels -> configured Telegram management menu
#   -> typed credential rotation -> typed allowed-user edit -> autosave
#   -> exit -> re-entry recognizes the saved state.
# Verifies the persisted canonical chat id and that the rotated token is
# stored encrypted (never as plaintext) in secrets.json. No api.telegram.org
# access: management saves run without the network probe.

Output "/tmp/tape-config-telegram.gif"

# Seed a configured Telegram channel so `netclaw config` renders the
# management flow instead of first-time credential prompts. The seed token is
# plaintext on purpose: the UI save path must encrypt it at rest.
Type "echo '{configVersion:1,Telegram:{Enabled:true,AllowedChatIds:[-1001234],AllowDirectMessages:false,MentionOnly:true,AllowedUserIds:[12345]}}' > $NETCLAW_HOME/config/netclaw.json"
Enter
Type "tok='12345:AAseed0000seed0000'; jq -n --arg tok $tok '{configVersion:1,Telegram:{BotToken:$tok}}' > $NETCLAW_HOME/config/secrets.json"
Enter

Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/

# Channels is row 3 in the dashboard list.
Down 2
Enter
Wait+Screen@10s /Which channels would you like to connect/
Wait+Screen@10s /Telegram/
Wait+Screen@10s /1 channel/

# Re-enter configured Telegram: management menu, not credential prompts.
Enter
Wait+Screen@10s /Telegram is configured/
Wait+Screen@10s /Manage channels and permissions/

# Rotate credentials using typed input, not paste. Telegram's management menu
# carries one extra mention-only row, so credentials sit five rows down.
Down 5
Enter
Wait+Screen@10s /Telegram > Credentials/
Type "12345:AArotate0000rotate0"
Enter
Wait+Screen@10s /Credential changes saved./

# Edit the allowed user list with typed input.
Escape
Wait+Screen@10s /Manage channels and permissions/
Down 2
Enter
Wait+Screen@10s /User IDs/
Right 32
Backspace 32
Type "4242"
Enter
Wait+Screen@10s /Allowed users saved./

# Leave and re-enter: the saved state must be recognized.
Escape
Wait+Screen@10s /Which channels would you like to connect/
Enter
Wait+Screen@10s /Telegram is configured/

Escape
Down 4
Enter
Wait+Screen@10s /Settings Areas/

Ctrl+Q

# Alt-screen restore guard (CSI ?1049l): give vhs a beat to catch the restored main buffer.
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_TELEGRAM_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_TELEGRAM_EXIT=0/

# Semantic assertions on persisted state.
Type "chat=$(jq -r '.Telegram.AllowedChatIds[0]' $NETCLAW_HOME/config/netclaw.json); echo TELEGRAM_CHAT=$chat"
Enter
Wait+Screen@5s /TELEGRAM_CHAT=-1001234/

Type "raw=$(jq -r '.Telegram.BotToken' $NETCLAW_HOME/config/secrets.json); case $raw in 12345:AA*) echo TELEGRAM_TOKEN_STATE=PLAINTEXT;; null) echo TELEGRAM_TOKEN_STATE=MISSING;; *) echo TELEGRAM_TOKEN_STATE=ENCRYPTED;; esac"
Enter
Wait+Screen@5s /TELEGRAM_TOKEN_STATE=ENCRYPTED/

Type "users=$(jq -r '.Telegram.AllowedUserIds[0]' $NETCLAW_HOME/config/netclaw.json); echo TELEGRAM_USER=$users"
Enter
Wait+Screen@5s /TELEGRAM_USER=4242/

Type "exit"
Enter
