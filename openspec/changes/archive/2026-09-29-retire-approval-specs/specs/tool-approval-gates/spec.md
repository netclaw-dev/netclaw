## REMOVED Requirements

### Requirement: Tool approval configuration per audience

**Reason**: `tool-authorization` restates this rule in TA-4. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-4.

### Requirement: Configurable hard deny list

**Reason**: `tool-authorization` restates this rule in TA-5. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-5.

### Requirement: Shell command pattern matching

**Reason**: `tool-authorization` restates this rule in TA-7, TA-10. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-7, TA-10.

### Requirement: IToolApprovalMatcher extension point

**Reason**: The requirement names an internal interface, not behavior. Candidate rules are in TA-7. Its `npm install` scenario describes the old verb-chain model; ShellSyntaxTree occurrences replaced it.

**Migration**: Use `tool-authorization` TA-7.

### Requirement: Mid-turn approval pause

**Reason**: `tool-authorization` restates this rule in TA-11. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-11.

### Requirement: ToolInteractionRequest/Response protocol

**Reason**: TA-10 states the consent request contract. The `DirectoryRoots` field that this requirement demands does not exist (`SessionProtocol.Outputs.cs`).

**Migration**: Use `tool-authorization` TA-10.

### Requirement: Approval provenance stays inclusive while third-party adopted policy is separate

**Reason**: `tool-authorization` restates this rule in TA-10. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-10.

### Requirement: Persistent approval storage

**Reason**: `tool-authorization` restates this rule in TA-13. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-13.

### Requirement: Global grant precedence over folder-scoped grants

**Reason**: `tool-authorization` restates this rule in TA-8. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-8.

### Requirement: Channel approval capability

**Reason**: TA-10 states channel capability and the headless result. The reason code `channel_does_not_support_approval` does not exist in the code; the tool result is "Tool requires approval but no interactive approval requester is available: <tool>".

**Migration**: Use `tool-authorization` TA-10.

### Requirement: Directory-root approvals for shell_execute

**Reason**: `tool-authorization` restates this rule in TA-8, TA-10. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-8, TA-10.

### Requirement: Reviewed diagnostic auto-allow below trusted roots

**Reason**: `tool-authorization` restates this rule in TA-8, TA-6. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-8, TA-6.

### Requirement: Guidance distinguishes file operations from shell semantics

**Reason**: The project-scope correction is in TA-9. Model guidance text is prompt content, not an authorization rule; it lives in the tool descriptions and `ToolChoiceGuidance`, and evals Category 9 measure it. A home for guidance rules is an open owner decision.

**Migration**: Use `tool-authorization` TA-9.

### Requirement: Five-button approval prompt with verb-and-directory framing

**Reason**: TA-10 states the options, keys, labels, label cap, and danger styling. The prompt now has up to six options, not five. Display layout detail is channel rendering and the channel prompt builder tests pin it.

**Migration**: Use `tool-authorization` TA-10.

### Requirement: Resolution message single-line format

**Reason**: Moved unchanged in substance to `netclaw-slack-socket` Requirement "Resolution message single-line format". It is not a tool authorization rule.

**Migration**: Use `netclaw-slack-socket` Requirement "Resolution message single-line format".

### Requirement: Pattern extraction refuses bash control-flow

**Reason**: `tool-authorization` restates this rule in TA-7. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-7.

### Requirement: Approval entry creation timestamp

**Reason**: `tool-authorization` restates this rule in TA-13. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-13.

### Requirement: Approval-gate near-miss diagnostics

**Reason**: `tool-authorization` restates this rule in TA-15. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-15.

### Requirement: Sub-agent approval bridge preserves prompt correlation

**Reason**: `tool-authorization` restates this rule in TA-12. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-12.

### Requirement: Sub-agent approval responses do not execute expired work

**Reason**: `tool-authorization` restates this rule in TA-12. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-12.

### Requirement: Approval pause persistence carries turn context

**Reason**: `tool-authorization` restates this rule in TA-11, TA-1. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-11, TA-1.

### Requirement: Approval responses use persisted requester context

**Reason**: `tool-authorization` restates this rule in TA-10, TA-11. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-10, TA-11.

### Requirement: Subagent approval evaluation uses the inherited parent cwd

**Reason**: `tool-authorization` restates this rule in TA-12. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-12.

### Requirement: Subagent inherits parent session-scoped approvals

**Reason**: `tool-authorization` restates this rule in TA-12, TA-8. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-12, TA-8.

