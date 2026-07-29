# mcp-oauth Specification

## ADDED Requirements

### Requirement: OAuth protocol ownership belongs to the MCP SDK, except client registration

The system SHALL delegate MCP OAuth protocol operations — authorization-server
selection, PKCE generation and validation, authorization-code exchange,
refresh-token redemption, and bearer-token injection — to the MCP C# SDK.
Netclaw SHALL NOT implement those protocol operations and SHALL NOT maintain a
separate OAuth metadata cache as a runtime dependency.

Netclaw SHALL own protected-resource discovery and RFC 7591 dynamic client
registration, because the SDK hard-codes
`token_endpoint_auth_method: "client_secret_post"` in its registration request
and never consults the authorization server's advertised
`token_endpoint_auth_methods_supported`, which makes registration impossible
against a server that accepts public clients only. Netclaw SHALL register with
the same method the SDK selects for the token request — the first entry of
`token_endpoint_auth_methods_supported`, defaulting to `client_secret_post` when
the server advertises none — so the registered method and the method used at the
token endpoint cannot diverge. Netclaw SHALL seed `ClientOAuthOptions.ClientId`
from the durable record so the SDK's registration path never executes.

Registration SHALL occur only during explicit authorization. A server that
advertises no protected-resource metadata SHALL be treated as requiring no
OAuth. A missing or failing `registration_endpoint` SHALL produce an
operator-facing error naming `OAuthClientId` as the remedy.

#### Scenario: A public-client-only server is registered as a public client

- **GIVEN** an authorization server advertising `token_endpoint_auth_methods_supported: ["none"]`
- **WHEN** the operator runs explicit authorization
- **THEN** the registration request carries `token_endpoint_auth_method: "none"`
- **AND** the SDK issues no registration request of its own
- **AND** the token request authenticates as a public client

#### Scenario: A server without dynamic registration names the remedy

- **GIVEN** an authorization server that publishes no `registration_endpoint`, or whose registration endpoint rejects the request
- **WHEN** the operator runs explicit authorization
- **THEN** the failure names `OAuthClientId` as the way to supply a manually registered client
- **AND** the daemon log records the registration endpoint, the HTTP status, and the RFC 7591 `error` / `error_description` fields
- **AND** the raw response body is not carried into any exception or log entry, because daemon logs are OTLP-exported when telemetry is enabled

#### Scenario: Startup with bound cached tokens requires no metadata cache

- **GIVEN** secrets.json contains a valid token set bound to the configured HTTP MCP resource identity
- **AND** no `mcp-oauth-metadata.json` file exists
- **WHEN** the daemon connects to that server
- **THEN** the SDK-provided OAuth support is installed on the connection
- **AND** the cached tokens are used or refreshed by the SDK
- **AND** the connection reaches `Connected` without interactive authorization

#### Scenario: Netclaw performs no direct token-endpoint calls

- **GIVEN** a configured HTTP MCP server with OAuth in use
- **WHEN** tokens are acquired or refreshed during any flow
- **THEN** every token-endpoint request originates from the MCP SDK
- **AND** no Netclaw-owned code constructs authorization or token requests

#### Scenario: Legacy metadata files are ignored, not deleted

- **GIVEN** a pre-existing `mcp-oauth-metadata.json` file from an earlier version
- **WHEN** the daemon starts
- **THEN** the file is not read on any runtime code path
- **AND** the file is not automatically deleted

### Requirement: Interactive authorization is brokered, not implemented

Explicit authorization (`netclaw mcp auth <name>`) SHALL retain its existing
CLI and HTTP surface. The daemon SHALL broker the SDK-generated authorization
URL to the operator and complete the SDK's redirect delegate from the local
callback endpoint. Flow state values SHALL be cryptographically opaque,
one-time, bound to a single server and flow, and SHALL expire after a bounded
lifetime of five minutes measured via `TimeProvider`. Netclaw SHALL validate
the callback state itself — the SDK neither generates nor validates the OAuth
state parameter. The first redirect-delegate invocation for a flow SHALL own
the authorization URL, callback code, and PKCE exchange. Concurrent delegate
invocations SHALL observe that authorization is already in progress and SHALL
NOT receive or reuse the owner's authorization code. At most one
interactive flow SHALL be active per server; concurrent start requests SHALL
coalesce onto that flow. The daemon SHALL own the flow and candidate lifetime
independently of the HTTP start and callback request cancellation tokens, and
SHALL cancel them on expiry or daemon shutdown. The lifetime SHALL match the
existing five-minute CLI and TUI polling timeout.

