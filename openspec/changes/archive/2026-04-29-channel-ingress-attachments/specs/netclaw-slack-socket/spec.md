## ADDED Requirements

### Requirement: Slack attachment ingestion implements the canonical contract

`SlackThreadBindingActor` SHALL implement the canonical cross-channel
attachment ingress contract defined in `netclaw-input-adapters` for
every `file_share` subtype and every `files` array on an inbound
Slack `message` or `app_mention` event. The current behavior of
hard-coding an `image/*`-only allowlist at the top of the file-handling
loop SHALL be removed; acceptance is driven by
`ToolAudienceProfile.ChannelAttachments.AllowedCategories` for the
resolved `TrustAudience` of the inbound message.

The Slack adapter SHALL:

- Download attachments via `url_private_download` with bot-token
  Bearer auth (unchanged).
- Use `SlackAclPolicy.ResolveAudience` to determine the `TrustAudience`
  before any attachment processing.
- Reject pre-download when category, size, or file-count policy is
  violated, posting the user-visible reply through the same
  `SafePostAsync` path already used for other inbound errors.
- Query `ModelCapabilityActor` with a deadline of 2 seconds via the
  `ActorRegistry` `ModelCapabilityActorKey`; on timeout, post a
  user-visible reply and skip the file.
- Write accepted files to
  `{SessionDirectory}/inbox/<sanitized>` where `SessionDirectory` is
  resolved through `NetclawPaths.SessionsDirectory`, NOT through any
  `SessionDirectoryHelper` overload that uses `Path.GetTempPath()`.
- Emit INFO-level logs for every accepted file including file name,
  MIME, size, resolved audience, the adapter's category decision, and
  whether the file was inlined.
- Emit WARN-level logs for every rejection with the same fields plus
  the rejection reason. The previous DEBUG-level silent-drop log for
  non-image files SHALL be removed entirely; no rejection is ever
  logged below INFO.

#### Scenario: PDF in a Team-trust DM is accepted and inlined

- **GIVEN** a direct message arrives with `report.pdf`
  (`application/pdf`, 284512 bytes)
- **AND** the resolved audience is `Team` and `Pdf` is in
  `AllowedCategories`
- **AND** the active model reports native PDF support via
  `ModelCapabilityActor`
- **WHEN** `SlackThreadBindingActor` processes the event
- **THEN** the file is written to `inbox/report.pdf` in the durable
  session directory
- **AND** the `ChannelInput.Contents` contains an `[attachment]` line
  with `inlined="true"` and a matching `DataContent`
- **AND** an INFO log entry records the accepted file

#### Scenario: Word document in public channel is rejected pre-download

- **GIVEN** a public channel message attaches `notes.docx`
- **AND** the resolved audience is `Public` with default
  `AllowedCategories = { Image }`
- **WHEN** `SlackThreadBindingActor` processes the event
- **THEN** no HTTP download of the file occurs
- **AND** a reply is posted to the originating thread explaining that
  documents are not allowed in public channels
- **AND** a WARN log is emitted with the file name, MIME,
  audience=Public, and rejection reason `category-not-allowed`

#### Scenario: Non-image file no longer silently dropped

- **GIVEN** a file of any non-image MIME type that the current
  audience policy would permit
- **WHEN** `SlackThreadBindingActor` processes the event
- **THEN** the file is NOT skipped with a DEBUG log line
- **AND** the file is processed through the full canonical pipeline
  (policy check, download, scan, capability query, inbox write,
  `[attachment]` injection)

#### Scenario: Capability timeout produces a user-visible reply

- **GIVEN** `ModelCapabilityActor` does not respond to
  `GetModelCapabilities` within 2 seconds
- **WHEN** `SlackThreadBindingActor` needs to decide inlining for a
  permitted file
- **THEN** a reply is posted to the originating thread stating the
  attachment could not be processed and suggesting retry
- **AND** no `DataContent` is appended for this file
- **AND** no `[attachment]` line with a fabricated `inlined` value is
  appended


## MODIFIED Requirements

### Requirement: Merge hydrated content into triggering ChannelInput

Hydrated gap content SHALL be merged directly into the triggering
inbound event's `ChannelInput` rather than delivered as separate
messages. The merge SHALL produce a single `ChannelInput` whose
`Contents` contain:

1. One `TextContent` that begins with the header
   `[thread history — messages exchanged before this inbound event]`,
   contains one entry per gap message with sender attribution and a
   UTC timestamp, ends with `[end thread history]`, and is followed
   by the triggering message's live text.
2. One `TextContent` block carrying an `[attachment]` line per
   attachment from any gap message that passes the same attachment
   policy and capability gates defined in
   `netclaw-input-adapters`. Each line carries the canonical `name`,
   `mime`, `size`, `path="inbox/..."`, `inlined`, and (when
   applicable) `note` fields. Attachments that are rejected by the
   current audience or capability gates SHALL NOT appear as
   `[attachment]` lines; instead the text block for that gap entry
   SHALL note the rejection in the form
   `[attachment rejected: <name> (<reason>)]` so the historical
   context is preserved without leaking prohibited content.
