## MODIFIED Requirements

### Requirement: Directory enumeration tool

The system SHALL provide a `file_list` first-party tool that returns a
single-level listing of a directory's entries, each entry identified by name
and type. It SHALL be read-only and SHALL NOT create, modify, or remove a
filesystem entry.

`file_list` SHALL be gated by the audience profile `AllowedTools` allowlist.
It SHALL authorize its target through the shared `Read` path access decision.
The read reach SHALL follow the Personal read rule of the `File read tool`
requirement. User approval SHALL NOT widen a file profile.

#### Scenario: Team session lists a directory within its read roots

- **GIVEN** a session resolved to the `Team` audience with `file_list` granted
- **WHEN** the agent invokes `file_list` on its session directory
- **THEN** the tool returns the directory's entries with name and type
- **AND** no filesystem entry is created, modified, or removed

#### Scenario: Public session cannot list outside its session directory

- **GIVEN** a session resolved to the `Public` audience
- **WHEN** the agent invokes `file_list` outside its permitted read roots
- **THEN** the invocation is denied
- **AND** the denial message does not disclose configured root paths

#### Scenario: Personal file_list reaches the write roots

- **GIVEN** a Personal profile with `ReadFiles` mode `Roots` `[A]` and
  `WriteFiles` mode `Roots` `[B]`
- **WHEN** the agent invokes `file_list` on a folder below `B`
- **THEN** the tool returns the entries of that folder

#### Scenario: Counterexample - approval cannot widen explicit read roots

- **GIVEN** an interactive Personal profile with `ReadFiles` mode `Roots` `[A]`
  and `WriteFiles` mode `Roots` `[A]`
- **WHEN** the agent invokes `file_list` on a folder outside `A`
- **THEN** file protection denies the invocation
- **AND** the invocation does not reach user approval

#### Scenario: file_list denied when not granted to the audience

- **GIVEN** an audience profile whose `AllowedTools` omits `file_list`
- **WHEN** the agent invokes `file_list`
- **THEN** the invocation is denied with reason
  `tool_not_allowed_for_audience_profile`

### Requirement: File read tool

The system SHALL provide a `file_read` first-party tool that authorizes the
requested path through the shared `Read` path access decision before inspecting
or reading bytes.

