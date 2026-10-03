## ADDED Requirements

### Requirement: Inline chat uses the primary terminal buffer

`netclaw chat` SHALL use the Termina inline presentation mode after the native
prototype passes the supported-terminal matrix. The primary terminal buffer
SHALL own settled transcript scrollback, native selection, and terminal search.
Termina SHALL own only a bounded live region and the active input surface.

The client SHALL fail with a visible diagnostic when inline mode cannot start.
It SHALL NOT silently fall back to the full-screen mode.

#### Scenario: Chat starts in the primary buffer

- **WHEN** an operator starts `netclaw chat` in a supported terminal
- **THEN** Termina does not enter the alternate screen
- **AND** settled output remains in native terminal scrollback

#### Scenario: Inline mode cannot start

- **GIVEN** the terminal cannot satisfy the inline-mode contract
- **WHEN** the operator starts `netclaw chat`
- **THEN** the client reports the unsupported terminal state
- **AND** the client does not start a full-screen chat as a fallback

### Requirement: Chat has named semantic regions

The chat SHALL use these named regions: Session Header, Transcript, Turn, Live
Deck, Activity Group, Event Row, Decision Gate, Composer, Hint Line, and
Inspector. The Transcript SHALL remain borderless. The Composer MAY use one
small border to identify editable text.

The Session Header and Hint Line SHALL be printed contextual content. They
SHALL NOT require fixed full-screen coordinates.

#### Scenario: Idle chat shows the primary regions

- **WHEN** a new chat becomes ready for input
- **THEN** the Session Header identifies the session and model
- **AND** the settled Transcript has no outer border
- **AND** the Composer shows the current input contract

#### Scenario: Active turn retains the Composer

- **WHEN** a submitted turn remains active
- **THEN** the Live Deck shows current work above the bottom dock
- **AND** the Composer remains available for later prompts
- **AND** the Hint Line shows the active input actions

### Requirement: Settled transcript content is immutable

Netclaw SHALL print each settled event once in chronological order. A settled
event SHALL leave the Live Deck and SHALL NOT receive later screen updates.
Each volatile event SHALL use a stable identity before settlement.

#### Scenario: Tool result settles one row

- **GIVEN** a tool row in the Live Deck has a stable `CallId`
- **WHEN** its terminal result arrives
- **THEN** Netclaw prints one settled block for that `CallId`
- **AND** Netclaw removes only that row from the Live Deck
- **AND** no later result can replace that settled block

#### Scenario: Parallel results arrive out of order

- **GIVEN** calls A, B, and C are active in one Activity Group
- **WHEN** their results arrive in the order B, C, and A
- **THEN** each result updates only its matching `CallId`
- **AND** all three settled records remain visible

### Requirement: Event lifecycle and display state are independent

Each Event Row SHALL have one lifecycle state and one display state. Lifecycle
states SHALL include Queued, Active, Succeeded, Failed, Denied, and Canceled.
Display states SHALL include Summary, Expanded, Selected, and Hidden.

A display action SHALL NOT change execution state or approval state.

#### Scenario: Expand an active event

- **GIVEN** an active Event Row has more detail
- **WHEN** the operator expands that row
- **THEN** its display state becomes Expanded
- **AND** its lifecycle state remains Active

### Requirement: Chat presents complete structured session output

The chat SHALL define distinct forms for user text, assistant text, thought,
tool call, tool result, sub-agent activity, approval, file, error, usage,
compaction, title, processing state, and turn outcome events.

Each form SHALL preserve every security-safe field that affects operator
understanding. The compact form MAY summarize a long value only when the
Inspector provides the complete value.

#### Scenario: Error output retains diagnostic identity

- **WHEN** the client receives an error with category, correlation ID, message,
  and detail
- **THEN** the compact row shows the category, message, and short correlation ID
- **AND** the Inspector provides the complete detail

#### Scenario: Usage output retains provider detail

- **WHEN** the provider supplies input, output, cached, and reasoning tokens
- **THEN** the turn usage form retains all supplied token classes
- **AND** narrow layouts remove low-priority display fields without changing
  the underlying event data

### Requirement: Thought activity gives immediate visible feedback

The first thought delta SHALL create an active Thought Row. Later deltas SHALL
update the same row. The settled form SHALL show duration and reasoning tokens
when available. Thought content SHALL follow provider and policy disclosure
rules.

#### Scenario: Thought precedes assistant text

- **WHEN** a model emits thought deltas before assistant text
- **THEN** the chat shows an active Thought Row after the first delta
- **AND** the row remains distinct from assistant content

#### Scenario: Thought disclosure is forbidden

- **GIVEN** provider or policy rules forbid thought-content disclosure
- **WHEN** the model reasons
- **THEN** the chat shows an active state without the hidden thought content
- **AND** no semantic copy path exposes that content

### Requirement: Decision Gate preserves approval context

A pending approval SHALL replace the Composer with a Decision Gate. The compact
state SHALL show the tool, target, effect, scope, and decision choices. `Ctrl+O`
SHALL toggle the complete safe detail without changing the selected decision.

One Escape press SHALL deny the request. Paste input SHALL NOT reach the hidden
Composer. Approval detail SHALL render terminal control characters as visible
safe text.

#### Scenario: Approval detail expands without a decision

