# config-audience.tape - edit Audience Profiles from netclaw config.
#
# Exercises:
#   netclaw config -> Security & Access -> Audience Profiles -> Team -> reset
# and verifies reset restores the full posture baseline, including hidden MCP /
# approval settings that are not recreated in this editor.

Output "/tmp/tape-config-audience.gif"

# Seed Team config with customized visible tools plus hidden MCP/approval fields.
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "export posture=Team mode=Allowlist mcp_mode=All deny=Deny approval=Approval"
Enter
Type "export file_read=file_read file_list=file_list attach_file=attach_file"
Enter
Type "export memorizer=memorizer search_memories=search_memories get=get shell_execute=shell_execute"
Enter
Type "jq -n '{configVersion:1,Security:{DeploymentPosture:$ENV.posture},Tools:{AudienceProfiles:{Team:{ToolsMode:$ENV.mode,AllowedTools:[$ENV.file_read,$ENV.file_list,$ENV.attach_file],McpServersMode:$ENV.mcp_mode,AllowedMcpServers:[$ENV.memorizer],McpServerToolGrants:{($ENV.memorizer):[$ENV.search_memories,$ENV.get]},ApprovalPolicy:{DefaultMode:$ENV.deny,ToolOverrides:{($ENV.shell_execute):$ENV.approval}}}}}}' > $NETCLAW_HOME/config/netclaw.json"
Enter

Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/
Down 8
Enter
Wait+Screen@10s /Security & Access/

# Open Audience Profiles, edit Team, then reset the full profile.
Down 2
Enter
Wait+Screen@10s /System default posture: Team/
Wait+Screen@10s /\* Team/
Down
Enter
Wait+Screen@10s /Audience Profile: Team/
Down
Space
Wait+Screen@10s /\[✓\] Web/
Down 7
Wait+Screen@10s /Reset overrides/
Enter
Wait+Screen@10s /Team overrides reset to the Team posture baseline/
Escape
Wait+Screen@10s /System default posture: Team/
Escape
Wait+Screen@10s /Security & Access/
Ctrl+Q

# Alt-screen restore guard (CSI ?1049l): give vhs a beat to catch the restored main buffer.
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_AUDIENCE_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_AUDIENCE_EXIT=0/

Type "exit"
Enter
