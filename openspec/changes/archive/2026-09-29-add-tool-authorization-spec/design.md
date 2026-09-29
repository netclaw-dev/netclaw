## Context

Tool authorization rules are spread across `tool-approval-gates` (2,681
lines), `shell-policy-evaluator-architecture`, `trust-context-integrity`,
`netclaw-acl`, `netclaw-gateway-security`, and parts of nine other specs. The
tool authorization consolidation program (decision D6, amended September 29)
asks for two artifacts with separate jobs:

- `docs/architecture/tool-authorization.md` explains the design for people.
- `openspec/specs/tool-authorization/spec.md` states the testable rules.

This change adds both. It changes no code and no runtime behavior.

## Goals / Non-Goals

**Goals:**

- One capability that states the current, verified rules, with stable IDs
  (TA-1 to TA-16) that the architecture document can link to.
- An authority flow, a decision owner table with data lifetimes, and a
  verification map inside the spec (Specification Review Contract).
- Correct the stale facts that the older specs state.

**Non-Goals:**

- Retire the older capabilities. The stacked change `retire-approval-specs`
  does that with a reason for each removed requirement.
- Describe the target design of the consolidation program as current
  behavior. The target shape appears only in the "Future scenarios" section of
  the architecture document and in notes with the label "Planned".
- Keep the shell case catalog in prose. The executable fixtures and the case
  catalog hold case-level detail.

## Decisions

1. **Rule IDs in requirement names.** Each requirement name starts with
   `TA-n`. GitHub anchors then stay stable when the wording changes, and a
   REMOVED reason in the stacked change can name its replacement.
   Alternative: plain names. Rejected because the architecture document links
   by ID.
2. **Owners in a table, behavior in requirements.** Requirements state
   observable behavior, wire strings, and reason codes. Class names appear only
   in the decision owner table and the verification map, which each code PR
   updates when it moves ownership.
3. **Current behavior only.** Each statement was checked against source at
   `0f1991ed0`. Where the tests pin a narrow fallback (for example a missing
   audience falls back to `Public`), the spec states the fallback instead of an
   ideal "fail loud" rule.
4. **Two stacked changes.** This change adds; the next one removes. A reviewer
   can check the new rules first and the removal mapping second.

## Corrected facts

| Older statement | Verified current fact | Evidence |
|---|---|---|
| Shell timeout default 60 s (`netclaw-tools`, `netclaw-gateway-security`, PRD-002) | 90 s default tool timeout (`Session.ToolExecutionTimeoutSeconds`) | `src/Netclaw.Configuration/SessionConfig.cs:58,179`; `ShellTool.cs` uses `context.ExecutionTimeout` |
| `ToolInteractionRequest.DirectoryRoots` field | No such field | `src/Netclaw.Actors/Sessions/SessionProtocol.Outputs.cs:356-458` |
| Five approval buttons | Up to six options: Once, This chat, Always here, This repository, Always anywhere, Deny | `ApprovalOptionKeys.cs:32-60`; `ToolAccessPolicy.cs:1109-1163` |
| Grant categories `shell`, `github`, `config_write`, `schedule_write` as an ACL | Categories are metadata; authorization uses audience profiles and MCP allow lists | `ToolAudienceProfiles.cs:29-64`; `ToolRegistry.GetToolsForGrants` has only test callers |
| Queryable tool audit records | Log lines and journal events only | `DispatchingToolExecutor.cs:238,359,667-724` |
| Reason code `channel_does_not_support_approval` | Tool result text `Tool requires approval but no interactive approval requester is available: <tool>` | `SessionToolExecutionPipeline.cs:676-690` |
| Public/Team roots include the shared sessions root | Only Personal gets the shared sessions root | `PathAccessPolicy.cs:756-770` |
| `ToolAccessDecision` with three outcomes | `ToolAuthorizationDecision` with four outcomes | `ToolAuthorizationDecision.cs:13-47` |
| Interactive commands rejected before launch | Stdin is closed; no pre-launch rejection | `ShellTool.cs:20,136` |
| Public default tools `file_read`, `file_list`, `attach_file` | Also `file_search` and `tool_output_read` | `ToolAudienceProfiles.cs:160-161` |

## Risks / Trade-offs

- [Two capabilities disagree until the stacked change merges] →
  `tool-authorization` states the verified behavior. The proposal says so, and
  the stacked PR follows directly.
- [The spec can drift from the code] → The Architecture Document Rule in
  `AGENTS.md` requires each authorization PR to update the document and the
  owner table in the same diff.
- [Some rules have no test yet] → The verification map lists the gaps (a shell
  grant never authorizes `file_read`, a launch-time hard deny case, a direct
  call to a hidden first-party tool). Consolidation PR 1b adds them.

## Migration Plan

Docs only. Revert the PR to roll back. No data or runtime effect.
