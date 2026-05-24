## 1. Durable prompt-handle projection

- [x] 1.1 Add a narrow durable store or projection keyed by `SessionId` + `CallId` that records the channel-specific handle for posted approval prompts (`messageTs`, `postId`, `messageId`) without moving channel-specific state into `SessionState`.
- [x] 1.2 Write the durable prompt-handle record when Slack, Mattermost, or Discord successfully posts an approval prompt, and remove or terminally mark that record once reconciliation completes.
- [x] 1.3 Fail loud in logs/telemetry when a prompt posts successfully but its durable reconciliation handle cannot be recorded.

## 2. Session-to-adapter reconciliation contract

- [x] 2.1 Add the smallest transport-facing session output or equivalent contract needed to report approval terminal outcomes relevant to prompt reconciliation: resolved, denied, expired, and abandoned/superseded.
- [x] 2.2 Ensure the session emits that reconciliation outcome on both live and recovered approval paths, including expired-prompt and abandonment cases.
- [x] 2.3 Keep requester authorization and expired-call classification session-owned; adapters must not infer security-sensitive outcomes from `CommandAck` / `CommandNack` alone.

## 3. Slack prompt reconciliation

- [x] 3.1 Update `SlackThreadBindingActor` to consult the durable prompt-handle projection when a cold-spawned approval response arrives with no local `_pendingApprovalRequests` entry.
- [x] 3.2 Reconcile the original Slack approval message into its resolved or expired state after the session reports the terminal outcome, eliminating the current "redraw skipped" path when durable state exists.
- [x] 3.3 Preserve the current fail-loud behavior when no durable handle exists: still forward the response to the session and emit explicit diagnostics.

## 4. Mattermost and Discord prompt reconciliation

- [x] 4.1 Update `MattermostSessionBindingActor` to use durable prompt handles for cold-spawn approval reconciliation instead of permanently skipping redraw.
- [x] 4.2 Update `DiscordSessionBindingActor` to use durable prompt handles for cold-spawn approval reconciliation instead of permanently skipping redraw.
- [x] 4.3 Ensure all three adapters remove interactive controls or otherwise render a terminal non-interactive state for resolved, denied, expired, and abandoned approvals when the platform supports updates.

## 5. Tests

- [x] 5.1 Extend adapter contract/integration tests to cover cold-spawn approval reconciliation with a durable prompt handle for Slack, Mattermost, and Discord.
- [x] 5.2 Extend approval rehydration tests to cover post-recovery prompt reconciliation for approved, expired, and abandoned approval paths.
- [x] 5.3 Add restart-drain / warm-restart regression coverage proving that an approval prompt posted before restart is still reconciled correctly when the user clicks it after restart.

## 6. Validation

- [x] 6.1 `openspec validate restart-safe-approval-prompt-reconciliation` passes.
- [x] 6.2 Relevant `dotnet test` suites for session recovery and channel adapters pass.
- [x] 6.3 `dotnet slopwatch analyze` passes with no new violations.
- [x] 6.4 `./scripts/Add-FileHeaders.ps1 -Verify` passes.

## 7. Finalization

- [x] 7.1 Run `/opsx-verify restart-safe-approval-prompt-reconciliation` after implementation lands.
- [x] 7.2 Run `/opsx-sync restart-safe-approval-prompt-reconciliation` to propagate the delta specs into `openspec/specs/`.
- [ ] 7.3 Run `/opsx-archive restart-safe-approval-prompt-reconciliation` once the change is merged.
