## ADDED Requirements

### Requirement: Canonical cross-channel attachment ingress contract

Every input adapter that transports user-originated file attachments SHALL implement the attachment ingress pipeline defined by this requirement before building the `ChannelInput.Contents` for a `SendUserMessage`, and the session actor SHALL NOT infer, strip, or rewrite attachments because ingress is the authoritative layer for modality routing and attachment policy. This requirement applies to the Slack Socket Mode adapter, the forthcoming Discord adapter, and any future transport (Teams, web widgets, etc.) that surfaces file uploads.

For every file attached to an inbound message, the adapter SHALL:

1. Resolve the `TrustAudience` for the inbound message using the
   adapter's audience-classification path, which MUST run before any
   attachment-specific processing.
2. Evaluate the file's MIME type against
   `ToolAudienceProfile.ChannelAttachments.AllowedCategories` for the
   resolved audience. If the file's category is not permitted, the
   adapter SHALL reject the file before downloading any bytes and
   SHALL post a user-visible reply naming the file and the category
   that is not allowed for the current audience.
3. Compare the adapter-reported file size against
   `ToolAudienceProfile.ChannelAttachments.MaxFileBytes`. Oversize
   files SHALL be rejected before download with a user-visible reply
   naming the file and the size limit.
4. Compare the total number of files on the inbound message against
   `ToolAudienceProfile.ChannelAttachments.MaxFilesPerMessage`. If the
   count exceeds the cap, the adapter SHALL reject the entire message's
   attachments with a single user-visible reply stating the
   per-message limit; text content SHALL still be delivered to the
   session.
5. Download the file bytes using the adapter's transport-specific
   mechanism.
6. Content-scan the downloaded bytes through `IContentScanner.ScanAsync`
   before any write to disk or LLM exposure. Scan rejections (where
   `scanResult.Error` is not `ScanFailure`) SHALL produce a
   user-visible reply with the scanner's reason and the file SHALL NOT
   be written to disk or delivered to the session.
7. Query `ModelCapabilityActor` for the active model's
   `InputModalities` before building `ChannelInput.Contents`. A
   capability query timeout SHALL produce a user-visible reply stating
   that the attachment could not be processed; the adapter SHALL NOT
   guess a modality or silently skip the capability gate.
8. Write accepted files atomically to the session's durable working
   directory at `{SessionDirectory}/inbox/{safeFilename}`, where
   `SessionDirectory` resolves through `NetclawPaths.SessionsDirectory`
   and NOT through any overload of `SessionDirectoryHelper` that
   targets `Path.GetTempPath()`. Filenames SHALL be sanitized via
   `FilenameSanitizer.Sanitize` and collisions SHALL be resolved by
   filesystem-level `_N` suffixing (`foo.pdf` → `foo_1.pdf` → … up to
   `_99`), checking the filesystem and not just the current inbound
   batch so that attachments from earlier turns are never overwritten.
9. Inject exactly one `TextContent` into `ChannelInput.Contents` per
   accepted file, in the canonical format:
   ```
   [attachment] name="<orig>" mime="<type>" size=<bytes> path="inbox/<safe>" inlined="true|false" [note="<one-sentence reason>"]
   ```
   Multiple accepted files from a single inbound message SHALL be
   batched as multiple lines within a single `TextContent` block, in
   the order they appeared on the inbound event. The `inlined` field
   SHALL be machine-parseable and mandatory. The `note` field SHALL
   be present if and only if `inlined="false"`.
10. Additionally append a `DataContent(bytes, mime)` to
    `ChannelInput.Contents` when and only when the file's category is
    natively renderable by the active model's reported modalities:
    `image/*` when `ModelModality.Image` is set, and `application/pdf`
    when the model reports native PDF support. In these cases the
    corresponding `[attachment]` line SHALL have `inlined="true"` and
    omit `note`. In all other cases the `[attachment]` line SHALL
    have `inlined="false"` and include a `note` drawn from the
    canonical note classes defined in the following requirement.
11. Produce no silent drops. Every rejected file — whether by
    category, size, count, scan, capability timeout, inbox write
    failure, or collision exhaustion — SHALL produce exactly one
    user-visible reply on the originating channel explaining what was
    rejected and why, and SHALL be logged at `WARN` or higher with
    file name, MIME type, audience, and rejection reason.

#### Scenario: Slack adapter delivers a PDF in a Team-trust channel on a PDF-capable model

- **GIVEN** a `file_share` event arrives on a channel whose resolved
  audience is `Team`
- **AND** the file is `report.pdf` with MIME `application/pdf` and
  size 284512 bytes
- **AND** `ChannelAttachments.AllowedCategories` for `Team` contains
  `Pdf` and `MaxFileBytes` is 25 MiB
- **AND** the active model reports `InputModalities` containing native
  PDF support
- **WHEN** the adapter processes the event
- **THEN** the adapter downloads the file, scans it, and writes the
  bytes to `{SessionDirectory}/inbox/report.pdf`
- **AND** appends a `TextContent` with the line
  `[attachment] name="report.pdf" mime="application/pdf" size=284512 path="inbox/report.pdf" inlined="true"`
- **AND** appends a `DataContent(bytes, "application/pdf")` to the
  same `ChannelInput.Contents`
- **AND** forwards the `SendUserMessage` to the session parent

