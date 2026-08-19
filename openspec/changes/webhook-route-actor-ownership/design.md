# Design: webhook-route-actor-ownership

## Context

`WebhookRouteStore` serializes read-modify-write over per-route JSON files with a named OS mutex, because two processes write: the daemon (agent tools) and the CLI (command + TUI). The daemon-internal race is real — two sessions can invoke `set_webhook` concurrently on tool-executor threads. The mutex-based test proved flaky on Windows CI: its failing assertion measured thread-pool dispatch latency, not store correctness (root cause confirmed by reproduction under forced pool limits). The maintainer direction: an actor should own this state; CLI configuration should route through the daemon's HTTP API.

## Goals / Non-Goals

**Goals:**

- One mutation authority for webhook routes inside the daemon.
- CLI writes route through the daemon when it is reachable.
- Deterministic tests: message ordering, not thread choreography.
- Full backward compatibility across a version-skew window in both directions.

**Non-Goals:**

- No mutex removal in this change (follow-up after the skew window).
- No change to delivery, verification, hot-reload, or route file format.
- No generalization to other config surfaces yet.

## Decisions

### D1: Plain actor over the existing store; disk stays canonical

`WebhookRouteActor` is an ordinary `ReceiveActor` (not persistent). It handles `UpsertRoute`/`DeleteRoute`/`GetRoute`/`ListRoutes` messages, validates via `WebhookRouteValidator`, and persists through the existing `WebhookRouteStore` (which keeps its atomic temp-file-and-move write and, for the skew window, its mutex). Disk is the canonical store; the actor is the serialization point, not a second source of truth. Rationale: Akka.Persistence would create journal types and a second copy of secret-bearing config — both forbidden by the back-compat and security constraints. Alternative considered: journaled actor with disk projection — rejected for exactly those reasons.

### D2: External file changes reconcile through the existing hot-reload signal

The `inbound-webhooks` spec already requires hot reload of route files. The actor subscribes to the same change signal the delivery pipeline uses and re-reads affected routes on external modification (old CLI or operator edits during the skew window). Reads served by the actor reflect disk after reconciliation; the mutex under the store keeps same-route cross-process RMW safe until the follow-up removes it. No new watcher machinery.

### D3: HTTP resource mirrors the reminders precedent

`/api/webhooks` endpoints are thin minimal-API handlers that `Ask` the actor with the standard request timeout and map results to HTTP statuses (validation failure → 400 with the validator's message; unknown route → 404; success → 200/204). Same auth middleware and exposure-mode rules as every `/api/*` surface. Agent tools inside the daemon `Ask` the actor directly — no loopback HTTP.

### D4: CLI mode selection is explicit and probe-based

`WebhooksCommand` resolves its write path once per invocation: daemon reachable and resource present → API path; daemon down, unreachable, or 404 on the resource (old daemon) → direct file path with one stderr notice naming the mode. Exit codes and stdout formats are identical in both modes. The notice goes to stderr so scripts that parse stdout are untouched. A hard API error other than unreachable/404 (e.g., 400 validation, 401 auth) fails the command — it does NOT fall back to the file path, because that would bypass the daemon's enforcement point.

### D5: Ask timeout and failure semantics

Tool and HTTP fronts use the daemon's standard ask timeout. A store I/O failure inside the actor faults the message with the error returned to the caller (tool result error / HTTP 500) — the actor does not swallow persistence failures. The actor itself restarts under default supervision on unexpected exceptions; its state is rebuilt from disk on restart, so a restart is always safe.

### D6: Test replacement, not test repair

The Windows-flaky choreography test is deleted and replaced by: (a) actor tests proving mailbox serialization of concurrent RMW (two `Ask`s, deterministic final state), (b) endpoint tests for `/api/webhooks` status mapping, (c) a CLI mode-selection test (daemon up → API call recorded; daemon down → file written and notice emitted), (d) ONE narrow store-level cross-process-guard test that exercises the mutex through the store API without asserting on scheduling (outcome-only, no bounded event waits) — retained only until the mutex follow-up removes both.

## Risks / Trade-offs

- [Skew window: old CLI writes while actor holds cached state] → D2 reconciliation from the existing hot-reload signal; mutex retained under the store; per-route files bound the blast radius to same-route RMW.
- [CLI now depends on daemon availability for its primary path] → D4 explicit dual mode preserves offline configuration; only reachability/404 selects the file path, so enforcement cannot be bypassed by inducing errors.
- [Actor becomes a throughput bottleneck] → route mutations are rare, low-volume operator/agent actions; a single mailbox is far above the required throughput.
- [Two fronts drift (HTTP vs tools)] → both are thin `Ask` adapters over the same messages; validation lives only in the actor.
- [Skill/docs drift] → `netclaw-operations` skill row updated in the same PR per the skills sync rule.

## Migration Plan

Ship steps 1 and 2 together in one release. No data migration; no config change. One release later, a follow-up change removes the store mutex and the cross-process-guard test once the skew window closes. Rollback is a PR revert; the disk format never changed.

## Open Questions

- Whether `InboundWebhooksConfigViewModel` (TUI) should share the exact mode-selection component with `WebhooksCommand` or call `DaemonApi` through its existing view-model seam — decided at implementation by whichever reuses the existing dynamic-validation plumbing without a new construct.
- Exact change-signal plumbing for D2 (reuse the delivery pipeline's watcher subscription vs. a second subscription) — implementation detail; the requirement is single watcher machinery, no polling.
