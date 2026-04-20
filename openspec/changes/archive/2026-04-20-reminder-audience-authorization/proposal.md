## Why

Reminder definitions currently risk being created with an execution audience that is broader than the authority of the channel, session, or operator that minted them. That violates PRD-002's default-deny, fail-closed security posture and PRD-008's requirement that scheduled work execute with policy-bounded grants.

## What Changes

- Require reminder creation, import, and administrative write paths to validate that the requested reminder audience does not exceed the creator's current source audience / authority
- Specify that conversational and tool-created reminders inherit the creating channel/session audience when `audience` is omitted, rather than falling back to a deployment default
- Allow reminders to be minted with a lower audience than the creator currently holds
- Require REST, admin, CLI, and import surfaces to reject invalid or over-privileged reminder definitions with clear fail-closed errors
- Clarify that execution may trust the stored reminder audience because minting-time audience validation is mandatory
- Add testable scenarios covering omitted audience inheritance, privileged audience rejection, lower-audience creation, and server-side validation across non-conversational write paths

In scope for MVP: reminder audience validation, inheritance rules, and server-side rejection semantics.
Out of scope for this change: new audience types, changes to the audience model ordering, or broader reminder execution/runtime refactors unrelated to minting validation.

## Capabilities

### New Capabilities

_None_ - this change tightens existing scheduling and security behavior.

### Modified Capabilities

- `netclaw-scheduling`: Require reminder definitions to inherit or declare an execution audience that is less than or equal to the creating authority, and treat stored audience as trusted at execution time.
- `netclaw-acl`: Define audience comparison and rejection rules for reminder minting and import/update paths, including the rule that lowering audience is always allowed.
- `netclaw-gateway-security`: Require reminder write surfaces to fail closed with clear errors when reminder audience is invalid, omitted in the wrong context, or exceeds the creator's authority.

## Impact

- **Reminder write paths**: conversational scheduling flows, tool-driven reminder creation, REST/admin APIs, CLI management commands, and import pipelines must all use the same audience validation rules.
- **Validation and errors**: reminder definition validation gains audience ordering checks and explicit rejection messages for over-privileged requests.
- **Execution semantics**: reminder execution no longer needs to recompute creator authority at dispatch time because the stored audience is guaranteed to have been validated when the reminder was created or imported.
- **Tests**: add coverage for conversation/session inheritance, lower-audience minting, invalid audience values, and over-privileged reminder rejection across server-side entry points.
