## Why

Source PRDs: `PRD-002`, `PRD-004`.

Tunnel and reverse-proxy modes cannot distinguish a host CLI from forwarded loopback traffic.
The current hub rule can therefore deny the host or grant remote traffic unsafe local authority.

## What Changes

- Add a versioned local-control endpoint that requires a host key-ring proof.
- Use a distinct ASP.NET Core Data Protection purpose for the proof.
- **BREAKING** Remove pairing-code generation from the SignalR hub.
- Keep valid device records, tokens, and exposure settings without migration.
- Keep a valid code after a duplicate device-name conflict or a registry failure.
- Add deterministic proof for host success and remote denial in every exposure mode.
- Update the operator skill and the website procedure after the next `0.27` beta.

The MVP scope includes host code generation, remote code exchange, upgrade guidance, and deterministic tests.
The MVP scope excludes a general local IPC service, OIDC changes, and automatic device-token replacement.

## Capabilities

### New Capabilities

- `local-control-proof`: Defines a versioned and replay-resistant proof of daemon-host key-ring access.

### Modified Capabilities

- `device-pairing`: Replaces hub code generation and makes code consumption transactional.
- `hub-auth`: Removes host-only pairing authority from the SignalR hub.
- `daemon-exposure`: Keeps host recovery available without an exposure-mode change.
- `netclaw-testing`: Requires a complete host-success and remote-denial security matrix.

## Impact

- The daemon adds one local-control HTTP endpoint and a bounded replay cache.
- The CLI uses the local key ring instead of SignalR for `netclaw daemon pair`.
- The pairing exchange endpoint changes its code-consumption order.
- Mixed old and new CLI or daemon versions fail with explicit upgrade guidance.
- Container operators run the CLI inside the daemon container.
- The public draft PR receives normal CI before the fixed beta.
- The website update describes functionality and procedures, not the advisory.
