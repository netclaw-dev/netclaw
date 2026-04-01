## Context

The daemon is hardcoded to bind `http://127.0.0.1:5199` in `Program.cs:92`.
The CLI already resolves a configurable endpoint via `DaemonApi.ResolveEndpoint()`
which checks `Daemon:Endpoint` config → `NETCLAW_DAEMON_ENDPOINT` env var →
default. However, the daemon side has no corresponding configuration — it
ignores what the CLI can already be told.

SPEC-006 defines four exposure modes but none are implemented. The security
model (`DeploymentPosture`, `TrustAudience`, `PrincipalClassification`) is
fully built for trust context derivation, but the network exposure layer that
determines *who can reach the daemon* does not exist.

This change adds the exposure mode declaration, configurable bind address,
startup validation, doctor checks, and wizard step. It does NOT add
authentication on the SignalR hub — that is a separate change (device pairing).

## Goals / Non-Goals

**Goals:**

- Daemon reads bind address from config instead of hardcoding it
- Operators can declare which tunnel infrastructure is in front of the daemon
- Daemon fails startup if declared tunnel prerequisites are missing
- `netclaw doctor` validates exposure health and flags unsafe configurations
- `netclaw init` wizard includes an exposure mode selection step
- Existing configs with no `Daemon` section continue working exactly as today

**Non-Goals:**

- Hub authentication (device pairing / bearer tokens) — separate change
- Tunnel lifecycle management (starting/stopping tailscaled, cloudflared)
- Webhook ingestion endpoints — depends on both exposure modes and device pairing
- Remote `netclaw init` — mount config volume or SSH for now
- Entra / OIDC integration — future managed offering

## Decisions

### D1: ExposureMode as a string enum in config, not a complex object

The `ExposureMode` config property is a simple string enum:
`local`, `tailscale-serve`, `tailscale-funnel`, `cloudflare-tunnel`.

**Alternative considered**: A nested object per mode with mode-specific
properties (e.g., `TailscaleServe: { AclPolicy: "..." }`). Rejected because
the daemon does not manage tunnels — it only validates their presence. Tunnel
configuration belongs to the tunnel tool itself (Tailscale admin console,
Cloudflare dashboard). Keeping the enum flat avoids coupling Netclaw config to
tunnel-specific schemas that will change independently.

### D2: DaemonConfig record in Netclaw.Configuration

New configuration type:

```csharp
public sealed class DaemonConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5199;
    public ExposureMode ExposureMode { get; set; } = ExposureMode.Local;
}

public enum ExposureMode
{
    Local,
    TailscaleServe,
    TailscaleFunnel,
    CloudflareTunnel
}
```

Bound from `Daemon` section via `IConfiguration`. JSON schema uses lowercase
kebab-case string values (`"tailscale-serve"`) with a `JsonStringEnumConverter`
for deserialization.

**Alternative considered**: Reusing `DeploymentPosture` to infer exposure mode
(Personal → local, Team → tailscale-serve, Public → tailscale-funnel).
Rejected because deployment posture is about trust level, not network topology.
A personal-mode daemon might run behind Tailscale Serve for remote access, and
a team-mode daemon might run local-only on a shared server.

### D3: Startup validation as an IHostedService gate

Tunnel prerequisite validation runs as an `IHostedService` that executes
before the SignalR hub accepts connections. The validation service:

1. Reads `DaemonConfig` from DI
2. If `ExposureMode` is `Local`, no validation needed — return immediately
3. If Tailscale mode: check if `tailscaled` process is running via process list
4. If Cloudflare mode: check if `cloudflared` process is running via process list
5. On failure: log the error and throw, causing `Host.RunAsync()` to fail

**Alternative considered**: Validation inside `Program.cs` before
`builder.Build()`. Rejected because DI services aren't available yet at that
point, and the check benefits from proper logging and structured error
reporting through the host pipeline.

**Alternative considered**: HTTP health probe against the tunnel endpoint.
Rejected for MVP — process detection is simpler and avoids requiring the tunnel
to be configured with a specific URL that Netclaw would need to know. Can be
added later as an enhancement.

### D4: Doctor check reads config, not daemon state

