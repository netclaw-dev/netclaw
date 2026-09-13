# Capture the Brave search entry view.
# No terminal action follows the screenshot.

Output "/tmp/tape-shot-config-search-brave-entry.gif"

Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "backend=duckduckgo; jq -n --arg backend $backend '{configVersion:1,Search:{Backend:$backend}}' > $NETCLAW_HOME/config/netclaw.json"
Enter
Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/
Down 5
Enter
Wait+Screen@10s /Choose the backend Netclaw uses for web search/
Wait+Screen@5s /DuckDuckGo works without setup/
Down
Enter
Wait+Screen@10s /Brave Search requires an API key/
Wait+Screen@5s /Enter Brave Search API key/
Wait+Screen@5s /Stored in secrets.json./
Sleep 250ms
