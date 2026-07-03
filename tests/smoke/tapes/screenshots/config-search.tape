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
# SelectBackendForEditing is synchronous. Two anchors are required: the title
# confirms the screen loaded, and the DuckDuckGo description confirms the
# cursor is on row 1 (DuckDuckGo). Without the second anchor, a Down key
# leaked from the "Down 5" Settings navigation can move the cursor to Brave
# before the screenshot fires, capturing the wrong row.
Wait+Screen@10s /Choose the backend Netclaw uses for web search/
Wait+Screen@5s /DuckDuckGo works without setup/
Screenshot "/tmp/shot-config-search-selection.png"

# ─── Frame 2: Brave entry state ────────────────────────────────────────────
# SelectBackendForEditing("brave") is synchronous — Wait+Screen is sufficient.
Down
Enter
Wait+Screen@10s /Brave Search requires an API key/
Screenshot "/tmp/shot-config-search-brave-entry.png"

# ─── Frame 3: Saved state ──────────────────────────────────────────────────
# SaveWithoutProbeOverride cascades multiple ReactiveProperty notifications
# and a ReloadPersistedDraft disk read before the frame settles. A single
# Wait+Screen on the success text is not enough — a second anchor on the
# key-binding bar text guarantees both CurrentScreen==Saved and
# ActiveDialog==None have committed to a stable rendered frame.
# "[Enter] Settings Areas" is emitted by BuildKeyBindings only in that state
# (SearchConfigEditorPage.cs line 212).
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
Wait+Screen@5s /\[Enter\] Settings Areas/
Screenshot "/tmp/shot-config-search-saved.png"

# Sleep 1s BEFORE Ctrl+Q: VHS's Screenshot is captured asynchronously on a
# later render tick, so a screen-destroying key here can race the deferred
# capture and land the restored shell transcript in the PNG instead of this
# frame. Guards this final capture against the timing jitter that broke
# mcp-permissions frame 2 (see that tape's Exit-TUI comment for mechanism).
Sleep 1s
Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "exit"
Enter
