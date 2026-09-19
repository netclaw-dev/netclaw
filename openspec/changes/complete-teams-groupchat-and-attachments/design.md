## Context

See [proposal.md](proposal.md) for the reason for this work.
The Teams channel already has SDK-free ingress contracts, durable Teams bindings, a Graph directory boundary, and shared attachment infrastructure.
The Microsoft Teams SDK 2.1 translator receives `MessageActivity` and reads `Conversation.ConversationType`.
SDK 2.1 exposes `ConversationType.GroupChat` as a distinct string-enum value.
The current translator maps only `Personal` and `Channel`.

## Goals / Non-Goals

**Goals:**

- Add GroupChat at every Teams boundary with explicit semantics.
- Keep one durable Teams session per approved group chat.
- Stage proven Teams attachments through the shared media and workspace path.
- Keep transport URLs, SDK types, and tokens outside actors and persistence.
- Retain Personal and Channel behavior without representation changes.

**Non-Goals:**

- No generic channel, approval, session, tool, memory, or transport redesign.
- No tenant-wide chat, message, SharePoint, OneDrive, or file permission.
- No arbitrary URL fetch or Teams document parser.
- No ordinary Channel or GroupChat file support without a proven bounded route.
- No live tenant test, deployment, merge, or final upstream pull request.

## Decisions

### Extend the typed Teams scope without changing old scope values

`TeamsConversationScope` gains `GroupChat` after its existing values.
All scope switches receive an explicit GroupChat case.
The canonical identifier codec adds `TryCreateGroupChat` and accepts `groupchat` only with the `conversation` thread key.
Personal and Channel keep their current wire representation and values.

This avoids treating a multi-human chat as a Personal conversation or as a Channel thread.
It also avoids an enum-wide `Enum.IsDefined` rule that would hide scope policy.

### Translate GroupChat at the daemon SDK boundary

The SDK translator maps `ConversationType.GroupChat` to the SDK-free GroupChat scope.
It removes a verified structured bot mention from GroupChat message text in the same way as Channel text.
The translator keeps GroupChat flat. It does not derive or validate a channel root activity ID.
Message update and delete behavior remains explicitly reviewed and fails closed unless the existing contract defines a safe GroupChat action.

The translator remains the only component that reads Teams SDK activity and attachment types.
The downstream record carries only bounded, SDK-free trust, sender, conversation, reply, and staged-attachment facts.

### Apply GroupChat gates before durable session work

The ingress edge applies this order: authenticated tenant, GroupChat enabled, canonical allowed chat, global sender authorization, and mention policy.
The GroupChat default audience is Team.
The global user list has a no-Graph fast path.
Global group membership uses the existing bounded Graph verification path.
An empty global principal set and every unavailable group result deny.
Channel access overrides are excluded from GroupChat policy selection.

When mention-only mode is off, the accepted GroupChat activity still requires authenticated delivery through the app's scoped RSC capability.
No remembered root or prior mention extends GroupChat authority.

### Reuse durable Teams delivery with flat destination requirements

The Teams binding preserves GroupChat scope, tenant, conversation ID, and validated service URL.
It omits Team ID, Channel ID, root activity ID, and Personal user ID from GroupChat destination requirements.
The existing output port uses this destination for reply, typing, approval-card, and proactive send operations.
The binding persistence records only the minimum non-secret values that recovery requires.

The current approval callback checks remain the authority for correlation, nonce, requester, action, expiry, and replay.
GroupChat adds routing support. It does not alter shared approval authority.

### Keep attachment URLs at the daemon boundary

The daemon receives an authenticated Teams SDK activity and translates only its safe shape into SDK-free metadata.
It captures a short-lived raw download URL in a daemon-only downloader capability.
No attachment bytes are downloaded before the actor admits the activity.

```
authenticated Teams SDK activity
    -> translator and daemon-only URL capture
    -> SDK-free attachment metadata
    -> ACL and durable activity reservation
    -> binding asks the daemon downloader
    -> shared scanner and managed attachment storage
    -> ChannelInput with executable text and untrusted attachment content
```

The durable activity-idempotency path selects one accepted activity owner before another retry can enqueue a duplicate.
A pre-dispatch failure that escapes the binding can release its reservation where retry is supported.
Handled attachment rejections are terminal: an attachment-only rejection retains the reservation and creates no model turn.
Raw URLs are not durable, so recovery cannot use a stale captured URL after a crash.
Partial files are removed on cancellation, timeout, validation failure, or failed admission.

### Reuse shared media policy for images and Personal files

The binding checks the existing maximum file count and per-file byte limit before and during streaming.
There is no configured aggregate byte limit.
It uses the existing MIME catalog, signature validation, scanner, image decode, image normalization, safe internal name, and workspace storage.
It sends model-native images through existing image-inline decisions.
It sends all other approved files as existing staged file references.
It never appends attachment bytes to executable text.
Nonempty safe text still creates one normal turn when another attachment is rejected.
An accepted attachment-only message creates one turn; a rejected attachment-only message creates none.

The stager ignores the existing Teams HTML text wrapper.
It accepts inline image shapes in Personal, Channel, and GroupChat.
It accepts the Teams file-download information shape only in Personal scope.
It rejects executables, unsafe archives, malformed payloads, unknown URL routes, redirects, MIME mismatch, and size-limit breach.

### Keep GroupChat metadata optional and least privileged

Group-chat metadata discovery is deferred.
The TUI always provides canonical-ID entry and an abbreviated canonical display fallback.
It never adds `Chat.ReadBasic.All`, `Chat.Read.All`, `Files.Read.All`, or `Sites.Read.All` for display convenience.

### Separate package RSC from Entra application permissions

The Teams package adds `groupchat`, `ChatMessage.Read.Chat`, and `supportsFiles`.
It retains `ChannelMessage.Read.Group` for channels.
The documentation states that a target group chat needs an app upgrade or installation for RSC consent.
Graph status lists any optional application permission separately from package RSC.

## Risks / Trade-offs

- [An SDK attachment shape lacks a safe authenticated route] -> Reject it with a stable safe reason. Do not use a general HTTP fetch.
- [A retry arrives during attachment staging] -> Let durable activity ownership select one accepted activity. Remove losing partial files.
- [GroupChat metadata is unavailable] -> Show an abbreviated canonical ID and keep canonical ID entry available.
- [An operator rolls back after new persistence state] -> Document persistence compatibility and take the same deployment care as existing Teams binding changes.
- [A GroupChat message omits a structured mention] -> Ignore it when mention-only mode is enabled. Do not infer intent from text.

## Migration Plan

1. Deploy additive configuration with GroupChat and attachment controls disabled.
2. Upgrade the Teams package in each target group chat and grant its RSC consent.
3. Add canonical chat IDs and global sender principals through the TUI or configuration.
4. Enable GroupChat and attachment ingress only after the owner runs the documented smoke plan.
5. To roll back access, disable the new controls. Existing Personal and Channel identities remain unchanged.
