# model-manager.tape — smoke the `netclaw model` TUI.
#
# Validates that the page can assign an image proxy through the TUI.
# The semantic assertion checks the stored definition and assignment.

Output "/tmp/tape-model-manager.gif"

# Seed one provider. The harness supplies a live Ollama endpoint.
Type "netclaw provider add smoke-ollama ollama --endpoint http://localhost:11434"
Enter
Wait+Screen@10s /Added provider 'smoke-ollama'|TAPE\$/

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw model"
Enter

# PanelNode title always renders regardless of daemon state.
Wait+Screen@10s /Model Manager/

# Select the ImageProxy row. The only provider starts discovery directly.
Down 3
Enter
Wait+Screen@30s /Select model for ImageProxy/

# Select the manual entry after the bounded discovered-model list.
Down 30
Enter
Wait+Screen@10s /Enter model ID/
Type "smoke-vision"
Enter
Wait+Screen@10s /Assign ImageProxy model/
Enter
Wait+Screen@10s /Set ImageProxy to smoke-ollama\/smoke-vision/

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo MODEL_MANAGER_EXIT=$?"
Enter
Wait+Screen@5s /MODEL_MANAGER_EXIT=0/

Type "exit"
Enter
