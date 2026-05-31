# inbound-webhooks Specification

## Purpose

Define config-driven inbound webhook routes, verified delivery handling,
autonomous session launch, prompt overlay injection, operational receipt
alerts, and reminder-style human notification behavior.

## Requirements

### Requirement: Named webhook routes

The daemon SHALL expose named inbound webhook routes from one JSON file per
route under `NetclawPaths/config/webhooks/`. Top-level `netclaw.json` SHALL only
control whether the inbound webhook feature is enabled. Each route SHALL be
reachable at a stable HTTP path derived from its route filename/name and SHALL
define the audience, prompt overlay, notify instructions, `DeliveryRequired`,
verification settings, and optional notification target used for accepted
deliveries.

#### Scenario: Configured route resolves by name

- **GIVEN** a route file named `github-issues.json` exists under
  `config/webhooks`
- **WHEN** an HTTP POST arrives at the webhook ingress path for `github-issues`
- **THEN** the daemon resolves that route definition
- **AND** uses that route's audience, prompt overlay, verifier, and notify
  settings for the delivery

#### Scenario: Top-level config only enables the feature

- **GIVEN** inbound webhooks are enabled in top-level `netclaw.json`
- **AND** route definitions exist only in `config/webhooks/*.json`
- **WHEN** the daemon starts or handles an inbound webhook request
- **THEN** route configuration is discovered from `config/webhooks`
- **AND** route definitions are not required to be embedded in `netclaw.json`

#### Scenario: Unknown route is rejected

- **WHEN** an HTTP POST arrives for an unconfigured webhook route name
- **THEN** the daemon returns `404 Not Found`
- **AND** no session is created

### Requirement: Accepted delivery launches autonomous webhook session

Each accepted webhook delivery SHALL create a fresh autonomous session with
`ChannelType.Webhook`. Session identity SHALL be unique per accepted delivery so
retries or later notifications do not reuse an unrelated invocation session.

#### Scenario: Accepted delivery creates unique session

- **GIVEN** a verified webhook delivery for route `github-issues`
- **WHEN** the daemon accepts the delivery
- **THEN** a new webhook session is created for that delivery
- **AND** the session uses the route's configured audience as its source
  audience

#### Scenario: Separate deliveries create separate sessions

- **GIVEN** two distinct accepted deliveries for the same route
- **WHEN** the daemon launches work for both
- **THEN** two separate webhook sessions are created

### Requirement: Route prompt overlay is additive context

The route prompt SHALL be injected as additive session context. It SHALL NOT
replace the base system prompt assembled from identity files. The normalized
delivery payload SHALL be provided to the session as the first delivery-specific
input.

#### Scenario: Route prompt supplements base system prompt

- **GIVEN** the daemon has a normal identity prompt configured
- **AND** route `github-issues` has a webhook prompt overlay
- **WHEN** an accepted delivery launches a webhook session
- **THEN** the session sees both the base system prompt and the route overlay
- **AND** the route overlay does not replace the base prompt

#### Scenario: Normalized payload becomes delivery input

- **GIVEN** an accepted webhook delivery with JSON payload content
- **WHEN** the webhook session starts
- **THEN** the normalized payload is provided as delivery-specific input to the
  session

### Requirement: Reminder-style delivery requirement

Webhook routes SHALL reuse reminder-style notification semantics.
`DeliveryRequired=false` means human-facing notification is optional.
`DeliveryRequired=true` means notification delivery is required when
notification instructions are present. If delivery is required and no
notification is produced for the configured target, execution SHALL be treated
as failed.

#### Scenario: DeliveryRequired=false may skip notification

- **GIVEN** a webhook route has `DeliveryRequired = false`
- **WHEN** the agent decides the delivery requires no human-facing update
- **THEN** the webhook execution completes successfully without notification

#### Scenario: DeliveryRequired=true fails without notification

- **GIVEN** a webhook route has `DeliveryRequired = true`
- **AND** notification instructions are present
- **WHEN** the webhook execution completes without producing a notification to
  the configured target
- **THEN** the execution is marked failed

