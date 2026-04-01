## 1. Claim Types and Identity

- [ ] 1.1 Add `NetclawClaimTypes` static class to `Netclaw.Configuration` with constants for `netclaw:principal`, `netclaw:transport`, `netclaw:device-id`
- [ ] 1.2 Add `ConnectionIdentity` record to `Netclaw.Actors.Channels` with `PrincipalClassification`, `TransportAuthenticity`, and `SenderId` properties
- [ ] 1.3 Add `ClaimsPrincipalMapper` service that converts `ClaimsPrincipal` → `ConnectionIdentity`, falling back to `UntrustedExternal` / `Unknown` when claims are missing

## 2. Loopback Authentication Scheme

- [ ] 2.1 Implement `LoopbackAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>` that checks `HttpContext.Connection.RemoteIpAddress` against `127.0.0.1` and `::1`
- [ ] 2.2 On loopback match: return success with Netclaw claims (`Operator`, `LocalProcess`, `SenderId = "local"`)
- [ ] 2.3 On non-loopback: return `AuthenticateResult.NoResult()` to defer to other schemes
- [ ] 2.4 Register the loopback scheme as the default authentication scheme in daemon DI

## 3. Hub Authorization

- [ ] 3.1 Add `AddAuthentication()` and `AddAuthorization()` to daemon `Program.cs` service registration
- [ ] 3.2 Add `[Authorize]` attribute to `SessionHub`
- [ ] 3.3 Add `app.UseAuthentication()` and `app.UseAuthorization()` to the middleware pipeline before hub mapping

## 4. Identity Propagation into MessageSource

- [ ] 4.1 Add `ClaimsPrincipal` parameter to `SessionRegistry.CreateSessionAsync` and `SendMessageAsync`
- [ ] 4.2 Inject `ClaimsPrincipalMapper` into `SessionRegistry`
- [ ] 4.3 Map `ConnectionIdentity` into `MessageSource.Principal`, `MessageSource.Provenance.TransportAuthenticity`, and `MessageSource.SenderId` when creating sessions and processing messages
- [ ] 4.4 Update `SessionHub` to pass `Context.User` to all `SessionRegistry` method calls

## 5. CLI Client Compatibility

- [ ] 5.1 Verify CLI's `DaemonClient` / `HubConnectionBuilder` works without changes for loopback connections (loopback scheme authenticates automatically, no token needed)
- [ ] 5.2 Add a `ConfigureAccessToken` extension point on `HubConnectionBuilder` that reads a bearer token from config/secrets when available (no-op for loopback, preparation for device pairing scheme)

## 6. Tests

- [ ] 6.1 Unit test `ClaimsPrincipalMapper` — loopback claims → `Operator`/`LocalProcess`, missing claims → `UntrustedExternal`/`Unknown`, bearer claims → `Operator`/`Verified`
- [ ] 6.2 Unit test `LoopbackAuthenticationHandler` — loopback IP → success with correct claims, non-loopback IP → `NoResult`
- [ ] 6.3 Integration test — unauthenticated non-loopback connection gets 401
- [ ] 6.4 Integration test — loopback connection succeeds and `MessageSource` carries `Operator` / `LocalProcess`
- [ ] 6.5 Unit test `SessionRegistry` — verify `MessageSource` is populated from `ClaimsPrincipal` instead of hardcoded defaults
