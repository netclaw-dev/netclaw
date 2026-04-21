## 1. OpenSpec planning artifacts and traceability

- [x] 1.1 Finalize proposal/design/spec deltas for Discord parity, slash command compatibility, interaction approvals, and deterministic text fallback.
- [x] 1.2 Verify traceability references to `PRD-009`, `PRD-001`, `PRD-002`, and `PRD-003` across all change artifacts.
- [x] 1.3 Review `openspec status --change discord-channel-with-interactions` and resolve any missing artifacts before apply.

## 2. Discord channel runtime parity implementation

- [x] 2.1 Add Discord adapter configuration, fail-closed validation, and adapter health diagnostics.
- [x] 2.2 Implement Discord inbound normalization to `SendUserMessage` with full source metadata and deterministic entity-key derivation.
- [x] 2.3 Enforce ACL checks before Discord session dispatch and emit structured deny diagnostics.
- [x] 2.4 Implement broadcast-based Discord reply delivery to originating thread/root context.

## 3. Slash command and approval UX behavior

- [x] 3.1 Ensure Discord inbound text content supports session-level `/name ...` dispatch without mandatory app-command registration.
- [ ] 3.2 Implement Discord interaction rendering for `ToolInteractionRequest` and response routing. _(Deferred: requires rich message component support on `IDiscordReplyClient`. Text fallback path is complete and functional.)_
- [x] 3.3 Implement deterministic Discord text fallback (A/B/C/D + keyword parsing) for approval decisions when interactions fail.
- [x] 3.4 Add operational diagnostics for interaction availability and fallback activation.

## 4. Offline testing and documentation

- [x] 4.1 Add offline unit/integration tests for Discord ingress, ACL gating, and entity-key stability without live Discord.
- [x] 4.2 Add offline tests validating deterministic text fallback, approval prompt builder, and message chunking.
- [x] 4.3 Update docs/runbooks for Discord setup, approval fallback behavior, and troubleshooting guidance.
- [x] 4.4 Maintain planning mockups in `docs/ui/UI-002-discord-tool-approval-mockups.md` for desktop and mobile approval UX.

## 5. Validation and readiness checks

- [x] 5.1 Run `openspec validate discord-channel-with-interactions --type change` and resolve all validation issues.
- [x] 5.2 Run repository quality gates required by implementation phase (`dotnet test`, `dotnet slopwatch analyze`) when code changes begin.
