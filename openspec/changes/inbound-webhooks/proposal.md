## Why

`PRD-001` explicitly calls out webhook input as the next input-expansion step after
Slack, reminders, and CLI. Netclaw already treats reminders as autonomous session
launchers with source-specific instructions, notification policy, and a target
channel; inbound webhooks should reuse that same shape so external events can
launch agent work without introducing a second automation model.

Source PRDs: `PRD-001` (Phase 2 input expansion via webhook).

## What Changes

- Add config-driven inbound webhook routes to the daemon, each with its own
  path, verifier/secret settings, event filters, audience, prompt overlay,
  notify instructions, notify policy, and notification target.
- Add an HTTP ingress pipeline for inbound webhooks: route matching, request
  verification, body size limits, delivery-id deduplication, per-route rate
  limiting, and operational receipt alerts.
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
- Keep webhook registration config-driven for MVP. Dynamic subscription CLI,
  onboarding wizard support, and non-Slack notification-target implementations
  remain out of scope.

## Capabilities

### New Capabilities

- `inbound-webhooks`: Named webhook route registration, ingress verification,
  deduplication/rate limiting, operational receipt alerts, one-session-per-
  invocation launch, prompt overlay injection, and reminder-style notification
  policy/target handling.

### Modified Capabilities

- `netclaw-gateway-security`: Inbound webhook routes SHALL fail closed when
  route verification is required but missing/invalid, and SHALL enforce
  request-size, deduplication, and rate-limit guards before dispatching agent
  work.

## Impact

- **Daemon config**: New `Webhooks` section with named routes, provider/verifier
  settings, prompt/notify fields, and notification targets.
- **Daemon runtime**: New authenticated/verified HTTP ingress path for webhook
  delivery, plus provider-specific verification helpers and delivery-id cache.
- **Session pipeline**: New `ChannelType.Webhook` and route-owned prompt overlay
  injection when launching sessions from webhook events.
- **Notification routing**: Shared reminder-style notify semantics reused for
  webhooks, with Slack as the first notification-target implementation. Slack
  notifications create Slack-native threads/sessions instead of rebinding the
  original webhook session.
- **Operational visibility**: Every accepted webhook delivery emits a
  deterministic receipt alert via `IOperationalNotificationSink`; normal human-
  facing channel notifications remain prompt-driven and optional.
- **Out of scope for MVP**: init wizard changes, `netclaw webhook subscribe`
  style dynamic registration, Discord implementation, and explicit trust-
  promotion workflows after human acceptance.
