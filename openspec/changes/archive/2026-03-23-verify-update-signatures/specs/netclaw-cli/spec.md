## ADDED Requirements

### Requirement: Signed manifest verification during update

The `netclaw update` command SHALL verify the minisign signature of
`manifest.json` before trusting its contents. The command SHALL download
`manifest.json.sig` alongside the manifest and verify the Ed25519 signature
against the embedded public key. The command SHALL reject the manifest and abort
the update if signature verification fails.

#### Scenario: Successful update with valid signature

- **WHEN** operator runs `netclaw update`
- **AND** the manifest signature verifies against the embedded public key
- **THEN** the update proceeds normally using the verified manifest checksums

#### Scenario: Update aborted on invalid signature

- **WHEN** operator runs `netclaw update`
- **AND** the manifest signature does not verify
- **THEN** the command exits with a non-zero code
- **AND** an error message warns of possible manifest tampering

#### Scenario: Update aborted when signature file missing

- **WHEN** operator runs `netclaw update`
- **AND** `manifest.json.sig` cannot be downloaded
- **THEN** the command exits with a non-zero code
- **AND** an error message explains the signature file is missing

### Requirement: Periodic daemon update check

The daemon SHALL periodically recheck for available updates while running.
The default recheck interval SHALL be 24 hours. The recheck SHALL use the same
`UpdateCheckService` and signature verification as the CLI update command.

#### Scenario: Daemon detects update after startup

- **GIVEN** the daemon started with no update available
- **WHEN** a new release is published and 24 hours elapse
- **THEN** the daemon detects the available update on the next periodic check

#### Scenario: Recheck interval respects cache

- **GIVEN** the update check cache duration is 1 hour
- **WHEN** the periodic timer fires at the 24-hour interval
- **THEN** a fresh manifest fetch is performed (cache has long expired)

### Requirement: Update availability operational alert

The daemon SHALL emit an `UpdateAvailable` operational alert via
`IOperationalNotificationSink` when an update is detected. The alert SHALL
be emitted at most once per detected version (deduplicated by the existing
webhook deduplication mechanism).

#### Scenario: Alert emitted on update detection

- **GIVEN** the daemon detects an available update
- **WHEN** the update check result indicates `IsUpdateAvailable`
- **THEN** an `UpdateAvailable` operational alert is emitted with severity
  "info"
- **AND** the alert summary includes the current and available versions

#### Scenario: Alert delivered to configured webhooks

- **GIVEN** a Slack webhook is configured in notifications config
- **WHEN** an `UpdateAvailable` alert is emitted
- **THEN** the webhook receives a notification formatted per the webhook format
  (Generic JSON or Slack Block Kit)

#### Scenario: Alert not duplicated within dedup window

- **GIVEN** an `UpdateAvailable` alert was recently emitted for the same version
- **WHEN** the periodic recheck runs again within the deduplication window
- **THEN** no duplicate alert is emitted
