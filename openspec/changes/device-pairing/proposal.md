## Why

The hub auth framework (change: `hub-auth-framework`) provides scheme-agnostic
authentication on the SignalR hub with a loopback scheme for local access. But
self-hosted operators who want remote CLI or web UI access without an external
identity provider have no way to authenticate. They need a lightweight,
Netclaw-native mechanism to establish trust between a remote client and the
daemon.

Device pairing provides this: a local operator (authenticated via loopback)
approves a one-time pairing code, and the remote client receives a long-lived
bearer token. This is the SSH `authorized_keys` model — you need physical or
shell access once, then remote access works forever after.

Source PRDs: `PRD-002` (SEC-005 authenticated access for non-local exposure).

## What Changes

- Add a bearer token authentication scheme that validates device tokens on
  SignalR connections from non-loopback addresses.
- Add a pairing flow: daemon generates a short-lived pairing code via
  `netclaw daemon pair`, remote CLI exchanges the code for a long-lived device
  token via `netclaw pair <endpoint>`.
- Add a paired device registry stored in `~/.netclaw/config/devices.json` that
  tracks device names, token hashes, creation dates, and last-used timestamps.
- Add `netclaw daemon devices` CLI command to list and revoke paired devices.
- Add startup validation: non-local exposure mode with no paired devices and no
  other auth scheme configured SHALL fail startup.
- CLI's `HubConnectionBuilder` attaches the bearer token from local secrets
  when connecting to a non-loopback endpoint.

## Capabilities

### New Capabilities

- `device-pairing`: Bearer token authentication scheme, pairing code exchange
  flow, paired device registry, device management CLI commands, and CLI token
  attachment for remote connections.

### Modified Capabilities

- `netclaw-gateway-security`: Non-local exposure mode SHALL fail startup if no
  paired devices exist and no alternative auth scheme (e.g., OIDC) is
  configured. Strengthens the fail-closed guarantee.

## Impact

- **New files**: Bearer token auth handler, pairing code service, device
  registry, device management CLI commands.
- **Config/secrets**: `devices.json` in config directory (token hashes, not
  raw tokens). Remote CLI stores its device token in `~/.netclaw/config/secrets.json`.
- **Daemon startup**: Startup validation gate extended to check for at least
  one paired device (or alternative auth scheme) when exposure mode is non-local.
- **CLI client**: `HubConnectionBuilder` reads bearer token from secrets and
  attaches it to the connection when the daemon endpoint is non-loopback.
- **Depends on**: `hub-auth-framework` (provides the `[Authorize]` gate,
  claim types, `ClaimsPrincipalMapper`, and `ConnectionIdentity` propagation).
- **No breaking changes**: Local-only deployments are unaffected — loopback
  scheme handles auth, no pairing needed.
