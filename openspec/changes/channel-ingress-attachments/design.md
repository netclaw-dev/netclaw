## Context

This change makes attachment ingress a first-class cross-channel concern.
The current design has three architectural problems layered on top of
each other:

1. **Silent drops at the wrong layer.**
   `SlackThreadBindingActor.MapSlackFiles`/`SlackThreadBindingActor.cs:218`
   hard-codes an `image/*` allowlist and `DEBUG`-drops everything else.
   `LlmSessionActor.cs:1705-1719` then silently strips images when the
   model isn't vision-capable, surfacing only an `[Images removed]`
   placeholder. Both layers make the same category of decision (what
   the model can render) but neither layer owns it, and neither layer
   tells the user anything actionable.

2. **Scanner / audience de-coupling.**
   `IContentScanner.ScanAsync` at `SlackThreadBindingActor.cs:234` is
   audience-blind. It asks "is this byte stream safe?" but not "is
   this class of file allowed to reach the agent from this trust
   level?". Those are different questions and the second one has no
   home in the current code.

3. **No cross-channel contract.**
   `netclaw-input-adapters` defines `SendUserMessage`, entity key
   routing, and source metadata — but says nothing about file
   attachments. Discord will arrive next, and the shortest path for
   its implementer is to copy the Slack code's patterns — including
   the silent drops.

The design below moves the capability-routing decision up to ingress,
introduces an audience-gated attachment policy on the existing
`ToolAudienceProfile`, and defines a uniform pipeline + persisted
text-block format (`[attachment] ... inlined="..." note="..."`) that
every channel adapter must implement. Slack becomes the first
implementation. `LlmSessionActor` stops owning modality decisions.

## Goals / Non-Goals

**Goals:**

- A single pipeline every channel runs for inbound attachments:
  policy gate → size/count gate → download → scan → inbox write →
  `[attachment]` text injection → capability-gated `DataContent`
  inlining.
- The agent is *told* (in the inbound text) whether each file was
  inlined and — if not — why, so its reply can acknowledge the
  attachment instead of going silent.
- Audience-trust policy governs which MIME categories are accepted
  per `TrustAudience`, reusing the existing `ToolAudienceProfile`
  surface rather than inventing a parallel knob.
- `LlmSessionActor` becomes a strict consumer: if it sees a modality
  the current model can't render, that is a **bug in the ingress
  adapter**, not a condition to be handled gracefully.
- Zero silent drops. Every rejection is user-visible; every
  accepted-but-not-inlined file is announced to the agent with an
  explicit `inlined="false"` + `note=`.
- Cross-channel portability: `netclaw-input-adapters` carries the
  normative language so Discord/Teams/web implementations follow
  the same contract.

**Non-Goals:**

- Server-side extraction, OCR, or conversion. Agents use
  `file_read` / `shell_execute` on `inbox/*` on demand.
- New `AIContent` subtypes for attachments. A plain `TextContent`
  line works across every provider with zero adapter surgery.
- Per-user or per-sender attachment allowlists. Audience is the
  axis for MVP; senders can be layered on later via ACL.
- Outbound file uploads. `attach_file` already covers that path and
  is untouched.
- Runtime category definition / operator-defined MIME categories.
  The category set is fixed in code; `AllowedCategories` picks from
  a closed enum.

## Decisions

### D1. Capability routing happens at ingress, not in `LlmSessionActor`

**Decision:** `SlackThreadBindingActor` (and every future channel's
equivalent) queries `ModelCapabilityActor` for the active model's
`InputModalities` *before* building `ChannelInput.Contents`, and uses
the answer to decide inline-vs-path-only per file. `LlmSessionActor`
stops stripping and starts asserting.

**Rationale:**

- The channel knows the session, which determines the model, which
  determines the capability. That information is available at ingress
  — pushing the decision deeper into `LlmSessionActor` forces the
  strip to happen after the inbound turn is already constructed,
  which means the agent's turn history gets rewritten after the fact.
  Rewriting persisted state is where `[Images removed]` placeholders
  came from.
