# config-search.tape — drive `netclaw config` into the redesigned Search flow.
#
# Covers:
#   - dashboard -> Search provider matrix
#   - provider selection happens directly from arrow navigation
#   - invalid save blocked on missing Brave API key
#   - provider switch back to DuckDuckGo from the same screen
#   - exit Search, re-enter it, and confirm test + SearXNG entry still work
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

# ─── Search provider matrix ───────────────────────────────────────────────
Wait+Screen@10s /Providers/
Wait+Screen@5s /No additional setup required/

# ─── Select Brave in-place and confirm invalid save stays inline ──────────
Down
Wait+Screen@10s /Brave API key/
Wait+Screen@5s /Brave API key/
Type "s"
Wait+Screen@10s /Brave requires an API key/

# ─── Switch provider back to DuckDuckGo ───────────────────────────────────
Up
Wait+Screen@10s /\(\*\) DuckDuckGo/

# ─── Leave Search, then re-enter to validate preserved-page lifecycle ─────
Escape
Wait+Screen@10s /Settings Areas/
Down 5
Enter
Wait+Screen@10s /\(\*\) DuckDuckGo/
Type "t"
Wait+Screen@10s /Last test:/
Down 2
Wait+Screen@10s /SearXng instance URL/
Wait+Screen@5s /Enter the base URL of your SearXNG instance/
Enter
Wait+Screen@10s /Press Enter to apply or Esc to cancel edit\./
Type "https://search.test.local"
Enter
Wait+Screen@10s /https:\/\/search.test.local/
Escape
Wait+Screen@10s /Unsaved changes\./
Escape
Wait+Screen@10s /Settings Areas/

# ─── Back out to shell ────────────────────────────────────────────────────
Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "echo CONFIG_SEARCH_EXIT=$?"
Enter
Wait+Screen@5s /CONFIG_SEARCH_EXIT=0/

Type "exit"
Enter
