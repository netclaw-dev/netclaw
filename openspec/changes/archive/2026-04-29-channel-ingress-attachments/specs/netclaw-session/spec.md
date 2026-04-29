## ADDED Requirements

### Requirement: Session actor is a strict modality consumer

The session actor SHALL treat inbound `ChannelInput.Contents` as
authoritative with respect to `DataContent` modalities. The session
SHALL NOT silently strip `DataContent` items whose modality is not
supported by the active model. If the session detects a `DataContent`
item whose modality is not in the active model's reported
`InputModalities`, it SHALL treat that as an ingress bug by:

- Emitting an `ERROR`-level log line naming the active model id, the
  model's declared `InputModalities`, and every offending attachment
  by name and modality, with text stating that the originating
  channel did not apply the cross-channel attachment ingress
  contract before inlining the `DataContent`.
- Dropping the offending `DataContent` items from the turn so the
  provider call does not fail outright.
- Appending a visible `TextContent` line to the turn's contents in
  the form
  `[system] an attachment was received but could not be delivered to the model due to an ingress bug; please retry or notify the operator`
  so the downstream reply cannot silently omit the incident.
- Completing the turn normally otherwise.

The session SHALL NOT substitute a placeholder such as
`[Images removed — the current model does not support vision input]`.
That pattern was a symptom of silent modality decisions happening in
the wrong architectural layer and is explicitly prohibited by this
requirement.

#### Scenario: Image DataContent on text-only model triggers loud error

- **GIVEN** an inbound `ChannelInput` contains a `DataContent` with
  MIME `image/png`
- **AND** the active model reports no `ModelModality.Image`
- **WHEN** the session actor processes the turn
- **THEN** an `ERROR` log is emitted naming the model id, modalities,
  and offending attachment
- **AND** the `DataContent` is dropped from the turn
- **AND** a `[system]` `TextContent` line is appended to the turn
  stating that an ingress bug prevented delivery
- **AND** the turn completes without failing the session

#### Scenario: Correctly routed PDF DataContent passes the consumer check

- **GIVEN** an inbound `ChannelInput` contains a `DataContent` with
  MIME `application/pdf`
- **AND** the active model reports native PDF support
- **WHEN** the session actor processes the turn
- **THEN** no `ERROR` log is emitted
- **AND** the `DataContent` is delivered to the provider unchanged

#### Scenario: No Images-removed placeholder is ever produced

- **GIVEN** any turn on any model configuration
- **WHEN** the session actor processes the turn
- **THEN** no outbound turn contains the text
  `Images removed — the current model does not support vision input`
- **AND** the legacy silent-strip code path has been removed from
  `LlmSessionActor`


### Requirement: Attachment-aware dynamic context layer

The session actor SHALL append a dedicated attachment-handling block to the system prompt during dynamic context layer assembly when, and only when, the session's audience profile grants `file_read`. The block SHALL:

- Name the session working directory's `inbox/` subdirectory as the
  canonical location for user-uploaded files.
- Document the canonical `[attachment]` line format, including the
  mandatory `inlined` field and the conditional `note` field.
- Define how the agent SHOULD interpret `inlined="false"` for each of
  the two canonical `note` prefix classes
  (`current model has no ...` → model-modality gap; `format not inlineable` →
  tool-accessible binary) and direct the agent to use `file_read` or
  `shell_execute` as appropriate when the agent chooses to process
  the file.
- Include an explicit imperative that the agent SHALL acknowledge
  every attachment it received in its reply, by name, even when the
  file was not inlined and cannot be rendered natively — silent
  omission of an unviewable attachment is prohibited.

When the session's audience profile does NOT grant `file_read`, the
session actor SHALL NOT inject the attachment-handling block. In that
case there is no supported path for the agent to inspect inbox files,
and injecting the block would advertise a capability the session
does not have.

The block SHALL be static text sourced from a single shared
constant, so wording does not drift per session or per channel.
Dynamic context layer injection SHALL remain compatible with existing
layers (skill index, recall bundle, project context, etc.).

#### Scenario: Team audience with file_read gets the attachment block

- **GIVEN** a session resolves to `TrustAudience.Team` with a profile
  that grants `file_read`
- **WHEN** the session actor assembles the system prompt
- **THEN** the system prompt contains the attachment-handling block
- **AND** the block names `inbox/` and the `[attachment]` line format
- **AND** the block explains both canonical `note` prefix classes

#### Scenario: Audience without file_read does not get the block

- **GIVEN** a session resolves to an audience profile that does NOT
  grant `file_read`
- **WHEN** the session actor assembles the system prompt
- **THEN** the attachment-handling block is NOT appended
- **AND** no text referring to `inbox/` or `[attachment]` is added

#### Scenario: Block instructs the agent to acknowledge unviewable attachments

- **GIVEN** the attachment-handling block is present in a system
  prompt
- **WHEN** the block content is inspected
- **THEN** it contains an imperative sentence directing the agent to
  acknowledge any attachment it cannot view natively, by name, in
  its turn reply
