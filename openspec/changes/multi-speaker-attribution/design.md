## Context

Netclaw's channel adapters support threaded conversations on Slack and Discord.
When a bot is mentioned mid-thread, the thread history backfill pipeline
hydrates prior messages into the first LLM turn. Today, hydrated messages carry
raw platform IDs (`<user: U0123, 2026-04-09 10:15 UTC>`) and live messages
carry no speaker identity at all. Additionally, when `AllowedUserIds` restricts
which users can interact with the bot, non-allowed users' messages in active
threads are hard-blocked at the conversation actor level, even though the
authorized user who tagged the bot may have intended their context to be visible.

The ACL layer currently has two outcomes: Allow and Deny. The Deny path drops
messages before they reach the session binding actor. There is no intermediate
state for "this user can't instruct the bot, but their message should be visible
as context."

The `ChannelPipeline.MapToCommand` method builds `SendUserMessage.Content` by
joining `TextContent` parts. It does not inject any speaker metadata. The
`MessageSource` (which carries `SenderId` and `Principal`) is marked
`[ProtoIgnore]` and is not persisted — it is ephemeral ACL/audit metadata only.

## Goals / Non-Goals

**Goals:**

- The LLM can distinguish who said what in multi-party threads, both for
  hydrated history and live ongoing messages.
- Non-allowed users' messages in active threads are included as read-only
  context with an "observer" role. The LLM is instructed not to execute
  observer instructions.
- A consistent speaker tag format across hydrated and live messages.
- The system prompt explains the authorized/observer distinction when
  `AllowedUserIds` is configured.

**Non-Goals:**

- Display name resolution from platform APIs (future work — raw IDs only).
- Changing the `AllowedUserIds` semantics for thread *creation*. Non-allowed
  users still cannot start a session — Observe only applies to existing threads.
- Per-message tool grant scoping based on speaker role (tool grants are
  session-level, not per-turn).
- Multi-speaker attribution for non-threaded channels (TUI, timer, SignalR).

## Decisions

### D1: Third ACL outcome (Observe) vs. session-level filtering

**Decision**: Add `AclOutcome.Observe` to the ACL layer.

**Alternatives considered**:
- *Session-level filtering*: Keep ACL as Allow/Deny. The binding actor checks
  `AllowedUserIds` and sets Principal accordingly. Downside: the binding actor
  needs access to the allow-list, which is currently a conversation-actor
  concern. Leaks channel config into the session binding.
- *Thread-context pass-through*: All messages in active threads pass through
  regardless of AllowedUserIds. Downside: loses the explicit ACL audit trail
  for observer messages.

**Rationale**: The ACL layer already computes all the relevant signals
(`AllowedUserIds`, `isExplicitUser`). Adding a third outcome there keeps the
authorization decision in one place and provides a clean audit trail. The
conversation actor already has `threadExists` from its routing policy check,
so the new parameter is zero-cost.

### D2: IsObserver flag vs. Principal-only signaling

**Decision**: Add `bool IsObserver` to `ChannelInput` and inbound message
records rather than relying on `PrincipalClassification.UntrustedExternal`.

**Rationale**: `UntrustedExternal` is the default for all non-explicit users
even when `AllowedUserIds` is empty (everyone is allowed). Using Principal
alone conflates "no allow-list, everyone can instruct" with "allow-list
active, this user is an observer." The explicit `IsObserver` flag is
unambiguous.

### D3: Speaker tags on all live messages vs. multi-speaker only

**Decision**: Add `<speaker:>` tags on all live messages, even single-speaker
sessions.

**Rationale**: Consistency with hydrated history (which always has tags). The
tag costs ~40 characters per message. Conditional tagging would require the
pipeline to track whether multiple speakers have been seen, adding complexity
for no meaningful benefit.

### D4: Prompt overlay vs. static AGENTS.md

**Decision**: Use `SessionPipelineOptions.PromptOverlay` for the multi-speaker
instruction-authority guidance.

**Rationale**: The guidance is conditional (only needed when `AllowedUserIds`
is non-empty) and per-session. AGENTS.md is static disk content shared by all
sessions. The overlay mechanism already exists and is plumbed through to the
LLM session actor.

### D5: Speaker tag format

**Decision**: `<speaker: {SenderId}, role={authorized|observer}, {ReceivedAt}>`

**Rationale**: Extends the existing `<user:>` format with the role dimension.
The angle-bracket format is already established in the hydration spec. Adding
`role=` makes it machine-parseable while remaining human-readable.

## Risks / Trade-offs

- **[Breaking tag format]** → Existing sessions with compacted history that
  included the old `<user:>` format will see a format mismatch if the same
  thread is re-hydrated. Mitigation: compacted history is a natural-language
  summary, not raw tags. The tags only appear in the first-turn merged content,
  which is itself compacted away on subsequent turns. No functional impact.

- **[Observer prompt injection]** → An observer could craft a message designed
  to trick the LLM into executing instructions despite the system prompt
  guidance. Mitigation: observer messages still pass through the existing
  `IPromptInjectionDetector` pipeline. The system prompt overlay is a
  defense-in-depth layer, not the sole control. High-risk messages are
  dropped regardless of role.

- **[Token cost of universal speaker tags]** → Adding ~40 characters per
  message increases token consumption slightly. Mitigation: negligible
  compared to typical message content and tool call overhead.

- **[Observe bypassing AllowedUserIds intent]** → Operators who set
  `AllowedUserIds` may expect complete isolation from non-allowed users.
  Mitigation: Observe only activates when a thread session already exists
  (the authorized user chose to engage in that thread). Non-allowed users
  still cannot create sessions. The observer role is clearly labeled and the
  LLM is instructed not to follow their commands.
