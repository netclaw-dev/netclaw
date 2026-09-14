## 1. Correction cutover

- [x] 1.1 Move shell correction collection into the coordinator. Verify project, native, temporary, Auto, and denial cases.
- [x] 1.2 Extend common delivery and delete caller selection branches. Verify parent/child parity, capabilities, retries, and recovery.
- [x] 1.3 Update the runbook and operational skill. Validate the spec and run applicable evals.

## 2. Integrated proof and review

- [x] 2.1 Run focused tests, the actor suite, Slopwatch, and headers. Record exact results and limits.
- [x] 2.2 Open a draft PR with the change and evidence. Request review before merge.

## Evidence

- Draft PR: https://github.com/netclaw-dev/netclaw/pull/2117. Review precedes merge.
- Base: `77f825a38f7bf260d54f3579bd9758223d39ca0d`.
- Tested code: `075898c5d711b68800856c87a8861a3bb44f9bbb`, Linux, .NET SDK 10.0.400.
- Focused tests: 560 passed before the final whitespace and comment pass.
- Full actor suite after that pass: 3,823 passed, six platform cases skipped, zero failures.
- Skips cover native Windows shell/path cases and the macOS temporary-root alias.
- Slopwatch, copyright headers, strict OpenSpec validation, and diff checks passed.
- OpenCover: collection method line coverage 97.29%; branch coverage 97.36%.
- Delivery factory line coverage 92.85%; branch coverage 73.52%.
- ReportGenerator flags complexity hotspots. It emits no CRAP score column for this report.
- These coverage figures do not prove process containment or project-wide simplification.

Full actor command:

```sh
dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj --no-restore \
  --test-adapter-path "$NUGET_PACKAGES/coverlet.collector/10.0.1/build/net10.0" \
  --collect 'XPlat Code Coverage' --settings coverlet.runsettings \
  --results-directory /tmp/netclaw-cutover-coverage --verbosity quiet \
  --logger 'trx;LogFileName=actors.trx'
```

The adapter path names the local cached collector; another host must use its corresponding package path.

Task 1.3 remains open: `./evals/run-evals.sh` exits before execution because no provider target is configured.
Required variables are `NETCLAW_EVAL_PROVIDER_TYPE`, `NETCLAW_EVAL_PROVIDER_ENDPOINT`, and `NETCLAW_EVAL_MODEL_ID`.
The operator must supply the target. No endpoint or credential is stored in this change.
Cross-platform CI and native smoke remain pending at this checkpoint. No merge or rollout occurred.
