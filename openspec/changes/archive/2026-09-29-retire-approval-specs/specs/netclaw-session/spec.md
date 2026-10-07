## REMOVED Requirements

### Requirement: Approval-paused turn lifecycle preserves original context

**Reason**: `tool-authorization` restates this rule in TA-11. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-11.

### Requirement: Approval recovery tests cover context directly

**Reason**: Test-process text, not runtime behavior. The Verification Map of `tool-authorization` lists `ApprovalRehydrationTests` and `ToolApprovalStateTests` as the proofs of TA-11.

**Migration**: None. No runtime behavior changes.
