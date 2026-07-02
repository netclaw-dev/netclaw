## Why

Today, when Netclaw starts without a valid inference provider/model configuration,
startup can fail outright or the host can land in a degraded state where chat
turns produce confusing low-level errors. Operators — especially first-run
operators following PRD-004 onboarding — lose the surface they need to discover
and fix the problem (`netclaw doctor`, `netclaw model`, `netclaw.json`). We want
Netclaw to launch successfully even with no valid provider so it can guide the
operator to a working configuration instead of crashing or silently degrading.

## What Changes

- Add a No-Op `IChatClient` implementation that satisfies the
  Microsoft.Extensions.AI abstraction without contacting any provider.
- Provider/model configuration validation gains a "no valid provider configured"
  outcome that is distinct from "validation failed" — this outcome is
  **non-fatal** for startup and selects the No-Op client.
- Chat client selection prefers the configured provider's `IChatClient`; when no
  valid configuration is detected, it falls back to the No-Op client and logs the
  reason at startup.
- The No-Op client's responses contain a single, deterministic message:
  - leads with `"No valid model configuration detected."`
  - lists actionable recovery steps: `netclaw doctor`, edit `netclaw.json`,
    `netclaw model`
  - if provider discovery data is available (cached provider list, known
    profiles), references the available provider options in the message
- Once a valid configuration is in place, a daemon restart replaces the No-Op
  client with the real configured client. (Hot-swap on config change is
  out-of-scope for this change; it is handled by [[netclaw-config-hot-reload]]
  in a follow-up if needed.)
- `netclaw doctor` diagnostics SHALL clearly indicate when the No-Op client is
  active so operators immediately understand why chat turns are returning the
  configuration message.

## Capabilities

### New Capabilities

None. This is a behavior change inside existing capabilities.

### Modified Capabilities

- `netclaw-model-providers`: add a no-op chat-client fallback path and a
  "no valid configuration" validation outcome that is non-fatal for startup;
  define the No-Op client's response contract.
- `netclaw-onboarding`: startup SHALL succeed (in degraded mode) when no valid
  provider/model configuration is present, instead of failing the daemon. The
  wizard / doctor surfaces SHALL report the degraded state.

## Impact

- **Code**
  - `Netclaw.ModelProviders` (or equivalent) — new `NoOpChatClient`,
    chat-client selection/factory wiring.
  - Provider/model configuration validation — distinguish "invalid" (fail) vs
    "no provider configured" (degraded but operational).
  - Host startup wiring — register the No-Op client when validation returns the
    degraded outcome instead of throwing.
  - `netclaw doctor` — surface the degraded/No-Op state.
- **PRDs**
  - PRD-005 (model provider strategy): degraded-startup behavior is an
    addition; PRD update needed to reflect that "no valid provider" is no
    longer fatal at startup.
  - PRD-004 (CLI onboarding & config): doctor output surfaces degraded mode.
- **Operational**
  - Operators upgrading from a version that failed-fast on missing provider
    config will now see Netclaw start successfully and respond to chat turns
    with the configuration message. This is intentional and discoverable
    via doctor; runbook update required.
- **Security**
  - The No-Op client does not contact any external service and has no tool
    access. It cannot leak data and cannot be used to bypass ACLs (it produces
    a fixed response and performs no tool calls). Default-deny posture is
    preserved.
- **Out of scope**
  - Hot-swapping the chat client at runtime when configuration changes
    (restart required to pick up the real provider).
  - Auto-running `netclaw doctor --fix` on startup when degraded.
  - Any UI affordance beyond the chat message itself and doctor reporting.
