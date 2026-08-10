## 1. Dependency and wrapper proof

- [x] 1.1 Update the central ShellSyntaxTree version to `0.3.0-alpha.2`.
- [x] 1.2 Add the exact POSIX argv proof for `pwsh`, `-NoProfile`, `-NonInteractive`, `-Command`, and one quoted static payload.
- [x] 1.3 Compare `pwsh` case-sensitively, compare its three option names case-insensitively, and reject every other host spelling, host option, flag order, dynamic or stdin payload, prefix wrapper, outer redirect, trailing argument, and Windows wrapper.
- [x] 1.4 Retain the outer `pwsh` occurrence so inherited Bash command resolution cannot hide behind safe children.

## 2. Shared child occurrence analysis

- [x] 2.1 Parse proved payloads with `PwshParser` in `Unknown` initial-state mode.
- [x] 2.2 Route hard-deny, protected-path, and approval matching through the same complete child occurrence list.
- [x] 2.3 Keep unknown identities, values, paths, redirects, execution regions, and command-resolution changes strict.

## 3. Approval review matrix

- [x] 3.1 Add composed host, safe-child, and stored-child approval cases for complete PowerShell commands.
- [x] 3.2 Add prompt cases for incomplete wrappers, `powershell`, `-WorkingDirectory`, dynamic values, unknown receivers, and command-resolution changes.
- [x] 3.3 Add deny and prompt cases for executable script-block bodies.
- [x] 3.4 Add a deny case for a protected path exposed only after outer decoding.
- [x] 3.5 Add exported-function and `BASH_ENV` cases that require outer-host approval.
- [x] 3.6 Update and inspect the review-table snapshot.
- [x] 3.7 Add a Windows-only matcher regression that rejects PowerShell host approval reuse.

## 4. Verification and tracking

- [x] 4.1 Run focused Netclaw.Security tests and the actor approval matrix.
- [x] 4.2 Run Slopwatch, header verification, and strict OpenSpec validation.
- [x] 4.3 Run the full solution test suite on the final diff.
- [x] 4.4 Update `IMPLEMENTATION_PLAN.md` with the delivered PowerShell evidence.
