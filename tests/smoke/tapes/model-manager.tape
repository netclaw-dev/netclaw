# model-manager.tape — smoke the `netclaw model` TUI.
#
# Validates configured roles and provider changes in the model picker.
# This covers the keyed-layout refresh and the .WithFillHeight() scroll fix
# from #1351 without a live daemon.

Output "/tmp/tape-model-manager.gif"

# ─── Seed configured roles ───────────────────────────────────────────
Type "netclaw provider add alpha-models openai-compatible --endpoint __SMOKE_LLM_ENDPOINT__"
Enter
Wait+Screen@10s /Added provider 'alpha-models'/

Type "netclaw provider add bravo-models openai-compatible --endpoint __SMOKE_LLM_ENDPOINT__"
Enter
Wait+Screen@10s /Added provider 'bravo-models'/

Type "netclaw model set main alpha-models deepseek-v4-flash"
Enter
Wait+Screen@10s /Set main model to alpha-models\/deepseek-v4-flash/

Type "netclaw model set fallback alpha-models fallback-model"
Enter
Wait+Screen@10s /Set fallback model to alpha-models\/fallback-model/

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw model"
Enter

Wait+Screen@10s /Model Manager/
Wait+Screen@10s /deepseek-v4-flash/
Wait+Screen@10s /fallback-model/

# ─── Change the selected provider ───────────────────────────────────
Enter
Wait+Screen@10s /Select provider for Main/
Enter
Wait+Screen@30s /Select model for Main.*alpha-models/

Escape
Wait+Screen@10s /Select provider for Main/
Down
Enter
Wait+Screen@30s /Select model for Main.*bravo-models/

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo MODEL_MANAGER_EXIT=$?"
Enter
Wait+Screen@5s /MODEL_MANAGER_EXIT=0/

Type "exit"
Enter
