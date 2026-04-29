## Why

Netclaw has no canonical contract for how channels deliver user-uploaded
file attachments to an agent session. Today the only channel that exists
(Slack Socket Mode) silently drops every non-image attachment at
`SlackThreadBindingActor.cs:218`, and `LlmSessionActor.cs:1705-1719`
additionally strips images from any session whose model lacks vision — the
user gets back a terse `[Images removed ...]` placeholder with no path
forward. A real user hit this in session `D0AC6CKBK5K/1775921191.341069`
by DMing a PDF; the agent reported it saw nothing. Both failures are the
same class of bug: silent drops buried in the wrong layer, with no
cross-channel contract to prevent the next channel (Discord, Microsoft
Teams, web widget) from reinventing them. This change establishes that
contract once, uses it to fix Slack, and leaves Discord and future channels
a paved path to implement the same behavior. It also resolves a latent
security gap: there is currently no audience-trust gate on inbound files,
so a `file_share` event from a public channel would be treated the same
as one from a DM. This change fixes that using the existing `TrustAudience`
taxonomy — images are safe at every trust level, but PDFs, documents, and
archives are rejected in `Public` channels by default because processing
them typically routes through `shell_execute` on user-controlled bytes.
Relevant PRDs: **PRD-009 (Input Adapters and Unified Input)** and
**PRD-002 (Gateway Security Envelope)** — both are silent on file
attachments today and gain a new section as part of this change.

## What Changes

1. **Canonical cross-channel attachment ingress contract.** Every input
   adapter (Slack today, Discord tomorrow, any future channel) SHALL
   produce a uniform pipeline for user-uploaded files:
   - Download via the adapter's transport-specific mechanism.
   - Content-scan the raw bytes through `IContentScanner` **before** any
     disk write or LLM exposure (unchanged requirement).
   - Apply per-audience attachment policy — reject with a loud,
     user-visible reply if the MIME category is not allowed for the
     resolved `TrustAudience` of the inbound message.
   - Enforce a per-file size cap and a per-message file-count cap;
     reject with user-visible replies on overflow.
   - Write accepted files atomically to
     `{SessionDirectory}/inbox/{safeFilename}` using the durable base
     path from `NetclawPaths.SessionsDirectory` — **never** the legacy
     `Path.GetTempPath()` overload of `SessionDirectoryHelper`.
   - Filesystem-level collision suffixing (`foo.pdf`, `foo_1.pdf`,
     `foo_2.pdf`, ...) across turns, not just within a single inbound
     message batch, so a turn-3 upload is not silently overwritten by a
     turn-7 upload with the same name.
   - Inject a `TextContent` block into `ChannelInput.Contents` of the
     form:
     ```
     [attachment] name="<orig>" mime="<type>" size=<bytes> path="inbox/<safe>" inlined="true|false" [note="<one-sentence reason>"]
     ```
     One line per accepted file, batched into a single `TextContent`
     when a message carries multiple files. The `inlined` field is
     machine-parseable and mandatory; the `note` field is present only
     when `inlined="false"` and carries a short, agent-facing
     explanation so the agent can reply to the user without having to
     reverse-engineer the gap between what it received and what it can
     natively "see".
   - Additionally inline as `DataContent` only when the session's active
     model reports the relevant `ModelModality` via
     `ModelCapabilityActor` — `image/*` when `ModelModality.Image` is
     set, `application/pdf` when the model natively accepts PDFs.
     Non-inlineable files are path-only; the agent uses its existing
     `file_read` / `shell_execute` / `file_edit` tools to process them
     on demand.
   - **Inlined vs. path-only is an explicit signal to the agent, not an
     inference.** The `inlined` field on each `[attachment]` line tells
     the agent whether the file is viewable natively in this turn. When
     it is not, the `note` field distinguishes *why*, using one of two
     canonical classes so the agent can respond intelligently:
     - **Model-modality gap** — the file is a class the model *could*
       handle on a different deployment (image on a text-only model,
       PDF on a model without native document support). The note says
       so, and the agent is expected to tell the user it can see the
       file exists but cannot view its contents on the current model,
       and can offer tool-based workarounds where they apply (e.g.,
       `shell_execute pdftotext` for a PDF on a non-PDF-capable model).
     - **Format-not-inlineable** — the file has no native model
       representation regardless of deployment (docx, zip, video,
       archive, octet-stream). The note says so, and the agent is
       expected to use `file_read` / `shell_execute` to process the
       bytes on demand, exactly as it would with any other file in the
       session working directory.
     The exact `note` strings are normative and defined in the spec so
     the agent's behavior does not drift with ad-hoc phrasing. Example
     lines:
     ```
     [attachment] name="report.pdf" mime="application/pdf" size=284512 path="inbox/report.pdf" inlined="true"
     [attachment] name="diagram.png" mime="image/png" size=12345 path="inbox/diagram.png" inlined="false" note="current model has no image modality; file is on disk but not viewable this turn"
     [attachment] name="notes.docx" mime="application/vnd.openxmlformats-officedocument.wordprocessingml.document" size=51234 path="inbox/notes.docx" inlined="false" note="format not inlineable; use file_read or shell_execute to process"
     ```

