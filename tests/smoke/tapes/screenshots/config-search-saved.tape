# Capture the saved search configuration view.
# No terminal action follows the screenshot.

Output "/tmp/tape-shot-config-search-saved.gif"

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
Escape
Wait+Screen@5s /Choose the backend Netclaw uses for web search/
Down
Enter
Wait+Screen@10s /Enter the base URL of your SearXNG instance/
Type "https://search.test.local"
Enter
Wait+Screen@10s /Search Validation Warning/
Down 2
Enter
Wait+Screen@10s /validated and saved/
Wait+Screen@10s /Saved Search settings./
Wait+Screen@5s /\[Esc\] Review backends/
Sleep 250ms
