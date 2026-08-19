# Proposal: webhook-route-actor-ownership

## Why

Webhook route files have two writer processes today: the daemon (agent tools `set_webhook`/`delete_webhook`) and the CLI (`netclaw webhooks`, config TUI). A named OS mutex in `WebhookRouteStore` guards the overlap. Locks guard shared state instead of removing the sharing; the guard already produced a flaky Windows CI test whose two-blocked-threads choreography asserts on thread-pool scheduling, not on the store. An actor that owns the state removes the race by construction and gives validation and ACL one enforcement point.

Source PRDs: PRD-002 (gateway security envelope), PRD-003 (operator UX and ops console), PRD-004 (CLI onboarding and config). Related capability: `inbound-webhooks` (delivery side, unchanged).

## What Changes

- **Step 1 — daemon-side ownership.** A new `WebhookRouteActor` becomes the single authority for webhook route mutations inside the daemon. `SetWebhookTool` and `DeleteWebhookTool` `Ask` the actor. The actor validates with `WebhookRouteValidator`, then persists through the existing store. Mailbox order replaces in-process mutex contention. The flaky mutex-choreography test is replaced by deterministic actor message-order tests.
- **Step 2 — CLI routes through the daemon.** New additive HTTP resource `/api/webhooks` (GET list, GET/PUT/DELETE by name) fronting the actor via `Ask`, in the same shape as the existing reminders HTTP-CRUD-over-actor endpoints. `WebhooksCommand` and `InboundWebhooksConfigViewModel` use the authenticated `DaemonApi` client when the daemon is reachable. When the daemon is down, or an old daemon returns 404 for the resource, the CLI writes route files directly and says so in its output. The mode is explicit and disclosed, not a silent fallback.
- **Backward compatibility is a hard requirement, not an aspiration:**
  - The per-route JSON file format is unchanged and disk stays the canonical store. Actor state is a cache of disk. No Akka.Persistence, no journal types.
  - CLI flags, exit codes, and stdout formats are unchanged.
  - The HTTP API change is additive only. Tool schemas for `set_webhook`/`delete_webhook` are unchanged.
  - The named `Global\` mutex in `WebhookRouteStore` REMAINS for one deprecation release to cover the old-CLI-writes-files version skew. The actor tolerates external file changes by reload. Mutex removal is a separate follow-up change after the skew window.

In scope: the actor, tool rewiring, `/api/webhooks`, CLI dual-mode write path, test replacement, `netclaw-operations` skill row for the new endpoints.
Out of scope: mutex removal (follow-up change); any change to webhook delivery, verification, or hot-reload semantics; routing other config surfaces (channels, providers) through the daemon.

## Capabilities

### New Capabilities

- `webhook-route-authority`: single-writer ownership of webhook route mutations — the daemon actor as authority, the HTTP resource and agent tools as thin fronts, the CLI dual-mode write path, and the version-skew tolerance rules.

### Modified Capabilities

<!-- none: inbound-webhooks delivery, verification, and hot-reload requirements are unchanged; hot reload is what makes the skew-window tolerance work -->

## Impact

- Code: `src/Netclaw.Actors` (new actor; two tools rewired), `src/Netclaw.Daemon` (new endpoints), `src/Netclaw.Cli` (`WebhooksCommand`, `InboundWebhooksConfigViewModel`, `DaemonApi` client), `src/Netclaw.Configuration.Tests` (replace the choreography test), `feeds/skills/.system/files/netclaw-operations/SKILL.md`.
- Security impact: validation and ACL for route mutations consolidate in the actor (Cross-Boundary Contract Rule). `/api/webhooks` rides existing pairing auth and exposure-mode rules; route files remain secret-bearing config with the same on-disk posture. Default-deny is unchanged.
- Operational impact: `netclaw webhooks` output gains one mode line when the daemon is unreachable; runbook and CLI help updated. No config migration, no schema change, no restart requirement.
- Rollout: step 1 and step 2 can ship in one release; mutex removal ships one release later. Revert is a plain PR revert; disk format never changes.
