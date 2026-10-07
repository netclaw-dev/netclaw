## Why

A shell call in an undeclared folder could get a "declare a project directory"
correction (`working_directory_not_declared`). The correction told the agent to
call `set_working_directory` so that a reviewed phrase could run without a
prompt. Owner decision D2 removed the unattended trust zone. Now each folder
that the agent can declare is also readable by the audience, so a reviewed
phrase there needs no declaration. The correction cannot occur for a
production shell, and its code is dead (PRD-002).

## What Changes

- Remove the project-directory correction: its type, its evaluation, the
  reviewed-safe check after a declaration, its selection, and its delivery.
- Remove the tests that reach the correction only directly.
- Keep `set_working_directory`. It still sets the project folder.
- Keep the `SetWorkingDirectory` remediation code. A file tool with no base
  folder still uses it.
- Keep the hint after a user denies a shell prompt. It is a different path.

In scope: authorization code, tests, the corpus probe, and the TA-9 text. Out
of scope: new corrections, prompt changes, and the denial hint.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-9 no longer names project advice. A new scenario
  states that a reviewed phrase in a readable, undeclared folder gets no
  project-declaration correction.

## Impact

- Code: `ToolCorrection`, `ToolCorrectionDelivery`, `ToolAccessPolicy`,
  `ReviewedSafeShellPolicy`, `ShellPolicyCoordinator`, `PathAccessPolicy`.
- Security: no authority changes. The removed code granted no authority. It
  only gave advice. The authorization corpus shows zero outcome changes.
- Operations: none. Operators never saw this correction in a production
  shell. Runbooks and system skills do not mention it.