- **GIVEN** Allow once is selected in a compact Decision Gate
- **WHEN** the operator presses `Ctrl+O`
- **THEN** the gate shows complete safe detail
- **AND** Allow once remains selected
- **AND** no approval response is sent

#### Scenario: Escape denies a pending approval

- **WHEN** the Decision Gate owns input and the operator presses Escape
- **THEN** Netclaw sends one denial response for that request
- **AND** the hidden Composer receives no Escape input

#### Scenario: Long approval detail uses a bounded view

- **GIVEN** expanded approval detail exceeds its maximum height
- **WHEN** the operator presses PageUp or PageDown
- **THEN** only the detail viewport moves
- **AND** the selected approval decision remains unchanged

### Requirement: Composer uses developer chat input conventions

Bare Enter SHALL submit the prompt. `Shift+Enter` SHALL add a newline. Up and
Down SHALL traverse prompt history at text boundaries. Down after the newest
history entry SHALL restore the saved draft.

Two Escape presses inside a `TimeProvider`-based window SHALL clear the prompt.
One Escape press SHALL preserve it. Multiline paste SHALL submit the exact
original content after its compact display summary.

#### Scenario: Shift Enter inserts a newline

- **GIVEN** the Composer owns input
- **WHEN** the operator presses `Shift+Enter`
- **THEN** the Composer inserts one newline
- **AND** Netclaw does not submit the prompt

#### Scenario: Down restores the draft

- **GIVEN** the operator has a draft and recalls an older prompt
- **WHEN** the operator moves down past the newest history entry
- **THEN** the Composer restores the original draft exactly

#### Scenario: Double Escape clears recalled text

- **GIVEN** a recalled prompt is in the Composer
- **WHEN** the operator presses Escape twice within the configured window
- **THEN** the Composer clears all prompt text and history-recall state

#### Scenario: Single Escape preserves text

- **GIVEN** a nonempty Composer owns input
- **WHEN** the operator presses Escape once and the window expires
- **THEN** the prompt text remains unchanged

### Requirement: Active-turn prompts form one follow-up batch

The client SHALL send each prompt through the current session input path while
the current turn remains active. The session actor SHALL retain each accepted
prompt in FIFO order. The Queue Shelf SHALL show every retained prompt.

At the next turn boundary, the session actor SHALL drain the complete retained
set before one follow-up model call. It SHALL NOT start one model call for each
retained prompt. The client SHALL remove the complete promoted set from the
Queue Shelf together.

If a send fails before admission, the client SHALL retain that prompt for the
ordered reconnect path. It SHALL show the reconnect state and SHALL NOT discard
the prompt.

#### Scenario: Three prompts join one follow-up model call

- **GIVEN** one model call remains active
- **WHEN** the operator submits prompts A, B, and C
- **THEN** the session actor retains A, B, and C in that order
- **AND** the Queue Shelf shows A, B, and C
- **AND** one follow-up model call includes A, B, and C in that order
- **AND** no separate model call starts for B or C

#### Scenario: Current turn completes

- **GIVEN** the Queue Shelf shows prompts A, B, and C
- **WHEN** the current turn completes
- **THEN** the complete displayed set leaves the Queue Shelf together
- **AND** the settled transcript retains A, B, and C in FIFO order

#### Scenario: Queued prompt send fails

- **GIVEN** the operator submits a prompt during an active turn
- **WHEN** session ingress rejects or cannot deliver the prompt
- **THEN** the client retains the prompt for ordered reconnect delivery
- **AND** the client shows a visible reconnect state
- **AND** the client does not report successful admission

### Requirement: Inspector and copy use semantic event data

The Inspector SHALL show complete tool arguments, tool results, error detail,
file metadata, and allowed thought detail. It SHALL support copy for one event
and one complete Turn.

Semantic copy SHALL exclude borders, rails, spinners, selection markers, hints,
ANSI sequences, and transient elapsed-time frames. A clipboard failure SHALL
produce a visible error.

#### Scenario: Copy a complete tool result

- **GIVEN** a compact tool row summarizes a long result
- **WHEN** the operator copies that event
- **THEN** the clipboard text contains the complete result
- **AND** the text contains no decorative screen characters

#### Scenario: Clipboard transfer fails

- **WHEN** every configured clipboard transport rejects the copy request
- **THEN** the chat reports a visible copy failure
- **AND** the chat does not report copy success

### Requirement: Chat layout degrades by semantic priority

The chat SHALL retain event identity, lifecycle state, approval choices, error
state, input text, and detail availability at every supported width. It SHALL
remove optional metadata before required state clips.

The automated layout matrix SHALL cover 40, 60, 80, and 120 columns.

#### Scenario: Render at 40 columns

- **WHEN** the chat renders parallel tools and a sub-agent at 40 columns
- **THEN** every event remains a distinct block
- **AND** every lifecycle state remains visible
- **AND** optional metadata leaves before required labels clip

### Requirement: Inline output has one owner

All interactive chat output SHALL pass through the inline host while it owns a
live region. Direct concurrent writes that could corrupt that region SHALL fail
with a visible diagnostic or route through the host.

#### Scenario: Background output reaches the chat process

- **GIVEN** the inline host owns a live region
- **WHEN** a background component attempts a direct console write
- **THEN** the output routes through the inline host or fails visibly
- **AND** the live region and settled transcript remain valid
