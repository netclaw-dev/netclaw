# model-manager.tape — smoke the `netclaw model` TUI.
#
# Validates configured roles and provider changes in the model picker.
# This covers the keyed-layout refresh and the .WithFillHeight() scroll fix
# from #1351 without a live daemon.
#
# It also edits the output limit and the thinking level of the Compaction
# definition in the P properties panel. The DeepSeek provider points at the
# smoke server, which reports no thinking capability, so only `off` is offered
# and the other levels show the unknown-capability reason.

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

Type "netclaw provider add charlie-ds deepseek --endpoint __SMOKE_LLM_ENDPOINT__/v1 --api-key smoke-key"
Enter
Wait+Screen@10s /Added provider 'charlie-ds'/

Type "netclaw model set compaction charlie-ds deepseek-v4-flash"
Enter
Wait+Screen@10s /Set compaction model to charlie-ds\/deepseek-v4-flash/

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

# ─── Clear an optional role with C ──────────────────────────────────
# Back out of the model list to the role table.
Escape
Escape
Wait+Screen@10s /Model Role Assignments/

# Arrows move the highlight only. C must clear the highlighted row without an
# Enter first. The rebuild keeps the cursor on that row, so a second C finds
# the role already unset — that proves both the clear and the repaint.
Down
Type "c"
Wait+Screen@10s /Cleared Fallback role/
Type "c"
Wait+Screen@10s /Fallback role is not set/

# Main is protected.
Up
Type "c"
Wait+Screen@10s /Cannot clear the main model role/

# ─── Edit the Compaction properties with P ──────────────────────────
# The cursor is on Main. Two rows down is Compaction.
Down
Down
Type "p"
Wait+Screen@10s /Max output tokens/

Type "32000"
Wait+Screen@10s /Max output tokens.*32000/

# The level picker greys out each level that the endpoint does not offer,
# with its reason.
Tab
Wait+Screen@10s /thinking support unknown for this endpoint/

# The picker rows are default, off, low, medium, high, max. Down selects off.
Down
Enter
Wait+Screen@10s /Saved model properties/

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo MODEL_MANAGER_EXIT=$?"
Enter
Wait+Screen@5s /MODEL_MANAGER_EXIT=0/

Type "exit"
Enter
