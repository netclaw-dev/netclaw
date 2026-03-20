## ADDED Requirements

### Requirement: Operators can list configured notification webhook targets
The system SHALL provide an offline CLI command to list configured outbound
notification webhook targets from merged config and secrets state. The output
SHALL include a stable zero-based index for each target, target identity
(`Name` when present, otherwise redacted URL identity), a redacted URL display,
and whether static headers are configured. The output MUST NOT include full
webhook paths, query strings, or header values.

#### Scenario: List configured targets with redacted header details
- **WHEN** the operator runs `netclaw notification webhook list` and two webhook targets are configured
- **THEN** the CLI prints one row per target with index, identity, and URL
- **AND** the CLI indicates whether headers are present without printing their values

#### Scenario: List output redacts secret-bearing webhook path
- **WHEN** the operator runs `netclaw notification webhook list` for a target
  whose webhook path contains a secret token
- **THEN** the CLI shows only origin-level URL identity with a redacted path
  marker
- **AND** the full webhook path does not appear in output

#### Scenario: List when notifications are not configured
- **WHEN** the operator runs `netclaw notification webhook list` and `Notifications.Webhooks` is absent or empty
- **THEN** the CLI reports that no notification webhook targets are configured
- **AND** the command exits successfully

### Requirement: Operators can add webhook targets with secret-safe persistence
The system SHALL provide an offline CLI command to add an outbound notification
webhook target. The command SHALL validate the resulting notification
configuration with the shared notification validator before persisting any
changes. Non-secret target properties SHALL be written to `netclaw.json`, while
webhook URLs and static header values SHALL be written to `secrets.json`.
Invalid targets SHALL NOT be persisted.

#### Scenario: Add valid HTTPS target with auth header
- **WHEN** the operator runs `netclaw notification webhook add` with a valid `https://` URL and an `Authorization` header
- **THEN** non-secret fields are written to `netclaw.json`
- **AND** the target URL is written only to `secrets.json`
- **AND** the `Authorization` header value is written only to `secrets.json`
- **AND** the command exits successfully without echoing the header value

#### Scenario: Legacy base-config secrets are normalized during CLI use
- **WHEN** the operator runs a notification webhook CLI command against a config
  where `Notifications.Webhooks[0].Url` or `Headers` still live in `netclaw.json`
- **THEN** the CLI migrates those secret-bearing values into `secrets.json`
- **AND** later CLI output uses the normalized merged state

#### Scenario: Reject invalid target before writing files
- **WHEN** the operator runs `netclaw notification webhook add` with a non-loopback `http://` URL
- **THEN** the CLI reports the shared validation failure with field-level remediation
- **AND** neither `netclaw.json` nor `secrets.json` is modified with the invalid target

### Requirement: Operators can remove webhook targets by stable selector
The system SHALL provide an offline CLI command to remove a notification webhook
target by zero-based index or by unique target name. Removing a target SHALL
delete the matching base-config entry and its associated secret overlay entry.
If a name matches multiple targets, the command SHALL fail and require an index.

#### Scenario: Remove target by index
- **WHEN** the operator runs `netclaw notification webhook remove --index 1`
- **THEN** the second target is removed from `Notifications.Webhooks` in base config
- **AND** the matching secrets overlay entry is removed from `secrets.json`

#### Scenario: Ambiguous name requires explicit index
- **WHEN** the operator runs `netclaw notification webhook remove --name ops-primary` and more than one target uses that name
- **THEN** the CLI reports that the selector is ambiguous
- **AND** the remediation tells the operator to rerun the command with `--index`

### Requirement: Operators can send an explicit webhook probe
The system SHALL provide an offline CLI command to send a single explicit probe
request to a selected notification webhook target. The command SHALL validate the
selected target before sending the probe, SHALL honor the configured timeout, and
SHALL NOT apply background-service retries or deduplication. Probe output MUST
redact header values and full webhook paths.

#### Scenario: Probe succeeds with HTTP 2xx response
- **WHEN** the operator runs `netclaw notification webhook test --index 0` and the target returns HTTP 204
- **THEN** the CLI reports the selected target identity and success status
- **AND** the command exits successfully

#### Scenario: Probe fails with timeout
- **WHEN** the operator runs `netclaw notification webhook test --index 0` and the request exceeds the configured timeout
- **THEN** the CLI reports a timeout failure for that target
- **AND** the command exits non-zero without retrying the request
