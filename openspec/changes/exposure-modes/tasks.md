## 1. Configuration Types and Schema

- [x] 1.1 Add `ExposureMode` enum (`Local`, `TailscaleServe`, `TailscaleFunnel`, `CloudflareTunnel`) to `Netclaw.Configuration` with `JsonStringEnumConverter` support for kebab-case values
- [x] 1.2 Add `DaemonConfig` record to `Netclaw.Configuration` with `Host` (string, default `"127.0.0.1"`), `Port` (int, default `5199`), and `ExposureMode` (default `Local`) properties
- [x] 1.3 Add `Daemon` section to `netclaw-config.v1.schema.json` with `Host` (string), `Port` (integer), `ExposureMode` (string enum), all with defaults. Section itself is optional.
- [x] 1.4 Register `DaemonConfig` binding from `IConfiguration` section `"Daemon"` in daemon DI setup

## 2. Daemon Bind Address

- [x] 2.1 Replace hardcoded `UseUrls("http://127.0.0.1:5199")` in `Program.cs` with config-driven bind address from `DaemonConfig.Host` and `DaemonConfig.Port`
- [x] 2.2 Verify existing `DaemonApi.ResolveEndpoint()` in CLI still works correctly with the new config section (no changes expected — it reads `Daemon:Endpoint`, not `Daemon:Host`/`Daemon:Port`)

## 3. Startup Prerequisite Validation

- [x] 3.1 Add `ExposureModeValidationService : IHostedService` that reads `DaemonConfig` and validates tunnel prerequisites before the hub accepts connections
- [x] 3.2 Implement process detection for `tailscaled` (for `TailscaleServe` and `TailscaleFunnel` modes)
- [x] 3.3 Implement process detection for `cloudflared` (for `CloudflareTunnel` mode)
- [x] 3.4 On validation failure, log a descriptive error naming the missing prerequisite and throw to fail startup
- [x] 3.5 For `Local` mode, skip all tunnel validation

## 4. Doctor Check

- [x] 4.1 Add `ExposureModeDoctorCheck : IDoctorCheck` that reads `DaemonConfig` from `netclaw.json`
- [x] 4.2 Report warning when bind address is non-loopback and exposure mode is `local`
- [x] 4.3 Report error when exposure mode is non-local and tunnel process is not detected
- [x] 4.4 Report pass when mode is `local` with loopback bind or non-local with tunnel process detected
- [x] 4.5 Register `ExposureModeDoctorCheck` in `DoctorRegistrationExtensions`

## 5. Init Wizard Step

- [x] 5.1 Add `DaemonConfigSection` record to `WizardConfigBuilder` typed sections
- [x] 5.2 Add `ExposureModeStepViewModel : IWizardStepViewModel` with `SelectionListNode` for the four exposure modes, `local` pre-selected
- [x] 5.3 Add `ExposureModeStepView` Termina rendering with mode descriptions and risk-level indicators
- [x] 5.4 Display high-risk warning panel with explicit confirmation for `tailscale-funnel` and `cloudflare-tunnel` selections
- [x] 5.5 Display informational notice for `tailscale-serve` selection
- [x] 5.6 Wire `ContributeConfig` to write `Daemon` section only when non-default mode is selected (local = omit section, defaults apply)
- [x] 5.7 Insert step into `InitWizardViewModel` step sequence after security posture, before Slack

## 6. Hot-Reload Exclusion

- [x] 6.1 Ensure `ConfigWatcherService` / `RestartCoordinator` does not apply `Daemon` section changes during hot-reload — log a warning that restart is required if the section changed

## 7. Tests

- [x] 7.1 Unit test `DaemonConfig` deserialization from JSON with kebab-case enum values, defaults, and missing section
- [x] 7.2 Unit test `ExposureModeValidationService` — local mode skips validation, non-local mode with missing process throws
- [x] 7.3 Unit test `ExposureModeDoctorCheck` — non-loopback warning, missing tunnel error, healthy tunnel pass
- [x] 7.4 Unit test `ExposureModeStepViewModel` — contributes correct config for each mode, omits section for local
- [x] 7.5 Schema validation test — verify `netclaw-config.v1.schema.json` accepts valid `Daemon` section, rejects invalid enum values, accepts missing section

## 8. Spec and Doc Updates

- [x] 8.1 Update `SPEC-006` to mark exposure mode configuration as implemented
- [x] 8.2 Update `SPEC-011` daemon architecture to reference `DaemonConfig` instead of hardcoded URL
