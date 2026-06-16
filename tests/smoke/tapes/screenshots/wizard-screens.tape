# wizard-screens.tape (screenshot) — walk `netclaw init` and capture two
# settled wizard states as PNGs.
#
# Capture-only tape: NO post-tape assertion. The screenshots mode in
# run-smoke.sh compares each emitted PNG byte-for-byte against the committed
# baseline at tests/smoke/screenshots/<frame>.approved.png.
#
# Frames captured:
#   shot-wizard-provider-picker  — "Choose your LLM provider:" list
#   shot-wizard-security-posture — "Who will interact with this Netclaw
#                                   instance" posture list
#
# The identity ("Agent name:") step is deliberately NOT screenshotted: it
# is a text-input step, and Termina blinks the input caret on its own
# timer. VHS's `Set CursorBlink false` only governs VHS's own terminal
# cursor, so the app-drawn caret cell is not byte-stable across runs.
#
# Navigation + anchors mirror tapes/init-wizard.tape; see that file and
# src/Netclaw.Cli/Tui/Wizard/Steps/*StepView.cs for the synchronization
# rationale. Each Screenshot is bracketed by Sleep 1s — VHS can otherwise
# capture an unsettled frame, or let the next keystroke leak into the shot.
#
# Prepended preamble: tapes/screenshot-preamble.tape (determinism-pinned).

Output "/tmp/tape-shot-wizard-screens.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw init"
Enter

# ─── Frame 1: Provider picker ────────────────────────────────────────
Wait+Screen@10s /Choose your LLM provider:/
Sleep 1s
Screenshot "/tmp/shot-wizard-provider-picker.png"
Sleep 1s

# Provider list ordering is alphabetical by TypeKey:
#   anthropic, github-copilot, ollama, openai, openai-compatible, openrouter
# Anthropic is the default; two Downs land on Ollama (one Down lands on
# github-copilot since it was added in PR #1075).
Down 2
Enter

# Endpoint input — native Ollama is at http://localhost:11434, which is
# also the prompt's default value.
Wait+Screen@10s /endpoint:/
# VHS has no End/Home — push the cursor right past the existing text
# (Right N is a no-op at end), then Backspace to clear all 22 chars.
Right 32
Backspace 32
Type "http://localhost:11434"
Enter

# Connection probe + model discovery. The "Connected! Found N models"
# success frame auto-advances; wait directly on the model list header.
Wait+Screen@45s /Select a model/
# Models are alphabetical: all-minilm first, qwen2 second.
Down
Enter

# ─── Identity (navigated through, not screenshotted) ─────────────────
# Identity immediately follows Provider in the current bootstrap flow, so its four
# substeps must be walked to reach the posture screen. These frames are deliberately
# NOT screenshotted — the text-input caret blink makes them non-byte-stable (see the
# header). Navigation + anchors mirror tapes/init-wizard.tape.
Wait+Screen@10s /Agent name:/
Enter

Wait+Screen@10s /Communication style:/
Enter

Wait+Screen@10s /Your name:/
Type "SmokeTester"
Enter

Wait+Screen@10s /Your timezone:/
Enter

# ─── Frame 2: Security posture ───────────────────────────────────────
Wait+Screen@10s /Who will interact with this Netclaw instance/
Sleep 1s
Screenshot "/tmp/shot-wizard-security-posture.png"
Sleep 1s

# Both frames captured; abandon the wizard. Ctrl+Q is the TUI quit
# shortcut (Ctrl+C requires a double-press under raw input mode).
Ctrl+Q

# VHS's screen scraper needs a beat to pick up the restored main buffer
# after the TUI tears down the alternate screen (CSI ?1049l).
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "exit"
Enter