### Requirement: Human-facing notification target opens channel-native session

When a webhook execution decides to notify an interactive channel, the notification target SHALL use the channel adapter's normal session model. For
Slack, this means opening a Slack-native thread/session rather than rebinding
the original webhook session onto the Slack thread.

#### Scenario: Slack notification opens Slack-native thread

- **GIVEN** a webhook route has a Slack notification target
- **WHEN** the webhook execution produces a Slack notification
- **THEN** the system posts to Slack using the proactive-thread path
- **AND** the resulting interactive thread uses a Slack-native session
- **AND** the original webhook session remains separate

### Requirement: Operational receipt alert per accepted delivery

Every accepted webhook delivery SHALL emit a deterministic operational receipt
alert independently of any human-facing notification policy. The alert SHALL
identify the route, delivery, and event that fired.

#### Scenario: Accepted delivery emits receipt alert

- **GIVEN** a verified webhook delivery is accepted for dispatch
- **WHEN** the daemon finishes ingress validation
- **THEN** an operational receipt alert is emitted
- **AND** the alert includes the route name and delivery identifier

#### Scenario: Human notification skipped still emits receipt alert

- **GIVEN** a webhook route uses `DeliveryRequired = false`
- **AND** the agent chooses not to notify a human-facing channel
- **WHEN** the delivery is accepted and processed
- **THEN** the operational receipt alert is still emitted

### Requirement: Route files are hot-reloaded and fail closed

The daemon SHALL reload route definitions from `config/webhooks` without daemon
restart. Request-time mtime-gated reload is acceptable for MVP. If a route file
is missing, malformed, or invalid, that route SHALL not be loaded, and a
previously loaded version SHALL be removed immediately instead of serving stale
config.

#### Scenario: Route file edit is picked up without restart

- **GIVEN** route `github-issues` is loaded from `github-issues.json`
- **AND** the route file changes on disk
- **WHEN** the next request arrives for `github-issues`
- **THEN** the daemon reloads the route definition before processing the request

#### Scenario: Invalid edit removes previously loaded route

- **GIVEN** route `github-issues` was previously valid and loaded
- **WHEN** `github-issues.json` is edited into an invalid state
- **THEN** the daemon stops serving route `github-issues`
- **AND** no stale prior route definition is used for later requests

### Requirement: Verification kinds are generic and minimal

Route verification SHALL be modeled as generic verification kinds rather than
one first-class verifier type per provider. MVP SHALL support a minimal set that
includes generic HMAC verification and shared-header secret verification.

#### Scenario: Generic HMAC verification is configured

- **GIVEN** a route file configures HMAC verification with header metadata and a
  shared secret
- **WHEN** a request arrives with a valid matching signature
- **THEN** the route verification succeeds without requiring a provider-specific
  verifier type

#### Scenario: Shared-header secret verification is configured

- **GIVEN** a route file configures shared-header secret verification
- **WHEN** a request arrives with the expected secret header value
- **THEN** the route verification succeeds without requiring a provider-specific
  verifier type

### Requirement: Route files are secret-bearing config

Route files MAY store inline verification secrets. The system SHALL treat
`config/webhooks` as secret-bearing configuration and SHALL NOT assume generic
file tools have unrestricted access to that directory.

#### Scenario: Route file contains inline verification secret

- **GIVEN** a route file stores an inline verification secret for HMAC or shared
  header validation
- **WHEN** tool access policy is evaluated for generic file tools
- **THEN** `config/webhooks` is treated as secret-bearing config
- **AND** unrestricted generic file access is not implied

### Requirement: Route file load failures emit operational alerts

Route file load, reload, or unload failures SHALL emit operational alerts via
the existing operational notification sink so route failures are never silent.

#### Scenario: Invalid route reload emits operational alert

- **GIVEN** a previously valid route file becomes invalid on edit
- **WHEN** the daemon attempts to reload that route
- **THEN** an operational alert is emitted identifying the route and reload
  failure
- **AND** the route remains unavailable until the file is valid again

