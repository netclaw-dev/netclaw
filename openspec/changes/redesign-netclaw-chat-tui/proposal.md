Source PRDs: `PRD-001`, `PRD-004`, `PRD-009`

## Why

The current chat flattens typed session events into one mutable text stream.
This model hides useful events and can show false tool state during parallel work.

The full-screen terminal model also replaces native scrollback, search, and
selection with incomplete application behavior. Netclaw needs a structured,
developer-focused chat before more event types and parallel activity increase
the current defects.

## What Changes

### In Scope

- Add a structured chat presentation model with stable tool-call and sub-agent
  identities.
- Show thought, parallel tool, sub-agent, approval, file, error, usage, and
  compaction events with distinct settled forms.
- Add a borderless visual grammar with named regions and responsive forms.
- Prototype an opt-in Termina inline mode that uses the primary terminal buffer.
- Keep Termina full-screen mode as the default for existing applications.
- Preserve compact and expanded approval states with `Ctrl+O`.
- Use `Shift+Enter` for a newline and bare `Enter` for prompt submission.
- Keep the Composer available during active work and show each later prompt in
  an ordered Queue Shelf.
- Send active-turn prompts through the current session input path so the actor
  includes the full FIFO set in one follow-up model call.
- Reject each new tool call that lacks its required model rationale before
  tool dispatch.
- Add prompt draft restoration and double-Escape prompt clearance.
- Preserve complete event detail through an inspector and semantic copy path.
- Preserve structured event chronology after session resume.
- Add deterministic headless tests and disposable visual checkpoint videos.
- Update `PRD-004` and the old TUI wireframe before implementation begins.
- Reuse Netclaw issues `#577` and `#1338` for their original defects.
- Reuse Termina issues `#45` and `#240` where their scopes match this work.

### Out of Scope

- A web or graphical chat client.
- A replacement for terminal-native scrollback or search in inline mode.
- A new daemon-side execution engine.
- Persistence of ephemeral tool progress in model context or the actor journal.
- A change to the default presentation mode for existing Termina applications.

## Capabilities

### New Capabilities

- `netclaw-chat-tui`: Defines the inline screen model, named regions, event
  forms, input modes, approval detail, copy behavior, and responsive grammar.

### Modified Capabilities

- `netclaw-cli`: Changes the `netclaw chat` presentation contract and the
  transition from the full-screen session picker to inline chat.
- `netclaw-session`: Adds correlated live activity fields and complete output
  parity for subscribers.
- `netclaw-subagents`: Adds stable run and parent-call correlation to sub-agent
  activity output.
- `session-resume`: Restores structured settled events instead of role and text
  content alone.
- `tool-call-metadata`: Enforces the required rationale on new tool calls while
  old transcript data remains readable.
- `netclaw-testing`: Requires event-contract coverage and disposable visual
  proof for the chat surface.

## Impact

### Netclaw

- `ChatPage` and `ChatViewModel` gain a structured presentation boundary.
- Session output records and SignalR DTOs gain additive correlation and detail
  fields.
- Session resume gains a structured history representation.
- Development review gains temporary chat videos and selected frame images.
- `PRD-004`, engineering specifications, and TUI wireframes change.

### Termina

- `TerminaRuntimeOptions` gains an explicit presentation mode.
- The runtime gains an opt-in primary-buffer host and terminal-owned scroll
  policy.
- The component library gains keyed live blocks and reliable prompt-history
  behavior.
- Existing full-screen applications retain their current default behavior.
- Netclaw can consume a prerelease package until a stable Termina release ships.

### Security

- Approval detail must render control characters as safe visible text.
- Semantic copy must not emit terminal control bytes.
- A compact approval must keep the target, effect, and scope visible.
- Clipboard and terminal-mode failures must produce visible errors.
- New output fields must not bypass existing audience filters or redaction.

### Operations

- The prototype must cover Linux, macOS, Windows Terminal, and tmux.
- Inline mode must restore cursor, input, paste, and terminal modes after every
  normal, canceled, and failed exit.
- The application must reject direct concurrent console output that can corrupt
  the owned live region.
