## 1. Config and ingress plumbing

- [x] 1.1 Add a top-level inbound-webhook feature toggle to `netclaw.json`, plus per-route JSON schema/contracts for `NetclawPaths/config/webhooks/*.json` where the filename defines the route name/path.
- [x] 1.2 Add daemon-side route-file discovery and registry services that load one route per file from `config/webhooks`, treat route files as secret-bearing config, and expose only enabled/valid routes.
- [x] 1.3 Add generic verification services for a minimal MVP set (for example HMAC and shared-header secret), event filtering, and delivery-id extraction without modeling one verifier type per provider.
- [x] 1.4 Expose `/api/webhooks/{route}` ingress endpoints with request-size enforcement, duplicate suppression, rate limiting, and fail-closed rejection behavior.
- [x] 1.5 Implement hot reload for route files without daemon restart using request-time mtime-gated refresh or equivalent minimal logic.
- [x] 1.6 Ensure invalid route edits remove the prior loaded route immediately instead of serving stale config, and emit load/unload failure alerts via `IOperationalNotificationSink`.

## 2. Webhook session execution

- [x] 2.1 Add `ChannelType.Webhook` and session-launch plumbing for accepted deliveries.
- [x] 2.2 Add additive route prompt-overlay injection so webhook routes augment the base system prompt without replacing it.
- [x] 2.3 Implement webhook invocation execution that normalizes the payload, launches one autonomous session per accepted delivery, and tracks `NotifyPolicy` success/failure.
- [x] 2.4 Emit deterministic operational receipt alerts for accepted deliveries with route/event/delivery metadata, separate from human-facing notification behavior.

## 3. Human-facing notification routing

- [x] 3.1 Add webhook notification-target handling that maps configured Slack targets into prompt/tool instructions without changing reminder semantics.
- [x] 3.2 Reuse the existing proactive Slack thread path so webhook-triggered notifications create Slack-native threads/sessions rather than rebinding the original webhook session.

## 4. Validation and documentation

- [x] 4.1 Add tests for route-file discovery, filename-derived route resolution, hot reload on edit, invalid-edit fail-closed removal, verifier failures, request-size rejection, duplicate suppression, rate limiting, accepted-session launch, prompt overlay injection, and `Required` vs `Conditional` notify behavior.
- [x] 4.2 Add tests that route load/reload/unload failures emit operational alerts and that accepted-delivery receipt alerts remain separate from reminder-style human notifications.
- [x] 4.3 Update config and operator docs for webhook feature enablement in `netclaw.json`, per-route file registration under `config/webhooks`, ingress security expectations, secret-bearing config handling, and Slack notification-target setup.
- [ ] 4.4 Update any required system skill/docs for config-format changes and run the relevant test/quality gates (`dotnet test`, `dotnet slopwatch analyze`, and evals if skill content changes).

## 5. Observability polish (0.10.1)

- [ ] 5.1 Emit `ToolResultOutput` for every tool result in `LlmSessionActor.ToolExecutionCompleted` so webhook/reminder execution actors can correctly track notification-tool completion (fixes false-positive "no notification tool was invoked" warning — Aaronontheweb/netclaw#546). Update existing session integration tests to drain the new output between `ToolCallOutput` and following assertions.
- [ ] 5.2 Add a `WebhookTelemetry` static class mirroring `ChannelTelemetry` with per-outcome `Interlocked` counters (`accepted`, `route_not_found`, `verification_failed`, `body_too_large`, `invalid_json`, `rate_limited`, `event_filtered`, `duplicate_delivery`), OpenTelemetry `Meter` instruments, `GetSnapshot()`, and `ResetForTests()`.
- [ ] 5.3 Instrument `/api/webhooks/{route}` ingress in `WebhookEndpointRouteBuilderExtensions` with structured daemon logs per outcome (fields: `route`, `reason`, `remote_ip`, `delivery_id`, `event_type`) and matching `WebhookTelemetry.Record*()` calls. Rejection paths MUST NOT emit outbound operational notification alerts (Aaronontheweb/netclaw#545).
- [ ] 5.4 Add `WebhookRouteCatalog.GetRouteCounts()` returning total / enabled / disabled / invalid route tallies; gate on the webhook feature flag.
- [ ] 5.5 Add `DaemonStats.Webhooks` to the stats contract, wire it through `DaemonStatsService`, and render a `webhooks:` section in `netclaw stats` text output, TUI, and help text (Aaronontheweb/netclaw#543).
- [ ] 5.6 Add tests covering rejection counter increments per reason, route-count tallies across enabled/disabled/invalid states, and the webhook stats contract surface.
- [ ] 5.7 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` to describe the new stats section and structured rejection log fields; bump `metadata.version`.
