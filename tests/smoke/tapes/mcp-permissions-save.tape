# Verify the native MCP permission save path.

Output "/tmp/tape-mcp-permissions-save.gif"

Hide
Type "netclaw provider add smoke-llm openai-compatible --endpoint __SMOKE_LLM_ENDPOINT__"
Enter
Wait+Screen@10s /TAPE\$/
Type "netclaw model set main smoke-llm __SMOKE_LLM_MODEL__"
Enter
Wait+Screen@10s /TAPE\$/
Type "netclaw mcp add --transport stdio --grant-all smoke-math -- __NETCLAW_SMOKE_MCP_SERVER__"
Enter
Wait+Screen@10s /TAPE\$/
Type "netclaw daemon start"
Enter
Wait+Screen@20s /TAPE\$/
Type "until netclaw mcp list 2>/dev/null | grep -q 'connected (4 tools)'; do sleep 2; done"
Enter
Wait+Screen@60s /TAPE\$/
Show

Type "netclaw mcp permissions; status=$?; echo MCP_PERMISSIONS_SAVE_EXIT=$status"
Enter
Wait+Screen@15s /MCP Permissions/
Wait+Screen@10s /Connected, 4 tools/
Enter
Wait+Screen@30s /record-tasks/
Wait+Screen@10s /Server default:/
Down
Space
Wait+Screen@5s /unsaved/
Enter
Wait+Screen@5s /Enter\/Y.*Save/
Enter
Wait+Screen@10s /Connected, 4 tools/
Ctrl+Q
Wait+Screen@5s /MCP_PERMISSIONS_SAVE_EXIT=0/
Type "exit"
Enter
