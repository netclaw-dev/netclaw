## ADDED Requirements

### Requirement: Discord gateway adapter lifecycle and health

Netclaw SHALL provide a Discord gateway adapter that establishes and maintains a
gateway connection lifecycle equivalent to Slack Socket Mode operationally
(connect, disconnect detection, reconnect attempts, and health reporting).
Adapter startup SHALL fail closed when required Discord security or connection
configuration is invalid.

#### Scenario: Discord adapter reports healthy connection

- **GIVEN** valid Discord adapter configuration is present
- **WHEN** Netclaw starts with Discord enabled
- **THEN** the adapter establishes a gateway connection
- **AND** operator diagnostics report Discord adapter health as connected

#### Scenario: Invalid Discord adapter config fails closed

- **GIVEN** Discord adapter configuration is missing required security-critical fields
- **WHEN** Netclaw starts
- **THEN** startup fails with explicit validation diagnostics
- **AND** Discord ingress does not run in permissive mode

### Requirement: Discord ingress normalization and ACL-gated dispatch

Discord inbound events SHALL be normalized into `SendUserMessage` with complete
source metadata and deterministic session identity. ACL evaluation SHALL run
before session dispatch for all Discord inbound paths.

#### Scenario: Discord inbound message normalized and dispatched

- **GIVEN** a Discord message event from an allowed sender/channel
- **WHEN** the Discord adapter processes the event
- **THEN** it produces `SendUserMessage` with normalized content and metadata
- **AND** it dispatches only after ACL allow decision

#### Scenario: Discord inbound message denied before dispatch

- **GIVEN** a Discord message event from a denied sender/channel
- **WHEN** ACL evaluates the inbound event
- **THEN** the event is denied before session dispatch
- **AND** a structured deny reason is recorded for diagnostics

### Requirement: Discord session identity and reply targeting parity

Discord session identity SHALL be deterministic and thread-aware using
`{channelId}/{threadIdOrMessageId}` where `threadIdOrMessageId` resolves to the
Discord thread ID when present, or the root message ID when not threaded.
Replies SHALL be delivered back to the originating Discord context represented
by that identity.

#### Scenario: Threaded Discord messages route to same session

- **GIVEN** two inbound Discord messages in thread `th-42` under channel `ch-7`
- **WHEN** session keys are derived
- **THEN** both map to `ch-7/th-42`
- **AND** both route to the same session actor

#### Scenario: Non-threaded Discord message uses root message identity

- **GIVEN** an inbound Discord message in channel `ch-7` without thread context
- **WHEN** session key is derived
- **THEN** key is `ch-7/<messageId>`
- **AND** reply delivery targets that originating message context

### Requirement: Text-first slash command compatibility on Discord

Discord adapter behavior SHALL preserve session-level text-first slash command
dispatch for inbound message content beginning with `/` without requiring
Discord app-command registration in MVP.

#### Scenario: Text slash command works without app-command registration

- **GIVEN** Discord app-command registration is not configured
- **WHEN** user sends `/netclaw-operations check health` as a Discord message
- **THEN** slash-command-dispatch processes the message deterministically
- **AND** no Discord platform registration is required for this behavior

### Requirement: Discord interactive approval with deterministic text fallback

The Discord adapter SHALL handle `ToolInteractionRequest` in Discord sessions by
preferring Discord interaction controls when available and SHALL always support
deterministic text fallback with equivalent approval options and outcomes.

#### Scenario: Discord interaction approval path succeeds

- **GIVEN** Discord interaction callbacks are available
- **WHEN** a tool approval request is emitted
- **THEN** the adapter renders interaction controls
- **AND** selected approval decision is routed as `ToolInteractionResponse`

#### Scenario: Interaction path unavailable falls back to text deterministically

- **GIVEN** Discord interaction callbacks are unavailable or fail
- **WHEN** a tool approval request is emitted
- **THEN** the adapter emits a text prompt with deterministic A/B/C/D options
- **AND** text reply parsing routes an equivalent `ToolInteractionResponse`
