## ADDED Requirements

### Requirement: Notification configuration diagnostics
The `netclaw doctor` command SHALL validate outbound operational notification
configuration before daemon startup. It SHALL use the same target URL and
numeric-range rules as daemon startup validation, and it SHALL provide
field-level remediation guidance when notification config is invalid. The doctor
command SHALL also warn when webhook URLs or auth-like webhook headers are
defined in base config instead of the secrets overlay.

#### Scenario: Invalid webhook URL reported with remediation
- **WHEN** the operator runs `netclaw doctor` with
  `Notifications.Webhooks[0].Url` set to a non-loopback `http://` URL
- **THEN** doctor reports the notification config as invalid
- **AND** the output tells the operator to switch to `https://` or use a
  loopback-only local-development endpoint

#### Scenario: Invalid numeric tuning reported with field path
- **WHEN** the operator runs `netclaw doctor` with
  `Notifications.MaxRetries` set above the supported range
- **THEN** doctor reports a notification config failure
- **AND** the output identifies the `Notifications.MaxRetries` field and the
  supported range

#### Scenario: Auth-like headers in base config produce warning
- **WHEN** the operator runs `netclaw doctor` and `netclaw.json` contains a
  notification header named `Authorization`
- **THEN** doctor emits a warning for notification config hygiene
- **AND** the remediation tells the operator to move the secret to
  `secrets.json` or `NETCLAW_` environment variables

#### Scenario: Webhook URL in base config produces warning
- **WHEN** the operator runs `netclaw doctor` and `netclaw.json` contains
  `Notifications.Webhooks[0].Url`
- **THEN** doctor emits a warning for notification config hygiene
- **AND** the remediation tells the operator to move the webhook URL to
  `secrets.json` or `NETCLAW_` environment variables
