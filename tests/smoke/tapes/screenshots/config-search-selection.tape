# Capture the initial search backend selection.
# No terminal action follows the screenshot.

Output "/tmp/tape-shot-config-search-selection.gif"

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
Wait+Screen@5s /DuckDuckGo works without setup, but may hit bot detection./
Wait+Screen@5s /\[Esc\] Back/
Sleep 250ms
