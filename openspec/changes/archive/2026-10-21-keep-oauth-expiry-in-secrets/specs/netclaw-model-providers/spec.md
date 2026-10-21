## ADDED Requirements

### Requirement: OAuth refresh state lives in secrets.json

The system SHALL save the OAuth access token, refresh token, account ID, and
token expiry of a provider to secrets.json in one atomic update. A token refresh
SHALL NOT write netclaw.json. A token refresh SHALL NOT cause a daemon restart.
When a refresh response holds no new refresh token, the system SHALL keep the
previous refresh token. When a refresh response holds no expiry, the system
SHALL store a JSON null expiry, so that the value hides a retired netclaw.json key.

Before the system spends a refresh token, it SHALL read secrets.json under the
provider gate. It SHALL use a newer token that another process saved. It SHALL
NOT present a refresh token that this process already exchanged for another
token. When a save fails after a successful exchange, the system SHALL keep the
new tokens in memory, present the live token at the next refresh, and log an
error that names the file path.

The system SHALL reject an expiry in secrets.json that is not a date. The error
SHALL name the provider and the key.

An `OAuthTokenExpiry` key in netclaw.json is retired. The key SHALL bind until
secrets.json holds a value for the provider. A value in secrets.json, including a
JSON null, SHALL take precedence. `netclaw doctor` SHALL report a retired key.
`netclaw doctor --fix` SHALL copy a parseable date into an existing secrets.json
provider entry that has no expiry, and then remove the key from netclaw.json
after a backup. The daemon SHALL log a startup warning for each retired key that
holds a value.

#### Scenario: A CLI refresh reaches the running daemon

- **GIVEN** the daemon holds an expired access token for a provider
- **AND** the `netclaw` CLI refreshed the token and saved it to secrets.json
- **WHEN** the daemon needs an access token
- **THEN** the daemon uses the stored token without a new refresh request

#### Scenario: A failed save never presents a spent token

- **GIVEN** a refresh worked and the token endpoint rotated the refresh token
- **AND** the save to secrets.json failed
- **WHEN** the same process refreshes again
- **THEN** it presents the rotated refresh token, not the spent one
- **AND** the log holds an error with the path of secrets.json

#### Scenario: A server that does not rotate the refresh token

- **GIVEN** a token endpoint that returns no new refresh token
- **WHEN** a refresh worked
- **THEN** secrets.json keeps the live refresh token
- **AND** a restarted process presents the live token, not a spent one

#### Scenario: An older secrets.json copy does not bring back a spent token

- **GIVEN** an older copy of secrets.json replaced the current file
- **AND** it holds a refresh token that this process already spent
- **WHEN** the process refreshes
- **THEN** the process presents its in-memory token

#### Scenario: A refresh never writes netclaw.json

- **GIVEN** a netclaw.json with comments and a retired expiry key
- **WHEN** the system saves refreshed tokens
- **THEN** netclaw.json is byte-identical to before the save

#### Scenario: A retired key binds until secrets.json has a value

- **GIVEN** netclaw.json holds `Providers.copilot.OAuthTokenExpiry` and secrets.json holds none
- **WHEN** the daemon binds the provider
- **THEN** the provider expiry is the netclaw.json value
- **AND WHEN** secrets.json later holds a different expiry, or a JSON null
- **THEN** the secrets.json value is the provider expiry

#### Scenario: Doctor fix moves the retired key

- **GIVEN** a retired expiry key in netclaw.json and a secrets.json provider entry without an expiry
- **WHEN** the operator runs `netclaw doctor --fix`
- **THEN** secrets.json holds the date unencrypted
- **AND** netclaw.json has no retired key and a backup exists

#### Scenario: Doctor fix keeps a value that is not a date

- **GIVEN** a retired key in netclaw.json with a value that is not a date
- **WHEN** the operator runs `netclaw doctor --fix`
- **THEN** netclaw.json keeps the key and secrets.json is unchanged

#### Scenario: Doctor fix does not create a provider entry

- **GIVEN** a retired key for a provider that has no secrets.json entry
- **WHEN** the operator runs `netclaw doctor --fix`
- **THEN** secrets.json does not gain an entry for that provider

#### Scenario: A netclaw.json with comments is not rewritten

- **GIVEN** a netclaw.json that holds comments and a retired key
- **WHEN** the operator runs `netclaw doctor --fix`
- **THEN** netclaw.json is unchanged
- **AND** the doctor report and the startup warning still name the key

#### Scenario: An expiry that is not a date fails loudly

- **GIVEN** secrets.json holds `Providers.copilot.OAuthTokenExpiry` with the text `soon`
- **WHEN** the system loads the stored tokens
- **THEN** the load fails with an error that names the provider and the key

#### Scenario: An encrypted expiry in secrets.json stops the daemon

- **GIVEN** secrets.json holds an encrypted `Providers.copilot.OAuthTokenExpiry`
- **WHEN** the daemon binds the provider configuration
- **THEN** startup fails with an error that names `Providers:copilot`
- **AND** a new sign-in with `netclaw provider` saves the date unencrypted
