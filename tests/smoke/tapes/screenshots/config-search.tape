# config-search.tape (screenshot) — capture the redesigned Search flow screens.
#
# Frames captured:
#   shot-config-search-matrix
#   shot-config-search-brave
#   shot-config-search-searxng-edit

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

# ─── Frame 1: Default provider matrix ──────────────────────────────────────
Wait+Screen@10s /\(\*\) DuckDuckGo/
Sleep 1s
Screenshot "/tmp/shot-config-search-matrix.png"
Sleep 1s

# ─── Frame 2: Brave selected in matrix ─────────────────────────────────────
Down
Wait+Screen@10s /Brave API key/
Sleep 1s
Screenshot "/tmp/shot-config-search-brave.png"
Sleep 1s

# ─── Frame 3: SearXNG inline edit mode ─────────────────────────────────────
Down
Wait+Screen@10s /SearXng instance URL/
Enter
Wait+Screen@10s /Press Enter to apply or Esc to cancel edit\./
Sleep 1s
Screenshot "/tmp/shot-config-search-searxng-edit.png"
Sleep 1s

Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "exit"
Enter
