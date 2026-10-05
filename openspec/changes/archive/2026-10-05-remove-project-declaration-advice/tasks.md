## 1. Proof

- [x] 1.1 Probe the real authorizer on dev for Bash and PowerShell hosts, and verify that only PowerShell on a Linux host reaches the correction
- [x] 1.2 Run the Actors suite with a tripwire, and verify that it never delivers the correction

## 2. Removal

- [x] 2.1 Remove the correction type, the delivery case, the policy evaluation, the reviewed-safe check, and the coordinator selection, and verify that the solution builds
- [x] 2.2 Remove the dead `EvaluateReviewedShellPath` parameters, and verify that the path-access mutation gate still kills 4 of 4
- [x] 2.3 Remove the direct tests and the harness mapping, and update the corpus probe, and verify that the Actors suite passes
- [x] 2.4 Move the shell-command-analysis gate anchor to `IsReviewedDiagnostic(`, and verify the expected counts

## 3. Verification

- [x] 3.1 Run the Security, Configuration, Actors, Actors.MutationTests, and Daemon suites, and verify that they pass
- [x] 3.2 Run the authorization corpus against dev, and verify zero outcome changes
- [x] 3.3 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`, and verify that both pass
- [x] 3.4 Sync the TA-9 delta into `openspec/specs/tool-authorization/spec.md`
