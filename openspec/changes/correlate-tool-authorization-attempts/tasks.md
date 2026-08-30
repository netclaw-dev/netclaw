## 1. Correlation Contract

- [x] 1.1 Add the internal authorization-attempt value object and per-call execution state; verify construction, formatting, parsing, and uniqueness with unit tests
- [x] 1.2 Add the glossary definition and update the implementation plan; verify all change artifacts use the shared term consistently

## 2. Parent Session Lifecycle

- [x] 2.1 Carry one identifier from parent tool-call start through policy, correction, prompt, live retry, and result; verify allow, correction, approve, deny, and concurrent-call tests
- [x] 2.2 Persist the identifier additively with pending approval events and restore it during cold redrive; verify current and legacy protobuf round trips plus recovery tests

## 3. Sub-agent Lifecycle

- [x] 3.1 Carry one identifier through child execution, parent approval bridge, approved retry, and child result; verify a deterministic bridged-approval test
- [x] 3.2 Emit the shared structured fields from child policy and actor boundaries; verify captured logs contain attempt, call, session, and sub-session identifiers where available

## 4. Verification

- [x] 4.1 Run strict OpenSpec validation and verify proposal, design, specs, and tasks are coherent
- [x] 4.2 Run focused authorization, session persistence, pipeline, and sub-agent tests and verify all pass
- [x] 4.3 Run repository quality gates: Release build, full Release tests, header verification, diff check, and Slopwatch; verify each command exits successfully
- [x] 4.4 Confirm model-facing tool schemas and results are unchanged; record hosted model evals as not applicable because the change is internal telemetry only