#### Scenario: Explicit authorization end to end

- **GIVEN** a configured HTTP MCP server requiring OAuth
- **WHEN** the operator starts explicit authorization
- **THEN** the daemon creates a pending flow and an unpublished candidate connection
- **AND** the SDK performs discovery, registration if needed, and PKCE, and supplies the authorization URL through its redirect delegate
- **AND** the CLI receives that URL through the existing start response and polls the existing status endpoint
- **AND** the callback completes the flow, the SDK exchanges the code, and the candidate is published only after initialization (including tool listing) succeeds
- **AND** polling reports `Completed` only after that publication succeeds

#### Scenario: Concurrent starts share one flow

- **GIVEN** an interactive authorization flow is active for a server
- **WHEN** another start request arrives for the same server
- **THEN** both callers receive the same state and authorization URL
- **AND** only one candidate connection and one credential update can result

#### Scenario: Invalid, reused, or expired state fails visibly

- **WHEN** the callback receives a state value that is missing, mismatched, already completed, or older than the flow lifetime
- **THEN** the callback fails with a safe `text/html` error response
- **AND** no token exchange is attempted for that request
- **AND** any pending flow for a different state value is unaffected

#### Scenario: Failed authorization preserves prior state

- **GIVEN** a server with an existing published connection and stored credentials
- **WHEN** an explicit authorization attempt fails or is cancelled
- **THEN** only the candidate connection is disposed
- **AND** the previously published connection remains live
- **AND** the previously stored credentials remain intact

#### Scenario: Existing valid token does not suppress explicit reauthorization

- **GIVEN** a server with a currently valid stored access token
- **WHEN** the operator starts explicit authorization
- **THEN** the SDK authorization path is entered without first deleting the stored credentials

#### Scenario: Closing the browser tab does not cancel the exchange

- **GIVEN** a pending flow whose callback request has delivered a valid code
- **WHEN** the browser connection closes before token exchange completes
- **THEN** the token exchange and candidate publication continue to completion

#### Scenario: Start request cancellation does not own the candidate

- **GIVEN** the start endpoint has returned an authorization URL
- **WHEN** its HTTP request cancellation token is cancelled
- **THEN** the daemon-owned flow and candidate remain active until completion, expiry, explicit cancellation, or daemon shutdown

#### Scenario: Concurrent challenges reuse one pending flow

- **GIVEN** a pending interactive flow for a server
- **WHEN** the SDK invokes the redirect delegate concurrently from parallel transport requests
- **THEN** one invocation owns the authorization URL and callback code
- **AND** every other invocation observes authorization in progress without prompting
- **AND** no authorization code is returned to more than one SDK invocation

#### Scenario: A cancelled exchange requires fresh authorization

- **GIVEN** a delivered authorization code whose token exchange is cancelled or fails
- **WHEN** the flow terminates
- **THEN** the flow is marked failed and the one-time code is not reused
- **AND** a subsequent attempt starts a new authorization rather than retrying the exchange

### Requirement: Durable credential persistence precedes publication

