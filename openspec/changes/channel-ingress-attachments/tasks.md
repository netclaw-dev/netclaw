> **Scope note (2026-04-11):** the original plan ran 15 phases and 76 tasks.
> After implementing Phases 1–6 it became clear that the core user-reported
> bug (silent PDF drop in Slack ingest) is fixed, and the remaining phases
> should be trimmed to what's actually required for ship-quality on that
> bug — not the "canonical cross-channel contract" vision that ballooned
> out of it. Phases 7, 11, 12, 13, and 15 are explicitly cut from this
> change and tracked as follow-up work in the "Deferred" section below.

## 1. Config surface and schema

- [x] 1.1 Add `AttachmentCategory` enum (`Image`, `Pdf`, `Document`, `Archive`, `Media`, `Other`) to `Netclaw.Configuration`
- [x] 1.2 Add `ChannelAttachmentPolicy` record with `AllowedCategories`, `MaxFileBytes`, `MaxFilesPerMessage`
- [x] 1.3 Add `ChannelAttachments` field on `ToolAudienceProfile` defaulting to `ChannelAttachmentPolicy.Empty` (fail-closed) when not set
- [x] 1.4 Implement `ToolAudienceProfileDefaults` entries for `Public` / `Team` / `Personal` using the matrix from design D4 (Public = {Image}; Team = everything except Other; Personal = all six; 25 MiB; 10 files)
- [x] 1.5 Extend `netclaw-config.v1.schema.json` with `ChannelAttachmentPolicy` + `AttachmentCategory` definitions and default values for `SchemaFixResolver`
- [x] 1.6 Validate size/count caps > 0 when `AllowedCategories` is non-empty; fail daemon startup in `Program.cs` on violation; also surfaced in `ToolAudienceProfilesDoctorCheck`
- [x] 1.7 Unit-test the defaults, validator, and MIME classifier (31 cases in `ChannelAttachmentPolicyTests`)

## 2. Central MIME → category mapping

- [x] 2.1 `AttachmentCategories.FromMime` in `Netclaw.Configuration` as the single classifier
- [x] 2.2 Tests for case-insensitivity, empty/null, unknown → `Other`, all canonical types
- [x] 2.3 Grep audit: the two existing `StartsWith("image/...")` call sites in the Slack channel are retired as part of Phase 6 + Phase 7. No other domain uses MIME→category mapping.

## 3. Session working directory hardening

- [x] 3.1 Removed legacy single-arg `SessionDirectoryHelper.GetSessionDirectory(SessionId)` overload
- [x] 3.2 Migrated all call sites to the base-path overload; audited tests
- [x] 3.3 Added `GetOrCreateInboxDirectory(sessionId, basePath)` + `InboxSubdirectory`/`MediaSubdirectory` constants
- [x] 3.4 Added `IsUnderTempPath` warning in `ToolAudienceProfilesDoctorCheck`
- [x] 3.5 Confirmed no session-directory cleanup hook exists today (neither `media/` nor any other subdirectory). Tracked as a pre-existing leak, out of scope for this change.
- [x] 3.6 `NetclawPaths` is now required on `SessionServices` and `SessionPipeline` — no more null-coalescing fallbacks to `Path.GetTempPath()`. 13 test fixtures migrated via the shared `AddTestNetclawPaths()` helper.
- [x] 3.7 **Log-path leak fix**: moved per-session logs from `{SessionsDirectory}/{id}/logs/` (inside the agent-readable session working dir) to `{LogsDirectory}/sessions/{id}/`, which the agent's `file_read` tool cannot reach. Renamed `NetclawPaths.LegacySessionLogsDirectory` → `SessionLogsDirectory`; updated `SessionLogActor`, `LlmSessionActor`, and `SessionCatalogService`.

## 4. Filename sanitization and atomic inbox write

- [x] 4.1 Verified `FilenameSanitizer.Sanitize` handles `..`, NUL, control chars, absolute paths, Windows reserved names
- [x] 4.2 `InboxWriter.ReserveUniquePath` with filesystem-checked `_1`..`_99` collision suffixing
- [x] 4.3 `InboxWriter.WriteAtomicAsync` via temp-file + `File.Move`
- [x] 4.4 `SanitizeReserveAndWriteAsync` convenience method reusing `FilenameSanitizer`
- [x] 4.5 9 unit tests covering happy path, collisions, exhaustion, temp-file cleanup, path traversal

## 5. ChannelIngressCapabilityQuery helper (built then deleted)

- [~] 5.1 Built `ChannelIngressCapabilityQuery.QueryAsync` + typed `CapabilityQueryResult` in `Netclaw.Actors.Channels`, then **deleted** after Phase 6 revealed the helper had zero production call sites. The Slack implementation reads `_dependencies.ModelCapabilities.InputModalities` directly — the active Main model is already in DI as a singleton with no round-trip cost. The helper was scaffolding for hypothetical future channels (Discord, subagents, failover) that don't exist.
- [~] 5.2 Same fate — typed result record and tests were built then removed.
- [~] 5.3 5 unit tests that never guarded any real code path, removed with the helper.
- **Lesson:** don't pre-build cross-channel scaffolding before the second channel exists. When Discord lands, re-add whatever shape makes sense for its actual needs. ~250 lines of code and tests removed; `AttachmentNotes` is the only cross-channel helper that survived this phase (and is used by Slack today).

## 6. Slack ingress rewrite (`SlackThreadBindingActor`)