- A strict consumer model (ingress guarantees valid modalities;
  session asserts) prevents the *next* bug in the same class. If
  another channel skips the capability gate by accident, the assert
  fires loudly in dev and in logs, rather than silently stripping.
- The `ModelCapabilityActor` already caches per-model data in memory
  (per exploration: ~1–10 ms on cache hits, ~10 s worst case on
  cold cache with dedupe via `_pending` waitlist). Ingress-side
  queries are effectively free on the hot path.

**Alternatives considered:**

- *Keep the strip in `LlmSessionActor`, just make it loud instead of
  silent.* Rejected. Moves the log-level knob but leaves the
  architectural smell — `LlmSessionActor` shouldn't be rewriting the
  turn it's about to process. It also can't generate a replacement
  `[attachment]` announcement for the turn without reaching back
  into channel-layer concerns.
- *Channel emits all raw attachments as `DataContent`, a middleware
  stage between channel and session does the capability gate.*
  Rejected. That's one more actor hop for no architectural gain —
  the channel is already the right place, and adding a dedicated
  "attachment router" actor is over-engineering for the two known
  channels (Slack now, Discord soon) that would pass through it.

### D2. `TextContent` announcement, not a bespoke `AIContent` subtype

**Decision:** The `[attachment]` line is plain `TextContent` with a
fixed format — `[attachment] name="..." mime="..." size=... path="inbox/..." inlined="true|false" [note="..."]`.

**Rationale:**

- Works across every LLM provider adapter without changes — no
  `AnthropicProviderPlugin` or `OpenAiProviderPlugin` surgery.
- Stable over future provider/model evolutions; `TextContent` is the
  one shape that will never change.
- Persisted naturally in `ChannelInput.Contents` → journal → replay,
  without per-type marshalling.
- Easy for eval harnesses and tests to regex-match and assert on.
- Small token cost per attachment (~30–40 tokens for the announcement
  line) — acceptable given the single-turn, once-per-upload scope.

**Alternatives considered:**

- *New `AttachmentContent : AIContent` subtype with typed fields.*
  Rejected. Adapter work in every provider, no observable gain — the
  model reads the same information either way.
- *Structured JSON inside a `TextContent`.* Rejected as overkill;
  the key=value format is already trivially parseable and less
  visually noisy for the model.

### D3. MIME categories, not raw MIME prefix allowlists

**Decision:** `ToolAudienceProfile.ChannelAttachments.AllowedCategories`
is a `HashSet<AttachmentCategory>` over a closed enum:

```csharp
public enum AttachmentCategory
{
    Image,     // image/*
    Pdf,       // application/pdf
    Document,  // word, excel, powerpoint, odf, rtf, text/*
    Archive,   // zip, tar, gz, 7z, rar
    Media,     // video/*, audio/*
    Other      // application/octet-stream, unknown MIME
}
```

A single internal function `MimeToCategory(string mime) → AttachmentCategory`
is the only place the mapping lives. Unknown / unrecognized MIME types
map to `Other`, which is only allowed in the `Personal` audience by
default — fail-closed.

**Rationale:**

- Operators reason about policy in human terms ("allow images") not
  MIME strings ("`image/png`, `image/jpeg`, `image/webp`, …"). The
  category vocabulary matches how people actually think about file
  trust.
- Centralized map is the only surface that needs updating when new
  MIME types emerge.
- Unknown → `Other` → fail-closed preserves the security posture;
  adversaries can't smuggle novel MIME types into `Public`-trust
  contexts by picking ones not in a hand-maintained allowlist.

**Alternatives considered:**

- *Config-defined category map.* Rejected as a configuration
  ergonomics trap — operators would end up copy-pasting MIME lists
  from blog posts and the security posture would drift per
  deployment. Closed enum in code is a better default; if a
  deployment truly needs a novel category it can raise a PR.
