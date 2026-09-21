## 1. Ingress safety

- [x] 1.1 Apply `PromptClassifier` to Teams executable ingress through an additive dependency contract and verify safe, high-risk, and detector-unavailable paths preserve the required boundaries.
- [x] 1.2 Use one Teams conversation-dependency factory for ordinary ingress, approval cold spawn, and reminder cold spawn; verify each path preserves detector behavior.

## 2. Teams approval and output lifecycle

- [x] 2.1 Route Teams approval callback session feedback through the shared pending lookup and approval response flow while preserving bounded Teams card validation and verify exact selected-key forwarding and requester rejection.
- [x] 2.2 Replace card-token expiry denial with persisted replacement-card binding and verify stale actions cannot authorize, a reissued action resolves once, and explicit deny releases the session wait.
- [x] 2.3 Route normal Teams reply failure through shared delivery feedback semantics and verify typing remains best effort while feedback-pipe failures are not swallowed.
- [x] 2.4 Preserve an uncertain approval submission as bounded Teams forwarding state, re-drive it safely after restart, and verify feedback failure neither strands the core approval nor executes the selected tool twice.
- [x] 2.5 Persist bounded presentation-recovery state for Adaptive Card delivery failure; reissue a fresh nonce on recovery without raw-nonce persistence, a tight retry loop, session denial, or terminal consumption.

## 3. Contracts, documentation, and validation

- [x] 3.1 Add `TeamsSessionBindingContractTests` for compatible lifecycle behavior in the shared contract suite and update the contract inventory with the verified assertion count.
- [x] 3.2 Update minimal Teams configuration and post-refactor retest-delta documentation, then verify it states no Graph permission or live tenant test was added.
- [x] 3.3 Run strict OpenSpec validation, focused Teams and actor tests, solution build/test, vulnerability, quality, header, whitespace, formatting, and lint checks; record results in the change artifacts.

## Validation record (2026-08-25)

- `dotnet restore Netclaw.slnx`: passed.
- `dotnet build Netclaw.slnx --no-restore --configuration Debug`: passed with 0 warnings and 0 errors.
- Shared Teams binding contract fixture: 8 passed.
- Focused Teams transport and persistence fixture: 79 passed.
- Feedback-failure retry and restart/recovery regression coverage: passed.
- Focused core policy, session pipeline, approval recovery, and Slack/Discord/Mattermost binding contracts: 406 passed.
- `dotnet test Netclaw.slnx --no-build --no-restore`: passed; 3,572 Actors tests and 1,293 Daemon tests passed, and 14 environment-gated integration tests remained skipped.
- `dotnet list Netclaw.slnx package --vulnerable --include-transitive`: no vulnerable packages.
- `dotnet slopwatch analyze`: 0 issues.
- `./scripts/Add-FileHeaders.ps1 -Verify`, `scripts/check-no-bom.sh`, and `git diff --check`: passed.
- `npx --yes @fission-ai/openspec@latest validate teams-channel-binding-approval-parity --strict`: passed.
- `git diff --check` passed. Scoped `dotnet format` reports legacy whitespace diagnostics in the large Teams actor, serializer, and routing-test files outside this corrective work; no broad formatting rewrite was applied.
