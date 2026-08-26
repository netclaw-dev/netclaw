## Why

This proposal uses the
[Netclaw engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

Recent tool-friction evidence shows agents still copy files into session scratch before attaching them and sometimes type a known Netclaw tool name into `shell_execute`. The first pattern wastes work and can trigger approval; the second turns a recoverable tool-choice mistake into a shell approval prompt. This change advances PRD-001 tool execution and PRD-002 least-authority behavior by making the intended structured path the easiest path.

## What Changes

- Add policy-exposed `attach_file` to the initial parent-session core tool set and state that it accepts an authorized source path directly; the agent does not need to copy the file into session scratch first.
- Before shell approval or execution, recognize a complete static shell
  analysis containing an authored command occurrence whose exact executable
  token names a policy-visible first-party Netclaw tool.
- Return the closed `UseNativeTool` remediation code with a
  `NativeToolSuggested` correction fact. Carry schema exposure as a separate
  actor-local fact.
- Keep dynamic or unresolved executable identities, unknown names, hidden tools, MCP tools, and policy-denied tools on the existing shell path. Arguments and surrounding static shell structure do not change the exact executable-name test and are never interpreted as native-tool arguments.
- Preserve all ordinary tool authorization: remediation and schema exposure grant no execution authority and create no approval grant.
- Add deterministic parent/child regressions, PII-free fixture coverage, and hosted before/after behavioral evidence.

### In scope

- Parent and child session tool execution.
- First-party Netclaw tools already allowed by the active audience policy.
- Parent-session Core exposure and guidance for `attach_file`; child exposure remains denied until an internal attachment handoff exists.

### Out of scope

- Parsing executable-specific command-line syntax.
- Inferring intended tools from aliases, fuzzy matches, shell arguments, or command output.
- Automatically executing or retrying the native tool.
- Changing MCP discovery, stored approvals, public APIs, durable messages, or configuration formats.
- Adding attachment fields to the public `SubAgentResult` contract.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `progressive-tool-disclosure`: Add `attach_file` to the policy-filtered parent-session core, keep it unavailable to children until an attachment handoff exists, and let one correction expose one deferred first-party schema.
- `tool-approval-gates`: Prevent an exact native-tool shell mistake from reaching approval or execution while preserving all other shell behavior.
- `netclaw-tools`: Clarify that `attach_file` accepts an authorized source path directly and performs its own safe session copy.

## Impact

- Affected runtime areas: core tool registration, shell preflight, parent and child tool-result handling, actor-local tool exposure, and attach-file guidance.
- Public API and durable impact: none; the correction and activation facts remain internal and call-local.
- Security impact: reduces shell authority requests without granting native-tool authority. Hidden or denied tool names are never disclosed, and every eventual native call still uses normal policy and approval checks.
- Operational impact: the parent model-visible core grows by one small first-party schema. Child exposure stays unchanged. Behavioral evals and schema-footprint evidence must be refreshed before delivery.
