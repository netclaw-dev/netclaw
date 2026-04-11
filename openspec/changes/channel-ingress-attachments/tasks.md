## 1. Config surface and schema

- [ ] 1.1 Add `AttachmentCategory` enum (`Image`, `Pdf`, `Document`, `Archive`, `Media`, `Other`) to `Netclaw.Configuration`
- [ ] 1.2 Add `ChannelAttachmentPolicy` record with `AllowedCategories` (HashSet<AttachmentCategory>), `MaxFileBytes` (long), `MaxFilesPerMessage` (int)
- [ ] 1.3 Add `ChannelAttachments` field on `ToolAudienceProfile` defaulting to `ChannelAttachmentPolicy.Empty` (fail-closed) when not set
- [ ] 1.4 Implement `ToolAudienceProfileDefaults` entries for `Public` / `Team` / `Personal` using the matrix from design D4 (Public = {Image}; Team = {Image, Pdf, Document, Archive, Media}; Personal = all six; 25 MiB; 10 files)
- [ ] 1.5 Extend `netclaw-config.v1.schema.json` with the new fields under each audience profile, using `"type": "string"` enums for categories and `"default"` values per audience so `SchemaFixResolver` can auto-migrate
- [ ] 1.6 Add `IValidateOptions` logic confirming size cap > 0 and file-count cap > 0 on startup; emit a config validation error naming the offending audience on violation
- [ ] 1.7 Unit-test the defaults: Public rejects Pdf, Team accepts Pdf+Docx, Personal accepts Other, migration via `netclaw doctor --fix` on a config missing `ChannelAttachments`

## 2. Central MIME → category mapping

- [ ] 2.1 Add an internal `MimeToCategory(string mime)` helper in `Netclaw.Configuration` (or closest neighbor) with a single switch expression covering every documented category
- [ ] 2.2 Unit tests covering representative MIME strings per category, case-insensitivity, empty/null, and the unknown-MIME → `Other` fallback
- [ ] 2.3 Confirm no other place in the codebase maps MIME → category; grep for `StartsWith("image/"` etc. and fold any open-coded checks into the helper

## 3. Session working directory hardening

- [ ] 3.1 Mark `SessionDirectoryHelper.GetSessionDirectory(string sessionId)` (single-arg, Path.GetTempPath) `[Obsolete("Use the NetclawPaths.SessionsDirectory overload")]`
- [ ] 3.2 Audit every call site of that overload; migrate production code to the base-path overload
- [ ] 3.3 Add an `inbox/` subdirectory helper (`GetOrCreateInboxDirectory(sessionId, basePath)`) that ensures the directory exists with correct permissions
- [ ] 3.4 Add startup warning in `ConfigSchemaDoctorCheck` when the resolved session directory base path is under `Path.GetTempPath()`
- [ ] 3.5 Confirm (read-only) that session lifetime cleanup already removes `{sessiondir}/media/`; if yes, extend the same hook to `{sessiondir}/inbox/`; if not, add one and cover both subdirectories

## 4. Filename sanitization and collision suffixing

- [ ] 4.1 Verify `FilenameSanitizer.Sanitize` already handles `..`, NUL, control chars, absolute paths, Windows reserved names (confirm from spec tests; harden if any gap)
- [ ] 4.2 Add an internal `InboxWriter.ReserveUniquePath(inboxDir, safeName)` helper that checks `File.Exists` and returns `foo.pdf` / `foo_1.pdf` / … up to `_99`, throwing a specific exception at exhaustion
- [ ] 4.3 Atomic write helper (`InboxWriter.WriteAtomicAsync(path, bytes, ct)`) that writes to a temp sibling then `File.Move`
- [ ] 4.4 Unit-test collision suffixing across multiple calls; unit-test atomic write failure (simulated move error) leaves no partial file
- [ ] 4.5 Unit-test the exhaustion exception propagates to the caller and surfaces as a rejection reply (in Slack tests downstream)

## 5. ModelCapabilityActor ingress query helper

