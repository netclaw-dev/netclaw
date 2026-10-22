## Why

Source PRDs: `PRD-002`, `PRD-004`.

The provider OAuth status endpoint gave raw tokens to a caller that the daemon classified as local.
A tunnel agent connects from loopback, so a source address cannot identify the host CLI.
After the daemon stopped trust in that classification, the host CLI could not finish provider OAuth in a tunnel mode.

## What Changes

- **BREAKING** The status endpoint `GET /api/provider/oauth/status/{state}` never returns tokens to any caller.
- Add `POST /api/local-control/v1/provider-oauth-result`, which returns the tokens only for a valid local-control proof.
- Add a second local-control operation, `read-provider-oauth-result`, with its own Data Protection purpose.
- Bind each provider OAuth result proof to one OAuth state.
- Keep the pairing proof purpose, operation value, and byte layout without change.
- The host CLI creates the proof and sends it only to the direct local-control endpoint.
- A refused read or an older daemon stops the CLI OAuth flow with a visible error.

The MVP scope includes the host CLI provider OAuth read in every exposure mode and deterministic denial tests.
The MVP scope excludes a token read for a remote paired device, a general local administration protocol, and MCP OAuth, which keeps its tokens in the daemon.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `local-control-proof`: Adds the provider OAuth result operation, its state binding, and its endpoint. The status endpoint loses its token fields.

## Impact

- The daemon adds one anonymous, rate-limited local-control endpoint. It reuses the proof validator and its replay cache.
- `DaemonApi` owns proof creation for both local-control operations.
- A new CLI with an old daemon gets `404` and prints upgrade guidance.
- An old CLI with a new daemon cannot finish provider OAuth. It reports "Provider OAuth completed without token payload."
- Security impact: a device token, a bootstrap token, a loopback address, or a forwarded address cannot read provider tokens.
- Operational impact: the operator runs the CLI on the daemon host, with the same Netclaw home, to add an OAuth provider.