3. Any `DataContent` items from gap messages for files the active
   model's modalities report as natively renderable (e.g., images on
   a vision-capable model, PDFs on a PDF-capable model), produced by
   the same capability gate that controls live-turn inlining.
4. The corresponding `[attachment]` and `DataContent` items for the
   triggering message's own attachments, produced exactly as on a
   non-hydrated turn.

The session layer SHALL receive exactly one `SendUserMessage` for the
triggering event with no special handling.

#### Scenario: Single merged message reaches the session

- **GIVEN** a gap of 3 historical messages and 1 triggering mention
- **WHEN** hydration completes
- **THEN** exactly one `ChannelInput` is written to the input channel
- **AND** its first `TextContent` contains the `[thread history …]`
  block followed by the live mention text

#### Scenario: Historical PDF included as DataContent on a PDF-capable model

- **GIVEN** a gap message has one `application/pdf` attachment in a
  `Team`-trust context
- **AND** the active model reports native PDF support
- **WHEN** the merge runs
- **THEN** the attachment is written to `inbox/` and appears as a
  `DataContent` on the merged `ChannelInput`
- **AND** an `[attachment] ... inlined="true"` line appears in the
  merged `[thread history]` block for that entry

#### Scenario: Historical image on a non-vision model is recorded path-only

- **GIVEN** a gap message has one `image/png` attachment
- **AND** the active model does not report `ModelModality.Image`
- **WHEN** the merge runs
- **THEN** the attachment is written to `inbox/`
- **AND** an `[attachment] ... inlined="false" note="current model has no image modality ..."`
  line appears in the merged `[thread history]` block for that entry
- **AND** no `DataContent` is added for this historical file

#### Scenario: Historical document in a public channel is recorded as rejected

- **GIVEN** the resolved audience is `Public` with default
  `AllowedCategories = { Image }`
- **AND** a gap message in the same channel had a `.docx` attachment
- **WHEN** the merge runs
- **THEN** the attachment SHALL NOT be written to `inbox/`
- **AND** no `[attachment]` line is produced for it
- **AND** the text block for that gap entry records
  `[attachment rejected: notes.docx (category not allowed in Public)]`

#### Scenario: Empty gap produces an unmerged inbound

- **GIVEN** the fetcher returns history but no messages fall strictly
  between the cursor and the triggering event
- **WHEN** the actor builds the merged input
- **THEN** the triggering event is enqueued with its original content
  only
- **AND** no `[thread history …]` block is added


### Requirement: Slack history fetch via conversations.replies

`SlackThreadHistoryFetcher` SHALL implement `IThreadHistoryFetcher`
using `ISlackApiClient.Conversations.Replies`. It SHALL paginate
through all replies, filter out the bot's own messages and any other
messages carrying a `bot_id`, and for every file attachment on any
surviving message it SHALL download the bytes via
`url_private_download` with bot-token Bearer auth and content-scan
the bytes through `IContentScanner` — regardless of MIME type.
Category-, size-, and capability-gating SHALL be applied by the
caller (`SlackThreadBindingActor` during the merge step) using the
same policy as live inbound events, not by the fetcher itself.
Per-attachment download or scan failures SHALL be skipped with a
warning. API-level failures (permission denied, server error) SHALL
return an empty list.

#### Scenario: Paginated fetch for long threads

- **GIVEN** a thread has more than 1000 messages
- **WHEN** the fetcher retrieves the thread
- **THEN** it paginates using the cursor returned by each response
  until no cursor remains
- **AND** returns all messages in chronological order

#### Scenario: Bot messages excluded

- **GIVEN** a thread contains messages from users, the Netclaw bot,
  and a CI bot
- **WHEN** the fetcher retrieves the thread
- **THEN** messages matching the Netclaw bot id are excluded
- **AND** messages carrying any other `bot_id` are excluded
- **AND** only human user messages remain

#### Scenario: All file types downloaded and scanned during fetch

- **GIVEN** a gap message has a mix of images, a PDF, and a docx
- **WHEN** the fetcher processes that message
- **THEN** the fetcher downloads the bytes for every file
- **AND** content-scans every file regardless of MIME type
- **AND** returns the bytes and MIME for each file so the caller can
  apply audience and capability policy at merge time

#### Scenario: Per-file download failure skipped with warning

- **GIVEN** a gap message has two attachments and one returns an HTTP
  403
- **WHEN** the fetcher processes that message
- **THEN** the failed attachment is dropped with a warning naming the
  file ID and the HTTP status
- **AND** the other attachment is returned normally

#### Scenario: API error does not block session creation

- **GIVEN** `conversations.replies` returns a permission error
- **WHEN** the fetcher runs
- **THEN** the fetcher logs a warning and returns an empty list
- **AND** the binding actor enqueues the triggering event with its
  original content only
