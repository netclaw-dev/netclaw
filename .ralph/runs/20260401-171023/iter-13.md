# Iteration 13 — M7.B4: Identity Propagation into MessageSource

## Task Selected
**M7.B4: Identity propagation into MessageSource**
- PRD: `docs/prd/PRD-002-gateway-security-envelope.md`
- OpenSpec: `openspec/changes/hub-auth-framework/` tasks 4.1–4.4, 5.1–5.2

## Surface Area Classification
- `Netclaw.Daemon/Gateway/SessionRegistry.cs` — core identity wiring
- `Netclaw.Daemon/Gateway/SessionHub.cs` — hub Context.User propagation
- `Netclaw.Daemon/Program.cs` — DI registration
- `Netclaw.Cli/Daemon/DaemonClient.cs` — token factory extension point
- `Netclaw.Cli/Daemon/HubConnectionBuilderExtensions.cs` — new extension (task 5.2)
- `Netclaw.Daemon.Tests/Gateway/SessionRegistryTests.cs` — unit tests (task 6.5)
- `openspec/changes/hub-auth-framework/tasks.md` — checkbox sync (3.1–3.3, 4.1–4.4, 5.1–5.2, 6.3–6.5)

## Verification Level
**L2** — unit tests + DI registration wiring. No I/O to real services. No UI changes.
Integration test coverage for auth/authz already completed in M7.B3 (`SessionHubAuthorizationTests`).

## Skills Consulted
- `.claude/skills/ralph-loop.md`

## Commands Run

| Command | Outcome |
|---------|---------|
| `dotnet build src/Netclaw.Daemon/Netclaw.Daemon.csproj` | ✅ 0 errors |
| `dotnet build src/Netclaw.Cli/Netclaw.Cli.csproj` | ✅ 0 errors (after fixing `IHubConnectionBuilder` return type) |
| `dotnet build src/Netclaw.Daemon.Tests/Netclaw.Daemon.Tests.csproj` | ✅ 0 errors (after adding `ISurrogated` + `IComparable` to `CapturingActorRef`) |
| `dotnet build src/Netclaw.Cli.Tests/Netclaw.Cli.Tests.csproj` | ✅ 0 errors |
| `dotnet test --filter SessionRegistryTests\|SessionHubAuthorization` | ✅ 15/15 passed |
| `dotnet test --filter DaemonClient` | ✅ 19/19 passed |
| `dotnet slopwatch analyze` | ✅ 2 warnings — both pre-existing SW005 entries in baseline, no new violations |

## Changes Made

### `Program.cs`
- Added `services.AddSingleton<ClaimsPrincipalMapper>()` before `SessionRegistry` registration.

### `SessionRegistry.cs`
- Added `using System.Security.Claims;`
- Injected `ClaimsPrincipalMapper mapper` into constructor; stored as `_mapper`
- Added `ClaimsPrincipal? principal = null` to `CreateSessionAsync`, `EnsureSessionAsync`, `AttachSessionAsync`, `SendMessageAsync`
- `SendMessageAsync`: replaced hardcoded `SenderId = "signalr-user"`, `Principal = Operator`, `TransportAuthenticity = LocalProcess` with values from `_mapper.Map(principal)` → `ConnectionIdentity`

### `SessionHub.cs`
- All 4 hub methods now pass `Context.User` to the corresponding registry calls

### `HubConnectionBuilderExtensions.cs` (new)
- `ConfigureAccessToken(this HubConnectionBuilder, string hubUrl, Func<Task<string?>>? tokenFactory)` — no-op for null factory (loopback), sets `AccessTokenProvider` otherwise

### `DaemonClient.cs`
- Added `Func<Task<string?>>? accessTokenProvider = null` parameter
- Stores as `_accessTokenProvider`, uses `ConfigureAccessToken` extension when building `HubConnection`
- All existing callers pass no factory → loopback behavior unchanged (task 5.1 verified)

### `SessionRegistryTests.cs`
- Updated `BuildRegistry` to accept optional `IRequiredActor<SignalRGatewayActorKey>`
- Added `CapturingRequiredActor` + `CapturingActorRef` inner classes (full `IActorRef` contract: `ISurrogated`, `IComparable`, `IComparable<IActorRef>`, `IEquatable<IActorRef>`)
- Added `SendMessage_populates_channel_input_from_claims_principal` — verifies loopback claims flow into `ChannelInput.SenderId`, `Principal`, and `Provenance.TransportAuthenticity`
- Added `SendMessage_uses_untrusted_defaults_when_no_principal_provided` — verifies null principal falls back to `UntrustedExternal`/`Unknown`

### `openspec/changes/hub-auth-framework/tasks.md`
- Marked 3.1–3.3 done (were implemented in M7.B3, checkboxes missed)
- Marked 4.1–4.4, 5.1–5.2, 6.3–6.5 done

## Build Fixes During Implementation
1. `HubConnectionBuilderExtensions.ConfigureAccessToken` — return type changed from `HubConnectionBuilder` to `IHubConnectionBuilder` because `WithUrl` returns the interface
2. `CapturingActorRef` — required `ISurrogated.ToSurrogate(ActorSystem)` and non-generic `IComparable.CompareTo(object?)` in addition to the typed interface members

## Deviations / Skips
- `ClaimsPrincipal?` parameter also added to `EnsureSessionAsync` and `AttachSessionAsync` (not mentioned in task 4.1 but required by task 4.4 "all registry calls"). Not used there yet — extension point only.
- Tasks 3.1–3.3 checkbox update: these were implemented in M7.B3 (commit `d54383e`) but the tasks.md wasn't updated then. Corrected here.

## Follow-ups Noticed but Deferred
- M7.C1–C5 (device pairing) builds on the `ConfigureAccessToken` extension — deferred per plan.
- The `ClaimsPrincipal` on `CreateSessionAsync`/`EnsureSessionAsync` is currently unused; future work (audit logging, per-session identity tracking) will consume it.
