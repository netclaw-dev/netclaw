## 1. Lock the Authorization Boundary

- [x] 1.1 Add matcher tests for bare status output, substitution, redirect, and unknown path; verify the tests fail for the current false prompt.
- [x] 1.2 Add coordinator cases for a stored grant and an uncovered verb; verify candidate coverage and approval options.

## 2. Implement and Document

- [x] 2.1 Exempt only a parser-classified bare `$?` argument on a complete output command without redirects; verify focused tests pass.
- [x] 2.2 Update `docs/spec/SPEC-003-acl-policy-and-security-controls.md` with the bounded output rule; verify its link to the OpenSpec contract.

## 3. Verify and Deliver

- [x] 3.1 Run Security and Actors tests, strict OpenSpec validation, Slopwatch, and header checks; verify all pass.
- [x] 3.2 Review the focused shell mutation scope and run its gate if this boundary has a selected mutant; verify no unsafe survivor.
- [x] 3.3 Review the sanitized live case against the prior and new analyzer builds; verify the new result offers reusable grants without authority drift.
- [x] 3.4 Open a Netclaw pull request with the evidence and queue auto-merge subject to required CI; verify the queue state.