- *No categories, allow-list by MIME prefix.* Rejected — same
  operator ergonomics problem inverted.

### D4. Default policy matrix is conservative at `Public`, permissive at `Personal`

**Decision:** Default `AllowedCategories` per audience:

| Audience | AllowedCategories | Size cap | File count |
|---|---|---|---|
| `Public` | `{ Image }` | 25 MiB | 10 |
| `Team` | `{ Image, Pdf, Document, Archive, Media }` | 25 MiB | 10 |
| `Personal` | `{ Image, Pdf, Document, Archive, Media, Other }` | 25 MiB | 10 |

**Rationale:**

- `Public` defaults to images-only because processing PDFs/docs/
  archives typically routes through `shell_execute` on user-
  controlled bytes. In a public Slack channel, any workspace member
  can upload — the attack surface is real and doesn't need to be
  open by default.
- `Team` and `Personal` get documents and archives because in those
  contexts the workspace auth boundary is a meaningful filter, and
  legitimate flows ("please summarize this PDF") are common.
- Only `Personal` gets `Other` (unknown MIME) because the failure
  mode is always "agent shells out on unknown bytes" and that is
  specifically the fail-closed posture `Public` and `Team` want to
  preserve.
- 25 MiB aligns with Anthropic's current PDF document block limit
  and is generous for typical work documents.
- 10 files per message is a belt-and-suspenders cap against
  mass-upload abuse (user drags a folder into the message box).

**Operators can override.** Every cell is a config field on the
existing `ToolAudienceProfile` surface. A security-sensitive
deployment can set `Public.AllowedCategories = []`; a low-risk one
can set `Public.AllowedCategories = { Image, Pdf }`.

### D5. Ingress pipeline order is policy → size → count → download → scan → capability → write → announce

**Decision:** The eleven-step pipeline, in order:

1. Parse Slack file metadata from the inbound event (no change).
2. Resolve `TrustAudience` for the inbound message via the existing
   `SlackAclPolicy.ResolveAudience` path (no change).
3. **Per-file audience/category gate** — reject immediately if the
   file's MIME category is not in `AllowedCategories` for this
   audience. *Before* download — no bytes consumed, no bandwidth
   burned on files that can't be accepted.
4. **Per-file size gate** — reject immediately if Slack's reported
   `size` exceeds `MaxFileBytes`. *Before* download.
5. **Per-message file-count gate** — reject the entire message if
   `files.Count > MaxFilesPerMessage`. *Before* download.
6. **Download bytes** — via existing Slack HTTP client with
   `url_private_download` and bot Bearer auth.
7. **Content scan** — existing `IContentScanner.ScanAsync` on the
   downloaded bytes. Scan-reject replies user-visibly.
8. **Capability query** — `Ask<ModelCapabilitiesResponse>` on
   `ModelCapabilityActor` with a 2-second cancellation. Cache hit is
   ~1 ms.
9. **Inbox write** — atomic write to `{SessionDirectory}/inbox/`
   with filesystem-level collision suffixing.
10. **Build announcement** — `[attachment]` TextContent with
    `inlined` and (conditionally) `note`.
11. **Build inline** — `DataContent(bytes, mime)` if capability
    gate said yes.

**Rationale for ordering:** the three pre-download gates (audience,
size, count) use metadata already present on the Slack event, so
they can short-circuit before burning any bandwidth. Scanner runs
on bytes so it has to wait until after download. Capability query
is independent and could run in parallel, but cache-hit latency is
so small it's not worth the orchestration complexity.

### D6. Inbox collisions are checked against the filesystem, not the current batch

**Decision:** Before writing `inbox/{name}`, check `File.Exists`. If
present, try `{stem}_1{ext}`, `{stem}_2{ext}`, … up to 99. If 99
collisions, reject that file with a user-visible reply. Reuse
`FilenameSanitizer.Sanitize` for the `{name}` sanitization and
`AttachFileTool`'s path-traversal-safe resolution pattern.

