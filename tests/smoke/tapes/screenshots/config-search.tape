# config-search.tape (screenshot) — capture the Search workflow screens.
#
# Frames captured:
#   shot-config-search-selection
#   shot-config-search-brave-entry
#   shot-config-search-saved

Output "/tmp/tape-shot-config-search.gif"

# ─── Seed minimal config so `netclaw config` can launch ────────────────────
Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "backend=duckduckgo; jq -n --arg backend $backend '{configVersion:1,Search:{Backend:$backend}}' > $NETCLAW_HOME/config/netclaw.json"
Enter

# ─── Launch ────────────────────────────────────────────────────────────────
Type "netclaw config"
Enter

Wait+Screen@10s /Settings Areas/
Down 5
Enter

# ─── Frame 1: Provider selection ───────────────────────────────────────────
Wait+Screen@10s /Choose the backend Netclaw uses for web search/
Sleep 1s
Screenshot "/tmp/shot-config-search-selection.png"
Sleep 1s

# ─── Frame 2: Brave entry state ────────────────────────────────────────────
Down
Enter
Wait+Screen@10s /Brave Search requires an API key/
Sleep 1s
Screenshot "/tmp/shot-config-search-brave-entry.png"
Sleep 1s

# ─── Frame 3: Saved state ──────────────────────────────────────────────────
Escape
Down
Enter
Wait+Screen@10s /Enter the base URL of your SearXNG instance/
Type "https://search.test.local"
Enter
Wait+Screen@10s /Search Validation Warning/
Down 2
Enter
Wait+Screen@10s /validated and saved/
Sleep 1s
Screenshot "/tmp/shot-config-search-saved.png"
Sleep 1s

Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "exit"
Enter
