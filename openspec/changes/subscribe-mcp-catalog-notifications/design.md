## Context

`McpClientManager` owns each daemon-scoped MCP client and its immutable tool and prompt generation.
It polls each connected server after a five-minute interval.

MCP revision 2026-07-28 uses `subscriptions/listen` and an acknowledgement notification.
Older revisions send direct list-change notifications when the server declares `listChanged` support.

The MCP SDK invokes notification handlers on its receive path.
A handler must not wait for another request on that client.

## Goals / Non-Goals

**Goals:**

- Refresh tool and prompt catalogs promptly after a supported server sends a change signal.
- Support modern subscription acknowledgements and legacy direct notifications.
- Preserve one atomic tool and prompt generation.
- Preserve the poll as the repair and compatibility path.
- Bound refresh work during a notification burst.
- Remove all notification state when its client generation ends.
- Keep protocol failures visible through safe structured logs.

**Non-Goals:**

- Add resource discovery, resource reads, or resource subscriptions.
- Add a new configuration option.
- Add status API, CLI, TUI, actor message, or persistence fields.
- Change MCP grants or tool approval behavior.
- Remove the catalog poll.

## Decisions

### Give each client candidate one notification lease

`McpClientManager` will create a required notification lease before it creates the MCP client.
The lease will supply the notification handlers for the client options.

The candidate snapshot will retain this lease with the client.
The manager will activate the lease only after it publishes the candidate generation.

The manager will deactivate an old lease before it replaces or disposes the old client.
An old lease cannot refresh a newer generation after a reconnect.

The alternative used manager-wide handlers without generation ownership.
That design permits stale notifications from an old transport to change a new catalog.

### Select the protocol adapter from the negotiated revision

For revision 2026-07-28, the lease will start one `subscriptions/listen` request.
It will request tool and prompt list changes with the subscription identifier `netclaw-catalog`.

The lease will wait up to 15 seconds for the matching acknowledgement.
It will use the injected `TimeProvider` for this timeout.

The acknowledgement can accept both event types, one event type, or neither event type.
The lease will enable only the accepted event types.

For older revisions, the lease will use direct notification handlers.
It will enable each handler only when the matching server capability declares `listChanged`.

The manager will install all handlers before client creation.
This order prevents a fast server from sending an event before handler registration.

The alternative sent `subscriptions/listen` to all protocol revisions.
That design converts expected legacy compatibility into avoidable method errors.

### Keep notification handlers non-blocking

Each enabled handler will write a signal to a bounded channel with capacity one.
The handler will then return without a catalog request.

One worker will consume the channel after publication.
The worker will run one refresh and retain at most one follow-up signal.

A pre-publication notification will remain in the channel until activation.
The active generation will process that signal after publication.

The alternative refreshed inside the SDK handler.
That design can deadlock the client receive path while it waits for its own response.

### Reuse one catalog refresh transaction

The poll and notification paths will call one refresh transaction.
That transaction will list all supported tools and prompts before publication.

The transaction will return `Changed`, `Unchanged`, or `Failed`.
`Changed` will publish one immutable generation.
`Unchanged` will retain the current generation.
`Failed` will retain the last good generation.

The poll will keep its five-minute throttle.
A notification will bypass that throttle.
A successful notification refresh will reset the poll timestamp.

The alternative added a separate notification refresh implementation.
That design could drift from poll fingerprints, publication order, and failure behavior.

### Use logs for notification diagnostics

The manager will log the selected compatibility mode at information level.
It will log acknowledgement timeouts, method failures, and unexpected stream closure at warning level.

Logs will identify the configured server and the safe failure category.
Logs will not include raw protocol payloads, credentials, command environment values, or prompt content.

The existing status object will remain unchanged.
This choice keeps the change inside the current lifecycle boundary.

## Actor and Persistence Boundaries

The MCP manager remains a daemon service outside the session actor hierarchy.
The change adds no actor messages and no persisted records.

Sessions continue to consume the existing tool registry and audience-filtered skill registry.
The existing MCP server grant remains the publication and use boundary.

## Failure and Recovery

- An unsupported modern method leaves the connection healthy and keeps the poll active.
- An acknowledgement timeout leaves the connection healthy and keeps the poll active.
- A partial acknowledgement enables only the accepted event types.
- An unexpected listener exit disables that lease and keeps the poll active.
- A failed catalog refresh retains the last good generation and the prior poll timestamp.
- A later notification or poll can repair a missed or failed refresh.
- A reconnect creates a new lease and disposes the old lease with its client generation.
- Shutdown stops each lease worker before it disposes the associated client.

## Risks / Trade-offs

- [Risk] A notification burst can produce excess list requests. -> The capacity-one channel permits one active refresh and one queued follow-up.
- [Risk] A legacy server can declare support but never send events. -> The existing poll remains active.
- [Risk] A modern server can acknowledge only one requested event. -> The lease enables only the acknowledged subset.
- [Risk] A notice can arrive during candidate initialization. -> The lease queues it until the candidate becomes active.
- [Risk] A notice can arrive from a replaced client. -> The generation-owned lease rejects work after deactivation.

## Migration Plan

1. Add the protocol adapter and generation-owned notification lease.
2. Reuse the catalog refresh transaction for poll and event paths.
3. Add modern, legacy, reconnect, failure, and poll-repair tests.
4. Update PRD-006 and the operations skill.

No persisted data migration is necessary.
A rollback removes the leases and restores poll-only behavior.

## Open Questions

No open question blocks this change.
GitHub issue #1807 will define resource subscription filters and resource ownership later.
