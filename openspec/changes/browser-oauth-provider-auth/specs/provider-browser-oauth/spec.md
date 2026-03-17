## ADDED Requirements

### Requirement: Browser-based OAuth Authorization Code + PKCE flow

The system SHALL support browser-based OAuth 2.0 Authorization Code flow with PKCE for provider authentication. The flow SHALL generate a cryptographic code_verifier and code_challenge, construct an authorization URL with requested scopes, and exchange the resulting authorization code for access and refresh tokens.

#### Scenario: Successful browser OAuth flow

- **WHEN** operator initiates OAuth login for a provider that supports `OAuthPkce`
- **THEN** the system generates a PKCE code_verifier and code_challenge
- **AND** constructs an authorization URL with `client_id`, `redirect_uri`, `scope`, `state`, `code_challenge`, and `response_type=code`
- **AND** attempts to open the URL in the default browser
- **AND** waits for the callback with the authorization code
- **AND** exchanges the code for access and refresh tokens via the token endpoint

#### Scenario: Token exchange includes PKCE verifier

- **WHEN** the OAuth callback delivers an authorization code
- **THEN** the token exchange request SHALL include the original `code_verifier`
- **AND** the token endpoint returns `access_token`, `refresh_token`, and `expires_in`

### Requirement: Provider OAuth callback endpoint

The daemon SHALL expose an HTTP callback endpoint for provider OAuth at `http://127.0.0.1:5199/api/provider/oauth/callback`. The endpoint SHALL accept `code` and `state` query parameters, validate the state against pending flows, and exchange the authorization code for tokens.

#### Scenario: Valid callback received

- **WHEN** the daemon receives `GET /api/provider/oauth/callback?code=X&state=Y`
- **AND** state `Y` matches a pending provider OAuth flow
- **THEN** the daemon exchanges the code for tokens
- **AND** signals the pending flow as complete
- **AND** returns an HTML success page to the browser

#### Scenario: Invalid or expired state

- **WHEN** the daemon receives a callback with an unrecognized or expired state
- **THEN** the daemon returns an HTML error page
- **AND** does not attempt token exchange

### Requirement: CLI-to-daemon OAuth orchestration

The CLI SHALL initiate provider OAuth flows by calling the daemon's `/api/provider/oauth/start` endpoint and poll `/api/provider/oauth/status/{state}` until the flow completes or times out.

#### Scenario: CLI starts OAuth flow via daemon

- **WHEN** the CLI initiates browser OAuth for a provider
- **THEN** the CLI calls `POST /api/provider/oauth/start` with provider type and descriptor metadata
- **AND** the daemon returns `{ authorizationUrl, state }`
- **AND** the CLI attempts to open the authorization URL in the browser

#### Scenario: CLI polls for completion

- **WHEN** the CLI is waiting for OAuth to complete
- **THEN** the CLI polls `GET /api/provider/oauth/status/{state}` at regular intervals
- **AND** the daemon returns `Completed`, `Pending`, or `Failed`
- **AND** on `Completed`, the CLI retrieves the tokens for probe validation

### Requirement: Redirect URL paste fallback

The system SHALL accept a manually pasted redirect URL as a fallback when the localhost callback cannot be received.

#### Scenario: User pastes redirect URL

- **WHEN** the operator pastes a URL matching the redirect URI pattern with `code` and `state` query parameters
- **THEN** the system extracts the authorization code and state
- **AND** sends them to the daemon for token exchange
- **AND** proceeds with probe validation on success

#### Scenario: Invalid pasted URL

- **WHEN** the operator pastes a URL that does not contain valid `code` and `state` parameters
- **THEN** the system displays an error message
- **AND** allows the operator to paste again or cancel

### Requirement: Shared PKCE and token service

The system SHALL provide a shared `OAuthPkceService` that encapsulates PKCE generation, authorization URL construction, token exchange, and token refresh. Both provider OAuth and MCP OAuth SHALL use this shared service for core OAuth operations.

#### Scenario: PKCE code verifier generation

- **WHEN** a new OAuth flow starts
- **THEN** the service generates a 32-byte random code_verifier encoded as base64url
- **AND** computes the code_challenge as SHA256 hash of the verifier encoded as base64url

#### Scenario: Token refresh via shared service

- **WHEN** a stored OAuth token is expired and a refresh token is available
- **THEN** the shared service exchanges the refresh token for a new access token
- **AND** preserves the existing refresh token if the server does not issue a new one