2. **Per-audience attachment policy** on `ToolAudienceProfile`.
   `ToolAudienceProfile` gains a `ChannelAttachments` record carrying an
   `AllowedCategories` set (`Image`, `Pdf`, `Document`, `Archive`,
   `Media`, `Other`), a `MaxFileBytes` cap, and a `MaxFilesPerMessage`
   cap. Defaults:

   | Audience | AllowedCategories |
   |---|---|
   | `Public` | `{ Image }` |
   | `Team` | `{ Image, Pdf, Document, Archive, Media }` |
   | `Personal` | `{ Image, Pdf, Document, Archive, Media, Other }` |

   Default size cap: 25 MiB. Default file-count cap: 10. Operators can
   widen or narrow any cell via config.

3. **BREAKING (internal): remove the silent image strip from
   `LlmSessionActor`.** The capability-gated routing decision moves to
   ingress. `LlmSessionActor` replaces lines 1705-1719 with a loud
   log+assertion: if an unsupported modality reaches the session actor,
   that is a bug in the ingress adapter, not something to paper over.
   This tightens the contract: ingress is authoritative for modality
   routing.

4. **Session working directory hint in dynamic context.** When a session
   has `file_read` granted, `LlmSessionActor.InjectDynamicContextLayers`
   appends a short block to the system prompt that (a) tells the agent
   uploaded attachments appear under `inbox/` and are announced via
   `[attachment] ... path="inbox/..." inlined="true|false"`, (b) defines
   how to interpret `inlined="false"` with each of the two canonical
   `note` classes, and (c) directs the agent to acknowledge to the user,
   in its turn reply, any attachment it received but could not view
   natively — never silently. Without this, the agent neither knows to
   call `file_read` on paths it sees in inbound text nor recognizes
   when it should tell the user "I see the file but can't view its
   contents directly."

5. **Eval cases: attachment round-trips.** Per CLAUDE.md's Eval Suite
   rule, add regression cases to `evals/` covering the three behaviors
   this change makes normative:
   - **Inlined happy path**: user uploads a PDF in a `Team`-trust
     channel on a PDF-capable model; agent answers a question about its
     contents. Exercises ingress + capability gate + `DataContent`
     inlining + system-prompt hint in one pass.
   - **Model-modality gap**: user uploads an image on a text-only
     model; agent acknowledges the attachment by name and explains it
     cannot view the image on the current model. Exercises the
     `inlined="false" note="current model has no image modality..."`
     path and the system-prompt hint telling the agent to surface this
     to the user.
   - **Format-not-inlineable**: user uploads a `.docx` in a `Team`
     channel; agent uses `shell_execute` to extract text and answers.
     Exercises the path-only `[attachment]` announcement and the
     agent's use of file tools on an uninlinable format.

## Capabilities

### New Capabilities

_None._ The cross-channel attachment contract is most naturally a
requirement of the existing `netclaw-input-adapters` capability rather
than a standalone spec — that's where "what every channel must do" lives.

### Modified Capabilities

- `netclaw-input-adapters`: adds normative requirements for the
  cross-channel attachment ingress contract (download, scan, audience
  policy, size caps, inbox write, `[attachment]` text injection,
  capability-gated `DataContent` inlining, loud rejection replies).
  This is the canonical surface Discord and future channels will
  implement.
- `netclaw-slack-socket`: Slack-specific delta — replace the
  `image/`-only allowlist in `SlackThreadBindingActor`, expand the
  existing image-download path to cover all audience-allowed MIME
  categories, and update the thread-history backfill path
  (`#### Scenario: Historical images included as DataContent`,
  spec.md:170) so historical attachments of any permitted category are
  replayed, not just images.
- `tool-approval-gates`: `ToolAudienceProfile` gains the
  `ChannelAttachments` policy surface (category set, size cap,
  file-count cap) as a new per-audience configuration field. Default
  values per `Public` / `Team` / `Personal` are normative. This spec is
  the existing home of `ToolAudienceProfile`-shaped per-audience config
  (via "Tool approval configuration per audience"), so attachment
  policy extends that surface rather than introducing a parallel one.
