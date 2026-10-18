## 1. Shared home comparison

- [ ] 1.1 Add one canonical-path and same-home helper in `Netclaw.Configuration` and make `SystemdUserService` and its tests use it. Verify `SystemdUserServiceTests` and the symlink cases of `DaemonCommandWiringTests` pass.

## 2. Daemon endpoint

- [ ] 2.1 Make `pid` and `home` query values of `POST /api/lifecycle/shutdown`. Return 400 for a missing or malformed value, and 409 plus one Warning log (both homes) for a mismatch. Send no alert and write no "stopping" line in these cases. Wrap the identity checks in the markers `// shutdown-identity:begin` and `// shutdown-identity:end`. Verify `LifecycleEndpointRouteBuilderExtensionsTests` passes.
- [ ] 2.2 Run `./scripts/run-shutdown-notice-identity-mutations.sh`, set `minimum_count` to the exact count of tested mutants, and verify that all of them die.

## 3. CLI stop

- [ ] 3.1 Send the notice to `DaemonApi.ResolveLocalControlEndpoint` with `pid` and the canonical home. Accept 200 only when the body PID equals the PID file PID. Verify `DaemonManagerSingletonGuardTests` passes.
- [ ] 3.2 Refuse without proof: no signal, keep the PID file, write `Nothing was stopped. PID <pid> is not shown to serve <home>.` to stderr, exit code 1. Use the Linux environment proof only when the daemon does not answer. Verify the `daemon stop` tests of `DaemonCommandWiringTests` and the reset test of `InitExistingInstallViewModelTests` pass.

## 4. CLI start

- [ ] 4.1 Set `NETCLAW_HOME` to the manager home in the start info of `DaemonManager.Start`. Verify `DaemonManagerStartHomeTests` passes.

## 5. Gates and docs

- [ ] 5.1 Add the gate row to `TOOLING.md` and the gate name to a group in `.github/workflows/pr_validation.yml`. Verify the CI job `Mutation Gates` runs it.
- [ ] 5.2 Decide whether the `netclaw-operations` skill needs the refusal text. If it does, bump its version and run `./evals/run-evals.sh`.
- [ ] 5.3 Run the full `Netclaw.Daemon.Tests` and `Netclaw.Cli.Tests`, `dotnet slopwatch analyze`, and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify they pass.
