## Why

Netclaw now derives repository approval eligibility from the request working directory.
This rule omits the repository choice when all shell candidates target one registered repository from another directory.

## What Changes

- Netclaw will derive repository scope from each grant-bearing candidate's effective directory.
- Netclaw will require all grant-bearing candidates to resolve under one canonical Git common directory.
- Netclaw will recheck candidate repository facts before it creates or reuses a grant.
- Netclaw will keep unknown, mixed, external, linked, moved, and forged scopes under normal approval.
- Tests and guidance will use synthetic paths and repository names.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-approval-gates`: Define repository approval eligibility from candidate scopes instead of the request working directory.

## Impact

This change affects `ToolAccessPolicy`, `ApprovalBucketBuilder`, `GitRepositoryApprovalScope`, repository grant reuse, tests, and the operations skill.
The change supports PRD-002 and PRD-006.

## Security and operational impact

Netclaw will use only structured candidate facts from ShellSyntaxTree.
It will not inspect raw command text for repository identity.
Every grant-bearing candidate must pass Git registration, path, link, audience, hard-deny, and protected-path checks.

## Scope

This change covers repository choice, grant creation, and grant reuse for complete reusable shell candidates.
It does not change folder grants, global grants, shell parsing, path policy, or user approval decisions.