**Rationale:** If turn 3 uploads `report.pdf` and turn 7 uploads a
different file also called `report.pdf`, a per-batch collision
check would silently overwrite turn 3's file — which the agent's
history still references. Filesystem-level checks make persistence
across turns consistent with persistence across files.

### D7. `LlmSessionActor` silent strip becomes a loud assertion

**Decision:** Replace `LlmSessionActor.cs:1705-1719` with:

```csharp
if (mediaRefs.Any(r => !_model.InputModalities.HasFlag(r.Modality.ToModelModality())))
{
    var offending = mediaRefs
        .Where(r => !_model.InputModalities.HasFlag(r.Modality.ToModelModality()))
        .Select(r => $"{r.Name}:{r.Modality}")
        .ToArray();
    _log.Error(
        "Ingress bug: session received DataContent modality the active model cannot render. " +
        "Model={ModelId} Modalities={Modalities} Offending={Offending}. " +
        "This indicates the originating channel did not query ModelCapabilityActor " +
        "before inlining DataContent. Dropping the unsupported refs and continuing the turn.",
        _model.Id, _model.InputModalities, string.Join(",", offending));
    // Drop the refs so the provider call doesn't fail, but surface the incident to the user:
    // one TextContent line appended to the inbound contents so the agent can tell the user
    // "I received a file but a system glitch prevented me from viewing it."
    contents = contents.Where(c => !(c is DataContent d && IsOffending(d))).ToList();
    contents.Add(new TextContent(
        "[system] an attachment was received but could not be delivered to the model " +
        "due to an ingress bug; please retry or notify the operator"));
}
```

Key differences from today:

- `_log.Error` not `_log.Debug`.
- No placeholder `[Images removed — model has no vision]` text. That
  was a symptom of a wrong-layer fix. The new message explicitly
  says "ingress bug" so an operator reading logs or the user reading
  the turn reply knows this is abnormal, not expected behavior.
- Still non-fatal — session completes the turn. The goal is visibility,
  not crashing the conversation.

**Rationale:** The target state is "this branch never fires in
practice because ingress always routes correctly". Making it loud
ensures that if a new channel ships without capability-gating, its
first attachment upload produces a log line that's obviously wrong.
Silent stripping would hide that signal for weeks.

**Alternative considered:** Delete the branch entirely and let the
provider reject the call. Rejected because provider errors are
noisier to diagnose and the turn fails entirely rather than
degrading; the explicit strip+annotate lets us fail soft while
logging loudly.

### D8. Dynamic-context hint is conditional, short, and names the canonical note classes

**Decision:** `LlmSessionActor.InjectDynamicContextLayers` conditionally
appends this block to the system prompt when the session's audience
profile has `file_read` granted:

```
Your session working directory contains an `inbox/` subdirectory
where user-uploaded attachments are placed. Each attachment is
announced in the inbound message as a single line:

    [attachment] name="..." mime="..." size=... path="inbox/..." inlined="true|false" [note="..."]

When `inlined="true"`, you can see the file content natively in this
turn. When `inlined="false"`:
  - If `note` begins with "current model has no": the file exists on
    disk but you cannot render it. Acknowledge it to the user by name
    in your reply, explain the limitation, and offer tool-based
    workarounds if applicable (e.g., `shell_execute pdftotext` for a
    PDF on a non-PDF model).
  - If `note` begins with "format not inlineable": use `file_read` or
    `shell_execute` to process the bytes. This is the normal path for
    docx, zip, archive, and media files.

Never silently ignore an attachment the user sent you — always
acknowledge what you received, even if you cannot fully process it.
```

**Rationale:**

