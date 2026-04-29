## 1. Paired Device Registry

- [x] 1.1 Add `PairedDevice` record with `Name`, `TokenHash`, `Salt`, `CreatedAt`, `LastUsedAt` properties
- [x] 1.2 Add `DeviceRegistry` service that reads/writes `~/.netclaw/config/devices.json` — list, add, remove, lookup-by-hash, update last-used
- [x] 1.3 Add `IRemoteAuthSchemeRegistration` marker interface so startup validation can detect registered remote auth schemes

## 2. Bearer Token Authentication Scheme

- [x] 2.1 Implement `DeviceTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>` that reads `Authorization: Bearer <token>` header
- [x] 2.2 Hash the presented token with each device's salt and compare against `DeviceRegistry`
- [x] 2.3 On match: succeed with Netclaw claims (`Operator`, `Verified`, device name as `SenderId`), update `LastUsedAt`
- [x] 2.4 On no match: return `AuthenticateResult.Fail`
- [x] 2.5 On missing header: return `AuthenticateResult.NoResult()` to defer to other schemes
- [x] 2.6 Register the bearer token scheme alongside the loopback scheme in daemon DI

## 3. Pairing Code Service

- [x] 3.1 Add `PairingCodeService` that generates, stores (in-memory), validates, and consumes pairing codes
- [x] 3.2 Code format: 8 characters from `23456789ABCDEFGHJKLMNPQRSTUVWXYZ`, displayed as `XXXX-XXXX`
- [x] 3.3 5-minute TTL, single-use, only one pending code at a time
- [x] 3.4 Token generation on successful exchange: 32 bytes from `RandomNumberGenerator`, base64url-encoded

## 4. Pairing Exchange Endpoint

- [x] 4.1 Add `POST /api/pair/exchange` endpoint — accepts `{ code, deviceName }`, returns `{ token }` on success
- [x] 4.2 Endpoint is unauthenticated (outside `[Authorize]` gate) but only functional when a pending code exists
- [x] 4.3 On success: generate token, hash with random salt, store in `DeviceRegistry`, return raw token
- [x] 4.4 On failure (invalid/expired/consumed code): return 401 with descriptive error
- [x] 4.5 Add rate limiting on the exchange endpoint (e.g., 5 attempts per minute per IP)

## 5. Daemon-Side CLI Commands

- [x] 5.1 Add `netclaw daemon pair` command — connects to daemon via SignalR, invokes `GeneratePairingCode()` hub method, displays code and expiration
- [x] 5.2 Add `GeneratePairingCode()` method to `SessionHub` (requires `Operator` principal — loopback only)
- [x] 5.3 Add `netclaw daemon devices` command — lists paired devices (name, created, last-used)
- [x] 5.4 Add `netclaw daemon devices revoke <name>` command — removes device from registry
- [x] 5.5 Daemon logs pairing code to stdout so Docker operators can read from container logs

## 6. Remote CLI Pairing Command

- [x] 6.1 Add `netclaw pair <endpoint>` command — prompts for pairing code and device name (default: hostname)
- [x] 6.2 POST to `<endpoint>/api/pair/exchange` with code and device name
- [x] 6.3 On success: store token in `~/.netclaw/config/secrets.json` under `DeviceToken`, store endpoint in `netclaw.json` as `Daemon:Endpoint`
- [x] 6.4 On failure: display error and suggest checking the pairing code or trying again

## 7. CLI Token Attachment

- [x] 7.1 Update `DaemonClient` / `HubConnectionBuilder` to detect non-loopback endpoint and read `DeviceToken` from secrets
- [x] 7.2 Attach token via `AccessTokenProvider` on the SignalR connection options
- [x] 7.3 On 401 response, display a message suggesting `netclaw pair <endpoint>`

## 8. Startup Validation Extension

- [x] 8.1 Extend `ExposureModeValidationService` — after tunnel checks, verify at least one paired device exists or `IRemoteAuthSchemeRegistration` is registered when exposure mode is non-local
- [x] 8.2 On failure: log descriptive error explaining that remote access requires at least one paired device or auth scheme

## 9. CI Smoke Test

- [x] 9.1 Add pairing smoke test section to `scripts/smoke/check.sh` that exercises the full pairing lifecycle inside the smoke sandbox container: generate pairing code via `netclaw daemon pair`, exchange via `curl POST /api/pair/exchange`, verify device appears in `netclaw daemon devices`, connect to hub with bearer token, revoke device, verify revoked token is rejected
- [x] 9.2 Ensure smoke test runs after daemon start and before teardown (after existing session/stats tests)

## 10. Unit and Integration Tests

- [x] 10.1 Unit test `DeviceRegistry` — add, remove, lookup-by-hash, last-used update, file round-trip
- [x] 10.2 Unit test `PairingCodeService` — generation, validation, expiry, single-use, replacement
- [x] 10.3 Unit test `DeviceTokenAuthenticationHandler` — valid token → success with correct claims, invalid token → fail, missing header → no-result
- [x] 10.4 Integration test — full pairing flow: generate code → exchange → connect with token → authenticated session
- [ ] 10.5 Integration test — non-local exposure with no devices fails startup
- [x] 10.6 Unit test CLI token attachment — non-loopback endpoint attaches token, loopback skips
