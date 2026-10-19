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
- [x] 5.2 Make no change to the `netclaw-operations` skill in this change (decided). The PR body lists the skill text as a follow-up. Verify with `git diff --stat feeds/skills` that it is empty.
- [x] 5.3 Run the full `Netclaw.Daemon.Tests` and `Netclaw.Cli.Tests`, `dotnet slopwatch analyze`, and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify they pass.

## 6. Review round 2 (tests exist; make them pass)

The tests of this section exist and fail on `cc55a4693`. The implementer writes product code only and edits no test.

- [x] 6.1 `HomeIdentity.Canonical`: walk the path one component at a time (design D9). Read links with `LinkTarget`, join a relative target to the resolved folder, restart with the rest of the path, and stop at 40 links with `IOException`. `Same` returns `false` when `Canonical` throws `IOException`. Keep a component that does not exist as spelled. Verify `HomeIdentityTests` and `HomeIdentityMutationTests` (link layouts, cycle, 40/41 limit, `/var` style).
- [x] 6.2 `HomeIdentity.EnvironmentServesHome`: read the target as the target would (design D8). Verify the `EnvironmentServesHome` cases of `HomeIdentityTests`, and the B1 cases of `StopProofMutationTests`, `DaemonManagerSingletonGuardTests`, and `DaemonCommandWiringTests`. `SystemdUserService` keeps using it; its tests set a `HOME=` entry for the default home.
- [x] 6.3 Endpoint: add `home` (the resolved home path of the daemon) to the 200 body; answer 400 for a `home` with NUL or one that `Path.IsPathFullyQualified` rejects; answer 409 (not 500) when `Same` cannot resolve the home; keep `SanitizeForLog` on the logged home. Verify `LifecycleEndpointRouteBuilderExtensionsTests` and `ShutdownNoticeIdentityMutationTests`. Set the exact `minimum_count` in `scripts/run-shutdown-notice-identity-mutations.sh` and in `TOOLING.md`.
- [x] 6.4 CLI notice answer: read the body by the table of the stop requirement (design D3). A missing `home` is Unconfirmed. A `pid` that is not a JSON number, and a `home` that is not a string, are Refused. Check `ValueKind` before any `TryGet*` call. Verify the added cases of `DaemonManagerSingletonGuardTests` and `StopProofMutationTests`.
- [x] 6.5 One writer for the refusal (design D5): a flag on `DaemonResult` marks it; `StopAsync` writes nothing for it; `Program.cs` writes it on stderr for `stop` and `start`; the flag passes through `UpdateDaemonStopResult.ToDaemonResult`. Add the second line of design D11 (cause by outcome, `delete <home>/netclaw.pid`, `kill <pid>`). Verify `DaemonCommandWiringTests` (the refusal tests).
- [x] 6.6 `GetStatus` and `Start` (design D12): prove the PID with the environment rule, or with the held lock file where the environment cannot be read. Without proof: `status` prints the first and second lines on stdout, exits 1, and keeps the PID file. `start` refuses on stderr, starts no process, and exits 1. Add a member to `DaemonStatus` for the exit code; `IsRunning` stays `true`. Replace the `pkill netclawd` text with the lock file text. Verify the status, start, and `pkill` tests.
- [x] 6.7 Factory reset: when the stop result is a refusal, show both lines and `Nothing was deleted`, and do not advise `systemctl --user stop netclaw.service`. Verify `InitExistingInstallViewModelTests`.
- [x] 6.8 Help text and docs: the `netclaw daemon` help names the refusal and `netclaw.pid` (test `Daemon_help_names_the_refusal_and_its_recovery`). The runbook and `SPEC-011` already hold the text; keep them in step with the code.
- [x] 6.9 Gates: run `./scripts/run-shutdown-notice-identity-mutations.sh` and `./scripts/run-stop-proof-mutations.sh`. Set the exact minimum counts in both scripts and in `TOOLING.md`. Verify that every tested mutant dies. Report a surviving mutant to the test author. A mutant that removes the link count of the walk makes a link cycle spin, and Stryker reports it as `Timeout`. Count `Timeout` as detected in the `home-match` check, as `scripts/run-exact-verb-chain-mutations.sh` does, and name the reason in a comment. A reference run (round 2 test author, scratch code, not committed) found 19 tested mutants in `shutdown-identity` and about 45 in `home-match`, all killed or timed out; the numbers of the real code can differ.
- [x] 6.10 Run the full `Netclaw.Daemon.Tests`, `Netclaw.Cli.Tests`, `Netclaw.Configuration.Tests`, and `Netclaw.Actors.MutationTests`, `dotnet slopwatch analyze`, and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify they pass.
