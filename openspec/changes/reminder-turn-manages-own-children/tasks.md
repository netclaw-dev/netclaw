## 1. Product code

- [x] 1.1 Replace the body of `TurnContext.CanControlChildRunOf` with the symmetric rule in design D1, and verify that `TurnContextChildControlTests` pass
- [x] 1.2 Make `LlmSessionActor.HasSameChildRequester` call `CanControlChildRunOf` with the request context as the instance, and verify that `ChildOwnerActorSpecs` pass
- [x] 1.3 Keep `HasSameRequester` and `HasSameAuthority` unchanged, and verify that `TurnContextRequesterTests` and `SessionInputFlowRequesterTests` still pass

## 2. Mutation gate

- [x] 2.1 Add a focused Stryker target for `CanControlChildRunOf` to `scripts/run-child-owner-mutations.sh` (or a script of its own), with the linked `TurnContextChildControlTests`, and verify that it kills every mutant
- [x] 2.2 Update the `TOOLING.md` table row and the "Child Owner Gate" section with the new span, the mutant count, and the run time

## 3. Skills

- [x] 3.1 Apply the sentences from the pull request description to `agent-coordination`, `netclaw-operations` (`references/child-runs.md`, `references/scheduling.md`), and `run-reminder`
- [x] 3.2 Bump `metadata.version` in each edited skill and verify that the skill checks pass
- [ ] 3.3 Run `./evals/run-evals.sh` because skill content changed, and add a case for a reminder that checks a child run of the same session

## 4. Verification

- [x] 4.1 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`
- [x] 4.2 Run `openspec validate reminder-turn-manages-own-children --strict`
- [ ] 4.3 After merge, run `/opsx-sync` and `/opsx-archive` for this change

## 5. Review fixes (PR #2550)

- [x] 5.1 Make `IsReminderTurn` require the transport authenticity `LocalProcess`, so that a webhook turn with the source kind `reminder` is not a reminder turn, and verify that `TurnContextChildControlTests` and `ChildOwnerActorSpecs` pass
- [x] 5.2 Update the expected mutant count in `scripts/run-child-control-mutations.sh` and `TOOLING.md`, and verify that the gate kills every mutant
- [x] 5.3 State in `agent-coordination` that the turn needs the same audience, boundary, and channel type
