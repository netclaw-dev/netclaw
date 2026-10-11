## REMOVED Requirements

### Requirement: Shell policy uses one explicit evaluation state

**Reason**: `tool-authorization` restates this rule in TA-8, TA-14. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-8, TA-14.

### Requirement: Shell policy phases have one fixed order

**Reason**: `tool-authorization` restates this rule in TA-5, TA-6, TA-7, TA-13. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-5, TA-6, TA-7, TA-13.

### Requirement: Actor grant evidence has one validation boundary

**Reason**: `tool-authorization` restates this rule in TA-7, TA-8. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-7, TA-8.

### Requirement: Syntax facts and policy authority remain separate

**Reason**: `tool-authorization` restates this rule in TA-7. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-7.

### Requirement: Prompt and one-time authority share one candidate context

**Reason**: `tool-authorization` restates this rule in TA-8, TA-10. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-8, TA-10.

### Requirement: Coverage and trace facts remain atomic

**Reason**: `tool-authorization` restates this rule in TA-15. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-15.

### Requirement: Refactor preserves observable policy behavior

**Reason**: Change-process text. `scripts/check-approval-outcome-direction.py` and the frozen case catalog now enforce outcome parity (see `TOOLING.md`, Outcome Direction Check).

**Migration**: None. No runtime behavior changes.

### Requirement: Refactor reduces policy complexity

**Reason**: Line-count targets are delivery goals, not behavior. `scripts/authorization-metrics.py` measures size.

**Migration**: None. No runtime behavior changes.

### Requirement: Internal failures remain fail-closed

**Reason**: `tool-authorization` restates this rule in TA-7. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-7.
