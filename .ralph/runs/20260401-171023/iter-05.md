# RALPH Iter-05 — M7.A5: Init wizard exposure mode step

**RUN_ID:** 20260401-171023
**ITERATION:** 5
**DATE:** 2026-04-01

## Task Selected

**M7.A5: Init wizard exposure mode step**

> Init wizard step for selecting daemon network exposure mode (Local / TailscaleServe / TailscaleFunnel / CloudflareTunnel). Inserted after security-posture, before slack. Writes Daemon section to config only for non-default modes. High-risk warning for funnel/cloudflare; informational notice for tailscale-serve.

## Surface Area Classification

- `Netclaw.Cli` — wizard step VM + View
- `Netclaw.Cli.Tests` — new unit tests
- No persistence, no I/O, no actor coordination

## Verification Level

**L1** — build + unit tests only.

Justification: This is a pure UI step (Termina wizard ViewModel + View) with no I/O coordination (no DB, no HTTP, no actors). The meaningful behavior (config contribution, sub-step navigation, risk flag) is fully exercised by unit tests.

## Skills Consulted

- `ralph-loop.md` — process discipline
- `csharp-coding-standards` (via CLAUDE.md guidance) — no implicit conversions, fail loudly

## Files Changed

| File | Change |
|------|--------|
| `src/Netclaw.Cli/Tui/Wizard/WizardConfigBuilder.cs` | Added `DaemonConfigSection` record, `ExposureModeExtensions.ToWireValue()`, `Daemon` property, and Daemon section emission in `BuildConfigDictionary` |
| `src/Netclaw.Cli/Tui/Wizard/Steps/ExposureModeStepViewModel.cs` | New — wizard step VM: mode selection, sub-step navigation, config contribution |
| `src/Netclaw.Cli/Tui/Wizard/Steps/ExposureModeStepView.cs` | New — Termina view: SelectionListNode for 4 modes, high-risk warning panel, tailscale-serve notice |
| `src/Netclaw.Cli/Tui/InitWizardViewModel.cs` | Inserted exposure-mode step after security-posture, before slack |
| `src/Netclaw.Cli.Tests/Tui/Wizard/ExposureModeStepViewModelTests.cs` | New — 35 tests covering ContributeConfig, wire value format, sub-step navigation, risk flags |

## Commands Run + Outcomes

```
dotnet build src/Netclaw.Cli/Netclaw.Cli.csproj -c Release --no-restore
→ Build succeeded. 0 Warning(s), 0 Error(s). Time: 12.45s

dotnet test src/Netclaw.Cli.Tests/Netclaw.Cli.Tests.csproj -c Release --no-restore --filter "ExposureMode"
→ Passed! Failed: 0, Passed: 35, Skipped: 0, Total: 35, Duration: 150ms

dotnet slopwatch analyze
→ Scan complete: 2 issue(s) found (Warnings: 2)
  Both are pre-existing SW005 entries (OPENAI001 suppression in Daemon/Providers .csproj)
  Confirmed present in .slopwatch/baseline.json — no new violations
```

## Design Decisions

- **kebab-case wire format**: `ExposureModeExtensions.ToWireValue()` converts enum → kebab-case string matching the JSON schema (`local`, `tailscale-serve`, `tailscale-funnel`, `cloudflare-tunnel`). Using `.ToString()` would emit PascalCase which would fail schema validation.
- **Local omits Daemon section**: Schema default is `local`, so writing it is redundant noise. Omit-on-default keeps configs minimal.
- **Two-sub-step flow**: Local completes in 1 sub-step. All non-local modes require a second sub-step (notice or warning) before the step resolves. This ensures the operator acknowledges the network exposure change.
- **High-risk flag on funnel/cloudflare**: TailscaleFunnel and CloudflareTunnel expose the daemon to the public internet. They get a yellow warning panel with an explicit "I understand" confirmation. TailscaleServe is tailnet-only and gets a lower-key informational notice.

## Deviations / Skips

None. All Done-when criteria satisfied.

## Follow-ups Noticed but Deferred

- M7.A6: Hot-reload exclusion for Daemon section — next task in M7.A sequence.
- M7.B1+: Hub auth framework (depends on exposure-modes being complete).
