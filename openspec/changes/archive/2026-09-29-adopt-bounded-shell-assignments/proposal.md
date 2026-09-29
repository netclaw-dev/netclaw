## Why

Static shell assignments currently make complete commands one-time approvals.
ShellSyntaxTree beta.5 supplies bounded assignment facts that can reduce these prompts without hiding environment effects.

## What Changes

- Netclaw will adopt ShellSyntaxTree `0.4.0-beta.5` after the public package exists.
- Bash analysis will use the fresh noninteractive parser contract that the launch environment enforces.
- PowerShell analysis will use the existing isolated no-profile contract that the launch environment enforces.
- Netclaw will consume assignment facts without parsing executable-specific environment variable rules.
- Environment-changing assignments will require an exact reusable assignment constraint.
- Stored constraints will contain no authored assignment value or local path.
- Unknown, dynamic, provider-scoped, hidden-execution, or unsupported assignments will remain one-time approvals.
- Tests and evidence will use synthetic names, values, paths, and commands.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `canonical-shell-execution`: Bind parser initial-state assertions to the exact child process launch contract.
- `tool-approval-gates`: Define reusable approval identity and fail-closed behavior for bounded assignment facts.

## Impact

This change supports PRD-002 and PRD-006.
It affects shell environment creation, approval candidates, typed approval storage, matching, serialization, runbooks, and the operations skill.

## Security and operational impact

ShellSyntaxTree remains the syntax authority.
Netclaw remains the authority owner.
The change will not store raw assignment values in the approval file.
All hard-deny, path, audience, repository, and launch checks will continue for each call.

## Scope

The MVP scope covers only assignment forms that ShellSyntaxTree beta.5 marks complete.
It excludes executable-specific environment semantics, dynamic assignment values, provider assignments, and hidden execution.
