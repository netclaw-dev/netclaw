# RALPH Iteration 01 — Flight Recorder

## Run metadata
- RUN_ID: 20260401-171023
- ITERATION: 1
- Date: 2026-04-01

## Task selected
**M7.A1: ExposureMode enum and DaemonConfig type**

OpenSpec change: `openspec/changes/exposure-modes/`
OpenSpec tasks: 1.1, 1.2, 1.4
Surface area: `Netclaw.Configuration`, `Netclaw.Daemon`

## Surface area classification
Pure configuration types. No I/O coordination, no DB/HTTP/actor dependencies.

## Verification level chosen
**L1** — configuration-only change. Unit tests exercise deserialization, defaults,
and DI registration plumbing. No integration or UI concerns.

## Skills consulted
- `openspec/changes/exposure-modes/design.md` — decisions D1, D2, D6 for type
  shapes, kebab-case wire format, and IConfiguration binding approach
- `openspec/changes/exposure-modes/tasks.md` — tasks 1.1, 1.2, 1.4
- `src/Netclaw.Configuration/SessionConfig.cs` — BindFromConfiguration factory
  pattern (existing codebase precedent for custom enum parsing)

## Commands run + outcomes

```
dotnet build src/Netclaw.Configuration/Netclaw.Configuration.csproj --no-incremental -c Debug
→ Build FAILED: CS0103 JsonNamingPolicy missing (needed `using System.Text.Json;`)
→ Fixed: added missing using directive

dotnet build src/Netclaw.Configuration/Netclaw.Configuration.csproj --no-incremental -c Debug
→ Build succeeded, 0 errors, 0 warnings

dotnet build src/Netclaw.Daemon/Netclaw.Daemon.csproj --no-incremental -c Debug
→ Build succeeded, 0 errors, 0 warnings

dotnet test src/Netclaw.Configuration.Tests/ -c Debug --filter "DaemonConfigTests"
→ Passed: 17, Failed: 0 (all DaemonConfigTests)

dotnet test src/Netclaw.Configuration.Tests/ -c Debug
→ Passed: 129, Failed: 0 (full suite, pre-existing tests intact)

dotnet slopwatch analyze
→ 2 SW005 warnings (OPENAI001 NoWarn in Daemon.csproj and Providers.csproj)
→ Confirmed pre-existing: same 2 violations present on base branch before any changes
→ Zero new violations introduced by this iteration
```

## Implementation notes

**ExposureMode enum** (`src/Netclaw.Configuration/ExposureMode.cs`):
- Values: `Local`, `TailscaleServe`, `TailscaleFunnel`, `CloudflareTunnel`
- Decorated with `[JsonConverter(typeof(ExposureModeJsonConverter))]`
- `ExposureModeJsonConverter` extends `JsonStringEnumConverter<ExposureMode>`
  with `JsonNamingPolicy.KebabCaseLower` — produces `local`, `tailscale-serve`,
  `tailscale-funnel`, `cloudflare-tunnel` in STJ contexts

**DaemonConfig record** (`src/Netclaw.Configuration/DaemonConfig.cs`):
- `sealed record` with `Host` (default `127.0.0.1`), `Port` (default `5199`),
  `ExposureMode` (default `Local`)
- `BindFromConfiguration(IConfigurationSection?)` factory — follows the same
  pattern as `SessionConfig.BindFromConfiguration`. Reads raw strings from
  IConfiguration, parses kebab-case or PascalCase via `ParseExposureMode`.
  Returns defaults when section is null or missing.
- `ParseExposureMode` internal method handles both kebab (`tailscale-serve`)
  and PascalCase (`TailscaleServe`) inputs, throws on unknown values.

**DI registration** (`src/Netclaw.Daemon/Program.cs`):
- Added at top of `ConfigureDaemonServices`: `DaemonConfig.BindFromConfiguration(...)`
  then `services.AddSingleton(daemonConfig)`.
- The `UseUrls` hardcoded string is NOT changed yet (that is M7.A2).

**Why BindFromConfiguration instead of standard Get<T>():**
`IConfiguration.Get<DaemonConfig>()` calls `Enum.Parse(ignoreCase: true)` which
rejects kebab-case values containing hyphens. The `BindFromConfiguration` factory
pattern (already used by `SessionConfig`) handles this cleanly with an explicit
switch expression.

## Deviations / skips
- None. All four done-when items for M7.A1 are satisfied.

## Follow-ups noticed but deferred
- M7.A2: Update `netclaw-config.v1.schema.json` with `Daemon` section; replace
  hardcoded `UseUrls("http://127.0.0.1:5199")` with `daemonConfig`-driven URL.
  Deferred per single-task rule.
- M7.A3–A6 and Phase B/C tasks: all in IMPLEMENTATION_PLAN.md, untouched.