Personal read rule (issue #2554, owner decision 18 Oct 2026): for the
`Personal` audience only, the read reach of a file tool (`file_read`,
`file_list`, `file_search`) SHALL be at least the write reach of the same
profile. The write reach is the shell trust zone. Thus a file that the shell
can write, a file tool can read.

- A `WriteFiles` mode `All` SHALL give the read decision mode `All`.
- A `WriteFiles` mode `Roots` SHALL add the write roots to the read roots.
- A `WriteFiles` mode `None` SHALL leave the read profile unchanged.
- The protected-path read deny list SHALL still apply.
- The rule SHALL apply attended and unattended alike (decision D2).
- The rule SHALL NOT apply to `Team` or `Public`. For those audiences an
  explicit `Roots` or `None` read profile SHALL remain authoritative, also when
  the write profile is wider.

Shell settings and user approval SHALL NOT widen a read profile. Only the
profile itself, with the Personal read rule, gives read authority.

Text-like files SHALL return decoded text for UTF-8,
UTF-16/UTF-32
Unicode, and common Windows-1252 text files using the existing offset/limit and
output-truncation behavior.

For non-text files, `file_read` SHALL NOT return raw binary content. It SHALL
detect the file category using the canonical attachment taxonomy where possible
and return structured metadata plus an explicit next-step message.

Images SHALL be eligible for model-visible handoff only when the active model's
input modalities include image support. The handoff SHALL use session media
references and the existing `DataContent` rehydration path, not binary content in
the tool-result string. Streaming tool-result persistence SHALL retain the media
references needed to recreate the handoff nudge during recovery.

PDF extraction, OCR, audio transcription, and video keyframe extraction SHALL NOT
be built into `file_read`.

#### Scenario: Text file read preserves existing behavior

- **GIVEN** a readable text file using UTF-8, UTF-16/UTF-32 Unicode, or Windows-1252
- **WHEN** the agent invokes `file_read` with optional offset and limit values
- **THEN** the tool returns text content with the existing line pagination and
  truncation behavior

#### Scenario: Example - Personal read reaches a write All profile

- **GIVEN** a Personal profile with `ReadFiles` mode `Roots`
  `[~/repositories, ~/.netclaw]` and `WriteFiles` mode `All` (the live shape
  of issue #2554)
- **WHEN** the agent invokes `file_read` on `/tmp/gifcheck/inline_01.png`
- **THEN** the path access decision allows the read

#### Scenario: Example - Personal read adds the write roots

- **GIVEN** a Personal profile with `ReadFiles` mode `Roots` `[A]` and
  `WriteFiles` mode `Roots` `[B]`
- **WHEN** the agent invokes `file_read` on `B/x` and on `C/x`
- **THEN** the read of `B/x` is allowed
- **AND** the read of `C/x` is denied

#### Scenario: Counterexample - a protected path stays denied

- **GIVEN** a Personal profile with `WriteFiles` mode `All`
- **WHEN** the agent invokes `file_read` on `<config dir>/secrets.json`
- **THEN** file protection denies the invocation

#### Scenario: Counterexample - Team keeps a write-only folder

- **GIVEN** a Team profile with `ReadFiles` mode `Roots` `[{session_dir}]` and
  `WriteFiles` mode `Roots` `[{session_dir}, D]`
- **WHEN** the agent invokes `file_read` on `D/x`
- **THEN** file protection denies the invocation

#### Scenario: Counterexample - approval cannot widen explicit read policy

- **GIVEN** an interactive Personal profile with `ReadFiles` and `WriteFiles`
  both mode `Roots` `[A]`
- **WHEN** the agent invokes `file_read` for a path outside `A`
- **THEN** file protection denies the invocation
- **AND** no shell setting or approval mode widens the read policy

#### Scenario: Image read on image-capable model becomes model-visible

- **GIVEN** a readable PNG file
- **AND** the active model supports image input
- **WHEN** the agent invokes `file_read`
- **THEN** the tool returns metadata indicating the image was loaded for visual
  inspection
- **AND** the next LLM call includes the image through a session media reference

#### Scenario: Sub-agent image read can become model-visible

- **GIVEN** a sub-agent uses `file_read` on a readable PNG file
- **AND** the sub-agent's selected model supports image input
- **WHEN** the tool result is returned to the sub-agent loop
- **THEN** the next sub-agent LLM call includes the image through a session media
  reference

#### Scenario: Image read on text-only model returns modality guidance

- **GIVEN** a readable PNG file
- **AND** the active model does not support image input
- **WHEN** the agent invokes `file_read`
- **THEN** the tool returns metadata and the canonical image modality-gap note
- **AND** no media reference is added to the next LLM call

#### Scenario: PDF read does not extract text

- **GIVEN** a readable PDF file
- **WHEN** the agent invokes `file_read`
- **THEN** the tool returns metadata identifying the file as a PDF
- **AND** the result says native PDF extraction is not built into `file_read`
- **AND** no raw PDF bytes are returned

#### Scenario: Unsupported binary read returns explicit guidance

- **GIVEN** a readable archive, audio file, video file, binary document, or
  unknown binary file
- **WHEN** the agent invokes `file_read`
- **THEN** the tool returns metadata and explicit unsupported-format guidance
- **AND** no raw bytes are returned

### Requirement: Attachment tool reach

All audiences SHALL apply the `ToolPathPolicy` read-deny check to attachment sources.

The system SHALL provide an `attach_file` first-party tool that sends a file to
the user. It SHALL authorize the source with the shared `Attach` path access
decision. For the `Personal` audience, the attach reach SHALL be at least the
write reach of the same profile, by the Personal read rule of the `File read
tool` requirement: a `WriteFiles` mode `All` gives attach mode `All`, and a
`WriteFiles` mode `Roots` adds the write roots to the attach roots. For `Team`
and `Public`, an explicit `Roots` or `None` attach profile SHALL remain
authoritative in every interaction mode. A Personal attach profile with mode
`All` MAY attach an external file after the protected-path checks pass. The tool SHALL
copy an admitted file into the current session's attachments directory before
delivery.

#### Scenario: Interactive Personal session attaches an external file

- **GIVEN** the default interactive Personal attach profile permits an external
  file
- **WHEN** the agent invokes `attach_file` for that file
- **THEN** the tool copies the file into the current session attachments directory
- **AND** the tool sends the copied file to the user

#### Scenario: Example - Personal attach reaches a write All profile

- **GIVEN** a Personal profile with `AttachFiles` mode `Roots` `[A]` and
  `WriteFiles` mode `All`
- **WHEN** the agent invokes `attach_file` on `/tmp/render/out.png`
- **THEN** the tool copies the file into the session attachments directory
- **AND** the tool sends the copied file to the user

#### Scenario: Counterexample - explicit attach roots remain authoritative

- **GIVEN** an interactive Personal profile with `AttachFiles` and
  `WriteFiles` both mode `Roots` `[A]`
- **WHEN** the agent invokes `attach_file` outside that root
- **THEN** file protection denies the invocation
- **AND** user approval does not widen the attach profile

#### Scenario: Protected control-plane file cannot be attached

- **GIVEN** an interactive Personal session requests a protected control-plane
  file
- **WHEN** the agent invokes `attach_file`
- **THEN** the tool denies the request
- **AND** broad Personal file authority does not bypass the denial

`PathAccessPolicy` SHALL validate each tool-managed destination before directory creation or file copy.
The destination SHALL remain inside the current session workspace without links across the workspace or its known storage ancestors.
The destination SHALL pass the protected-path write check, including collision-suffix candidates.
Source attach permission authorizes this bounded copy; the copy SHALL NOT require general `WriteFiles` permission.
These checks are call-local. They do not form an operating-system sandbox against concurrent filesystem changes.

#### Scenario: Counterexample - attachment destination redirects a copy

- **GIVEN** an admitted source outside the current workspace
- **AND** the attachments directory or its session ancestor is a link to another directory
- **WHEN** the agent invokes `attach_file`
- **THEN** the tool returns `AccessDenied` before directory creation or file copy
- **AND** the other directory remains unchanged and the tool emits no attachment

#### Scenario: Counterexample - attachment destination is write protected

- **GIVEN** an admitted source and a write-protected destination
- **WHEN** the agent invokes `attach_file`
- **THEN** the tool returns `AccessDenied` without a destination file or new directory

#### Scenario: Attach-only profile preserves the bounded copy

- **GIVEN** an attach profile admits a source and the write profile is `None`
- **AND** the current workspace destination passes containment, link, and protected-path checks
- **WHEN** the agent invokes `attach_file`
- **THEN** the tool copies the source and emits the attachment
- **AND** an existing destination file retains its bytes through the existing suffix rule