### Requirement: Approval evaluation uses admitted turn authority

**Reason**: `tool-authorization` restates this rule in TA-1. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-1.

### Requirement: Shell policy uses the canonical grammar and dialect

**Reason**: `tool-authorization` restates this rule in TA-7. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-7.

### Requirement: Shell policy coordinator preserves actor ownership

**Reason**: TA-7, TA-8, and TA-13 state the behavior. The decision owner table of `tool-authorization` names the coordinator and the approval actor as current owners.

**Migration**: Use `tool-authorization` TA-7, TA-8, TA-13.

### Requirement: Candidate coverage composes authorization sources

**Reason**: `tool-authorization` restates this rule in TA-8. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-8.

### Requirement: Causal approval intent is separate from execution scope

**Reason**: TA-7 and TA-8 state the general rules. The case catalog (`ShellApprovalCaseCatalog.cs`) and the policy fixtures (`src/Netclaw.Security.Tests/Evidence/ApprovalEvidence/`) hold the Bash causal-intent cases, per decision D6.

**Migration**: Use `tool-authorization` TA-7, TA-8.

### Requirement: Shell approval decision trace is bounded and redacted

**Reason**: `tool-authorization` restates this rule in TA-15. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-15.

### Requirement: Exact sanitized beta approval catalog

**Reason**: Change-process evidence, not runtime behavior. The fixtures now live in `src/Netclaw.Security.Tests/Evidence/ApprovalEvidence/` and `ShellApprovalEvidenceContractTests` reads them (see the Verification Map of `tool-authorization`).

**Migration**: None. No runtime behavior changes.

### Requirement: Executable post-1952 live approval regression corpus

**Reason**: Test-corpus text, not runtime behavior. `post-1952-live-approval-harvest.json` in `src/Netclaw.Security.Tests/Evidence/ApprovalEvidence/` holds the corpus.

**Migration**: None. No runtime behavior changes.

### Requirement: Delegated managed-temp alignment is measured without prescribing the answer

**Reason**: Eval-process text, not runtime behavior. The eval stays in `evals/run-evals.sh`.

**Migration**: None. No runtime behavior changes.

### Requirement: Version 3 approval store wire contract

**Reason**: `tool-authorization` restates this rule in TA-13. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-13.

### Requirement: Exact-authority version 2 migration

**Reason**: `tool-authorization` restates this rule in TA-13. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-13.

### Requirement: Canonical trust-verb phrase creation

**Reason**: `tool-authorization` restates this rule in TA-16, TA-7. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-16, TA-7.

### Requirement: Version 3 recovery boundary

**Reason**: `tool-authorization` restates this rule in TA-13. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-13.

### Requirement: Bounded Bash stdin data has a constrained receiver grammar

**Reason**: TA-7 states the general rule for unresolved and bounded data. The case catalog holds the heredoc and here-string cases, per decision D6.

**Migration**: Use `tool-authorization` TA-7.

### Requirement: Bash command-resolution mutation stays strict

**Reason**: `tool-authorization` restates this rule in TA-7. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-7.

### Requirement: Explicit unmanaged temporary writes receive a managed-temp correction

**Reason**: `tool-authorization` restates this rule in TA-9. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-9.

### Requirement: Intentional unmanaged-temp retry reaches ordinary approval

**Reason**: `tool-authorization` restates this rule in TA-9, TA-10. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-9, TA-10.

### Requirement: Parent and subagent managed-temp corrections are equivalent

**Reason**: `tool-authorization` restates this rule in TA-9. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-9.

### Requirement: Product proof separates runtime contracts from model behavior

**Reason**: Change-process text, not runtime behavior. The Verification Map of `tool-authorization` names the deterministic proofs.

**Migration**: None. No runtime behavior changes.

### Requirement: Bare exit-status output preserves static shell approval candidates

**Reason**: `tool-authorization` restates this rule in TA-8. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-8.

### Requirement: Complete Bash compounds retain scoped grant candidates

**Reason**: `tool-authorization` restates this rule in TA-8, TA-7. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-8, TA-7.

### Requirement: A proved Bash scope remains valid at process launch

**Reason**: `tool-authorization` restates this rule in TA-14. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-14.

### Requirement: Directory advice keeps the original command inert

**Reason**: `tool-authorization` restates this rule in TA-9. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-9.

### Requirement: Repository grants cover registered Git worktrees only by explicit choice

**Reason**: `tool-authorization` restates this rule in TA-8, TA-10. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-8, TA-10.
