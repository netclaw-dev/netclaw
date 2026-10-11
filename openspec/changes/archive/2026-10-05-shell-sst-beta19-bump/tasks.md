## 1. Adopt ShellSyntaxTree 0.4.0-beta.19

- [x] 1.1 Update the package version and verify that the Security, Actors, and mutation test suites pass
- [x] 1.2 Use `MayPathnameExpand` for data operands and the exact-candidate rule, and verify the new mutation tests pass
- [x] 1.3 Apply the credential store text hints to proved values, and verify the ANSI-C review row is denied
- [x] 1.4 Add the review rows, record the brace program-word change in the ledger, and verify `check-approval-outcome-direction.py` passes

## 2. Verification

- [x] 2.1 Run the authorization corpus differential against dev and verify that each changed decision is an intended direction