- `netclaw-session`: the session layer stops silently stripping
  `DataContent` on capability mismatch and instead asserts that ingress
  delivered valid modalities. Adds a new requirement for strict
  modality consumption and a second new requirement for the
  attachment-aware dynamic-context hint injected into the system
  prompt when `file_read` is granted.

## Impact

### Code

- `src/Netclaw.Channels.Slack/SlackThreadBindingActor.cs` — rewrite the
  attachment loop (lines 213-267) around the new contract.
- `src/Netclaw.Channels.Slack/SlackChannelOptions.cs` — plumb
  configurable size/count caps if the operator wants to override the
  `ToolAudienceProfile` defaults on a per-workspace basis.
- `src/Netclaw.Configuration/ToolAudienceProfiles.cs` — add the
  `ChannelAttachments` record and per-audience defaults.
- `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` —
  schema-sync for the new policy fields (CLAUDE.md Configuration Schema
  Sync Rule). Include `"default"` values so `netclaw doctor --fix` can
  auto-migrate existing configs.
- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs` — remove silent
  strip (lines 1705-1719), add dynamic-context hint in
  `InjectDynamicContextLayers` (~line 2254).
- `src/Netclaw.Actors/Protocol/SessionDirectoryHelper.cs` — audit
  callers; every production callsite must pass the
  `NetclawPaths.SessionsDirectory` base path, not the legacy
  `Path.GetTempPath()` overload. The legacy overload stays for tests
  but gets `[Obsolete]` with a migration message pointing at the
  durable overload.
- `src/Netclaw.Security/FilenameSanitizer.cs` — reuse as-is (verified
  solid: strips `..`, nulls, control chars, platform-problematic
  characters). No changes required.
- `evals/` — new PDF round-trip regression case.

### APIs and contracts

- **Internal BREAKING**: `LlmSessionActor` stops silently stripping
  unsupported modalities. Any channel adapter that currently relies on
  this safety net will break; the expectation is that ingress does the
  right thing. Slack is the only current adapter and this change updates
  it in the same PR, so external impact is zero.
- **Config schema**: additive new fields on `ToolAudienceProfile`. Old
  configs are auto-migrated by `netclaw doctor --fix` via the
  `SchemaFixResolver` path (CLAUDE.md Configuration Schema Sync Rule).

### Security and operations

- **Net reduction in attack surface.** Today, any non-image file
  silently goes to `/dev/null`, meaning the security model is
  accidentally "deny all documents regardless of channel trust". This
  change explicitly allows documents in trusted audiences (`Team`,
  `Personal`) where the workspace auth fence is meaningful, while
  tightening the `Public` default to images-only — a loud, auditable
  policy rather than an accidental one.
- **No silent fallbacks anywhere on the path.** Every rejection
  (unsupported category, oversize, too many files, scan failure,
  capability-check failure) produces a user-visible reply stating what
  was rejected and why. Per CLAUDE.md "No silent fallbacks" rule this is
  a hard requirement, not an aspiration.
- **Disk footprint**: per-session session directory may grow. The
  working directory already exists for tool-driven writes; this change
  adds an `inbox/` sibling to `media/`. Existing session lifetime /
  cleanup behavior applies unchanged — if it's adequate for tool
  outputs today it is adequate for ingest files.
- **Operational telemetry**: each accepted inbound file emits an `INFO`
  log (`name`, `mime`, `size`, `audience`, `categoryDecision`,
  `inlined: true|false`). Each rejection emits a `WARN` log with the
  same fields plus the rejection reason. These replace the current
  `DEBUG` silent-drop log.

### In-scope for MVP

1. Contract + Slack implementation + `ToolAudienceProfile` policy
   surface + `LlmSessionActor` silent-strip removal + dynamic-context
   hint + PDF eval case + doc updates to PRD-009 and PRD-002.
2. All categories (`Image`, `Pdf`, `Document`, `Archive`, `Media`,
   `Other`) mapped from MIME prefixes in code, so adding a new category
   is a one-file change.
3. Schema migration via `netclaw doctor --fix`.

### Out-of-scope (explicit non-goals)

1. Server-side document extraction, OCR, or conversion. The agent uses
   its existing `shell_execute` / `file_read` tools on demand; the
   ingress layer just delivers bytes.
2. Discord / Teams / web widget adapters. This change defines the
   contract they will follow; implementation of those adapters is
   separate work.
3. Per-user or per-sender attachment allowlists (orthogonal to
   audience). Can be layered on later via ACL extensions.
4. Outbound attachments — `attach_file` already covers the agent-writes
   path and is not touched.
5. Slack file-threading niceties (quoting, reactions on the source
   message, etc.). Unchanged.
