# config-teams.tape - configure Microsoft Teams entirely through its offline
# canonical-ID paths in `netclaw config`.
#
# Exercises:
#   netclaw config -> Channels -> Microsoft Teams first-connect -> configured
#   management -> manual Team/channel, user, group, and Group Chat paths -> safe return.
# All identifiers are synthetic. The tape never submits a directory search, so
# it does not acquire a Graph token or contact a Teams tenant.

Output "/tmp/tape-config-teams.gif"

# `config` deliberately refuses an uninitialised home. Seed only the schema
# marker; the Teams first-connect flow below owns every Teams setting.
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "jq -n '{configVersion:1}' > $NETCLAW_HOME/config/netclaw.json"
Enter

Type "netclaw config"
Enter
# The self-contained native CLI can need a cold extraction on a fresh tape HOME.
Wait+Screen@30s /Settings Areas/

# Channels is row 3 in the dashboard list. Microsoft Teams is the fourth
# adapter in the picker, immediately before the semantic Done row.
Down 2
Enter
Wait+Screen@10s /Which channels would you like to connect/
Wait+Screen@10s /Slack/
Wait+Screen@10s /Discord/
Wait+Screen@10s /Mattermost/
Wait+Screen@10s /Microsoft Teams/
Wait+Screen@10s /Done adding channels/

Down 3
Enter
Wait+Screen@10s /Entra tenant ID/
Type "11111111-1111-1111-1111-111111111111"
Enter
Wait+Screen@10s /Entra application \/ client ID/
Type "22222222-2222-2222-2222-222222222222"
Enter
Wait+Screen@10s /Teams bot ID/
Type "33333333-3333-3333-3333-333333333333"
Enter
Wait+Screen@10s /Teams client secret/
Type "teams-smoke-client-secret"
Enter
Wait+Screen@10s /canonical Team IDs/
Type "44444444-4444-4444-4444-444444444444"
Enter
Wait+Screen@10s /canonical channel IDs/

# Leave the initial channel list blank. The manual channel path below proves
# the same persisted canonical boundary without performing directory lookup.
Enter
Wait+Screen@10s /Allow Teams direct messages/
Down
Enter
Wait+Screen@10s /global Entra user IDs/
Type "66666666-6666-6666-6666-666666666666"
Enter
Wait+Screen@10s /global Entra group IDs/
Type "88888888-8888-8888-8888-888888888888"
Enter
Wait+Screen@10s /Microsoft Teams > Channels & Permissions/
Wait+Screen@10s /Microsoft Teams channel setup saved/
Wait+Screen@10s /Done adding channels/

# Return to the picker, then prove that re-entering Teams opens management
# rather than credential prompts.
Escape
Wait+Screen@10s /Microsoft Teams is configured/
Wait+Screen@10s /Manage channels and permissions/
Wait+Screen@10s /Add a channel or Group Chat/
Wait+Screen@10s /Group Chat ingress: OFF/
Wait+Screen@10s /Add users or groups/
Wait+Screen@10s /Manage users and groups/
Wait+Screen@10s /Attachments/
Wait+Screen@10s /Directory \/ Graph status/
Wait+Screen@10s /Connection & credentials/

# Exercise the configured connection form through the actual Termina input
# path. Tab must wrap from the secret to tenant, and Shift+Tab must return to
# the secret. These values replace the first-connect values above.
Down 8
Enter
Wait+Screen@10s /Microsoft Teams > Connection & credentials/
Wait+Screen@10s /Entra tenant ID/
Backspace 64
Type "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
Tab
Backspace 64
Type "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
Tab
Backspace 64
Type "cccccccc-cccc-cccc-cccc-cccccccccccc"
Tab
Type "teams-smoke-replacement-secret"
Tab
Shift+Tab
Enter
Wait+Screen@10s /Microsoft Teams is configured/
Wait+Screen@10s /Microsoft Teams connection saved/

# Reopen the form after persistence. Non-secret IDs must load. The secret
# input stays blank and identifies its persisted value without exposing it.
Escape
Wait+Screen@10s /Which channels would you like to connect/
# The picker keeps its prior cursor position. Clamp it to Slack, then select
# Teams explicitly, so this remains deterministic after any management flow.
Up 3
Down 3
Enter
Wait+Screen@10s /Microsoft Teams is configured/
Down 8
Enter
Wait+Screen@10s /aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/
Wait+Screen@10s /bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/
Wait+Screen@10s /cccccccc-cccc-cccc-cccc-cccccccccccc/
Wait+Screen@10s /configured - leave blank to keep/
Escape
Wait+Screen@10s /Microsoft Teams is configured/
Escape
Wait+Screen@10s /Which channels would you like to connect/
Wait+Screen@10s /Microsoft Teams/
# The picker retains its prior cursor position. Clamp it to Slack, then select
# Teams explicitly before re-entry.
Up 3
Down 3
Enter
Wait+Screen@10s /Microsoft Teams is configured/

