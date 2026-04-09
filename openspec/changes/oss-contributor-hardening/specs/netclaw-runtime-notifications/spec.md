# netclaw-runtime-notifications Delta Spec

## ADDED Requirements

### Requirement: Generic runtime notification targets
The system SHALL model reminder notifications, webhook-driven notifications, and operational alerts through generic runtime notification targets rather than through Slack-only target kinds or Slack-only tool names.

#### Scenario: Reminder failure notification uses generic target contract
- **WHEN** a reminder execution reaches a notification-worthy failure state
- **THEN** the runtime emits a generic notification request referencing a typed target and channel kind
- **AND** the reminder flow does not hardcode a Slack-only target kind or Slack-only delivery tool name

#### Scenario: Operational alert uses generic target contract
- **WHEN** the daemon emits an operational alert
- **THEN** the alert is expressed through the generic runtime notification contract
- **AND** delivery selection is resolved by channel kind rather than by Slack-specific branching in the alert producer

### Requirement: Notification delivery resolves through channel modules
Runtime notification delivery SHALL resolve through the compiled-in channel module seam. Notification producers SHALL NOT bypass the channel registry with direct Slack-specific delivery logic.

#### Scenario: Slack remains first notification delivery implementation
- **WHEN** a generic runtime notification targets a Slack-backed channel during early hardening phases
- **THEN** delivery resolves through the compiled-in channel module registry
- **AND** the resulting Slack delivery preserves current runtime behavior for successful delivery

#### Scenario: Unknown notification channel kind fails closed
- **WHEN** a notification target references an unknown or unregistered channel kind
- **THEN** validation fails explicitly or the runtime rejects delivery with a loud error
- **AND** the system does not silently reroute the notification to Slack or any other channel

### Requirement: Typed notification seam values
Notification targets, channel kinds, and runtime routing identifiers SHALL use shared seam value objects at generic notification boundaries.

#### Scenario: Notification routing uses typed seam values
- **WHEN** a runtime notification is created and routed for delivery
- **THEN** the producer and router exchange typed notification target and channel identifiers
- **AND** raw free-form strings are limited to explicit parsing and serialization boundaries
