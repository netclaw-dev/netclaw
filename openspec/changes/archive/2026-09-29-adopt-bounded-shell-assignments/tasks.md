## 1. Parser and Process Contract

- [x] 1.1 Update ShellSyntaxTree to `0.4.0-beta.5` and verify restore uses the public package.
- [x] 1.2 Bind Bash analysis to the sanitized launch contract and verify a missing condition returns unknown state.
- [x] 1.3 Bind PowerShell analysis to the isolated no-profile launch contract and verify its fixed arguments match.
- [x] 1.4 Recheck the parser mode before process start and verify a changed launch contract blocks execution.

## 2. Assignment Approval Identity

- [x] 2.1 Add `ApprovalAssignmentDigest` validation and verify malformed or noncanonical values fail closed.
- [x] 2.2 Calculate the digest from typed assignment facts and verify order, shell, scope, name, and value affect it.
- [x] 2.3 Add the digest to approval candidates and verify incomplete assignment facts retain one-time approval.
- [x] 2.4 Compare the digest before safe-verb and scope reuse, and verify unqualified grants cannot authorize qualified candidates.

## 3. Authority Transport and Persistence

- [x] 3.1 Carry the digest through the parent bridge and pending state, then verify recovery preserves it.
- [x] 3.2 Validate the prompt response against pending state and verify a changed digest cannot create a grant.
- [x] 3.3 Add `assignmentDigest` to token-prefix entries and verify invalid store forms make the complete store unavailable.
- [x] 3.4 Include the digest in equality, list, revoke, and duplicate rules, then verify distinct values remain distinct.
- [x] 3.5 Verify no new journal or approval field stores the assignment name, authored value, or effective scalar.
- [x] 3.6 Use versioned reusable option keys and verify an older runtime denies them after rollback.
- [x] 3.7 Verify a resolved reusable approval redrives only its assignment-qualified call once.

## 4. Operator Guidance and Verification

- [x] 4.1 Update the approval runbook and operations skill, then verify the skill version increases.
- [x] 4.2 Add sanitized Bash and PowerShell regressions, then verify supported forms reuse only exact qualified grants.
- [x] 4.3 Add strict negative tests and verify dynamic, hidden, provider, and unsupported forms remain one-time.
- [x] 4.4 Run focused mutation tests for digest creation, matching, transport, persistence, and launch checks.
- [ ] 4.5 Run Slopwatch, copyright, strict OpenSpec, evaluation, and native smoke gates, then retain their results.
- [x] 4.6 Audit changed fixtures and artifacts for PII, then verify the audit finds no live path, user, or session value.
