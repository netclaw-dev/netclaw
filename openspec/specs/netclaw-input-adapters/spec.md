## ADDED Requirements

### Requirement: Channel interactive approval capability

Each channel implementation SHALL declare whether it supports interactive
approval via a capability flag (`SupportsInteractiveApproval`). The capability
SHALL be queryable from `ToolExecutionContext` or `MessageSource` at tool
invocation time. Channels that support interactive approval MUST be able to
render `ToolInteractionRequest` outputs and route `ToolInteractionResponse`
messages back to the session actor.

#### Scenario: Slack channel declares approval support

- **GIVEN** the Slack channel is active
- **WHEN** the system queries channel capabilities
- **THEN** `SupportsInteractiveApproval` is `true`

#### Scenario: Headless channel declares no approval support

- **GIVEN** the headless (single-prompt CLI) channel is active
- **WHEN** the system queries channel capabilities
- **THEN** `SupportsInteractiveApproval` is `false`

#### Scenario: Capability flows to tool execution context

- **GIVEN** a session on the Slack channel
- **WHEN** a tool execution context is created
- **THEN** the context includes the channel's `SupportsInteractiveApproval`
  value
- **AND** `ToolAccessPolicy` can use it to determine approval behavior

### Requirement: Text rendering for approval-capable basic channels

Channels that support interactive approval but use text interactions SHALL
render approval prompts as numbered or lettered text option lists and parse
user responses by option number, letter, or keyword matching.

#### Scenario: Text-only channel renders ABCD options

- **GIVEN** a channel with interactive approval support but no rich UI
- **WHEN** a `ToolInteractionRequest` is received
- **THEN** the channel posts:
  ```
  I'd like to run: git push origin main
  Reply with:
    A) Approve once
    B) Approve for this chat
    C) Approve always
    D) Deny
  ```
- **AND** user replies "A", "a", or "approve once" are accepted

#### Scenario: Text-only channel routes parsed response

- **GIVEN** the user replies "B" to an approval prompt
- **WHEN** the channel parses the reply
- **THEN** it sends a `ToolInteractionResponse` with `ApprovedSession`

### Requirement: Adopted-context handoff distinguishes presence from third-party policy

Threaded adapters SHALL preserve two distinct handoff facts when constructing an authorized turn with an adopted window:

- `HasAdoptedContext`: true when the adopted window is non-empty.
- `HasThirdPartyAdoptedContext`: true when any adopted sender id differs from
  the current authorized sender for the executable message.

The handoff SHALL also preserve adopted-speaker provenance as the full set of
sender ids present in the adopted window. That provenance SHALL remain inclusive
even when the adopted window contains only prior messages from the current
authorized sender.

#### Scenario: Self-only adopted window carries truthful handoff metadata

- **GIVEN** a threaded adapter adopts prior messages from the same sender as the
  current authorized message
- **WHEN** it constructs the authorized turn handoff
- **THEN** `HasAdoptedContext` is true
- **AND** `HasThirdPartyAdoptedContext` is false
- **AND** adopted-speaker provenance still includes that sender id

#### Scenario: Mixed-sender adopted window marks third-party state

- **GIVEN** a threaded adapter adopts prior messages from `U111` and `U222`
- **AND** the current authorized sender is `U111`
- **WHEN** it constructs the authorized turn handoff
- **THEN** `HasAdoptedContext` is true
- **AND** `HasThirdPartyAdoptedContext` is true
- **AND** adopted-speaker provenance includes both `U111` and `U222`