- [ ] 5.1 Add a thin helper on the Slack binding actor (or a shared `ChannelIngressCapabilityQuery` utility if Discord is coming soon) that `Ask`s `ModelCapabilityActor` with a 2-second timeout
- [ ] 5.2 Translate timeout into a typed result (`CapabilityQueryResult.Ok(modalities)` / `CapabilityQueryResult.Timeout`) so callers fail loudly rather than defaulting modalities
- [ ] 5.3 Unit-test cache-hit happy path, timeout path, and exception path with a fake `ModelCapabilityActor`

## 6. Slack ingress rewrite (`SlackThreadBindingActor`)

- [ ] 6.1 Delete the `image/`-only allowlist and the `_log.Debug("Skipping non-image file attachment...")` line
- [ ] 6.2 Implement the eleven-step pipeline in order from design D5: audience gate → size gate → count gate → download → scan → capability query → inbox write → announcement → inline
- [ ] 6.3 Build the `[attachment]` line exactly per the canonical format; use a private formatter method that other channels can lift later
- [ ] 6.4 Source `note` strings from a shared helper (`AttachmentNotes.ModelMissingImage`, `AttachmentNotes.ModelMissingPdf`, `AttachmentNotes.FormatNotInlineable`) so the canonical prefixes never drift
- [ ] 6.5 Batch multi-file announcements into a single `TextContent` block, preserving original order
- [ ] 6.6 Wire user-visible rejection replies for every pre-download and post-download failure mode (category, size, count, scan, capability timeout, inbox write error, collision exhaustion) through `SafePostAsync`
- [ ] 6.7 Upgrade the accepted-file log line from the old DEBUG drop to INFO with `{Name, Mime, Size, Audience, CategoryDecision, Inlined}` fields
- [ ] 6.8 Emit WARN log with the same fields on every rejection path
- [ ] 6.9 Ensure pre-download gates short-circuit on Slack-reported metadata; no `HttpClient.SendAsync` call is made for rejected files (verified by test double)
- [ ] 6.10 Confirm `ChannelInput.Audience` is set on the outbound command (should already be — regression guard via test)

## 7. Slack thread history backfill updates (`SlackThreadHistoryFetcher` + merge path)

- [ ] 7.1 Rewrite `SlackThreadHistoryFetcher` so it downloads and scans every attachment regardless of MIME type, returning `(bytes, mime, name, size)` tuples, NOT pre-filtered image bytes
- [ ] 7.2 Move the audience/capability gate for historical attachments into `SlackThreadBindingActor`'s merge step so it reuses the same policy helper as the live inbound path
- [ ] 7.3 Update the merge block to emit `[attachment] ... inlined="..." [note="..."]` lines per historical attachment, plus `[attachment rejected: name (reason)]` entries for policy-rejected historical files
- [ ] 7.4 Inline historical `DataContent` only for capability-gated categories, matching live-turn behavior
- [ ] 7.5 Integration test: a thread with a mix of historical images, a PDF, and a docx replays through backfill with the correct per-attachment routing on both a vision-capable and a text-only model

## 8. `LlmSessionActor` changes

- [ ] 8.1 Delete `LlmSessionActor.cs:1705-1719` silent image-strip block
- [ ] 8.2 Add replacement: `ERROR`-log + drop offending `DataContent` + append `[system]` TextContent per design D7 and the `netclaw-session` spec requirement
- [ ] 8.3 Grep-assert that no code path produces the old `[Images removed — the current model does not support vision input]` string
- [ ] 8.4 Add the attachment-aware dynamic-context block to `InjectDynamicContextLayers` (~line 2254), conditional on `file_read` being granted via the resolved `ToolAudienceProfile`
- [ ] 8.5 Source the block text from a single static constant so it is immutable and easy to snapshot
- [ ] 8.6 Unit test: session with `file_read` granted gets the block, session without `file_read` does not

## 9. Slack unit tests (TestKit, no `Thread.Sleep` / `Task.Delay`)

