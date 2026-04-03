## Why

`PRD-001` explicitly calls out webhook input as the next input-expansion step after
Slack, reminders, and CLI. Netclaw already treats reminders as autonomous session
launchers with source-specific instructions, notification policy, and a target
channel; inbound webhooks should reuse that same shape so external events can
launch agent work without introducing a second automation model.

Source PRDs: `PRD-001` (Phase 2 input expansion via webhook).

## What Changes

- Add an inbound-webhook feature flag in top-level `netclaw.json`, while moving
  route definitions into one JSON file per route under
  `NetclawPaths/config/webhooks/`. Each route file owns its verifier/secret
  settings, event filters, audience, prompt overlay, notify instructions,
  notify policy, and notification target.
- Add an HTTP ingress pipeline for inbound webhooks: route matching, request
  verification, body size limits, delivery-id deduplication, per-route rate
  limiting, route-file hot reload, and operational alerts for both receipt and
  route load/unload failures.
- Create a fresh session for each accepted webhook invocation using
  `ChannelType.Webhook`, then inject the route prompt overlay plus a normalized
  event payload into the session.
- Reuse reminder-style notification semantics: `NotifyPolicy` remains
  `Required` or `Conditional`, and the prompt determines whether the agent posts
  nothing, a summary, or opens a live thread in the configured notification
  target.
- Treat webhook execution and human-facing notification as separate sessions for
  MVP. If the agent decides to notify Slack, it opens a Slack-native
  thread/session; the original webhook session remains autonomous and does not
  require durable cross-channel session rebinding.
- Keep webhook registration file-driven for MVP. Dynamic subscription CLI,
  onboarding wizard support, and non-Slack notification-target implementations
  remain out of scope.

## Capabilities

### New Capabilities

- `inbound-webhooks`: Per-route webhook file registration, ingress
  verification, deduplication/rate limiting, route hot reload, fail-closed
  invalidation, operational receipt/load alerts, one-session-per-invocation
  launch, prompt overlay injection, and reminder-style notification
  policy/target handling.

### Modified Capabilities

- `netclaw-gateway-security`: Inbound webhook routes SHALL fail closed when
  route verification is required but missing/invalid, SHALL disappear
  immediately when their route file becomes invalid, and SHALL enforce
  request-size, deduplication, and rate-limit guards before dispatching agent
  work.

## Impact

- **Daemon config**: Top-level `netclaw.json` only enables/disables inbound
  webhooks. Route definitions live as one JSON file per route under
  `config/webhooks` beneath `NetclawPaths`.
- **Daemon runtime**: New authenticated/verified HTTP ingress path for webhook
  delivery, generic verification helpers, delivery-id cache, and mtime-gated
  route-file reload on request.
- **Session pipeline**: New `ChannelType.Webhook` and route-owned prompt overlay
  injection when launching sessions from webhook events.
- **Notification routing**: Shared reminder-style notify semantics reused for
  webhooks, with Slack as the first notification-target implementation. Slack
  notifications create Slack-native threads/sessions instead of rebinding the
  original webhook session.
- **Operational visibility**: Every accepted webhook delivery emits a
  deterministic receipt alert via `IOperationalNotificationSink`; invalid route
  file load/unload events also alert through the same sink so failures are not
  silent. Normal human-facing channel notifications remain prompt-driven and
  optional.
- **Security posture**: Route files may store inline verification secrets and
  therefore must be treated as secret-bearing config; generic file tools should
  not receive unrestricted access to `config/webhooks`.
- **Out of scope for MVP**: init wizard changes, `netclaw webhook subscribe`
  style dynamic registration, Discord implementation, provider-specific
  verifier proliferation, and explicit trust-promotion workflows after human
  acceptance.