#### Scenario: Image attachment on a text-only model routes as path-only

- **GIVEN** a message arrives with an `image/png` attachment in a
  `Team`-trust channel
- **AND** the active model reports no `ModelModality.Image` support
- **WHEN** the adapter processes the event
- **THEN** the file is written to `{SessionDirectory}/inbox/<name>.png`
- **AND** the `TextContent` line has `inlined="false"` and
  `note="current model has no image modality; file is on disk but not viewable this turn"`
- **AND** no `DataContent` is appended for this file

#### Scenario: Word document in a public channel is rejected before download

- **GIVEN** a message arrives in a channel whose resolved audience
  is `Public`
- **AND** the file's MIME is
  `application/vnd.openxmlformats-officedocument.wordprocessingml.document`
  (category `Document`)
- **AND** `ChannelAttachments.AllowedCategories` for `Public` is
  `{ Image }` by default
- **WHEN** the adapter evaluates the file
- **THEN** the adapter SHALL NOT download the file
- **AND** the adapter SHALL post a user-visible reply explaining that
  documents are not allowed in this audience and naming the file
- **AND** no `[attachment]` line or `DataContent` is added to the
  `ChannelInput.Contents`
- **AND** a `WARN` log is emitted with the file name, MIME, audience,
  and rejection reason

#### Scenario: Oversize file is rejected pre-download

- **GIVEN** a message arrives with a file whose reported size is 50
  MiB
- **AND** `ChannelAttachments.MaxFileBytes` is 25 MiB for the resolved
  audience
- **WHEN** the adapter evaluates the file
- **THEN** the adapter SHALL NOT download the file
- **AND** the adapter SHALL post a user-visible reply naming the file
  and the 25 MiB limit
- **AND** the text content of the inbound message SHALL still be
  delivered to the session

#### Scenario: Filename collision across turns does not overwrite prior files

- **GIVEN** a previous turn already wrote `inbox/report.pdf` for the
  same session
- **WHEN** a new inbound message attaches a different file also named
  `report.pdf`
- **THEN** the adapter writes the new file to `inbox/report_1.pdf`
- **AND** the `[attachment]` line references
  `path="inbox/report_1.pdf"`
- **AND** the previous `inbox/report.pdf` is unchanged on disk

#### Scenario: Capability query timeout produces a loud rejection

- **GIVEN** `ModelCapabilityActor` does not respond within the
  adapter's capability query deadline
- **WHEN** the adapter attempts to build the `[attachment]` line
- **THEN** the adapter SHALL post a user-visible reply stating the
  attachment could not be processed and the user should retry
- **AND** the adapter SHALL NOT guess a modality or append any
  `DataContent` for this file
- **AND** a `WARN` log is emitted naming the file and the capability
  timeout

#### Scenario: Multi-file message batches attachment lines into one TextContent

- **GIVEN** an inbound message attaches three accepted files
- **WHEN** the adapter builds `ChannelInput.Contents`
- **THEN** exactly one `TextContent` block contains three
  `[attachment]` lines, one per file, in the order they appeared on
  the inbound event
- **AND** each line carries its own `inlined` and (when applicable)
  `note` fields


### Requirement: Canonical `[attachment]` `note` classes

Every `[attachment]` line with `inlined="false"` SHALL carry a `note` field whose text begins with one of two canonical prefixes defined by this requirement, and adapter implementations SHALL source those strings from a single shared helper so that system prompts, evals, and future tooling can distinguish classes of non-inlined attachments by stable textual signal rather than ad-hoc phrasing.

The two canonical classes are:

- **Model-modality gap** — the file is a class the model could handle
  on a different deployment. The `note` SHALL begin with
  `"current model has no "` followed by the modality name and a short
  remediation hint when one applies. For example:
  `"current model has no image modality; file is on disk but not viewable this turn"`
  or
  `"current model has no native PDF support; use shell_execute (e.g., pdftotext) to extract text"`.
- **Format-not-inlineable** — the file has no native model
  representation regardless of deployment (Office documents, archives,
  video, audio, unknown binary). The `note` SHALL begin with
  `"format not inlineable"` followed by a short tool-based remediation
  hint. For example:
  `"format not inlineable; use file_read or shell_execute to process"`.

Adapter implementations SHALL source `note` strings from a shared
helper so all channels produce identical text for identical
situations. The canonical strings SHALL NOT be rephrased per-channel.

#### Scenario: Non-vision model produces the model-modality note for an image

- **GIVEN** an image attachment on a text-only model
- **WHEN** the adapter writes the `[attachment]` line
- **THEN** `note` begins with `current model has no image modality`

#### Scenario: Non-PDF model produces the model-modality note for a PDF with remediation hint

- **GIVEN** a PDF attachment on a model that reports no native PDF
  modality
- **WHEN** the adapter writes the `[attachment]` line
- **THEN** `note` begins with `current model has no native PDF support`
- **AND** `note` mentions `shell_execute` as a remediation hint

#### Scenario: Docx produces the format-not-inlineable note

- **GIVEN** a `.docx` attachment on any model
- **WHEN** the adapter writes the `[attachment]` line
- **THEN** `note` begins with `format not inlineable`
- **AND** `note` mentions `file_read` or `shell_execute` as a
  remediation hint
