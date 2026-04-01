## 1. Configuration Types and Schema

- [ ] 1.1 Add `ExposureMode` enum (`Local`, `TailscaleServe`, `TailscaleFunnel`, `CloudflareTunnel`) to `Netclaw.Configuration` with `JsonStringEnumConverter` support for kebab-case values
- [ ] 1.2 Add `DaemonConfig` record to `Netclaw.Configuration` with `Host` (string, default `"127.0.0.1"`), `Port` (int, default `5199`), and `ExposureMode` (default `Local`) properties
- [ ] 1.3 Add `Daemon` section to `netclaw-config.v1.schema.json` with `Host` (string), `Port` (integer), `ExposureMode` (string enum), all with defaults. Section itself is optional.
- [ ] 1.4 Register `DaemonConfig` binding from `IConfiguration` section `"Daemon"` in daemon DI setup

## 2. Daemon Bind Address

- [ ] 2.1 Replace hardcoded `UseUrls("http://127.0.0.1:5199")` in `Program.cs` with config-driven bind address from `DaemonConfig.Host` and `DaemonConfig.Port`
- [ ] 2.2 Verify existing `DaemonApi.ResolveEndpoint()` in CLI still works correctly with the new config section (no changes expected — it reads `Daemon:Endpoint`, not `Daemon:Host`/`Daemon:Port`)

## 3. Startup Prerequisite Validation

- [ ] 3.1 Add `ExposureModeValidationService : IHostedService` that reads `DaemonConfig` and validates tunnel prerequisites before the hub accepts connections
- [ ] 3.2 Implement process detection for `tailscaled` (for `TailscaleServe` and `TailscaleFunnel` modes)
- [ ] 3.3 Implement process detection for `cloudflared` (for `CloudflareTunnel` mode)
- [ ] 3.4 On validation failure, log a descriptive error naming the missing prerequisite and throw to fail startup
- [ ] 3.5 For `Local` mode, skip all tunnel validation

## 4. Doctor Check

- [ ] 4.1 Add `ExposureModeDoctorCheck : IDoctorCheck` that reads `DaemonConfig` from `netclaw.json`
- [ ] 4.2 Report warning when bind address is non-loopback and exposure mode is `local`
- [ ] 4.3 Report error when exposure mode is non-local and tunnel process is not detected
- [ ] 4.4 Report pass when mode is `local` with loopback bind or non-local with tunnel process detected
- [ ] 4.5 Register `ExposureModeDoctorCheck` in `DoctorRegistrationExtensions`

## 5. Init Wizard Step

- [ ] 5.1 Add `DaemonConfigSection` record to `WizardConfigBuilder` typed sections
- [ ] 5.2 Add `ExposureModeStepViewModel : IWizardStepViewModel` with `SelectionListNode` for the four exposure modes, `local` pre-selected
- [ ] 5.3 Add `ExposureModeStepView` Termina rendering with mode descriptions and risk-level indicators
- [ ] 5.4 Display high-risk warning panel with explicit confirmation for `tailscale-funnel` and `cloudflare-tunnel` selections
- [ ] 5.5 Display informational notice for `tailscale-serve` selection
- [ ] 5.6 Wire `ContributeConfig` to write `Daemon` section only when non-default mode is selected (local = omit section, defaults apply)
- [ ] 5.7 Insert step into `InitWizardViewModel` step sequence after security posture, before Slack

## 6. Hot-Reload Exclusion

- [ ] 6.1 Ensure `ConfigWatcherService` / `RestartCoordinator` does not apply `Daemon` section changes during hot-reload — log a warning that restart is required if the section changed

## 7. Tests

- [ ] 7.1 Unit test `DaemonConfig` deserialization from JSON with kebab-case enum values, defaults, and missing section
- [ ] 7.2 Unit test `ExposureModeValidationService` — local mode skips validation, non-local mode with missing process throws
- [ ] 7.3 Unit test `ExposureModeDoctorCheck` — non-loopback warning, missing tunnel error, healthy tunnel pass
- [ ] 7.4 Unit test `ExposureModeStepViewModel` — contributes correct config for each mode, omits section for local
- [ ] 7.5 Schema validation test — verify `netclaw-config.v1.schema.json` accepts valid `Daemon` section, rejects invalid enum values, accepts missing section

## 8. Spec and Doc Updates

- [ ] 8.1 Update `SPEC-006` to mark exposure mode configuration as implemented
- [ ] 8.2 Update `SPEC-011` daemon architecture to reference `DaemonConfig` instead of hardcoded URL