The system SHALL treat secrets.json as the durable authority for active MCP
OAuth credentials. Published-connection token updates SHALL persist before the
SDK store call returns. Token rotations acquired by an ordinary startup or
reconnect candidate SHALL also persist before the SDK store call returns because
the authorization server may already have consumed the prior refresh token.
Credentials acquired by an unpublished interactive
candidate SHALL remain local to that candidate until tool listing succeeds,
then SHALL replace the sole durable active record before the candidate is
published. A persistence failure SHALL fail the candidate visibly without
changing active credentials or the published connection. Failed or expired
candidates SHALL require no durable cleanup. When a token response omits a
refresh token, the system SHALL retain a previous refresh token only when the
resource and effective client identity match. The effective client ID — and client secret, when issued — from
dynamic client registration SHALL be stored with the token record and
supplied to the SDK's OAuth options on subsequent connections. The record
SHALL also contain the normalized configured MCP resource identity used when
the credentials were obtained. Before returning cached credentials to the SDK,
Netclaw SHALL compare that binding with the current configured resource and
SHALL withhold the credentials when they differ. The canonical identity SHALL
be the absolute configured endpoint URI after `System.Uri` normalization, with
scheme and host normalized, default port normalized, fragment removed, and path
and query retained.

#### Scenario: Persistence failure is loud

- **GIVEN** durable persistence of a rotated token set fails
- **WHEN** the SDK stores tokens through the token-cache boundary
- **THEN** the store operation reports failure to the SDK
- **AND** the in-memory token view is not advanced
- **AND** the connection attempt fails with an actionable error

#### Scenario: Failed candidate does not replace active credentials

- **GIVEN** a server has active durable credentials and a published connection
- **AND** an explicit authorization candidate stores replacement credentials in its local cache
- **WHEN** candidate initialization or tool listing fails before publication
- **THEN** the candidate-local credentials are discarded
- **AND** the prior active credential record remains authoritative and unchanged

#### Scenario: Omitted refresh token is retained

- **GIVEN** a stored token set containing a refresh token
- **WHEN** a token response returns a new access token without a refresh token
- **THEN** the persisted record keeps the previous refresh token

#### Scenario: Registered client identity survives restart

- **GIVEN** a token set persisted with a client ID (and client secret, when issued) from dynamic client registration
- **WHEN** the daemon restarts and reconnects to that server
- **THEN** the SDK OAuth options are seeded with the persisted client ID and any persisted client secret
- **AND** no re-registration occurs while the stored registration remains valid

#### Scenario: Repointed profile cannot reuse old credentials

- **GIVEN** stored credentials are bound to one configured MCP resource identity
- **WHEN** the same server profile name is changed to a different resource identity
- **THEN** the old token set, dynamically registered client ID, and client secret are not supplied to the SDK connection for the new resource
- **AND** the server reports `AwaitingAuth` for the new identity
- **AND** the old durable record remains intact until replacement credentials are stored successfully

#### Scenario: Legacy credentials for the same resource migrate on upgrade

- **GIVEN** a stored credential record predates the canonical resource binding and carries a legacy resource that describes the configured endpoint
- **WHEN** the daemon loads the server after upgrade
- **THEN** the record is stamped with the canonical resource identity and remains usable without reauthorization
- **AND** the legacy resource field is retained so the migration can be repeated if the endpoint is corrected later
- **AND** an absent obtained-at timestamp is stamped, so the credential is not computed as expired

#### Scenario: Legacy credentials for a different audience fail closed

- **GIVEN** a legacy record whose resource differs from the configured endpoint in scheme, host, port, query, or sibling path
- **WHEN** the daemon loads the server after upgrade
- **THEN** no token, client ID, or client secret from that record is supplied to the SDK
- **AND** the server reports `AwaitingAuth` with `netclaw mcp auth <name>` as the remedy
- **AND** the rejected binding and the configured endpoint are both logged
- **AND** the record on disk is left unchanged

#### Scenario: Revoked dynamic registration can be replaced explicitly

- **GIVEN** credentials contain a dynamically registered client identity that the authorization server rejects as `invalid_client`
- **WHEN** the operator runs explicit authorization
- **THEN** that flow fails visibly and the rejected client identity is discarded from the durable record
- **AND** the stored tokens are left intact, because only the client identity was rejected
- **AND** the next explicit authorization registers a new client and completes
- **AND** the prior active credentials remain unchanged until the replacement candidate is published
- **BUT** an explicitly configured static client ID is never discarded or replaced automatically

