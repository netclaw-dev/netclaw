## 1. ShellSyntaxTree Foundation

- [x] 1.1 Upgrade ShellSyntaxTree to 0.2.0-alpha and adapt the existing Bash semantics to the new API without changing runtime shell selection
- [x] 1.2 Add focused compatibility tests for Bash parsing, wrapped command strings, canonical verbs, and dynamic syntax

## 2. Pipeline Security Hardening

- [x] 2.1 Make hard-deny, safe-verb, trust-zone, approval matching, and approval display evaluate every executable pipeline clause
- [x] 2.2 Add regressions for safe or approved pipeline heads followed by denied, unsafe, dynamic, or unapproved tail clauses

## 3. Canonical Runtime and Agent Context

- [x] 3.1 Add the required immutable execution environment and use it for parser selection and shell process startup
- [x] 3.2 Switch Windows execution to PowerShell 7 and fail visibly without `cmd.exe` or Windows PowerShell fallback
- [x] 3.3 Compose execution-environment inspection into `WorkingContextSnapshotProvider` with audience-aware rendering and child-run propagation
- [x] 3.4 Update the embedded operating core and versioned `netclaw-operations` skill with environment-grounded shell guidance
- [x] 3.5 Add prompt-prefix, compaction, sub-agent, prompt assembly, and behavioral eval coverage for execution context

## 4. Windows Approval and Validation

- [x] 4.1 Prove Windows directory-scoped approval persistence and runtime comparison use the same canonical representation
- [x] 4.2 Add native-platform PowerShell parser/execution tests and CI coverage for supported and missing-shell behavior
- [x] 4.3 Update operational documentation with supported shells and the PowerShell 7 prerequisite
- [ ] 4.4 Run focused suites, full tests, behavioral evals, Slopwatch, file-header verification, OpenSpec verification, and diff checks
