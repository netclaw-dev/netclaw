# model-manager.tape — smoke the `netclaw model` TUI.
#
# Validates that the page shows configured main and fallback roles, then
# exits cleanly on Ctrl+Q. This covers the initial keyed-layout refresh
# and the .WithFillHeight() scroll fix from #1351 without a live daemon.

Output "/tmp/tape-model-manager.gif"

# ─── Seed configured roles ───────────────────────────────────────────
Type "netclaw provider add smoke-models ollama --endpoint http://localhost:11434"
Enter
Wait+Screen@10s /Added provider 'smoke-models'/

Type "netclaw model set main smoke-models deepseek-v4-flash"
Enter
Wait+Screen@10s /Set main model to smoke-models\/deepseek-v4-flash/

Type "netclaw model set fallback smoke-models fallback-model"
Enter
Wait+Screen@10s /Set fallback model to smoke-models\/fallback-model/

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw model"
Enter

Wait+Screen@10s /Model Manager/
Wait+Screen@10s /deepseek-v4-flash/
Wait+Screen@10s /fallback-model/

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo MODEL_MANAGER_EXIT=$?"
Enter
Wait+Screen@5s /MODEL_MANAGER_EXIT=0/

Type "exit"
Enter