- [x] 6.1 Deleted the `image/`-only allowlist and the silent-drop DEBUG log
- [x] 6.2 Implemented the nine-step pipeline (audience gate → size gate → count gate → download → scan → direct modality read → inbox write → `[attachment]` line → DataContent inline). Pre-download gates short-circuit on Slack metadata; no bandwidth burned on rejected files.
- [x] 6.3 Private `BuildAttachmentLine` formatter emitting the canonical text shape with quoted-value escaping
- [x] 6.4 `note` strings sourced from a shared `AttachmentNotes` static class so the canonical prefixes never drift
- [x] 6.5 Multi-file announcements batched into a single `TextContent` block in original order
- [x] 6.6 User-visible rejection replies for every failure mode via `SafePostAsync`
- [x] 6.7 Accepted-file INFO log with structured fields (`name`, `mime`, `size`, `audience`, `category`, `inlined`)
- [x] 6.8 WARN log on every rejection path with the same fields plus a rejection reason
- [x] 6.9 Pre-download gates verified — no `HttpClient.SendAsync` call for rejected files
- [x] 6.10 `ChannelInput.Audience` still populated via the existing `SlackAclPolicy.ResolveAudience` path

## 7. `LlmSessionActor` strict-consumer contract

- [ ] 7.1 Delete the existing silent image-strip block in `LlmSessionActor.cs:1705-1719` (`mediaRefs` + `ModelModality.Image` check + `[Images removed ...]` placeholder)
- [ ] 7.2 Replacement: `ERROR` log naming the model id, modalities, and offending attachments; drop the unsupported `DataContent` items from the turn; append a `[system]` `TextContent` line noting the ingress bug; complete the turn normally
- [ ] 7.3 Grep-assert that no code path emits the old `[Images removed — the current model does not support vision input]` string
- [ ] 7.4 Add the attachment-aware dynamic-context block to `InjectDynamicContextLayers`, conditional on `file_read` being granted by the resolved `ToolAudienceProfile`. Source the block text from a single static constant.
- [ ] 7.5 Unit tests:
  - inbound `ChannelInput` with valid modalities passes through untouched
  - inbound `ChannelInput` with an unsupported-modality `DataContent` produces an `ERROR` log, drops the ref, appends the `[system]` TextContent, completes the turn
  - `file_read`-granted session has the attachment block in its system prompt
  - `file_read`-ungranted session does not

## 8. Slack regression tests for the rewritten pipeline

- [ ] 8.1 PDF in a Team-trust DM on a vision-capable model → file at `inbox/report.pdf`, `[attachment] ... inlined="true"`, matching `DataContent`
- [ ] 8.2 Image on a text-only model → file at `inbox/foo.png`, `[attachment] ... inlined="false" note="current model has no image modality..."`, no `DataContent`
- [ ] 8.3 `.docx` on any model → inbox write, `[attachment] ... inlined="false" note="format not inlineable..."`, no `DataContent`
- [ ] 8.4 Public channel + `.docx` → no HTTP download, user-visible rejection reply, WARN log
- [ ] 8.5 30 MiB file → no HTTP download, user-visible rejection reply
- [ ] 8.6 15-file inbound → entire batch rejected with one reply; text content still forwarded
- [ ] 8.7 Filename collision across turns → second upload lands at `foo_1.pdf`, first file unchanged
- [ ] 8.8 Scanner rejects (non-`ScanFailure`) → user-visible reply, no inbox write, no `[attachment]` line

## 9. Quality gates

- [ ] 9.1 `dotnet build` clean on the full solution
- [ ] 9.2 `dotnet test` green on Configuration + Actors test projects
- [ ] 9.3 `dotnet slopwatch analyze` — no new violations
- [ ] 9.4 Schema round-trip: load a config without `ChannelAttachments` through `netclaw doctor --fix` and confirm defaults are inserted

## Deferred (explicitly out of scope for this change)

The following were in the original plan but are deferred as either
secondary-path work, process/documentation, or post-ship bookkeeping.
Each should land as its own small follow-up once the core change is
merged.

- **SlackThreadHistoryFetcher backfill rewrite.** The fetcher still
  hard-filters to image attachments (`SlackThreadHistoryFetcher.cs:148`),
  so backfilled historical PDFs/docs are invisible to the agent. Live
  ingest is fixed; backfill is not. Follow-up issue: generalize the
  fetcher to all MIME types and move the audience/capability gate into
  the merge step in `SlackThreadBindingActor`, mirroring the live-turn
  pipeline. Includes scenarios in the `netclaw-slack-socket` spec delta
  (already in this change's `specs/` folder) for historical-attachment
  routing on vision-capable vs text-only models.

- **Eval suite regression cases.** PDF round-trip, model-modality gap,
  and format-not-inlineable. These are the behavioral guarantees the
  new ingress contract makes, and evals are the right regression tool
  per CLAUDE.md. Not a blocker for the bug fix itself.

- **PRD updates.** PRD-009 (Input Adapters) and PRD-002 (Gateway
  Security Envelope) should grow new sections covering the attachment
  ingress contract and the per-audience policy surface. Documentation,
  not behavior.

- **System skill sync.** `feeds/skills/.system/files/netclaw-operations/SKILL.md`
  should gain agent-facing guidance about `inbox/` and the `[attachment]`
  line format once the dynamic-context block lands in Phase 7. Tied to
  Phase 7, not the core ingress fix.

- **OpenSpec finalization (`/opsx-verify`, `/opsx-sync`, `/opsx-archive`).**
  The proposal/design/specs currently describe the fuller "canonical
  cross-channel contract" vision and reference scenarios (historical
  PDF backfill, normative note strings used by Discord, etc.) that this
  truncated change does not deliver. Syncing the delta specs into
  `openspec/specs/` before the deferred follow-ups land would publish a
  contract ahead of implementation. Leave the change in `openspec/changes/`
  as-is until the follow-ups catch up, or sync with explicit notes about
  the subset delivered.
