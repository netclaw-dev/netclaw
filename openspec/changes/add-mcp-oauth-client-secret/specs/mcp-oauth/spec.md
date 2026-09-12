## ADDED Requirements

### Requirement: Configured confidential-client credentials are secure

The system SHALL support an optional OAuth client secret for a configured MCP client ID.
The CLI SHALL reject a client secret without a client ID before it writes either configuration file.
The CLI SHALL store the client secret only as encrypted durable configuration.
The CLI and MCP diagnostics SHALL NOT print the secret.
The MCP server profile SHALL own the configured client secret.
OAuth token records SHALL NOT become a second authority for that configured secret.

#### Scenario: Operator adds a confidential client

- **GIVEN** an operator supplies a client ID and client secret for an HTTP MCP server
- **WHEN** the CLI adds the server
- **THEN** the public configuration contains the client ID and no client secret
- **AND** the encrypted secrets file contains the client secret under that server profile
- **AND** CLI output does not contain the client secret

#### Scenario: Client secret has no client ID

- **GIVEN** neither configuration file contains the new MCP server profile
- **WHEN** an operator supplies a client secret without a client ID
- **THEN** the CLI rejects the command
- **AND** neither configuration file gains that server profile

#### Scenario: Public client remains valid

- **GIVEN** an operator supplies a client ID without a client secret
- **WHEN** the CLI adds the HTTP MCP server
- **THEN** the CLI stores the public-client configuration
- **AND** OAuth can continue without confidential-client authentication

### Requirement: Configured client identity survives OAuth lifecycle changes

The system SHALL supply the configured client ID and client secret to the SDK-owned OAuth flow.
The system SHALL use that identity for authorization-code exchange and refresh-token redemption.
The configured identity SHALL remain authoritative after a token update and a daemon restart.
A stored dynamic registration SHALL NOT override a configured identity.

#### Scenario: Confidential client exchanges an authorization code

- **GIVEN** an OAuth server requires a matching client ID and client secret
- **WHEN** an operator completes MCP authorization
- **THEN** the SDK token request contains the configured client ID and client secret
- **AND** the MCP client publishes only after token storage and tool discovery succeed

#### Scenario: Confidential client refreshes after restart

- **GIVEN** a configured client identity and a stored refresh token
- **WHEN** the daemon restarts and the SDK refreshes the token
- **THEN** the token request contains the configured client ID and current configured secret
- **AND** no dynamic client registration occurs

#### Scenario: Operator rotates the configured secret

- **GIVEN** a token record exists for a configured client ID
- **AND** the operator replaces that profile's configured secret
- **WHEN** the next daemon instance refreshes the token
- **THEN** the SDK uses the replacement configured secret
- **AND** no secret from the token record overrides it
