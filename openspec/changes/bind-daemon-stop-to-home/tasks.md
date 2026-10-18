## 1. Shared home comparison

- [x] 1.1 Add `src/Netclaw.Configuration/HomeIdentity.cs` with a canonical path (it resolves the links of every path component, so `/var` and `/tmp` on macOS work), a same-home comparison, and `ProcessServesHome(pid, home)`. Move `ProcessServesHome` and `ResolveLinks` out of `SystemdUserService` and make it use the helper. `ProcessServesHome` returns `false` when the environment of the process cannot be read, so the caller needs no platform check. Wrap the three members in `// home-match:begin` and `// home-match:end`. Verify `SystemdUserServiceTests` and the symlink cases of `DaemonCommandWiringTests` pass.

## 2. Daemon endpoint

- [x] 2.1 Make `pid` and `home` query values of `POST /api/lifecycle/shutdown`. Return 400 for a missing or malformed value, and 409 plus one Warning log (both homes) for a mismatch. Send no alert and write no "stopping" line in these cases. Wrap the identity checks in the markers `// shutdown-identity:begin` and `// shutdown-identity:end`. Inject `ILogger<T>` for the Warning; do not write a literal logger category. Keep the 200 and the `daemon.stopping` alert in the endpoint (the move to the SIGTERM drain is a separate follow-up). Verify `LifecycleEndpointRouteBuilderExtensionsTests` passes.
- [x] 2.2 Run `./scripts/run-shutdown-notice-identity-mutations.sh`, set `minimum_count` to the exact count of tested mutants, and verify that all of them die.

## 3. CLI stop

- [x] 3.1 Send the notice to `DaemonApi.ResolveLocalControlEndpoint` with `pid` and the canonical home. Accept 200 only when the body PID equals the PID file PID. Verify `DaemonManagerSingletonGuardTests` passes.
- [x] 3.2 Refuse without proof: no signal, keep the PID file, write `Nothing was stopped. PID <pid> is not shown to serve <home>.` to stderr, exit code 1. Use the environment proof (`HomeIdentity.ProcessServesHome`) only when the daemon does not answer. Write only the refusal to stderr; do not change how `WriteDaemonResult` prints other failures. Verify the `daemon stop` tests of `DaemonCommandWiringTests` and the reset test of `InitExistingInstallViewModelTests` pass.

- [x] 3.3 Mark the proof for the gate in `DaemonManager.cs`: `// stale-pid:begin`/`end` around the PID-file branch of `TryGetRunningPid`, `// stop-notice:begin`/`end` around the method that sends the notice and reads the answer, and `// stop-proof:begin`/`end` around the decision in `StopAsync`. Keep `OperatingSystem` checks out of these spans (they give a mutant that no test on Linux can kill). Run `./scripts/run-stop-proof-mutations.sh`, set the exact minimum counts in the script, and verify that all mutants die.

## 4. CLI start

- [x] 4.1 Set `NETCLAW_HOME` to the manager home in the start info of `DaemonManager.Start`. Verify `DaemonManagerStartHomeTests` passes.

## 5. Gates and docs

- [x] 5.1 Check that the two gate rows in `TOOLING.md` and the gate names in `.github/workflows/pr_validation.yml` (`shutdown-notice-identity` in group `verb-chain`, `stop-proof` in group `guard-and-authority`) match the final mutant counts. Verify the CI job `Mutation Gates` runs both.
- [x] 5.2 Make no change to the `netclaw-operations` skill in this change (decided). Verify with `git diff --stat feeds/skills` that it is empty.
- [x] 5.3 Run the full `Netclaw.Daemon.Tests` and `Netclaw.Cli.Tests`, `dotnet slopwatch analyze`, and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify they pass.
