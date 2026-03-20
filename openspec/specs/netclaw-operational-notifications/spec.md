# netclaw-operational-notifications Specification

## Purpose

Define secure configuration and delivery behavior for outbound operational
notifications.

## Requirements

### Requirement: Secure webhook target configuration

The system SHALL load outbound operational notification targets from the
`Notifications.Webhooks` configuration section. Each configured target SHALL
provide an absolute webhook URL. Target URLs SHALL use `https://`, except that
`http://` is permitted only for explicit loopback hosts used for local
development (`localhost`, `127.0.0.1`, or `::1`). Malformed URLs, unsupported
schemes, and URLs with fragments SHALL be rejected as invalid configuration.

#### Scenario: Valid HTTPS target accepted

- **WHEN** the operator configures `Notifications.Webhooks[0].Url` with a valid
  absolute `https://` URL
- **THEN** notification configuration validation succeeds for that target

#### Scenario: Loopback HTTP target accepted for local development

- **WHEN** the operator configures `Notifications.Webhooks[0].Url` as
  `http://localhost:8080/hooks/netclaw`
- **THEN** notification configuration validation accepts the target as a
  loopback-only development exception

#### Scenario: Non-loopback HTTP target rejected

- **WHEN** the operator configures `Notifications.Webhooks[0].Url` as
  `http://alerts.internal.example/hooks/netclaw`
- **THEN** notification configuration validation fails for that target
- **AND** the error explains that non-loopback plaintext HTTP is not allowed

### Requirement: Bounded notification delivery settings

The system SHALL validate notification delivery tuning before enabling outbound
webhook delivery. `DeduplicationWindowSeconds` SHALL be between `0` and `86400`.
`MaxRetries` SHALL be between `0` and `5`. `TimeoutSeconds` SHALL be between `1`
and `60`. When the `Notifications` section is absent or contains no webhook
targets, the runtime SHALL use a no-op notification sink and SHALL NOT register
the webhook delivery service.

#### Scenario: Notifications omitted disables outbound delivery

- **WHEN** the operator runs Netclaw without a `Notifications` section or with an
  empty `Notifications.Webhooks` list
- **THEN** the daemon uses the no-op operational notification sink
- **AND** outbound webhook delivery is not started

#### Scenario: Out-of-range delivery setting rejected

- **WHEN** the operator sets `Notifications.TimeoutSeconds` to `0`
- **THEN** notification configuration validation fails
- **AND** the error identifies the `Notifications.TimeoutSeconds` field and its
  accepted range

### Requirement: Best-effort delivery isolation

For valid notification configuration, outbound webhook delivery SHALL remain
best-effort and isolated from daemon liveness. Delivery attempts SHALL retry only
for retryable failures up to the configured retry count, SHALL retry HTTP `429`
and `5xx` responses, SHALL NOT retry other 4xx responses, and SHALL log delivery
failures without crashing the daemon or blocking alert producers. Duplicate-alert
suppression SHALL only be recorded after at least one successful target delivery.
When the notification queue is saturated, newly emitted alerts SHALL be dropped
with an explicit warning instead of silently evicting older queued alerts.

#### Scenario: Client error not retried

- **WHEN** a webhook target responds with HTTP 400 to a notification delivery
- **THEN** the delivery attempt is logged as failed
- **AND** no retry attempt is made for that target

#### Scenario: Rate limit response is retried

- **WHEN** a webhook target responds with HTTP 429 to a notification delivery
- **THEN** the delivery attempt is retried up to the configured retry count
- **AND** later success records the alert as emitted for deduplication purposes

#### Scenario: Delivery failure does not crash daemon

- **WHEN** all configured webhook targets fail during notification delivery
- **THEN** the daemon continues running
- **AND** later alerts may still be emitted and attempted

#### Scenario: Failed delivery does not suppress a later retryable alert

- **WHEN** an alert delivery fails for all targets
- **AND** the same alert is emitted again within the deduplication window
- **THEN** the later alert is still attempted
- **AND** duplicate suppression does not hide the retryable alert

#### Scenario: Saturated queue drops new alert with warning

- **WHEN** the notification queue is full and a new alert is emitted
- **THEN** the new alert is dropped instead of evicting an older queued alert
- **AND** a warning is logged explaining that queue saturation occurred

### Requirement: Notification header values are redacted from diagnostics

The system SHALL support static per-target webhook headers, but logs and
diagnostic output SHALL NOT include configured header values. Target identity may
be logged using the configured target name plus a redacted URL display that keeps
only origin-level information. Secret-bearing values and full webhook paths MUST
remain redacted.

#### Scenario: Configured headers not echoed in diagnostics

- **WHEN** a webhook target includes an `Authorization` header in configuration
- **THEN** notification delivery logs omit the header value
- **AND** operator-facing diagnostics do not print the configured secret

#### Scenario: Secret-bearing webhook path not echoed in diagnostics

- **WHEN** a webhook target URL contains a secret-bearing path or query string
- **THEN** logs and diagnostics show only origin-level URL identity with a
  redacted path marker
- **AND** the full webhook path does not appear in operator-facing output

### Requirement: Streaming provider failures still emit operational alerts

Provider alerting SHALL cover failures that occur while consuming a streaming
chat response, not just failures that occur while creating the stream object.
Single-provider setups SHALL emit `provider.unreachable` alerts for streaming
enumeration failures, and failover setups SHALL emit `provider.failover` and
`provider.unreachable` alerts when primary or fallback streaming enumeration
fails before a stable response is produced.

#### Scenario: Single-provider stream enumeration failure emits unreachable alert

- **WHEN** the streaming response enumerator throws before the first chunk in a
  single-provider setup
- **THEN** the system emits a `provider.unreachable` operational alert
- **AND** the original exception still propagates to the caller

#### Scenario: Fallback stream enumeration failure emits unreachable alert

- **WHEN** the primary streaming provider fails before the first chunk and the
  fallback stream also throws during enumeration
- **THEN** the system emits a `provider.failover` alert for the primary failure
- **AND** the system emits a `provider.unreachable` alert for the fallback failure
