## 1. Fix the provider

- [x] 1.1 In `GitHubCopilotProviderPlugin.CreateChatClient`, create one
  `ApiKeyCredential` and pass it to both the `OpenAIClient` and the
  `CopilotRequestPolicy`.
- [x] 1.2 In `CopilotRequestPolicy.ProcessAsync`, call `credential.Update(token)`
  with the freshly exchanged Copilot token and stop writing the `Authorization`
  header directly; keep setting `copilot-integration-id`, `editor-version`, and
  `openai-intent`.
- [x] 1.3 Update the `CopilotRequestPolicy` XML docs to explain why the
  credential is updated rather than the header set.

## 2. Test seam

- [x] 2.1 Add `internal PipelineTransport? TransportOverride { get; init; }` to
  `GitHubCopilotProviderPlugin` and assign it to `OpenAIClientOptions.Transport`
  when set.

## 3. Tests

- [x] 3.1 Add an end-to-end regression test in `GitHubCopilotProviderPluginTests`
  that drives the OpenAI SDK pipeline through an `HttpClientPipelineTransport` and
  asserts the outbound `Authorization` header is `Bearer <exchanged-token>`.
- [x] 3.2 Verify the new test fails against the old behavior (header set directly)
  and passes with the fix.
- [x] 3.3 Update `CopilotRequestPolicyTests` for the new constructor and behavior
  (credential updated; only the three custom headers asserted; sync path still
  throws `NotSupportedException`).

## 4. Quality gates

- [x] 4.1 `dotnet test` GitHubCopilot suite green (21/21).
- [x] 4.2 `dotnet slopwatch analyze` reports 0 issues.
- [x] 4.3 `./scripts/Add-FileHeaders.ps1 -Verify` passes.
- [x] 4.4 Manual verification: live daemon chat turn succeeds via Copilot.
