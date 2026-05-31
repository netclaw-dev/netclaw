# netclaw-input-adapters Specification

## Purpose

Define how inbound channel adapters construct trusted inputs, preserve channel
capabilities and provenance, and resolve message audience.
## Requirements
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

### Requirement: Inbound adapters supply explicit trust context

Every inbound channel adapter SHALL stamp complete, explicit trust context —
audience, principal, boundary, and provenance — onto each `ChannelInput` it
constructs. The session pipeline SHALL NOT synthesize a default audience,
principal, boundary, or provenance for an inbound message. The
`ChannelInput`-to-`MessageSource` factory SHALL carry trust context through by
direct assignment, with no null-coalescing fallback.

#### Scenario: Adapter omitting trust context fails to compile

- **WHEN** an inbound adapter constructs a `ChannelInput` without every trust
  field set
- **THEN** the build fails with a missing-required-member error

#### Scenario: History-fetched messages carry the resolved audience

- **GIVEN** a Slack DM configured with `Slack.ChannelAudiences["dm"] = "personal"`
- **WHEN** the thread-history fetcher converts a historical message into a
  `ChannelInput`
- **THEN** the `ChannelInput` carries `TrustAudience.Personal` as resolved by
  the channel's audience policy
- **AND** the pipeline applies Personal-level grants without any Public
  fallback

#### Scenario: Pipeline does not synthesize trust context

- **WHEN** the message-source factory builds a `MessageSource` from a
  `ChannelInput`
- **THEN** every trust field on the `MessageSource` is the value carried on the
  `ChannelInput`
- **AND** no value originates from a pipeline-level default

### Requirement: Direct-message audience resolution requires an operator-vetted sender

A channel adapter resolving the audience for an inbound direct message SHALL
resolve it to the `Team` audience only when the sender is an operator-
allowlisted user or the conversation is an explicitly allowlisted channel. A
direct message from a sender who is not on the channel allow-list SHALL
resolve to the `Public` audience.

Explicit `ChannelAudiences` overrides SHALL continue to take precedence: an
operator MAY map the `dm` key, or a specific channel id, to any audience, and
that override SHALL be honored ahead of the default resolution above.

#### Scenario: DM from a non-allowlisted user resolves to Public

- **GIVEN** direct messages are enabled with an empty allowed-users list
- **AND** no `ChannelAudiences` override applies
- **WHEN** a user who is not on the allow-list sends a direct message
- **THEN** the resolved audience is `Public`

#### Scenario: DM from an allowlisted user resolves to Team

- **GIVEN** a user is on the channel allowed-users list
- **WHEN** that user sends a direct message
- **THEN** the resolved audience is `Team`

#### Scenario: dm audience override takes precedence

- **GIVEN** `ChannelAudiences["dm"]` is set to `team`
- **WHEN** a non-allowlisted user sends a direct message
- **THEN** the resolved audience is `Team` as specified by the override

### Requirement: Attachment ingress uses catalog-backed media classification

Channel attachment ingress SHALL classify inbound files through the shared media
catalog. Broad MIME prefixes such as `image/`, `audio/`, and `video/` SHALL NOT
grant attachment categories unless the concrete MIME type is explicitly present
in the catalog.

#### Scenario: Public image attachment uses explicit catalog support

- **GIVEN** a Public session receives an attachment declared as `image/png`
- **AND** the media catalog classifies `image/png` as `Image`
- **WHEN** channel attachment policy is evaluated before download
- **THEN** the Public profile can allow the attachment category

#### Scenario: Unknown media subtype does not bypass Team policy

- **GIVEN** a Team session receives an attachment declared as `video/x-unknown`
- **WHEN** channel attachment policy is evaluated before download
- **THEN** the attachment is not accepted as `Media` by prefix alone
- **AND** it is rejected unless the catalog explicitly supports that MIME type

### Requirement: Attachment ingress uses verified MIME after content scanning

Channel attachment ingress SHALL keep declared transport MIME separate from
scanner-verified MIME. Accepted attachment announcements, inline `DataContent`,
session media references, and logs that describe delivered file type SHALL use
the verified canonical MIME returned by the scanner. Declared MIME MAY be logged
as source metadata.

#### Scenario: Octet-stream PNG is delivered as verified PNG

- **GIVEN** a channel attachment is declared as `application/octet-stream`
- **AND** its filename and bytes validate as PNG
- **WHEN** the attachment is accepted
- **THEN** the attachment announcement uses `image/png`
- **AND** any inlined `DataContent` uses `image/png`

#### Scenario: Spoofed image is rejected before delivery

- **GIVEN** a channel attachment is declared as `image/png`
- **AND** its bytes contain an executable signature
- **WHEN** the content scanner evaluates the file
- **THEN** the attachment is rejected
- **AND** no `DataContent` or session media reference is produced

