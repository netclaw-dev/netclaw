## 1. Session actor

- [x] 1.1 Add `ErrorCategory.ModelBehavior` and use it for the text-only violation and repeated empty replies; verify the two updated Actors tests pass.

## 2. Headless CLI

- [x] 2.1 Print the stderr token, write the `TURN_FAILED` record, and add the JSON keys; verify `HeadlessTurnErrorCategoryTests` pass.
- [x] 2.2 Exit 1 with `kind=Cancelled` on cancel, and set the exit code before `StopApplication` on each terminal path; verify the cancel test passes.
- [x] 2.3 Drive each failure path through the real session actor; verify `HeadlessTurnErrorCategoryEndToEndTests` pass.

## 3. Documentation

- [x] 3.1 Document the output in `SPEC-004-cli-contract.md` and the `netclaw-operations` skill; verify the skill version is bumped.