The `ExposureModeDoctorCheck` is an offline check that reads `netclaw.json`
and probes for tunnel processes. It does NOT require the daemon to be running.
This follows the existing pattern — most doctor checks (schema validation,
secrets permissions, ACL validation) operate on config files directly.

The check reports:
- `Pass` if mode is `local` and bind address is loopback
- `Warning` if bind address is non-loopback and mode is `local`
- `Pass` if mode is non-local and tunnel process is detected
- `Error` if mode is non-local and tunnel process is missing

### D5: Wizard step after security posture, before Slack

The exposure mode step inserts between the security posture step and the Slack
step in the wizard sequence. This position is natural because:
- Security posture sets the trust level (what the bot can do)
- Exposure mode sets the network reach (who can contact the bot)
- Slack configuration depends on knowing whether the daemon is local-only

The step follows the `SecurityPostureStepViewModel` pattern: single sub-step,
`SelectionListNode` for mode choice, no async operations, contributes a
`DaemonConfigSection` to `WizardConfigBuilder`.

When a public mode is selected (`tailscale-funnel`, `cloudflare-tunnel`), the
step displays a warning panel and requires explicit confirmation before
allowing progression. `tailscale-serve` shows an informational notice only.

### D6: Daemon bind address reads from config with loopback default

`Program.cs` changes from:

```csharp
builder.WebHost.UseUrls("http://127.0.0.1:5199");
```

To:

```csharp
var daemonConfig = builder.Configuration
    .GetSection("Daemon").Get<DaemonConfig>() ?? new DaemonConfig();
builder.WebHost.UseUrls($"http://{daemonConfig.Host}:{daemonConfig.Port}");
```

This is the only change needed to make the daemon configurable. The `DaemonConfig`
defaults ensure existing configs without a `Daemon` section produce the same
`http://127.0.0.1:5199` URL as today.

### D7: Exposure mode excluded from hot-reload

SPEC-011 already lists "Network binding / exposure mode" under "What Does Not
Reload (requires restart)". The `ConfigWatcherService` must not attempt to
apply exposure mode changes during hot-reload. If the `Daemon` section changes,
the daemon logs a warning that a restart is required and continues with the
current binding.

## Risks / Trade-offs

**[Process detection is coarse]** → Checking if `tailscaled` or `cloudflared`
is running doesn't prove the tunnel is correctly configured for Netclaw's port.
The tunnel process could be running but serving a different service. Mitigation:
this is a "necessary but not sufficient" check — it catches the most common
misconfiguration (tunnel not installed/running) without coupling to
tunnel-specific APIs. Doctor checks can be enhanced later with deeper probes.

**[No auth on non-local binding]** → Until device pairing ships, an operator
could configure `Daemon.Host=0.0.0.0` and expose an unauthenticated hub. The
doctor warning flags this, and startup validation for tunnel modes provides a
safety net, but direct non-loopback binding with `ExposureMode=local` is
allowed with a warning only. Mitigation: the doctor warning is prominent, and
the device pairing change will add the actual auth gate.

**[Wizard step count increases]** → Adding the exposure mode step increases
the wizard from its current step count. This is a minor UX cost. Mitigation:
the step is single sub-step with a simple selection list — it adds seconds,
not minutes. The `IsApplicable` method could skip the step for users who
select Personal posture (where local is almost always correct), but this
optimization can be deferred.

## Migration Plan

**Forward migration**: No action required. Existing configs without a `Daemon`
section use defaults (`127.0.0.1:5199`, `local`). The `SchemaFixResolver`
does not need to insert a `Daemon` section because all properties have
defaults and the section itself is optional.

**Rollback**: Remove the `Daemon` section from `netclaw.json`. The daemon
reverts to hardcoded loopback binding. No data migration needed.

## Open Questions

1. **Should the wizard skip the exposure step for Personal posture?** Most
   personal deployments will stay local. Skipping would reduce wizard friction.
   However, personal deployments behind Tailscale Serve (for remote access from
   your own devices) are a real use case.

2. **Should tunnel process detection use `Process.GetProcessesByName()` or
   shell out to `pgrep`/`ps`?** The .NET API is cross-platform but may have
   permission limitations in containerized environments. Shell commands are
   platform-specific but more reliable in containers.
