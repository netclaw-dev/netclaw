## Why

The SignalR hub at `/hub/session` has no authentication. Every connection is
treated as `LocalOperator` with full trust. This is safe when the daemon binds
loopback-only, but once exposure modes allow non-local access (tailscale-serve,
tailscale-funnel, cloudflare-tunnel), any client that can reach the port has
unrestricted control of the daemon.

Before any authentication scheme (device pairing, OIDC/Entra) can be added,
the hub needs a scheme-agnostic auth framework: ASP.NET Core authentication
middleware, an `[Authorize]` gate on the hub, a loopback exemption for local
CLI access, and a mapper that converts auth claims into the existing
`PrincipalClassification` and `TransportAuthenticity` types.

This change builds the framework. Concrete auth schemes plug in separately.

Source PRDs: `PRD-002` (SEC-005 exposure policy, authenticated access).

## What Changes

- Add ASP.NET Core `AddAuthentication()` pipeline to daemon startup with a
  loopback authentication scheme that grants `LocalProcess` /  `Operator`
  claims for connections from `127.0.0.1` / `::1`.
- Add `[Authorize]` attribute to `SessionHub`, requiring all connections to be
  authenticated via at least one scheme.
- Add a claims-to-principal mapper that converts ASP.NET Core `ClaimsPrincipal`
  into `PrincipalClassification` and `TransportAuthenticity` values.
- Wire the mapper into `SessionRegistry` so `MessageSource` on every SignalR
  session carries real identity from the auth pipeline instead of hardcoded
  defaults.
- Add a `ConnectionIdentity` type that carries device/principal metadata from
  auth claims through the session lifecycle.

## Capabilities

### New Capabilities

- `hub-auth`: ASP.NET Core authentication middleware on the SignalR hub,
  loopback authentication scheme, claims-to-principal mapping, and
  `ConnectionIdentity` propagation into `MessageSource`.

### Modified Capabilities

- `netclaw-gateway-security`: The "Privileged action approval" requirement
  gains a concrete identity source — authenticated connections now carry
  real `PrincipalClassification` values instead of defaults.

## Impact

- **Daemon startup**: `AddAuthentication()` + loopback scheme registration in
  `Program.cs` / DI setup.
- **SessionHub**: `[Authorize]` attribute added. Unauthenticated connections
  are rejected with 401 before reaching hub methods.
- **SessionRegistry**: `CreateSessionAsync` and `SendMessageAsync` extract
  `ConnectionIdentity` from `HubCallerContext.User` and propagate it into
  `MessageSource`.
- **CLI client**: For loopback connections, no changes — the loopback scheme
  authenticates automatically. Remote connections will need a bearer token
  (provided by a future auth scheme), but the CLI's `HubConnectionBuilder`
  must be wired to attach tokens when configured.
- **No breaking changes for local-only deployments**: The loopback scheme
  ensures existing setups work identically. Only non-loopback connections
  without an auth scheme will be rejected.
