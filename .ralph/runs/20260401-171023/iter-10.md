# Iteration 10 — Flight Recorder

**RUN_ID:** 20260401-171023
**ITERATION:** 10
**DATE:** 2026-04-01

---

## Task Selected

**M7.B1: Claim types and ClaimsPrincipalMapper**

First incomplete task in the active milestone (Milestone 7 — Daemon Exposure and Hub Auth, Phase B: Hub Auth Framework).

---

## Surface Area Classification

- `Netclaw.Configuration` — new `NetclawClaimTypes` static class
- `Netclaw.Actors.Channels` — new `ConnectionIdentity` record and `ClaimsPrincipalMapper` service
- `Netclaw.Actors.Tests/Channels` — unit tests

No I/O, no actors, no DB, no HTTP. Pure value types and a stateless mapping service.

---

## Verification Level

**L1** — as specified in IMPLEMENTATION_PLAN.md for M7.B1.

Reason: surface area is entirely in-process value types (no I/O coordination, no DB/HTTP/actors). Unit tests cover all meaningful branches.

---

## Skills Consulted

- `.claude/skills/ralph-loop.md` — process discipline
- `openspec/changes/hub-auth-framework/design.md` — decisions D3, D4, D5
- `openspec/changes/hub-auth-framework/tasks.md` — task 1.1–1.3, 6.1

---

## Commands Run + Outcomes

```
dotnet build src/Netclaw.Actors/Netclaw.Actors.csproj -c Debug --no-incremental
→ Build succeeded. 0 Warning(s), 0 Error(s)

dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj --filter "ClaimsPrincipalMapper"
→ Passed! Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 32ms

dotnet slopwatch analyze
→ Exit code: 0 (2 pre-existing SW005 warnings in Netclaw.Daemon.csproj and Netclaw.Providers.csproj, not introduced by this iteration)
```

---

## Files Created

1. `src/Netclaw.Configuration/NetclawClaimTypes.cs` — `NetclawClaimTypes` static class with `PrincipalClassification`, `TransportAuthenticity`, `DeviceId` string constants.
2. `src/Netclaw.Actors/Channels/ConnectionIdentity.cs` — `ConnectionIdentity` positional record with `Principal`, `Transport`, `SenderId`.
3. `src/Netclaw.Actors/Channels/ClaimsPrincipalMapper.cs` — `ClaimsPrincipalMapper` singleton service; parses Netclaw claim types from `ClaimsPrincipal`, falls back to `UntrustedExternal`/`Unknown` per-claim when missing.
4. `src/Netclaw.Actors.Tests/Channels/ClaimsPrincipalMapperTests.cs` — 5 unit tests: null principal, loopback claims, bearer claims, missing claims, unrecognised claim values.

---

## Design Decisions

- Placed `ClaimsPrincipalMapper` in `Netclaw.Actors.Channels` (alongside `ConnectionIdentity`) rather than `Netclaw.Daemon` — keeps the mapper testable without daemon dependencies; M7.B2 will register it in daemon DI.
- `System.Security.Claims.ClaimsPrincipal` is BCL — no new package references needed.
- Null `ClaimsPrincipal` → full fallback; missing individual claims → per-claim fallback. Unauthenticated-but-non-null principals also fall back per-claim (defensive; `[Authorize]` in M7.B3 ensures only authenticated principals reach the hub in production).

---

## Deviations / Skips

None. All done-when criteria satisfied.

---

## Follow-ups Noticed (Deferred)

- M7.B2: `LoopbackAuthenticationHandler` — next task in Phase B
- M7.B3: Hub authorization middleware (`[Authorize]`, `UseAuthentication`, `UseAuthorization`)
- M7.B4: Identity propagation into `MessageSource` via `SessionRegistry` changes
- `ClaimsPrincipalMapper` needs to be registered in daemon DI (will happen in M7.B2 or M7.B3 when auth is wired up)
