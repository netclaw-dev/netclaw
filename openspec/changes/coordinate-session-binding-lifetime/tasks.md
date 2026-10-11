## 1. Session idle policy

- [x] 1.1 Keep the existing session idle timer, set its default to one hour, and defer idle passivation for active work.
- [x] 1.2 Keep subscriber count out of idle eligibility and preserve journaled approval recovery.
- [x] 1.3 Verify active shell-job state blocks passivation, reaped records do not, and `Processing` still disables the idle timeout.

## 2. Channel binding lifetime

- [x] 2.1 Remove independent idle stops from Slack, Discord, and Mattermost bindings.
- [x] 2.2 Keep each binding's session output subscription until committed `SessionDeactivated`; drain its pipeline and then stop.
- [x] 2.3 Keep conversation parents alive while binding children exist. Verify each parent stops after its last binding child terminates.
- [x] 2.4 Verify the Slack and Discord lifecycle tests and the three approval-recovery tests pass. Record that Mattermost has no lifecycle test.

## 3. Contract reconciliation and validation

- [x] 3.1 Update the old-contract tests for active-job reaping, subscriber veto, and the one-hour default.
- [x] 3.2 Sync the four affected main OpenSpec capabilities with this change.
- [ ] 3.3 Update the mapped operational skill to describe the session-owned channel lifetime.
- [x] 3.4 Run the full test feed and required repository checks.
