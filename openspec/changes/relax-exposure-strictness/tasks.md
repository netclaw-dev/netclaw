## 1. Configuration Layer

- [ ] 1.1 Add `ReverseProxy` value to `ExposureMode` enum with wire value `"reverse-proxy"`, update `ToWireValue()` and `GetRequiredProcessName()` (returns `null`)
- [ ] 1.2 Add `SkipTunnelProcessCheck` (bool, default false) and `TrustedProxies` (IReadOnlyList<string>, default empty) properties to `DaemonConfig`
- [ ] 1.3 Update `DaemonConfig.ParseExposureMode()` to accept `"reverse-proxy"` / `"reverseproxy"`
- [ ] 1.4 Update `netclaw-config.v1.schema.json`: add `"reverse-proxy"` to ExposureMode enum, add `SkipTunnelProcessCheck` boolean with default, add `TrustedProxies` array of strings with default

## 2. Startup Validation

- [ ] 2.1 Modify `ExposureModeValidationService.StartAsync`: when `GetRequiredProcessName()` returns null (reverse-proxy mode), skip process check entirely but still run auth guard
- [ ] 2.2 Modify `ExposureModeValidationService.StartAsync`: when `SkipTunnelProcessCheck` is true and process not found, log warning instead of throwing
- [ ] 2.3 Add unit tests: reverse-proxy mode starts without process check, auth guard still enforced
- [ ] 2.4 Add unit tests: SkipTunnelProcessCheck=true logs warning and continues; auth guard still enforced; flag has no effect on local mode

## 3. Forwarded Headers Middleware

- [ ] 3.1 Add `UseForwardedHeaders()` wiring in `Program.cs` before `UseAuthentication()` — activate only when `TrustedProxies` is non-empty AND mode is non-local
- [ ] 3.2 Parse `TrustedProxies` entries as `IPAddress` or `IPNetwork` into `ForwardedHeadersOptions.KnownProxies`/`KnownNetworks`, set `ForwardLimit = 1`
- [ ] 3.3 Add integration tests: trusted proxy resolves real client IP; untrusted proxy header ignored; empty TrustedProxies disables middleware; local mode disables middleware regardless

## 4. Doctor Check Updates

- [ ] 4.1 Update `ExposureModeDoctorCheck`: when mode is tunnel type + SkipTunnelProcessCheck=true + process missing → report Warning instead of Error
- [ ] 4.2 Add doctor scenario: reverse-proxy mode + loopback bind → Warning
- [ ] 4.3 Add doctor scenario: reverse-proxy mode + empty TrustedProxies → Warning
- [ ] 4.4 Add doctor scenario: reverse-proxy mode + non-loopback + TrustedProxies configured → Pass
- [ ] 4.5 Add unit tests for new doctor check scenarios

## 5. Setup Wizard

- [ ] 5.1 Add `reverse-proxy` option to `ExposureModeStepViewModel` mode selection with appropriate informational notice
- [ ] 5.2 Ensure wizard still bootstraps a paired device token when reverse-proxy is selected (same as other non-local modes)

## 6. Verification

- [ ] 6.1 Run full test suite — ensure no regressions in existing exposure mode tests
- [ ] 6.2 Run `dotnet slopwatch analyze` — no new violations
- [ ] 6.3 Run `./scripts/Add-FileHeaders.ps1 -Verify` — copyright headers present
- [ ] 6.4 Validate JSON schema accepts new config properties via `netclaw doctor`
