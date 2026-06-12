# provider-add.tape — drive the `netclaw provider` TUI through the
# add flow introduced in PR #997, including the new "Name your
# provider" step that lets the user override the generated default
# (`my-ollama`, `my-anthropic`, …) before any credential entry.
#
# Flow shape: when no providers are configured yet, the provider list
# shows every known provider TYPE as a "(not configured)" row plus
# the "+ Add new provider..." sentinel at the bottom. Activating a
# type row goes straight to the AddName step for that type. The
# sentinel only routes through the explicit AddSelectType step and
# is only useful when every type is already configured (and we want
# to add another instance of one).
#
# Synchronization: every step waits on a stable substring from
# src/Netclaw.Cli/Tui/ProviderManagerPage.cs. Each state transition
# is followed by a brief `Sleep 300ms` — Termina rebuilds the layout
# (including swapping the focused SelectionList) on every state
# transition, and vhs's screen anchor matches the rendered text
# slightly before the new list's input handler is wired up.

Output "/tmp/tape-provider-add.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw provider"
Enter

# List view: every type as unconfigured + the sentinel. Anchor on
# "Ollama" (row 2 — guaranteed to be present in the type list) rather
# than the sentinel so we know the type rows have rendered.
Wait+Screen@10s /Ollama/
Sleep 300ms

# Provider type list (alphabetical by TypeKey): anthropic,
# github-copilot, ollama, openai, openai-compatible, openrouter. With
# Anthropic highlighted by default, two Downs land on Ollama (one Down
# now lands on github-copilot).
Down 2
Enter

# ─── Step 1: Name your provider (NEW in #997) ────────────────────────
Wait+Screen@10s /Name your provider/
Sleep 300ms
# Default pre-filled is "my-ollama" (9 chars). Cursor is at end
# (explicit ConsoleKey.End synth in BuildAddNameView). Backspace
# clears all 9 chars; 16 backspaces is safely over.
Backspace 16
Type "smoke-add-ollama"
Enter

# ─── Step 2: Endpoint ────────────────────────────────────────────────
Wait+Screen@10s /Endpoint/
Sleep 300ms
# Default is "http://localhost:11434" (22 chars).
Right 32
Backspace 32
Type "http://localhost:11434"
Enter

# ─── Step 3: Probe + persisted provider confirmation ─────────────────
# Probe is async; with qwen2:0.5b + all-minilm baked into native
# ollama, this resolves in seconds. The provider is persisted before
# the success screen renders, so wait directly on the persisted-provider
# confirmation (skips the transient "Validating..." frame).
Wait+Screen@45s /Provider 'smoke-add-ollama' added/
Sleep 300ms
Enter

# ─── Back at provider list ───────────────────────────────────────────
# StatusMessage on the list view: "Added provider 'smoke-add-ollama'.
# Restart daemon for changes to take effect."
Wait+Screen@10s /Added provider 'smoke-add-ollama'/
Sleep 300ms

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "echo PROVIDER_ADD_EXIT=$?"
Enter
Wait+Screen@5s /PROVIDER_ADD_EXIT=0/

Type "exit"
Enter
