## 1. Approval Mode Resolution Precedence

- [x] 1.1 Update `ToolAccessPolicy.ResolveApprovalMode` to resolve matcher-derived override key before base tool key and then fall back to fail-closed/default mode
- [x] 1.2 Add policy tests for control-plane file mutation precedence (`file_write:control-plane` override beats `file_write`)
- [x] 1.3 Add policy tests for base-key fallback when matcher-specific override is absent

## 2. Approve-Once Retry Matching Alignment

- [x] 2.1 Update `DispatchingToolExecutor` approval flow so one-time bypass checks run against the filtered unapproved pattern set returned by `IToolApprovalService`
- [x] 2.2 Preserve approve-once scope to immediate retry only (no persistent writes, no broader session cache)
- [x] 2.3 Add executor and pipeline tests verifying approve-once does not reprompt on immediate retry but prompts on a later invocation
- [x] 2.4 Add path-aware tests for control-plane file mutation approve-once matching (same path bypass, different path reprompt)

## 3. Shell Hard-Deny Composition

- [x] 3.1 Codify shell deny composition order (operation hard-deny before resource hard-deny) with explicit deny reason precedence
- [x] 3.2 Add tests for operation-first precedence when both deny categories match
- [x] 3.3 Add tests for resource hard-deny when operation hard-deny does not match

## 4. Spec and Validation Sync

- [x] 4.1 Update `openspec/changes/tool-approval-composition-fixes/specs/tool-approval-gates/spec.md` scenarios as implemented
- [x] 4.2 Run targeted test suites for tool approval policy/executor/pipeline and shell deny behavior
- [x] 4.3 Run `dotnet slopwatch analyze` and address any new violations
