# config-search.tape — drive `netclaw config` into the Search workflow.
#
# Covers:
#   - dashboard -> Search launcher workflow
#   - provider selection -> entry state
#   - static validation failure preserves typed input
#   - dynamic validation failure opens explicit override flow
#   - save-anyway persists and returns cleanly
#
# Post-tape assertion validates the Search section persisted to netclaw.json.

Output "/tmp/tape-config-search.gif"

# ─── Seed minimal config so `netclaw config` can launch ────────────────────
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "backend=duckduckgo; jq -n --arg backend $backend '{configVersion:1,Search:{Backend:$backend}}' > $NETCLAW_HOME/config/netclaw.json"
Enter

# ─── Launch ────────────────────────────────────────────────────────────────
Type "netclaw config"
Enter

# ─── Dashboard -> Search ──────────────────────────────────────────────────
Wait+Screen@10s /Settings Areas/
# Search is row 6 in the dashboard list.
Down 5
Enter

# ─── Search provider selection ────────────────────────────────────────────
Wait+Screen@10s /Search/
Wait+Screen@5s /DuckDuckGo works without setup/

# ─── Select Brave and confirm static validation blocks progression ─────────
Down
Enter
Wait+Screen@10s /Brave Search requires an API key/
Wait+Screen@5s /Stored in secrets.json/
Enter
Wait+Screen@10s /Brave requires an API key/
Escape

# ─── Select SearXNG and complete happy path ───────────────────────────────
Down
Enter
Wait+Screen@10s /Enter the base URL of your SearXNG instance/
Type "https://search.test.local"
Enter
Wait+Screen@10s /Validating Search configuration/
Wait+Screen@10s /Search Validation Warning/
Down 2
Enter
Wait+Screen@10s /validated and saved/
Escape
Wait+Screen@10s /Choose the backend Netclaw uses for web search/

# ─── Back out to shell ────────────────────────────────────────────────────
Escape
Wait+Screen@10s /Settings Areas/
Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_SEARCH_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_SEARCH_EXIT=0/

Type "exit"
Enter
