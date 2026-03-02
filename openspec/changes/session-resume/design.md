## Context

Netclaw sessions are persistent Akka actors keyed by entity ID. When a session
passivates (idle timeout), the actor stops but its full conversation history
survives in the Akka journal. Rehydration is automatic — any message routed to
that entity ID recreates the actor and replays the journal.

The session catalog (`SessionCatalogService`) tracks all sessions in SQLite with
metadata: title, channel, turn count, last activity, log path. The
`GET /api/sessions` REST endpoint already exposes this data. The actor layer
already supports multiple concurrent subscribers via `JoinSession` with
`OutputFilter` bitmask — there is no single-subscriber constraint.

The gap is in the SignalR/registry layer: `SessionRegistry.CreateSessionAsync()`
always generates a fresh `signalr/{guid}` session ID. There is no path for a
SignalR client to say "materialize a pipeline for this existing session ID" in a
way that survives actor passivation (the existing `EnsureSessionAsync` only
reattaches to sessions already materialized in the registry's in-memory
dictionary, not to arbitrary persistent sessions).

## Goals / Non-Goals

**Goals:**

- Let users browse recent sessions and resume any of them from the TUI
- Let users resume a session directly via `netclaw chat --resume <id>`
- Support cross-channel resume (e.g., resume a Slack-originated session from TUI)
- Properly rehydrate passivated sessions by routing through the session manager

**Non-Goals:**

- Full conversation history replay in the TUI (show a "resumed" indicator, not
  all prior messages — journal replay is internal to the actor)
- Session search/filtering beyond the existing catalog columns
- Session deletion or archival commands (future work)
- Multi-user session sharing (daemon is single-user)

## Decisions

### Decision: Reuse `EnsureSessionAsync` for resume

**Choice:** Extend the existing `SessionRegistry.EnsureSessionAsync()` to handle
resume by always materializing a pipeline when a session ID is provided but not
found in the in-memory dictionary, rather than adding a separate
`JoinExistingSessionAsync` method.

**Why:** `EnsureSessionAsync` already has the "if session ID provided, attach or
create" logic. The only missing piece is that when the session ID isn't in the
in-memory dictionary, it should still materialize against that ID (triggering
actor rehydration) rather than failing. This is actually already implemented —
the method calls `MaterializeAndBindSessionAsync` for unknown session IDs. The
flow just needs the CLI to pass the catalog's session ID instead of `null`.

**Alternative considered:** New `ResumeSessionAsync` method on the hub. Rejected
because it would duplicate the ensure/materialize logic for no benefit.

### Decision: Session listing via REST, not SignalR

**Choice:** `ListSessionsAsync()` on `DaemonClient` calls the existing
`GET /api/sessions` REST endpoint rather than adding a new SignalR hub method.

**Why:** Session listing is a simple request/response query with no streaming
or subscription semantics. The REST endpoint already exists and returns the
right data. Adding a SignalR method would duplicate it for no benefit.
`DaemonClient` already has the daemon endpoint URL for health checks; reusing
it for session listing is natural.

### Decision: TUI sessions page as a Terminal.Gui ListView

**Choice:** Add a `SessionsPage` using Terminal.Gui's `ListView` bound to
catalog entries. Selecting a session navigates to the existing `ChatPage` with
the selected session ID passed as a resume parameter.

**Why:** The TUI already uses Terminal.Gui pages (`ChatPage`). A list view is
the simplest UI that lets users scan and select. No custom rendering needed —
format each row as `[channel] title (N turns, last active X ago)`.

**Alternative considered:** A `TableView` with sortable columns. Overkill for
MVP — can be added later if users want sorting/filtering.

### Decision: `--resume` flag on `chat`, not a separate `sessions` command

**Choice:** `netclaw chat --resume <id>` for direct resume. The TUI session
browser is accessible via `netclaw sessions` (new subcommand) or potentially
via a keyboard shortcut within the chat TUI.

**Why:** Resume is a variant of "start chatting" — it makes sense as a flag on
`chat`. The browser is a separate entry point for discovery. Both converge on
the same `ChatPage` with a session ID.

### Decision: Channel type preserved, not overridden

**Choice:** When resuming a Slack-originated session from the TUI, the channel
type stays as `slack` in the catalog. The TUI subscriber joins with its own
`OutputFilter` but doesn't change the session's recorded channel.

**Why:** The channel type reflects the session's origin, not the current
viewer. A resumed session might still have Slack delivering messages to it. The
TUI is an additional subscriber, not a replacement.

## Risks / Trade-offs

**[Risk] Dual-subscriber output divergence** — If a Slack session is live and a
TUI client resumes it, both receive output. The Slack subscriber formats for
Slack (markdown blocks), the TUI formats for terminal. This is by design (each
subscriber handles its own rendering), but users might be surprised that typing
in the TUI triggers a response that also appears in Slack.
→ *Mitigation:* Show a "live on: slack" indicator in the TUI when resuming a
session that has other active subscribers. Defer to post-MVP if complex.

**[Risk] Stale catalog entries** — The catalog tracks `last_activity` but not
whether the session actor is currently live or passivated. Resuming a very old
session will work (journal replay) but could be slow if the journal is large.
→ *Mitigation:* None needed for MVP. Journal replay is bounded by snapshot
frequency (the actor snapshots before passivation).

**[Risk] Session ID opacity** — Session IDs like `signalr/abc123` or
`C07ABC/1234567890.123456` are not user-friendly. The `--resume` flag requires
the exact ID.
→ *Mitigation:* The TUI browser shows human-readable titles; the ID is only
needed for the CLI `--resume` flag. Consider adding short aliases later.

## Open Questions

- Should the TUI sessions page be the default landing page (replacing immediate
  chat creation), or only accessible via `netclaw sessions`?
- Should resumed sessions show a summary of the last N messages for context, or
  just a turn count indicator? Showing history would require a new query against
  the persistence journal, which is more complex.
