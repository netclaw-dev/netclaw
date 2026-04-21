## 1. OpenSpec planning artifacts and traceability

- [ ] 1.1 Finalize proposal/design/spec deltas for Discord parity, slash command compatibility, interaction approvals, and deterministic text fallback.
- [ ] 1.2 Verify traceability references to `PRD-009`, `PRD-001`, `PRD-002`, and `PRD-003` across all change artifacts.
- [ ] 1.3 Review `openspec status --change discord-channel-with-interactions` and resolve any missing artifacts before apply.

## 2. Discord channel runtime parity implementation

- [ ] 2.1 Add Discord adapter configuration, fail-closed validation, and adapter health diagnostics.
- [ ] 2.2 Implement Discord inbound normalization to `SendUserMessage` with full source metadata and deterministic entity-key derivation.
- [ ] 2.3 Enforce ACL checks before Discord session dispatch and emit structured deny diagnostics.
- [ ] 2.4 Implement broadcast-based Discord reply delivery to originating thread/root context.

## 3. Slash command and approval UX behavior

- [ ] 3.1 Ensure Discord inbound text content supports session-level `/name ...` dispatch without mandatory app-command registration.
- [ ] 3.2 Implement Discord interaction rendering for `ToolInteractionRequest` and response routing.
- [ ] 3.3 Implement deterministic Discord text fallback (A/B/C/D + keyword parsing) for approval decisions when interactions fail.
- [ ] 3.4 Add operational diagnostics for interaction availability and fallback activation.

## 4. Offline testing and documentation

- [ ] 4.1 Add offline unit/integration tests for Discord ingress, ACL gating, and entity-key stability without live Discord.
- [ ] 4.2 Add offline tests validating interaction approval path and deterministic text fallback equivalence.
- [ ] 4.3 Update docs/runbooks for Discord setup, approval fallback behavior, and troubleshooting guidance.
- [ ] 4.4 Maintain planning mockups in `docs/ui/UI-002-discord-tool-approval-mockups.md` for desktop and mobile approval UX.

## 5. Validation and readiness checks

- [ ] 5.1 Run `openspec validate discord-channel-with-interactions --type change` and resolve all validation issues.
- [ ] 5.2 Run repository quality gates required by implementation phase (`dotnet test`, `dotnet slopwatch analyze`) when code changes begin.