# Add one channel via the explicit manual canonical-ID path. Do not submit a
# directory query. An empty search selects the advanced local ID entry.
Up 20
Down
Enter
Wait+Screen@10s /Microsoft Teams > Add a destination/
Wait+Screen@10s /Channel/
Enter
Wait+Screen@10s /Microsoft Teams > Add channel/
Enter
Wait+Screen@10s /Advanced: canonical Teams channel IDs/
Type "55555555-5555-5555-5555-555555555555"
Enter
Wait+Screen@10s /Microsoft Teams > Channels & Permissions/
Wait+Screen@10s /at the Team default and saved/

# Return to management and add an allowed user via its manual path.
Escape
Wait+Screen@10s /Microsoft Teams is configured/
Up 20
Down 3
Enter
Wait+Screen@10s /Microsoft Teams > Add users or groups/
Wait+Screen@10s /User/
Enter
Wait+Screen@10s /Microsoft Teams > Allowed users/
Enter
Wait+Screen@10s /Advanced path: enter canonical Entra user IDs/
Type ",77777777-7777-7777-7777-777777777777"
Enter
Wait+Screen@10s /Microsoft Teams is configured/
Wait+Screen@10s /Allowed users saved/

# Repeat for allowed groups. This covers the real focused text-input route,
# rather than a view-model-only persistence path.
Escape
Wait+Screen@10s /Which channels would you like to connect/
Up 3
Down 3
Enter
Wait+Screen@10s /Microsoft Teams is configured/
Up 20
Down 3
Enter
Wait+Screen@10s /Microsoft Teams > Add users or groups/
Down
Enter
Wait+Screen@10s /Microsoft Teams > Allowed groups/
Enter
Wait+Screen@10s /Advanced path: enter canonical Entra group IDs/
Type ",99999999-9999-9999-9999-999999999999"
Enter
Wait+Screen@10s /Microsoft Teams is configured/
Wait+Screen@10s /Allowed group settings saved/

# Save a canonical chat ID while ingress stays disabled. This reproduces
# an existing destination that needs the main-menu ingress control.
Escape
Wait+Screen@10s /Which channels would you like to connect/
Up 3
Down 3
Enter
Wait+Screen@10s /Microsoft Teams is configured/
Up 20
Down
Enter
Wait+Screen@10s /Microsoft Teams > Add a destination/
Down
Enter
Wait+Screen@10s /Microsoft Teams > Find a Group Chat/
Wait+Screen@10s /Group Chat name/
Wait+Screen@10s /search continues automatically/
Type "bostontech"
Wait+Screen@10s /bostontech/
Backspace 10
Wait+Screen@10s /Group Chat name/
Down
Wait+Screen@10s /Advanced/
Enter
Wait+Screen@10s /Microsoft Teams > Group Chats/
Wait+Screen@10s /Canonical Group Chat IDs/
Type "19:smoke-group-chat@thread.v2"
Enter
Wait+Screen@10s /Microsoft Teams is configured/
Wait+Screen@10s /Group Chat settings saved/
Wait+Screen@10s /Group Chat ingress: OFF/

# The main Teams menu must expose ingress without a new search or ID edit.
Up 20
Down 2
Enter
Wait+Screen@10s /Microsoft Teams > Group Chats/
Wait+Screen@10s /Enable Group Chat ingress/
Wait+Screen@10s /19:smoke-group-chat@thread.v2/
Space
Wait+Screen@10s /\[✓\] Enable Group Chat ingress/
Enter
Wait+Screen@10s /Microsoft Teams is configured/
Wait+Screen@10s /Group Chat ingress: ON/

# Disable, then enable again through the same visible control.
Up 20
Down 2
Enter
Wait+Screen@10s /Microsoft Teams > Group Chats/
Wait+Screen@10s /\[✓\] Enable Group Chat ingress/
Space
Wait+Screen@10s /\[ \] Enable Group Chat ingress/
Enter
Wait+Screen@10s /Microsoft Teams is configured/
Wait+Screen@10s /Group Chat ingress: OFF/
Up 20
Down 2
Enter
Wait+Screen@10s /Microsoft Teams > Group Chats/
Wait+Screen@10s /19:smoke-group-chat@thread.v2/
Space
Wait+Screen@10s /\[✓\] Enable Group Chat ingress/
Enter
Wait+Screen@10s /Microsoft Teams is configured/
Wait+Screen@10s /Group Chat ingress: ON/

# Enable supported Teams attachments through the dedicated management control.
Up 20
Down 5
Enter
Wait+Screen@10s /Microsoft Teams > Attachments/
Wait+Screen@10s /Allow supported Teams attachments/
Space
Wait+Screen@10s /Microsoft Teams attachments enabled and saved/

# Return through the Teams adapter menu, then use the picker-level semantic
# Done key rather than an adapter count.
Escape
Wait+Screen@10s /Microsoft Teams is configured/
Escape
Wait+Screen@10s /Which channels would you like to connect/
Wait+Screen@10s /Done adding channels/
Type "d"
Wait+Screen@10s /Settings Areas/

Ctrl+Q

# Alt-screen restore guard (CSI ?1049l): avoid typing into the restoring TUI.
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_TEAMS_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_TEAMS_EXIT=0/

Type "exit"
Enter