### Requirement: Webhook rejections emit structured logs and counters

Every webhook ingress outcome — accepted, rejected, filtered, or rate-limited — SHALL increment a durable in-process counter and emit a structured daemon log
line with at minimum the route name, outcome reason, client remote IP, and
delivery identifier (when available). Rejection paths SHALL NOT emit outbound
operational notification alerts, to avoid spamming operator channels on
adversarial or misconfigured traffic.

Counters SHALL cover: `accepted`, `route_not_found`, `verification_failed`,
`body_too_large`, `invalid_json`, `rate_limited`, `event_filtered`, and
`duplicate_delivery`.

#### Scenario: Route-not-found emits log and counter

- **GIVEN** no route file exists for `unknown-route`
- **WHEN** an HTTP POST arrives at the webhook ingress path for `unknown-route`
- **THEN** the daemon returns `404 Not Found`
- **AND** the `route_not_found` counter is incremented
- **AND** a structured warning log line is emitted with `route=unknown-route`,
  `reason=route_not_found`, and the client remote IP
- **AND** no outbound operational notification alert is emitted for the rejection

#### Scenario: Verification failure emits log and counter

- **GIVEN** a route with HMAC verification is configured
- **WHEN** a request arrives with an invalid signature
- **THEN** the daemon returns `401 Unauthorized`
- **AND** the `verification_failed` counter is incremented
- **AND** a structured warning log line is emitted including the route name,
  the delivery identifier (when the provider supplied one), and
  `reason=verification_failed`

#### Scenario: Duplicate delivery increments counter

- **GIVEN** a webhook delivery has already been processed within the
  deduplication window
- **WHEN** the same delivery identifier arrives again for the same route
- **THEN** the daemon returns `202 Accepted` with `reason=duplicate_delivery`
- **AND** the `duplicate_delivery` counter is incremented

### Requirement: Stats surface exposes webhook route counts and delivery counters

The `netclaw stats` CLI surface and `/api/stats` daemon endpoint SHALL include
webhook metrics covering both route registry counts and delivery counters, so
operators can see at a glance how many routes are configured and how ingress
traffic is being handled.

Route counts SHALL cover `total`, `enabled`, `disabled`, and `invalid` routes.
Delivery counters SHALL cover the same set defined by the rejection
observability requirement above.

#### Scenario: Stats response includes webhook section

- **GIVEN** webhook routes are configured under `config/webhooks`
- **WHEN** an operator requests `netclaw stats`
- **THEN** the response includes a dedicated webhooks section
- **AND** the section reports route counts (total, enabled, disabled, invalid)
- **AND** the section reports delivery counters (accepted, filtered, duplicate,
  and each rejection reason)

#### Scenario: Invalid route files counted as invalid

- **GIVEN** three route files exist under `config/webhooks` and one fails to
  parse or validate
- **WHEN** an operator requests `netclaw stats`
- **THEN** the invalid route counter reflects the single unparseable file
- **AND** the enabled counter reflects only the successfully loaded routes

### Requirement: Notification tool invocation surfaces as session output

The session actor SHALL emit a tool-result session output for every tool
invocation whose results are fed back into the conversation. Subscribers that
track notification-tool completion (such as webhook and reminder execution
actors) SHALL rely on those session outputs to determine whether the agent
fulfilled a required notification, rather than waiting on information that is
never emitted in production.

#### Scenario: Required notification succeeds when agent invokes notification tool

- **GIVEN** a webhook route configures `DeliveryRequired = true` with a Slack
  notification target
- **AND** notification instructions are present
- **WHEN** the agent successfully invokes the Slack notification tool during
  the webhook session
- **THEN** the webhook execution completes successfully
- **AND** the daemon does not log a false-positive warning that no notification
  tool was invoked

#### Scenario: Required notification still fails when no notification tool invoked

- **GIVEN** a webhook route configures `DeliveryRequired = true`
- **AND** notification instructions are present
- **WHEN** the agent completes its turn without invoking any notification tool
- **THEN** the webhook execution is marked failed with the "no notification
  tool was invoked" reason
