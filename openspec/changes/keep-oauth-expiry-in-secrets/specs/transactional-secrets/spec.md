## ADDED Requirements

### Requirement: One provider leaf stays readable

The system SHALL encrypt every string leaf in secrets.json at rest, with one
exception. The leaf at the JSON path `Providers.<name>.OAuthTokenExpiry`, which
has exactly three segments, SHALL stay unencrypted. Segment names SHALL match
without regard to case. A string leaf at any other path SHALL be encrypted,
including a leaf with the same key name at another depth or in another section.
A JSON null SHALL never be encrypted. A value that already carries the
encryption prefix SHALL NOT be encrypted again.

The system SHALL read an encrypted value at the exempt path back as plaintext.
The next write SHALL store it unencrypted. The encryption status count SHALL NOT
count the exempt leaf as an unencrypted secret.

#### Scenario: A provider token expiry stays plaintext

- **GIVEN** a secrets document with `Providers.copilot.OAuthTokenExpiry` set to a date
- **WHEN** the system writes the document
- **THEN** the stored date is plaintext
- **AND** `Providers.copilot.OAuthRefreshToken` and `Providers.copilot.OAuthAccessToken` are encrypted

#### Scenario: The exempt path matches without case

- **GIVEN** a secrets document with `providers.Copilot.oauthtokenexpiry` set to a date
- **WHEN** the system writes the document
- **THEN** the stored date is plaintext

#### Scenario: A JSON null expiry is kept as null

- **GIVEN** a secrets document with `Providers.copilot.OAuthTokenExpiry` set to null
- **WHEN** the system writes the document
- **THEN** the value is still null

#### Scenario: A leaf with the same key name at another path is encrypted

- **GIVEN** string leaves at `McpOAuthTokens.srv.ExpiresAt`, top-level `OAuthTokenExpiry`, `Providers.OAuthTokenExpiry`, `Providers.copilot.VendorOptions.OAuthTokenExpiry`, and `Channels.copilot.OAuthTokenExpiry`
- **WHEN** the system writes the document
- **THEN** each of those leaves is encrypted

#### Scenario: An array item is encrypted

- **GIVEN** an array item under `Providers.copilot.Tags` with the text `OAuthTokenExpiry`
- **WHEN** the system writes the document
- **THEN** the item is encrypted

#### Scenario: An old encrypted expiry heals on the next write

- **GIVEN** a secrets file that holds an encrypted `Providers.copilot.OAuthTokenExpiry`
- **WHEN** a transactional update reads the file and writes it back
- **THEN** the update sees the plaintext date
- **AND** the saved file holds the date unencrypted

#### Scenario: A direct write leaves an encrypted expiry as it is

- **GIVEN** a document that holds an already encrypted `Providers.copilot.OAuthTokenExpiry`
- **WHEN** the system writes the document without a transactional update
- **THEN** the value is neither decrypted nor encrypted again
