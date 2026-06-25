# Webhooks & Inbound Attachments


## Webhook Management


Webhooks are gated on `Webhooks.Enabled` in `netclaw.json` (default `true`).
When disabled, the webhook HTTP endpoint returns 404 for all routes and
webhook tools are hidden from discovery.

Inbound webhooks use a split config model:

- `~/.netclaw/config/netclaw.json` -> `Webhooks.Enabled` toggles the feature
- `~/.netclaw/config/webhooks/*.json` -> one route per file; filename is the
  route name used at `/api/webhooks/{route}`

Use the dedicated tools instead of generic file tools when available:

- `set_webhook`
- `list_webhooks`
- `delete_webhook`

When using `set_webhook`, use `delivery_required` (bool, default `true`) to
control required notification behavior. `notify_policy` is deprecated.

`set_webhook` inherits the audience of the channel/session that created it when
`audience` is omitted — the same provenance model as reminders. A route cannot be
minted with a broader audience than the creator holds; downgrading is always
allowed. A webhook created from a Team channel runs as Team (and therefore cannot
run `shell_execute`); one created from a Personal CLI session runs as Personal.

Route files are secret-bearing config because they may contain inline
verification secrets. Treat `config/webhooks` like `secrets.json` and avoid
broad file reads/writes there unless the user explicitly wants raw config work.

Verification kinds are generic:

- `Hmac`
- `HeaderSecret`

Route files hot-reload without restarting the daemon. If a route file becomes
invalid, Netclaw removes that route immediately and emits an operational alert.

**Approval gate:** Webhooks run without a human — they cannot prompt for
approval. The same rules as reminders apply: shell commands must be pre-approved
in `tool-approvals.json`, and path arguments are scoped by the route's audience
the same way `file_write` is. See "Approval Requirements for Reminders and
Webhooks" in the Scheduling section.

### Webhook observability

`netclaw stats` includes a `webhooks:` section with:

- **Route counts** — `total`, `enabled`, `disabled`, `invalid` (files on disk,
  classified by parse/validation status plus the per-route `Enabled` flag).
- **Delivery counters** — `accepted`, `filtered` (event not in allowlist),
  `duplicate` (delivery id already seen), plus per-rejection counts:
  `404` (route_not_found), `401` (verification_failed), `413` (body_too_large),
  `400` (invalid_json), `429` (rate_limited).

Every ingress outcome writes a structured line to `daemon-{date}.log`:

```
Webhook rejected route={Route} reason={Reason} remote_ip={RemoteIp}
  delivery_id={DeliveryId} event_type={EventType}
```

Rejection paths only log + increment counters — they do NOT fire outbound
operational notifications, so bad or adversarial traffic does not spam the
configured notification target.

## Inbound Attachments


When a user sends a file in Slack, Discord, or Mattermost, Netclaw runs the
attachment through an ingress pipeline before it reaches the LLM:

1. **Policy gate** — uses declared MIME plus filename extension for a provisional
   catalog-backed category, then checks audience and per-message file count
   against `ChannelAttachmentPolicy`
2. **Size gate** — rejects files above the per-audience byte limit
3. **Download** — fetches from Slack's private file API with bot-token auth
4. **Content scan** — runs the configured `IContentScanner` and produces a
   scanner-verified canonical MIME type
5. **Inbox write** — saves to `~/.netclaw/sessions/{session-id}/inbox/`
6. **Announcement line** — appends `[attachment]` text to the user turn

The `[attachment]` line format is:
```
[attachment] name="..." mime="..." size=N path="inbox/..." inlined="true|false"
```

`inlined="true"` means the file bytes were forwarded to the model as
`DataContent` (currently image files on image-capable models). `inlined="false"`
means the model only sees the path reference.

Declared transport MIME is metadata, not proof. Attachment announcements,
inlined `DataContent`, and model-input handoff use the scanner-verified MIME.
Unknown image/audio/video subtypes do not get privileged categories by prefix;
they must be explicitly present in the media catalog. OpenAI-compatible
providers only serialize image `DataContent` through `image_url` and fail loudly
if non-image bytes reach that boundary.

`file_read` follows the same file taxonomy as chat attachments. It reads
text-like files directly, including UTF-8, UTF-16/UTF-32 Unicode text, and
common Windows-1252 text files. For images, it can load the file for visual
inspection when the active model or delegated sub-agent supports image input.
For PDFs, audio/video, archives, binary documents, and unknown binaries, it
returns metadata plus explicit guidance; it does not perform PDF extraction,
OCR, transcription, keyframe extraction, or raw binary output.

**Historical thread backfill** follows the same download/scan flow for all
file types (not just images) — PDFs and other documents from prior thread
messages are included.

**When attachment ingress fails**, Netclaw posts a stable user-facing message
(e.g. "Couldn't download `file.pdf` — please try again later.") and logs the
full exception internally. Exception details are **never** forwarded to Slack.

| Symptom | Check |
|---------|-------|
| File rejected before download | audience/category policy gate; check `ChannelAttachmentPolicy` config |
| Download timeout | bot token valid? Slack network reachable? check `daemon-{date}.log` |
| Content scan rejection | `netclaw status` scanner section; check scan config |
| Inbox write failure | disk space? permissions on `~/.netclaw/sessions/`? |