### Requirement: Callback identity derives from daemon configuration

The OAuth redirect URI SHALL be
`http://127.0.0.1:{DaemonConfig.Port}/api/mcp/oauth/callback`, derived from
the configured daemon port. The system SHALL NOT hard-code the port and SHALL
NOT derive the callback host from an incoming request's Host header.

#### Scenario: Non-default port is used consistently

- **GIVEN** the daemon is configured with a non-default port
- **WHEN** dynamic client registration and authorization occur
- **THEN** the registered and requested redirect URIs both use the configured port
- **AND** the local callback succeeds without manual URL correction

### Requirement: Startup and reconnect never block on interactive authorization

The system SHALL create every configured HTTP MCP transport without an
operator-configured `Authorization` header with SDK OAuth support whose
non-interactive redirect delegate returns no authorization code. A configured
`Authorization` header SHALL suppress SDK OAuth so the SDK cannot replace it
after a challenge. OAuth support SHALL remain dormant for unauthenticated
servers, and all operator-provided headers SHALL remain authoritative. When interactive authorization is required, the
connection SHALL report `AwaitingAuth` and direct the operator to
`netclaw mcp auth <name>` instead of opening a browser or waiting for input.

#### Scenario: Static header authentication is unchanged

- **GIVEN** a server authenticated by operator-configured headers
- **WHEN** the daemon connects
- **THEN** the configured headers are sent unmodified
- **AND** a configured `Authorization` header disables SDK OAuth challenge handling for that transport
- **AND** the connection succeeds as before

#### Scenario: Unauthenticated server is unchanged

- **GIVEN** a configured HTTP MCP server that requires no authentication
- **WHEN** the daemon connects
- **THEN** the connection succeeds without any OAuth activity

#### Scenario: OAuth-required server awaits the operator

- **GIVEN** a server that answers with an OAuth challenge and no usable stored credentials
- **WHEN** the daemon starts or reconnects
- **THEN** no browser is opened and startup does not block
- **AND** the server's status is `AwaitingAuth`
- **AND** the operator-facing alert names `netclaw mcp auth <name>` as the remediation

### Requirement: OAuth failures produce actionable diagnostics

Authenticated OAuth API endpoints SHALL return a structured error response for
discovery, registration rejection, credential persistence, and connection
initialization failures. The anonymous browser callback SHALL retain its
existing `text/html` response and SHALL render a safe actionable error for
callback validation or code-exchange failures. The daemon SHALL log the full
exception with provider and server context. No client-facing JSON or HTML SHALL
contain tokens, authorization codes, PKCE verifiers, or secret values. The CLI
SHALL parse structured responses when present, SHALL fall back to the HTTP
status and reason when the body is empty or malformed, and SHALL NOT print a
blank error. The authenticated OAuth status response SHALL carry an optional
structured error for terminal failures that occur after the start response has
already returned.

#### Scenario: Registration rejection surfaces a reason

- **GIVEN** a provider that advertises dynamic client registration but rejects it with HTTP 403 and no body
- **WHEN** the operator runs explicit authorization
- **THEN** the CLI displays the failing operation and the HTTP status
- **AND** the daemon log contains the registration endpoint and the HTTP status

#### Scenario: Bodyless daemon error still yields CLI output

- **GIVEN** the daemon returns an error status with an empty or malformed body
- **WHEN** the CLI reports the failure
- **THEN** the CLI prints the HTTP status and reason phrase rather than a blank error line

#### Scenario: Late candidate failure is available through status

- **GIVEN** an authorization start response has already returned successfully
- **WHEN** credential persistence, code exchange, or candidate initialization later fails
- **THEN** the authenticated status response reports `Failed`
- **AND** it includes a safe structured error naming the failed operation

#### Scenario: Callback validation failure remains browser-safe HTML

- **GIVEN** the anonymous callback receives invalid or expired state
- **WHEN** it reports the failure to the browser
- **THEN** the response content type is `text/html`
- **AND** the rendered message contains no code, token, verifier, or secret value
