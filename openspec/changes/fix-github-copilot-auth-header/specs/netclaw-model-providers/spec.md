## ADDED Requirements

### Requirement: GitHub Copilot request authorization integrity

The GitHub Copilot provider SHALL transmit the exchanged short-lived Copilot API
token on every chat completion request to `api.githubcopilot.com`. The token the
provider obtained from `/copilot_internal/v2/token` SHALL be the value present in
the outbound `Authorization: Bearer <copilot-api-token>` header as actually sent
on the wire — not a placeholder or any other credential.

Because the OpenAI SDK's own credential pipeline policy writes the
`Authorization` header from the client's `ApiKeyCredential` after any
caller-registered policy runs, the provider SHALL ensure the credential the SDK
reads carries the current Copilot token (e.g. by updating a shared mutable
`ApiKeyCredential` per request) rather than writing the `Authorization` header
directly, since a directly-written header is overwritten by the SDK and rejected
by Copilot with `HTTP 400 "Authorization header is badly formatted"`.

#### Scenario: Outbound Copilot request carries the exchanged token

- **GIVEN** a `github-copilot` provider entry with a valid GitHub OAuth token
- **AND** the token exchange returns the Copilot API token `T`
- **WHEN** a chat completion request is sent through the provider's chat client
- **THEN** the request that reaches `api.githubcopilot.com` carries
  `Authorization: Bearer T`
- **AND** the header value is NOT `Bearer placeholder` or any other credential

#### Scenario: SDK credential policy does not override the Copilot token

- **GIVEN** the OpenAI SDK is constructed with a placeholder `ApiKeyCredential`
- **WHEN** the provider issues a chat completion request
- **THEN** the SDK's credential auth policy emits the exchanged Copilot token,
  not the placeholder, because the shared credential was updated before the auth
  policy ran
