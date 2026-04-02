## Context

Netclaw already has one automation pattern that works well: reminders create a
fresh session, inject reminder-specific instructions, run autonomously, and
optionally notify a Slack destination according to a simple `NotifyPolicy`
(`Required` or `Conditional`). Webhooks need the same shape, but with verified
HTTP ingress, route-owned trust metadata, and deterministic operational receipt
alerts.

The existing Slack proactive-thread path is useful for human-facing escalation,
but it assumes the Slack thread itself is the session key. Rebinding an already-
running webhook session to an arbitrary Slack thread would require a durable
channel-thread-to-session registry and restart recovery behavior that Netclaw
does not currently have. That is unnecessary complexity for MVP.

The design therefore keeps webhook execution and human-facing interaction as
separate sessions: the webhook runs autonomously in `ChannelType.Webhook`; if it
decides to notify Slack, it opens a normal Slack-native thread/session using the
existing proactive-thread path.

## Goals / Non-Goals

**Goals:**

- Add config-driven named inbound webhook routes to the daemon.
- Verify inbound requests before any agent work runs.
- Launch one autonomous webhook session per accepted delivery.
- Inject route instructions as additive session context, not as a replacement
  for the global identity prompt.
- Reuse reminder-style notify semantics (`Required` / `Conditional`) and prompt-
  driven human notification behavior.
- Emit deterministic operational receipt alerts for accepted deliveries.

**Non-Goals:**

- Dynamic route registration via CLI or hot-reload subscriptions.
- Wizard/`netclaw init` support for webhook configuration in this change.
- Non-Slack notification-target implementations.
- Durable rebinding of an existing webhook session onto a Slack/Discord thread.
- Trust-promotion workflows after human acceptance.

## Decisions

### 1. Webhook routes are static config entries for MVP

Webhook registration lives in daemon config under a named route collection.
Each route provides:

- verifier settings (provider or shared-secret mode)
- optional event filters
- audience
- prompt overlay
- notify instructions
- notify policy
- notification target

This keeps registration predictable, schema-validatable, and easy to operate in
self-hosted deployments. Hermes-style dynamic subscriptions are intentionally
deferred.

**Alternatives considered:**

- Dynamic `netclaw webhook subscribe` registration: rejected for MVP because it
  adds persistence, mutation APIs, and lifecycle rules before the basic ingress
  model exists.

### 2. Verified ingress happens before session launch

Inbound requests terminate at a daemon HTTP endpoint such as
`/api/webhooks/{route}`. The ingress pipeline performs, in order:

1. route lookup
2. body size enforcement
3. request verification / secret validation
4. delivery-id deduplication
5. per-route rate limiting
6. operational receipt alert emission
7. session launch

Rejected deliveries never reach actor/session code. Accepted deliveries are
normalized into a `WebhookInvocation` object with route metadata, event type,
delivery ID, and parsed JSON payload.

**Alternatives considered:**

- Verifying inside the actor/session layer: rejected because security checks and
  duplicate suppression must happen before any agent work or tool exposure.

### 3. Route instructions are additive session overlays

Webhook routes provide a prompt overlay that is injected alongside existing
system/context layers. The base system prompt still comes from the identity
files and is reassembled dynamically. The webhook overlay contributes source-
specific behavior such as triage guidance, notification expectations, and
audience-specific caution without replacing the global identity.

For webhook turns, the first user-visible payload is the normalized event body,
not the route instructions themselves.

**Alternatives considered:**

- Packing route instructions into the first user message, reminder-style:
  rejected because the desired behavior is an additive context layer, not a
  replacement for the normal system prompt stack.

### 4. Human-facing notifications reuse reminder semantics but create new Slack sessions

Webhook routes reuse the same simple notification policy reminders already use:

- `Conditional`: the agent may skip human-facing notification if nothing needs
  reporting.
- `Required`: the agent must produce some notification to the configured target.

When the agent decides to notify Slack, it uses the existing proactive-thread
mechanism. That creates a Slack-native thread/session. The original webhook
session remains autonomous; the Slack thread is the human-facing handoff.

This keeps the implementation small and avoids cross-channel session-binding
state.

**Alternatives considered:**

- Rebinding the original webhook session onto a later Slack thread: rejected for
  MVP because restart-safe binding would require a new durable registry and more
  trust-state rules than the feature currently needs.

### 5. Reminder reuse happens at the execution model level, not full data-model unification

Webhooks should reuse the reminder pattern conceptually:

- one autonomous session per invocation
- route-specific instructions
- simple notify policy
- optional human-facing channel notification

But reminders remain unchanged as persisted schedule definitions in this change.
The shared reuse point is the automation model and prompt/notify semantics,
not a full reminder-schema refactor.

**Alternatives considered:**

- Fully generalizing reminder and webhook definitions into a single persisted
  automation schema first: rejected as unnecessary scope expansion.

## Risks / Trade-offs

- **Webhook session and Slack handoff are separate sessions** -> Human replies do
  not resume the original webhook transcript. Mitigation: require the webhook
  prompt to produce a concise summary/handoff message when opening Slack.
- **Slack is the only notification-target implementation initially** -> The data
  model should remain generic so Discord can plug in later without rewriting the
  route schema.
- **Static config requires restart to add routes** -> Acceptable for MVP;
  revisit with dynamic subscriptions later if operational friction is high.
- **Provider-specific verification can sprawl** -> Start with a minimal verifier
  abstraction and implement only the providers actually needed for MVP.

## Migration Plan

1. Add the new `Webhooks` config section and schema entries.
2. Add daemon ingress services and HTTP route registration behind the new config.
3. Add `ChannelType.Webhook`, route overlay injection, and webhook execution
   path.
4. Reuse existing Slack proactive-thread notifications for human-facing
   escalation.
5. Rollback is configuration-based: disable the `Webhooks` section and restart
   the daemon.

## Open Questions

- Which verifier set is required for MVP beyond GitHub HMAC and a generic
  shared-secret/header mode?
- Should accepted-but-filtered deliveries (e.g., event type not in route allow-
  list) emit a receipt alert, or only fully dispatched deliveries?
