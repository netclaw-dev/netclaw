# Capture the MCP tool grid with the test server connected.
# No terminal action follows the screenshot.

Output "/tmp/tape-shot-mcp-permissions-tool-grid.gif"

Hide
Type "netclaw provider add smoke-llm ollama --endpoint http://127.0.0.1:11434"
Enter
Wait+Screen@10s /TAPE\$/
Type "netclaw model set main smoke-llm qwen2:0.5b"
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

Type "netclaw mcp permissions"
Enter
Wait+Screen@15s /MCP Permissions/
Wait+Screen@10s /Connected, 4 tools/
Enter
Wait+Screen@30s /record-tasks/
Wait+Screen@10s /Server default:/
Wait+Screen@5s /Audience:/
Sleep 250ms
