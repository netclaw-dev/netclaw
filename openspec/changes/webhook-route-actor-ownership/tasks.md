# Tasks: webhook-route-actor-ownership

Implementation branch: decided at apply time (standalone off `dev`, or stacked on stack #2003 once it merges). Every group ends with a green build (zero warnings), the affected suites green, `dotnet slopwatch analyze` clean, and header verification clean, and is one commit.

## 1. WebhookRouteActor (daemon-side authority)

- [x] 1.1 Add `WebhookRouteActor` (plain `ReceiveActor`) with `UpsertRoute`/`DeleteRoute`/`GetRoute`/`ListRoutes` messages; validate via `WebhookRouteValidator` before persistence; persist through the existing `WebhookRouteStore`; store I/O failures return errors to the caller, never swallowed
- [x] 1.2 Register the actor in daemon wiring; rewire `SetWebhookTool` and `DeleteWebhookTool` to `Ask` the actor; tool schemas and result shapes unchanged
- [x] 1.3 RESOLVED BY DESIGN AMENDMENT (D2 rewritten): no route change signal exists in the codebase — the actor is cacheless instead, so every operation reads disk and external changes need no reconciliation; proven by `An_external_writer_change_is_visible_to_the_next_actor_read`
- [x] 1.4 Actor tests: two concurrent FIELD-LEVEL updates to the same route lose neither field (the real RMW lost-update proof — mutation messages carry data, the actor does read-modify-write per message), validation-rejection-does-not-persist, restart rebuilds from disk, reconciliation on the hot-reload SIGNAL (fake the signal; the existing inbound-webhooks hot-reload coverage owns file-to-signal — do NOT write a new filesystem-watcher timing test)

## 2. /api/webhooks resource

- [x] 2.1 Add minimal-API endpoints (GET list, GET/PUT/DELETE by name) that `Ask` the actor; map validation failure → 400, unknown route → 404, success → 200/204; same auth middleware and exposure-mode rules as sibling `/api` surfaces
- [x] 2.2 Endpoint tests: status mapping per outcome, auth rejection parity with an existing `/api` surface, upsert-persists-through-actor round trip

## 3. CLI dual-mode write path

- [ ] 3.1 Extend the `DaemonApi` client with the webhook resource calls
- [ ] 3.2 `WebhooksCommand`: probe-based mode selection per D4 (reachable+present → API; unreachable/404 → direct file + one stderr notice; other API errors fail without fallback); stdout and exit codes identical in both modes
- [ ] 3.3 `InboundWebhooksConfigViewModel`: route saves through the same mode selection (reuse the command's seam per the design's open question — no parallel construct)
- [ ] 3.4 CLI tests: mode selection (API path recorded when daemon up; file written + notice when daemon DOWN; file written + notice on 404 from an OLD daemon — a distinct test from daemon-down; 400 and 401 fail the command with NO file write); existing `WebhooksCommandTests` stay green unchanged in file mode
- [ ] 3.5 View-model fake-failure test: a failed API save in `InboundWebhooksConfigViewModel` blocks the save BEFORE any persistence (Automation Floor rule for dynamic validation)

## 4. Test replacement and skew guard

- [ ] 4.1 Delete `Update_serializes_read_modify_write_operations_across_store_instances_and_path_aliases` and the same choreography pattern in `Update_lock_wait_honors_cancellation`; replace with ONE outcome-only store-level cross-process-guard test (no bounded event waits, no scheduling asserts) retained until the mutex follow-up
- [ ] 4.2 Verify no remaining test in the repo asserts on thread-pool scheduling for this capability (grep for the choreography pattern)

## 5. Finish

- [ ] 5.1 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` for the new endpoints and the CLI mode notice; bump `metadata.version`
- [ ] 5.2 Full solution build, full `Netclaw.Actors.Tests` + `Netclaw.Daemon.Tests` + `Netclaw.Cli.Tests` + `Netclaw.Configuration.Tests`, slopwatch, headers; native smoke tapes for the webhooks TUI surface if touched (Termina rule)
- [ ] 5.3 `/opsx-sync` the `webhook-route-authority` spec; file the follow-up issue for mutex removal after the skew window; PR with the back-compat story in the body
