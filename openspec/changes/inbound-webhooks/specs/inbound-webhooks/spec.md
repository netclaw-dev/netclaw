# inbound-webhooks Specification

## Purpose

Define config-driven inbound webhook routes, verified delivery handling,
autonomous session launch, prompt overlay injection, operational receipt
alerts, and reminder-style human notification behavior.

## Requirements

### Requirement: Named webhook routes

The daemon SHALL expose config-driven named webhook routes. Each route SHALL be
reachable at a stable HTTP path derived from its route name and SHALL define the
audience, prompt overlay, notify instructions, notify policy, verifier settings,
and optional notification target used for accepted deliveries.

#### Scenario: Configured route resolves by name

- **GIVEN** a route named `github-issues` is configured
- **WHEN** an HTTP POST arrives at the webhook ingress path for `github-issues`
- **THEN** the daemon resolves that route definition
- **AND** uses that route's audience, prompt overlay, verifier, and notify
  settings for the delivery

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

### Requirement: Reminder-style notify policy

Webhook routes SHALL reuse reminder-style notification semantics. `Conditional`
routes MAY complete without human-facing notification. `Required` routes SHALL
be treated as failed if no notification is produced for the configured target.

#### Scenario: Conditional route may skip notification

- **GIVEN** a webhook route has `NotifyPolicy = Conditional`
- **WHEN** the agent decides the delivery requires no human-facing update
- **THEN** the webhook execution completes successfully without notification

#### Scenario: Required route fails without notification

- **GIVEN** a webhook route has `NotifyPolicy = Required`
- **WHEN** the webhook execution completes without producing a notification to
  the configured target
- **THEN** the execution is marked failed

### Requirement: Human-facing notification target opens channel-native session

When a webhook execution decides to notify an interactive channel, the
notification target SHALL use the channel adapter's normal session model. For
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

- **GIVEN** a webhook route uses `NotifyPolicy = Conditional`
- **AND** the agent chooses not to notify a human-facing channel
- **WHEN** the delivery is accepted and processed
- **THEN** the operational receipt alert is still emitted