- Conditional on `file_read` grant: without the tool, telling the
  agent about `inbox/` is a lie (it can't read from it). Check the
  audience profile's `AllowedTools` list at context-build time.
- Names the two canonical note-prefix patterns explicitly so the
  agent can branch without having to fuzzy-match.
- Ends with an imperative acknowledgment rule so the model doesn't
  "helpfully" skip mentioning an unviewable file.
- ~180 tokens total; acceptable overhead on a system prompt that is
  already in the several-thousand-token range.

## Risks / Trade-offs

- **[Risk] Disk exhaustion in `/tmp` if the operator deployed against
  the legacy `SessionDirectoryHelper.GetSessionDirectory(sessionId)`
  overload (single-arg, uses `Path.GetTempPath()`)** →
  Mitigation: mark that overload `[Obsolete]` with a migration
  message pointing at the durable `NetclawPaths.SessionsDirectory`
  path. Audit all call sites and migrate them. Add a
  `ConfigSchemaDoctorCheck` warning on startup if the session dir
  resolves under `Path.GetTempPath()`.

- **[Risk] `ModelCapabilityActor` returns stale capabilities after a
  mid-session model swap** → Mitigation: out of scope for this change;
  document the known limitation in the spec. Mid-session model swaps
  are rare; cache-invalidation on swap is a separate piece of work.
  If it bites someone in practice, the silent behavior is "the
  attachment routes with pre-swap capabilities for one message" —
  not a silent drop, just a transient mis-route.

- **[Risk] The agent ignores the `note` field and replies
  generically** → Mitigation: the eval suite gets a regression case
  for the model-modality-gap path specifically asserting the agent
  mentions the attachment name in its reply. If that eval fails, the
  dynamic-context hint gets sharpened.

- **[Risk] Unknown MIME types get silently categorized as `Other` and
  only allowed in `Personal`** → Mitigation: emit an `INFO` log on
  unknown-MIME classification so operators can notice and extend
  `MimeToCategory` if there's a legitimate pattern. Fail-closed is
  still the right default.

- **[Risk] Inbox files survive session cleanup** → Mitigation: confirm
  session-dir lifetime management during implementation; if there
  isn't a cleanup hook today, this change adds one that removes
  `{sessiondir}/inbox/*` on session expiry (same policy as existing
  `media/` files). Tracked as an implementation task, not a separate
  change.

- **[Risk] `LlmSessionActor` loud-log path fires in production
  because a new channel is shipped without capability-gating** →
  That is exactly the design intent. Surfacing that bug immediately,
  loudly, is better than silently stripping.

- **[Trade-off] `[attachment]` announcement costs ~30–40 tokens per
  file** → Accepted. The alternative (rich `AIContent` subtype) costs
  adapter complexity instead, which is worse. For the typical
  1-file-per-message case this is a rounding error on a multi-thousand-
  token turn.

- **[Trade-off] Pre-download gates use Slack-reported size, which
  the uploader can technically forge** → Accepted. A forged small
  size still hits the post-download scan and the runtime byte-length
  check. The pre-download gate is a bandwidth optimization, not a
  security boundary — the scanner remains authoritative.

## Actor Boundaries & Persistence

- **`SlackConversationActor` → `SlackThreadBindingActor`** — unchanged
  envelope, but `SlackThreadInbound` now carries the resolved
  `TrustAudience` alongside the file list (it already does, per
  exploration).
- **`SlackThreadBindingActor` → `ModelCapabilityActor`** — new `Ask`
  dependency. Resolved via the existing `ActorRegistry` pattern
  (`ActorRegistryKeys.ModelCapabilityActorKey`). 2-second timeout;
  timeout → user-visible "can't process attachment right now" reply,
  no inlining, no guessing.
- **`SlackThreadBindingActor` → `LlmSessionActor`** — payload shape
  unchanged (`ChannelInput` with `Contents`). The contents list now
  reliably contains correct-modality `DataContent` items only.
- **Persistence:** `ChannelInput` is journaled as part of
  `LlmSessionActor.PersistenceId = "session-{entityId}"`. The
  `[attachment]` `TextContent` IS persisted as part of the turn
  history — a session replay on restart will see the line exactly as
  the live turn did. This matches current `DataContent` persistence
  for images.
- **Inbox files on disk** live at
  `{NetclawPaths.SessionsDirectory}/{sanitized-sessionId}/inbox/`.
  Under the default production path (`~/.netclaw/sessions/`) this
  survives restart. Under the legacy `/tmp` path it does not — the
  mitigation in Risks above deprecates the legacy path.

## Failure Modes & Recovery

| Failure | Recovery |
|---|---|
| Pre-download policy reject (category / size / count) | User-visible reply explaining the specific policy; no disk write; no LLM turn. |
| HTTP download failure | User-visible reply ("couldn't download your attachment — please retry"); `WARN` log with Slack file ID; continue processing other files in the same message. |
| Content scanner internal error | Same as today — allow the file through with an `ERROR` log (scanner-error fallback is reviewed prior art). |
| Content scanner rejects (malware, magic-byte mismatch) | User-visible reply with the scanner's rejection reason; no disk write; no LLM turn for that file. |
| `ModelCapabilityActor` timeout | User-visible reply ("having trouble processing your attachment right now"); `WARN` log; no inlining, no fallback guess. Per "no silent fallbacks". |
| Inbox write failure (disk full, permissions) | User-visible reply ("couldn't save your attachment"); `ERROR` log with path; no LLM turn for that file. |
| Filename collision exhaustion (99 `_N` suffixes) | User-visible reply ("too many files with that name in this session"); `WARN` log; no LLM turn for that file. |
| Audience resolution returns `Public` due to unrecognized channel | Treat as `Public` — the most restrictive default. Fail-closed; no inlining of sensitive categories. |
| `LlmSessionActor` receives an unsupported-modality `DataContent` | `ERROR` log with model/modality/offending refs (D7); drop the refs; append a `[system]` TextContent noting the ingress bug; complete the turn. |

## Migration Plan

1. **Phase 0 (this change)**: ship the pipeline, the new
   `ToolAudienceProfile.ChannelAttachments` config surface with
   defaults, the `LlmSessionActor` assertion, the dynamic-context
   hint, and the Slack channel implementation. Update
   `netclaw-config.v1.schema.json` with `"default"` values for each
   new field so `netclaw doctor --fix` auto-migrates stale configs.
2. **Deprecate the legacy `SessionDirectoryHelper` single-arg
   overload** (`[Obsolete("Use GetSessionDirectory(sessionId, basePath) with NetclawPaths.SessionsDirectory")]`).
   Migrate all production call sites in the same PR. Leave for tests.
3. **Eval suite**: add the three regression cases from the proposal
   (inlined happy path, model-modality gap, format-not-inlineable).
4. **Rollback**: this is not a hot-patch-worthy surface; rollback is
   via revert of the PR. The assertion in `LlmSessionActor` means a
   partial rollback (keep new ingress, revert `LlmSessionActor`)
   would cause the loud-log path to fire — acceptable as a transient,
   but clean rollback is a full revert.

## Open Questions

- **Session-directory cleanup hook**: does one exist today for
  `media/`? If yes, inbox/ reuses it. If no, this change adds one —
  will resolve during implementation rather than pre-deciding here.
- **Per-workspace override of `ToolAudienceProfile` defaults**:
  Slack-specific. Not in scope for this change — deferred to a
  follow-up if the three-audience default matrix proves too coarse
  in practice.
- **Image on non-vision model — should the file even be saved to
  disk?** Current decision: yes, save to `inbox/` so the agent can
  at least `shell_execute file` to probe basic metadata and
  acknowledge the file by name. Alternative would be path-only text
  announcement without a disk write. Keeping the disk write for
  uniformity — all accepted files land in `inbox/` regardless of
  inlining status. Will reconsider if the disk-write path turns
  out to be onerous.
