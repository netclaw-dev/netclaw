## 1. Product code

- [ ] 1.1 Replace the body of `TurnContext.CanControlChildRunOf` with the rule in design D1, and verify that `TurnContextChildControlTests` pass
- [ ] 1.2 Make `LlmSessionActor.HasSameChildRequester` call `CanControlChildRunOf` with the request context as the instance, and verify that `ChildOwnerActorSpecs` pass
- [ ] 1.3 Keep `HasSameRequester` and `HasSameAuthority` unchanged, and verify that `TurnContextRequesterTests` and `SessionInputFlowRequesterTests` still pass

## 2. Mutation gate

- [ ] 2.1 Add a focused Stryker target for `CanControlChildRunOf` to `scripts/run-child-owner-mutations.sh` (or a script of its own), with the linked `TurnContextChildControlTests`, and verify that it kills every mutant
- [ ] 2.2 Update the `TOOLING.md` table row and the "Child Owner Gate" section with the new span, the mutant count, and the run time

## 3. Skills

- [ ] 3.1 Apply the sentences from the pull request description to `agent-coordination`, `netclaw-operations` (`references/child-runs.md`, `references/scheduling.md`), and `run-reminder`
- [ ] 3.2 Bump `metadata.version` in each edited skill and verify that the skill checks pass
- [ ] 3.3 Run `./evals/run-evals.sh` because skill content changed, and add a case for a reminder that checks a child run of the same session

## 4. Verification

- [ ] 4.1 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`
- [ ] 4.2 Run `openspec validate reminder-turn-manages-own-children --strict`
- [ ] 4.3 After merge, run `/opsx-sync` and `/opsx-archive` for this change
