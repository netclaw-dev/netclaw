## Context

Netclaw already has one automation pattern that works well: reminders create a
fresh session, inject reminder-specific instructions, run autonomously, and
optionally notify a Slack destination according to a simple
`DeliveryRequired` flag. Webhooks need the same shape, but with verified
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

- Add a top-level inbound-webhook feature toggle plus per-route webhook files
  under `NetclawPaths/config/webhooks/`.
- Verify inbound requests before any agent work runs.
- Launch one autonomous webhook session per accepted delivery.
- Inject route instructions as additive session context, not as a replacement
  for the global identity prompt.
- Reuse reminder-style delivery requirement semantics (`DeliveryRequired`) and
  prompt-driven human notification behavior.
- Emit deterministic operational receipt alerts for accepted deliveries.
- Fail closed on invalid route-file edits and emit operational alerts for route
  load/unload failures.

**Non-Goals:**

- Dynamic route registration via CLI or hot-reload subscriptions.
- Wizard/`netclaw init` support for webhook configuration in this change.
- Non-Slack notification-target implementations.
- Durable rebinding of an existing webhook session onto a Slack/Discord thread.
- Trust-promotion workflows after human acceptance.

## Decisions

### 1. Top-level config only enables the feature; routes live in per-route files

Top-level `netclaw.json` only controls whether inbound webhooks are enabled.
Individual routes live as one JSON file per route under
`NetclawPaths/config/webhooks/`, using the filename as the stable route name and
HTTP path segment. Each route file provides:

- generic verification settings
- optional event filters
- audience
- prompt overlay
- notify instructions
- delivery required flag
- notification target

This keeps the main config small, lets operators manage routes individually, and
fits the existing file-backed operating model. Route files are secret-bearing
config because verification secrets may be stored inline. Hermes-style dynamic
subscriptions are intentionally deferred.

**Alternatives considered:**

- Keeping route definitions in top-level `netclaw.json`: rejected because route
  collections would bloat the main config and make per-route secret handling and
  hot reload more awkward.
- Dynamic `netclaw webhook subscribe` registration: rejected for MVP because it
  adds persistence, mutation APIs, and lifecycle rules before the basic ingress
  model exists.

### 2. Route files are hot-reloaded on request and fail closed on invalid edits

The daemon reloads route definitions from `config/webhooks` without restart.
Hermes-style mtime-gated reload on request is acceptable for MVP: when a
request arrives for route `foo`, the daemon checks whether `foo.json` changed
since the last successful load and refreshes it before processing.

Fail-closed semantics are required:

- if a route file is missing, malformed, or schema-invalid, that route is not
  loaded
- if an existing valid route becomes invalid on edit, the previous in-memory
  route is removed immediately rather than serving stale config
- load, reload, and unload failures emit operational alerts through the
  existing `IOperationalNotificationSink`

This keeps behavior explicit and avoids hidden stale-state fallbacks.

**Alternatives considered:**

- `FileSystemWatcher` subscription graph: rejected for MVP because request-time
  mtime checks are smaller and sufficient.
- Serving last-known-good route definitions after an invalid edit: rejected
  because stale config would hide operator mistakes and violate fail-closed
  behavior.

### 3. Verified ingress happens before session launch

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

### 4. Verification kinds are generic, minimal, and secret-aware

Verification must be modeled generically rather than as one verifier type per
provider. MVP should start with a minimal set such as:

- HMAC over the raw request body using a configured secret and header contract
- shared-header secret comparison

Provider-specific conventions can be expressed as data within those generic
schemes instead of creating `GitHubVerification`, `StripeVerification`, and so
on. Route files may store the secrets inline, so the config surface must be
treated as secret-bearing and excluded from broad file-tool access.

**Alternatives considered:**

- Provider-specific verifier classes/config schemas for each source: rejected
  because it adds surface area before the generic primitives are proven.

### 5. Route instructions are additive session overlays

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

### 6. Human-facing notifications reuse reminder semantics but create new Slack sessions

Webhook routes reuse the same simple notification delivery requirement reminders
already use:

- `DeliveryRequired=false`: human-facing notification is optional.
- `DeliveryRequired=true`: notification delivery is required when notification
  instructions are present.

When the agent decides to notify Slack, it uses the existing proactive-thread
mechanism. That creates a Slack-native thread/session. The original webhook
session remains autonomous; the Slack thread is the human-facing handoff.

This keeps the implementation small and avoids cross-channel session-binding
state.

**Alternatives considered:**

- Rebinding the original webhook session onto a later Slack thread: rejected for
  MVP because restart-safe binding would require a new durable registry and more
  trust-state rules than the feature currently needs.

### 7. Reminder reuse happens at the execution model level, not full data-model unification

Webhooks should reuse the reminder pattern conceptually:

- one autonomous session per invocation
- route-specific instructions
- simple delivery required flag
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
- **Per-route file reload can add request-path I/O** -> Acceptable for MVP;
  mitigate with mtime-gated reload on request.
- **Secret-bearing route files are operator-editable JSON** -> Treat
  `config/webhooks` like other secret-bearing config and do not grant generic
  file tools unrestricted access.
- **Verification can sprawl** -> Keep the verifier model generic and minimal;
  encode provider differences as data, not new first-class verifier types.

## Migration Plan

1. Add a top-level inbound-webhook feature toggle to `netclaw.json` and add a
   per-route file schema for `config/webhooks/*.json`.
2. Add daemon route-file discovery/reload services and HTTP route registration
   behind the feature flag.
3. Add `ChannelType.Webhook`, route overlay injection, and webhook execution
   path.
4. Reuse existing Slack proactive-thread notifications for human-facing
   escalation.
5. Rollback is configuration-based: disable inbound webhooks in `netclaw.json`
   or remove route files; no daemon restart is required for route-file removal.

## Open Questions

- Should accepted-but-filtered deliveries (e.g., event type not in route allow-
  list) emit a receipt alert, or only fully dispatched deliveries?