- [ ] 9.1 Test: PDF in Team-trust channel on PDF-capable model → `inbox/report.pdf` exists, `ChannelInput.Contents` has matching `[attachment] ... inlined="true"` line and a `DataContent(application/pdf)`
- [ ] 9.2 Test: image on text-only model → `inbox/foo.png` exists, `[attachment] ... inlined="false" note="current model has no image modality..."` line, no `DataContent`
- [ ] 9.3 Test: `.docx` on any model → inbox write, `[attachment] ... inlined="false" note="format not inlineable..."`, no `DataContent`
- [ ] 9.4 Test: public channel + `.docx` → no HTTP download, user-visible rejection reply posted, WARN log
- [ ] 9.5 Test: 30 MiB file → no HTTP download, user-visible rejection reply, WARN log
- [ ] 9.6 Test: 15-file inbound → entire attachment batch rejected with user-visible reply, text content still forwarded
- [ ] 9.7 Test: filename collision across turns → second upload lands at `foo_1.pdf`, first file unchanged
- [ ] 9.8 Test: `ModelCapabilityActor` timeout → user-visible "can't process your attachment" reply, no `DataContent`, no `[attachment]` line fabricated
- [ ] 9.9 Test: scanner rejects (non-`ScanFailure`) → user-visible reply with reason, no inbox write
- [ ] 9.10 Test: inbox write I/O failure (simulated) → user-visible reply, ERROR log, no `[attachment]` line

## 10. `LlmSessionActor` unit tests

- [ ] 10.1 Test: inbound `ChannelInput` with valid modalities passes through untouched
- [ ] 10.2 Test: inbound `ChannelInput` with an unsupported-modality `DataContent` produces an `ERROR` log, drops the ref, appends the `[system]` TextContent, and still completes the turn
- [ ] 10.3 Test: `file_read`-granted session has the attachment block in its assembled system prompt
- [ ] 10.4 Test: `file_read`-ungranted session has no attachment block

## 11. Eval suite regression cases

- [ ] 11.1 Eval: PDF round-trip — user uploads a PDF in Team-trust, agent answers a question about its contents; asserts on `[attachment] ... inlined="true"` in the inbound and on a content-accurate answer in the reply
- [ ] 11.2 Eval: model-modality gap — user uploads an image on a text-only model, agent mentions the filename by name in its reply and explicitly tells the user it can't view images on the current model
- [ ] 11.3 Eval: format-not-inlineable — user uploads a `.docx` in Team-trust, agent uses `shell_execute` (or similar) to extract content and answers about it
- [ ] 11.4 Run `./evals/run-evals.sh` and confirm all three new cases pass; update baselines as needed

## 12. PRD updates

- [ ] 12.1 Update `docs/prd/PRD-009-input-adapters-and-unified-input.md` with a new section describing the canonical attachment ingress contract, referencing `netclaw-input-adapters` as the spec home
- [ ] 12.2 Update `docs/prd/PRD-002-gateway-security-envelope.md` with a new section on audience-gated attachment policy and the default matrix, referencing `tool-approval-gates` as the spec home
- [ ] 12.3 Grep the other PRDs for any stale language that assumes "only images are accepted from Slack" and update if found

## 13. System skill sync (per CLAUDE.md skill-sync rule)

- [ ] 13.1 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` with a section explaining `inbox/`, the `[attachment]` line format, and the two canonical note prefixes so the running agent's operational guidance matches the new dynamic-context block
- [ ] 13.2 Bump the skill's `metadata.version` in the YAML frontmatter
- [ ] 13.3 Do NOT run `generate-skill-manifest.sh` locally (CI handles publishing)

## 14. Quality gates

- [ ] 14.1 `dotnet build` the Slack channel, Actors, and Configuration projects — clean
- [ ] 14.2 `dotnet test` for the affected test projects — all green
- [ ] 14.3 `dotnet slopwatch analyze` — no new violations
- [ ] 14.4 `./evals/run-evals.sh` — all cases pass including the three new ones
- [ ] 14.5 Schema round-trip: load an old config without `ChannelAttachments` under `netclaw doctor --fix`, confirm defaults are inserted and the fixed config passes schema validation

## 15. OpenSpec finalization

- [ ] 15.1 `/opsx-verify channel-ingress-attachments` — confirm implementation matches artifacts
- [ ] 15.2 `/opsx-sync channel-ingress-attachments` — sync the delta specs into `openspec/specs/`
- [ ] 15.3 `/opsx-archive channel-ingress-attachments` — archive after CI is green on the PR
