## Context

See [proposal.md](proposal.md) and the linked specification deltas.
The existing Teams UI uses a shared Channels view model and an SDK-neutral directory boundary.
Its saved configuration uses canonical IDs and structured Team/channel overrides.
The existing Group Chat capability deliberately deferred chat metadata discovery.

## Goals / Non-Goals

**Goals:**

- Keep saved configuration authority separate from friendly metadata.
- Make add, inspect, and remove operations explicit for each configuration scope.
- Make asynchronous search results safe for the Termina event loop.
- Add optional, bounded chat metadata discovery through the current Graph adapter.

**Non-Goals:**

- No tenant-wide user or chat enumeration.
- No Group Chat message history, app installation change, or new persistent metadata.
- No generic approval or actor framework redesign.
- No live tenant test, deployment, merge, or upstream pull request.

## Decisions

### Use typed row identities and explicit edit scope

The UI will carry global scope or an exact Team/channel pair through navigation and confirmation.
Rows will use canonical typed identity instead of display labels.
This avoids accidental removal from another scope when names match.

The alternative was nullable channel state on search screens.
That state caused scope leakage and cannot distinguish duplicate display names.

### Invalidate and marshal every directory result

Each input, screen, scope, or participant change cancels the current request and increments its generation.
The background continuation will use `InvokeAsync` to apply data on the Termina loop.
The queued action will repeat screen, query, scope, participant, and generation checks.

```text
loop event -> capture request facts -> directory call
directory return -> queue loop action
loop action -> compare captured facts -> apply or discard
```

This schematic flow omits the existing credential and cancellation gates.
The alternative was direct reactive state mutation after `ConfigureAwait(false)`.
That approach can race rendering and navigation.

### Extend the directory seam with paged chat metadata

The SDK-neutral contract will expose chat metadata, one page result, and opaque continuation values.
The Graph adapter will keep SDK types and next URLs private.
The continuation owner will bind each value to tenant, selected user, and request generation.

The adapter will call `/users/{id}/chats` with an approximately 25-record page size.
It will use local filtering for topic and participant preview.
It will accept only records with `chatType == group` and a canonical Group Chat ID.

The alternative was a TUI Graph client or raw next-link storage.
Both alternatives breach the adapter boundary or allow foreign continuation use.

### Preserve canonical configuration and existing runtime activation

The configuration writer will validate new values before it mutates its draft.
It will save only canonical IDs and existing structured overrides.
The runtime ACL consumer remains the authority after the established host activation.

No chat metadata, selected participant, continuation, or label cache enters configuration or actor persistence.

## Risks / Trade-offs

- [The tenant lacks `Chat.ReadBasic.All`] → Friendly chat discovery fails visibly. Manual valid-ID entry and existing ingress remain available.
- [Graph gives incomplete labels] → The UI retains a bounded canonical ID suffix and removal action.
- [A request ignores cancellation] → The generation test discards its result.
- [The final restriction is removed] → The confirmation states the legacy effective access outcome before save.

## Migration Plan

1. Deploy the additive UI and directory capability with no new required config fields.
2. Grant optional `Chat.ReadBasic.All` only when friendly discovery is needed.
3. Restart the coordinated host after a completed configuration save.
4. To roll back, remove the new binary or use canonical manual chat IDs. Existing configuration remains compatible.
