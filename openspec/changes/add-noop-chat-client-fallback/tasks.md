## 1. Validation outcome plumbing

- [x] 1.1 Add a tri-state result type to provider/model validation
      (`valid` / `no-provider-configured` / `invalid`) in
      `Netclaw.Daemon/Configuration/ModelSelectionValidator.cs`
      (or sibling), preserving the existing "invalid" path for malformed
      configurations.
- [x] 1.2 Update `ProviderPluginFactory` and any callers so that the
      "no-provider-configured" outcome short-circuits before any plugin is
      instantiated (no provider plugin loaded, no credentials read).
- [x] 1.3 Unit tests for each outcome: missing provider section, missing
      model, missing required credential for a declared provider, schema
      violations, and a fully valid config. Cover both `netclaw.json`-only
      and DI-overridden config flows.

## 2. No-Op chat client

- [x] 2.1 Implement `NoOpChatClient : IChatClient` in
      `Netclaw.Daemon/Configuration/` (or `Netclaw.Configuration/` if the
      type needs to be visible to tests in another assembly). Streaming
      and non-streaming both return the fixed configuration banner.
- [x] 2.2 Banner content: leads with the exact phrase
      `"No valid model configuration detected."`, includes the three
      recovery steps (`netclaw doctor`, `netclaw model`, edit
      `netclaw.json`), and appends an "Available providers: …" line only
      when discoverable provider profiles can be enumerated without
      network I/O.
- [x] 2.3 Tool-call suppression: assert `NoOpChatClient` returns zero tool
      calls regardless of the tools registered on the request.
- [x] 2.4 Streaming path emits the banner as a single `ChatResponseUpdate`
      plus completion signal (no simulated token chunking).
- [x] 2.5 Unit tests covering: exact banner text & ordering, recovery-step
      presence, providers-line included/omitted, zero tool calls,
      streaming single-chunk behavior.

## 3. Chat-client provider selection

- [x] 3.1 Implement `NoOpChatClientProvider : IChatClientProvider` that
      returns the same `NoOpChatClient` instance for every `ModelRole`.
- [x] 3.2 Add `IChatClientProvider.IsDegraded` (or equivalent
      surface from Design open question) so doctor and diagnostics can
      query the degraded state without `is`-checking concrete types.
      Default `false` in `NetclawChatClientProvider`, `true` in
      `NoOpChatClientProvider`. Update both implementations.
- [x] 3.3 Update composition in
      `Netclaw.Daemon/Configuration/DaemonProviderServiceExtensions.cs`
      (and any other registration site) to branch on the validation
      outcome from Task 1 and register either provider implementation.
- [x] 3.4 Emit a single WARN-level structured log at startup when the
      No-Op provider is selected, naming the reason and referencing
      `netclaw doctor`. Do not log per chat turn.
- [x] 3.5 Verify decorators (`ResilientChatClientProviderDecorator`,
      `RetryingChatClient`, `LoggingChatClient`,
      `AlertingChatClientDecorator`, `FailoverChatClient`) interact
      correctly with the No-Op client — i.e., they treat a No-Op response
      as a successful call (no retry storm, no failover trigger).

## 4. Doctor integration

- [x] 4.1 Add a chat-client health check in `netclaw doctor` that uses
      `IChatClientProvider.IsDegraded` to report **pass** (real client),
      **warn** (No-Op active), or **fail** (validation rejected config and
      daemon is not running).
- [x] 4.2 Warn-level message includes the recovery commands
      `netclaw model` and editing `netclaw.json`.
- [x] 4.3 Integration test for doctor output in each of the three states.

## 5. Onboarding wizard integration

- [x] 5.1 In the wizard's health-check step, treat "no provider
      configured" as a **warn**-level item with remediation guidance —
      distinct from a fail-level startup validation rejection and not
      collapsed into `Daemon did not become ready`.
- [ ] 5.2 Wizard test (or smoke tape under `tests/smoke/tapes/`) that
      simulates running the daemon with no provider configured and
      asserts the warn-level item appears with the expected message.

## 6. Optional metric

- [x] 6.1 Decide whether to emit a `chat.noop_responses_total` counter
      (Design open question). If yes, add the metric in `NoOpChatClient`
      and expose it through the existing metrics pipeline. If no, document
      the decision in `design.md` (resolve the open question).

## 7. Spec sync and docs

- [x] 7.1 PRD-005 (model provider strategy): document that "no valid
      provider configured" is non-fatal at startup and selects the No-Op
      client. Link to the doctor warn item.
- [x] 7.2 PRD-004 (CLI onboarding & config): document the doctor warn
      item for the No-Op chat client.
- [x] 7.3 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md`
      with operator guidance on what the configuration banner means and
      what to do about it; bump `metadata.version`.
- [ ] 7.4 Update the release-notes / runbook entry covering the behavior
      change for deployments that previously failed startup on missing
      provider configuration.

## 8. Quality gates

- [x] 8.1 `dotnet slopwatch analyze` passes with no new violations.
- [x] 8.2 `./scripts/Add-FileHeaders.ps1 -Verify` passes for any new
      `.cs` files.
- [ ] 8.3 Eval suite passes (`./evals/run-evals.sh`) — the No-Op banner
      should not appear in any non-degraded eval run; consider adding a
      regression case asserting the real client is selected when config
      is valid.
- [x] 8.4 `openspec validate add-noop-chat-client-fallback` passes; run
      `/opsx-verify` once implementation lands.
