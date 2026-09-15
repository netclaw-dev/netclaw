## Context

See [proposal.md](proposal.md) for the owner-reported failure and superseded discovery decisions.
Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

The CLI already has an SDK-neutral Teams directory boundary and a Group Chat result view.
The Graph client uses application credentials.
Microsoft documents `/users/{id}/chats` for that access mode, but no tenant-wide chat-title search endpoint.
See [List chats](https://learn.microsoft.com/en-us/graph/api/chat-list?view=graph-rest-1.0).

## Goals / Non-Goals

**Goals:**

- Reuse the current Group Chat review and canonical configuration writer.
- Keep metadata requests bounded and continuation state private to the Graph adapter.
- Make typed and pasted chat titles own their asynchronous results.

**Non-Goals:**

- No persistent chat index or background tenant scan.
- No Graph SDK types, raw next links, or Graph credentials in the TUI contract.
- No actor messages, journal changes, or new configuration properties.

## Decisions

### Search title metadata through tenant users

The Graph adapter reads user ID pages, then each user's chat metadata pages.
It filters locally by `chatType == group`, a valid canonical chat ID, and a case-insensitive topic substring.
It deduplicates matches by canonical ID across users and pages.

The adapter reuses `User.Read.All` and optional `Chat.ReadBasic.All`.
It does not read messages or derive access from discovery sources.

The rejected alternatives were another mandatory participant input and use of configured grants as discovery sources.
Both can hide the chat the operator wants to add.
A direct Graph `$search` request is also unsuitable because the chat list API does not document that operation.

### Bound each batch and preserve explicit continuation

Each search action makes at most ten Graph requests.
Each request has a five-second timeout, with automatic retries disabled.
The adapter keeps cursors in temporary memory and binds them to tenant, normalized title query, and result limit.
Cursors expire five minutes after the last successful page.
The adapter rejects foreign or expired cursors without a metadata request.

The adapter retains deduplication state for at most 10,000 matched canonical IDs.
If that bound is reached, it reports a resource-limit error and asks for a narrower query.
It does not silently truncate and claim a complete search.

Errors do not consume the prior continuation state.
The operator can retry a transient failure or start a new search.
A user-chat 404 skips that source and increments an unavailable-source count.
The result retains that count, so the TUI reports partial coverage even when no continuation remains.
Consent errors, rate limits, timeouts, and malformed responses fail the current batch explicitly.

### Keep the current UI loop and save authority

The view model opens the existing Group Chat result screen directly.
Its focused input is `Group Chat name`.
Input changes, navigation, and cancellation invalidate the previous request generation.
The UI applies a result only if its screen, query, and generation still match.

Schematic sequence; existing credential and canonical-ID checks remain required:

```text
Group Chat add -> title input -> bounded metadata batch
metadata result -> UI loop -> compare screen, query, generation
incomplete result -> Continue search -> next bounded batch
selected canonical ID -> existing review -> existing configuration writer
```

The directory client owns call-local HTTP responses and temporary continuation state.
The view model owns temporary query, result, and selection state.
The configuration writer owns the durable canonical destination list.
The daemon's current Teams authorization code consumes that list after configuration activation.
No discovery user, title, member preview, or continuation enters configuration or actor persistence.

## Risks / Trade-offs

- [Large tenant] → Require explicit continuation between bounded batches and show incomplete coverage.
- [Graph throttles requests] → Display the error and retain a reusable prior cursor.
- [A user has no available chat metadata] → Skip only a 404 and show the unavailable-source count.
- [Named chat does not yet appear in metadata] → Keep manual canonical-ID entry available.
- [A stale request finishes after input changes] → Reject it with the existing request-generation guard.

## Migration Plan

No configuration migration is required.
The owner deploys the corrected binary and tests partial and full chat-title searches with existing consent.
Rollback uses the previous binary or advanced canonical-ID entry; saved IDs remain compatible.
