# Design: webhook-route-actor-ownership

## Context

`WebhookRouteStore` serialized read-modify-write over per-route JSON files with a named OS mutex, because two processes wrote: the daemon (agent tools) and the CLI (command + TUI). The daemon-internal race is real — two sessions can invoke `set_webhook` concurrently on tool-executor threads. The mutex-based test proved flaky on Windows CI: its failing assertion measured thread-pool dispatch latency, not store correctness (root cause confirmed by reproduction under forced pool limits). The maintainer direction: an actor should own this state; CLI configuration should route through the daemon's HTTP API.

## Goals / Non-Goals

**Goals:**

- One mutation authority for webhook routes inside the daemon.
- CLI writes routes only through the daemon.
- Deterministic tests: message ordering, not thread choreography.
- An old CLI keeps working against a new daemon: its direct file write stays visible, because the actor holds no cache.
- A new CLI against an old daemon fails loudly and names the upgrade. Per D4 it does not fall back.

**Non-Goals:**

- No change to delivery, verification, hot-reload, or route file format.
- No generalization to other config surfaces yet.

## Decisions

### D1: Plain actor over the existing store; disk stays canonical

`WebhookRouteActor` is an ordinary `ReceiveActor` (not persistent). It handles `UpsertRoute`/`DeleteRoute`/`GetRoute`/`ListRoutes` messages, validates via `WebhookRouteValidator`, and persists through the existing `WebhookRouteStore` (which keeps its atomic temp-file-and-move write). Disk is the canonical store; the actor is the serialization point, not a second source of truth. Rationale: Akka.Persistence would create journal types and a second copy of secret-bearing config — both forbidden by the back-compat and security constraints. Alternative considered: journaled actor with disk projection — rejected for exactly those reasons.

### D2: The actor is cacheless — external file changes need no reconciliation

Implementation finding (supersedes the original signal-based wording): no route change signal exists. The `inbound-webhooks` spec permits request-time mtime-gated reload, and `WebhookRouteCatalog` re-reads route files lazily; there is no watcher on the webhooks directory. Rather than build one, the actor holds no cache: every read and every read-modify-write goes through the store to disk. An external write (old CLI, operator edit) is therefore visible to the very next actor operation with no reconciliation step. This is the direct consequence of D1 — the actor is the serialization point, not a second source of truth. The store takes no cross-process lock: each write is atomic on its own, and the accepted worst case during version skew is one lost same-route update. No new watcher machinery.

### D3: HTTP resource mirrors the reminders precedent

`/api/webhooks` endpoints are thin minimal-API handlers that `Ask` the actor with the standard request timeout and map results to HTTP statuses (validation failure → 400 with the validator's message; unknown route → 404; success → 200/204). Same auth middleware and exposure-mode rules as every `/api/*` surface. Agent tools inside the daemon `Ask` the actor directly — no loopback HTTP.

### D4: CLI route mutations are daemon-only

Maintainer decision: no fallback of any kind — no dual mode, no `--offline` flag, no local write path. One store, one writer.

`WebhooksCommand` probes availability once per invocation, immediately before the write. Three answers fail the command with exit code 1 and no file change:

- The daemon does not answer (transport failure, timeout, or no daemon client at all) → "The daemon is not reachable. Start the daemon to manage webhook routes."
- The daemon answers 404 for the resource (a daemon that predates it) → "This daemon does not serve the webhook route API. Upgrade the daemon."
- The daemon answers any other failure status → the daemon's own message, because the daemon is the enforcement point.

A transport failure between the probe and the write also fails the command. The daemon may have applied the change before the connection broke, so the CLI reports the uncertainty rather than repeating the write.

Reads (`list`, `show`, `validate`) stay on canonical disk. That is the read path, not a fallback: disk is the route store, the actor holds no cache, and `show --show-secret` needs a secret the API never returns. Argument grammar, `--dry-run`, and the merge preview all run before the probe, so they keep their own messages and exit codes with no daemon present.

The supported daemon-absent authoring path is a route file written on disk outside the CLI, which the daemon loads at startup.

### D5: Ask timeout and failure semantics

Tool and HTTP fronts use the daemon's standard ask timeout. A store I/O failure inside the actor faults the message with the error returned to the caller (tool result error / HTTP 500) — the actor does not swallow persistence failures. The actor itself restarts under default supervision on unexpected exceptions; its state is rebuilt from disk on restart, so a restart is always safe.

### D6: Test replacement, not test repair

The Windows-flaky choreography test is deleted and replaced by: (a) actor tests proving mailbox serialization of concurrent RMW (two `Ask`s, deterministic final state), (b) endpoint tests for `/api/webhooks` status mapping, (c) CLI command tests per daemon answer (daemon up → API call recorded and no file written; daemon down → exit 1 with the unreachable message and no file written; old daemon 404 → exit 1 with the upgrade message and no file written; 400/401/403 → exit 1 with the daemon's message and no file written), (d) no store-level cross-process-guard test, because the store holds no lock to guard.

## Risks / Trade-offs

- [Skew window: old CLI writes while the actor serves a request] → D2 keeps the actor cacheless; atomic writes keep every file complete; per-route files bound the blast radius to one lost same-route update.
- [CLI now depends on daemon availability for every route mutation] → accepted by maintainer decision (D4). A fallback would hide a misconfigured or stopped daemon and would let an operator bypass the enforcement point by inducing an error. An operator without a daemon authors the route file on disk and starts the daemon, which loads it.
- [Actor becomes a throughput bottleneck] → route mutations are rare, low-volume operator/agent actions; a single mailbox is far above the required throughput.
- [Two fronts drift (HTTP vs tools)] → both are thin `Ask` adapters over the same messages; validation lives only in the actor.
- [Skill/docs drift] → `netclaw-operations` skill row updated in the same PR per the skills sync rule.

## Migration Plan

Ship steps 1 and 2 together in one release. No data migration; no config change. Rollback is a PR revert; the disk format never changed.

## Open Questions

- RESOLVED: `InboundWebhooksConfigViewModel` needs no mode-selection seam — it has no route save. It writes only the `Webhooks.Enabled`/`ExecutionTimeoutSeconds` section of `netclaw.json` and delegates route authoring to the `netclaw webhooks` command. Its route read runs against canonical disk and is already correct under the cacheless design.
- RESOLVED BY D2 AMENDMENT: no change-signal plumbing exists or is needed — the actor is cacheless, so no watcher machinery was built.
