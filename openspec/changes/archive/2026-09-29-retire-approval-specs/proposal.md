## Why

The `tool-authorization` capability now states the testable rules of tool
authorization (TA-1 to TA-16). Five older capabilities still state the same
rules, often in older or wrong form, and nine others repeat parts of them.
Two owners for one rule let them drift. Decision D6 of the tool authorization
consolidation program retires the old capabilities so that one capability owns
the rules.

Source PRDs: PRD-002 (SEC-003, SEC-007, SEC-008, SEC-009), PRD-006 (MCP-003),
PRD-008 (reminders), PRD-009 (input adapters).

## What Changes

- Retire `tool-approval-gates`, `shell-policy-evaluator-architecture`,
  `trust-context-integrity`, `netclaw-acl`, and `netclaw-gateway-security`.
  Each removed requirement names the `tool-authorization` rule that replaces
  it, the spec that now holds it, or the reason for its removal.
- Move the non-authorization ingress rules of `netclaw-gateway-security`
  first: reminder write validation to `netclaw-scheduling`, webhook ingress to
  `inbound-webhooks`, the Mattermost callback endpoint to
  `netclaw-mattermost-socket`, and the inbound channel ACL to
  `netclaw-input-adapters`. Exposure modes are already in `daemon-exposure`.
- Move the Slack approval resolution line from `tool-approval-gates` to
  `netclaw-slack-socket`.
- Fold `skill-trust-tiers` into `skill-tools` and retire it. State the name
  collision rule: exactly one `.system` skill wins a collision in its root.
  This rule depends on PR #2249.
- Trim the authorization requirements from `netclaw-tools`, `session-cwd`,
  `session-resume`, `netclaw-session`, `netclaw-subagents`,
  `skill-execution-routing`, and `audience-context-filtering`. Correct the
  default tool timeout in `netclaw-tools` from 60 to 90 seconds.
- Add four details to `tool-authorization` so that no removed rule is lost:
  the parsed audience of a tool context (TA-1), the startup check for
  attachment configuration (TA-3), legacy log and cross-session file authority
  (TA-6), and adopted-context provenance on a consent request (TA-10).

No code changes. No behavior changes.

In scope: normative spec text. Out of scope: code, settings, and new behavior.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-1, TA-3, TA-6, and TA-10 gain the details listed above.
- `tool-approval-gates`, `shell-policy-evaluator-architecture`,
  `trust-context-integrity`, `netclaw-acl`, `netclaw-gateway-security`,
  `skill-trust-tiers`: all requirements removed; capability retired.
- `netclaw-tools`: authorization requirements removed; shell timeout corrected.
- `session-cwd`, `session-resume`, `netclaw-session`, `netclaw-subagents`,
  `audience-context-filtering`: authorization requirements removed.
- `skill-execution-routing`: routed authorization reduced to a link.
- `netclaw-scheduling`, `inbound-webhooks`, `netclaw-mattermost-socket`,
  `netclaw-input-adapters`, `netclaw-slack-socket`, `skill-tools`: moved
  requirements added.

## Impact

- `openspec/specs/`: five approval capabilities and `skill-trust-tiers` are
  deleted. Links in `TOOLING.md` and `openspec/specs/README.md` move to
  `tool-authorization`.
- Two source comments still name retired requirements
  (`MattermostActionEndpointExtensions.cs`, `SlackApprovalBlockBuilder.cs`).
  The moved requirements keep their names, so the comments stay findable. A
  code PR can update the comments.

### Security and operational impact

- Security: none at runtime. Every retired authority rule maps to a
  `tool-authorization` rule, a moved requirement, or a stated removal reason.
- Operations: none. Operators use the runbook, which already points at
  `tool-authorization`.
