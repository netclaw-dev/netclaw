## 1. Requester check and drain rule

- [x] 1.1 Add `TurnContext.HasSameRequester` and `HasSameAuthority`; verify `SessionInputFlowRequesterTests` pass
- [x] 1.2 Split buffered batches by requester and adopt the recorded authority of the first input; verify `SessionInputFlowAccordantTests` and `Buffered_messages_are_batched_into_follow_up_LLM_call` pass
- [x] 1.3 Derive trust, crash context, and prompt audience for an adopted authority; verify `AdoptedAuthorityTrustTests` passes

## 2. Automated input and open approvals

- [x] 2.1 Return `WaitForApproval` for automated input while an approval is open; verify `NewInputAbandonmentPlanTests` and `RecoveredApprovalAbandonmentTests` pass
- [x] 2.2 Run waiting input as its own turn after the approval closes; verify `AutomatedInputApprovalWaitTests` and `WaitingInputStartsAfterApprovalTests` pass

## 3. Recovery and restart

- [x] 3.1 Rebuild waiting input from pending `InputAdmitted` records when an approval stays parked; verify `WaitingReminderRestartTests` pass
- [x] 3.2 Let the session passivate while input waits; verify `WaitingReminderPassivationTests` passes
- [x] 3.3 Run a waiting input once after idle abandonment of a half-answered batch; verify `WaitingReminderIdleAbandonmentTests` pass
- [x] 3.4 Keep a live background job alive after the deferred idle passivation; verify `WaitingReminderIdleAbandonmentBackgroundJobTests` pass
- [x] 3.5 Collapse duplicate copies of one input after an actor restart; verify `WaitingInputActorRestartTests` passes

## 4. Gates and docs

- [x] 4.1 Add the focused Stryker gate `scripts/run-input-one-authority-mutations.sh` and verify it kills 10 of 10 mutants
- [x] 4.2 Update `docs/architecture/tool-authorization.md`, `docs/spec/GLOSSARY.md`, `TOOLING.md`, and `docs/research/session-contract-test-plan.md`
- [x] 4.3 Run the full Actors and Daemon test suites, `dotnet slopwatch analyze`, and the header check; verify 0 failures and 0 issues

## 5. Open items

- [x] 5.1 After the stack rebase, change `docs/prd/PRD-001-netclaw-mvp.md` FR-017. Replace "Ordinary user inputs SHALL retain their existing ordered batch and authority." with "Ordinary user inputs SHALL run in ordered batches of one requester, each under its own recorded authority." Verify with `grep -n "FR-017" docs/prd/PRD-001-netclaw-mvp.md`.
- [ ] 5.2 After the stack rebase, count only parent approvals as open approvals in `PlanNewInputAbandonment`, `HasParkedApprovals`, and `PendingDiagnosticCount`; verify a child approval does not hold a reminder
- [ ] 5.3 Sync the delta specs into `openspec/specs/` with `/opsx-sync`, then archive the change
