## Context

The SignalR hub (`SessionHub`) is a 59-line class with no auth logic.
`SessionRegistry` creates `MessageSource` with hardcoded defaults
(`UntrustedExternal`, `StrictDefault()` provenance) because there is no
identity source. The trust context pipeline downstream (`TrustContextDeriver`,
`EffectiveTrustContext`, policy enforcement) is fully built and awaiting real
identity values.

This change inserts the auth framework between the SignalR transport and the
session lifecycle, filling in the identity gap.

## Goals / Non-Goals

**Goals:**

- All SignalR connections must be authenticated by at least one scheme
- Loopback connections are automatically authenticated (zero friction for local)
- Auth claims map to existing `PrincipalClassification` / `TransportAuthenticity`
- `MessageSource` carries real identity instead of hardcoded defaults
- Adding a new auth scheme requires only DI registration + claim production

**Non-Goals:**

- Implementing any concrete remote auth scheme (device pairing, OIDC)
- Modifying the TrustContextDeriver or downstream policy logic
- Adding authorization policies beyond "must be authenticated"
- Changing the CLI's SignalR connection for remote scenarios (no remote auth
  scheme exists yet — the CLI only connects to loopback today)

## Decisions

### D1: Use ASP.NET Core's standard authentication middleware

Register authentication via `builder.Services.AddAuthentication()` with the
loopback scheme as the default. Add `[Authorize]` to `SessionHub`. This is the
standard ASP.NET Core pattern — no custom middleware needed.

**Alternative considered**: Custom middleware that inspects `HttpContext` before
the hub. Rejected because ASP.NET Core's auth pipeline already handles scheme
selection, challenge/forbid flows, and `ClaimsPrincipal` population. Reinventing
this would be fragile and lose compatibility with standard auth schemes.

### D2: Loopback scheme as a custom AuthenticationHandler

Implement `LoopbackAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>`
that checks `HttpContext.Connection.RemoteIpAddress` against loopback addresses.

On loopback match: succeed with claims for `Operator` + `LocalProcess`.
On non-loopback: return `AuthenticateResult.NoResult()` to defer to other schemes.

This runs on every connection, is stateless, and has zero external dependencies.

### D3: Netclaw claim types as constants

Define well-known claim types in a `NetclawClaimTypes` static class:

```csharp
public static class NetclawClaimTypes
{
    public const string PrincipalClassification = "netclaw:principal";
    public const string TransportAuthenticity = "netclaw:transport";
    public const string DeviceId = "netclaw:device-id";
}
```

All auth schemes produce these claims. The mapper reads them. This is the
contract between auth schemes and the rest of the system.

### D4: ClaimsPrincipalMapper as a singleton service

A `ClaimsPrincipalMapper` service converts `ClaimsPrincipal` → a
`ConnectionIdentity` record:

```csharp
public sealed record ConnectionIdentity(
    PrincipalClassification Principal,
    TransportAuthenticity Transport,
    string SenderId);
```

The mapper reads Netclaw claim types from the principal. If claims are missing,
it falls back to `UntrustedExternal` / `Unknown` — no silent upgrade.

Registered as singleton in DI, injected into `SessionRegistry`.

### D5: SessionRegistry extracts identity from HubCallerContext

`SessionHub` methods already receive `Context.ConnectionId`. The hub also
exposes `Context.User` (the `ClaimsPrincipal`). However, `SessionRegistry`
doesn't have access to the hub context directly.

Solution: `SessionRegistry.CreateSessionAsync` gains an overload (or the
existing method gains a `ClaimsPrincipal?` parameter) that the hub passes
from `Context.User`. The registry uses `ClaimsPrincipalMapper` to produce
`ConnectionIdentity`, then populates `MessageSource.Principal`,
`MessageSource.Provenance`, and `MessageSource.SenderId` accordingly.

For backward compatibility during rollout: if `ClaimsPrincipal` is null
(shouldn't happen with `[Authorize]` but defensive), use strict defaults.

### D6: No authorization policies beyond authentication

The hub requires authentication but does not enforce role-based or policy-based
authorization. Every authenticated principal (Operator, TrustedInternal,
VerifiedAutomation) can access all hub methods. Fine-grained authorization
(e.g., "only Operator can approve privileged actions") is enforced downstream
in the policy engine, not at the hub transport level.

**Alternative considered**: Hub-level `[Authorize(Policy = "Operator")]` on
specific methods. Rejected because the policy engine already handles this via
`EffectiveTrustContext`, and duplicating authorization at two layers creates
a maintenance burden.

## Risks / Trade-offs

**[Loopback detection may be unreliable in some Docker networks]** →
Docker's default bridge network maps container-to-container traffic through
non-loopback addresses even on the same host. Mitigation: for Docker
deployments, the daemon binds `0.0.0.0` and Docker's port mapping exposes on
host loopback. The connection from host CLI → container arrives as the Docker
bridge IP inside the container, not `127.0.0.1`. This means the loopback
scheme won't match, and a remote auth scheme is needed. However, for the
`-p 127.0.0.1:5199:5199` mapping with host networking or port forwarding,
the connection does arrive as loopback. This edge case is documented and
addressed by the device pairing scheme when needed.

**[Adding ClaimsPrincipal parameter to SessionRegistry changes the internal
API]** → All callers of `CreateSessionAsync` and `SendMessageAsync` must pass
the principal. Currently only `SessionHub` calls these, so the blast radius
is small.

**[No remote auth scheme means non-loopback connections are hard-rejected]** →
Until device pairing or OIDC is added, any non-loopback connection gets 401.
This is intentional fail-closed behavior, not a bug.

## Open Questions

1. **Should the loopback scheme also match Docker bridge gateway IP
   (`172.17.0.1`)?** This would auto-trust host-to-container connections on
   the default Docker bridge. Pro: smoother Docker experience. Con: trusting
   a specific IP range is fragile and could be exploited if multiple containers
   share the bridge. Lean toward: no, require a real auth scheme for Docker
   cross-network scenarios.
