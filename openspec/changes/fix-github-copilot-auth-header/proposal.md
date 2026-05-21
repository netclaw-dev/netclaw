## Why

The GitHub Copilot provider (introduced by the active `add-github-copilot-provider`
change) never authenticated successfully: every chat turn failed with
`ProviderFailure`, surfacing to users as "I encountered an error processing your
message." The Copilot endpoint returned `HTTP 400 "bad request: Authorization
header is badly formatted"` because requests went out with `Authorization: Bearer
placeholder` instead of the exchanged short-lived Copilot token — a direct
violation of the existing requirement that each request carry
`Authorization: Bearer <copilot-api-token>`.

## What Changes

- Fix `CopilotRequestPolicy` so the exchanged Copilot token actually reaches the
  wire. The policy no longer writes the `Authorization` header itself (the OpenAI
  SDK's own `ApiKeyCredential` auth policy runs after any registered policy and
  overwrote it with the placeholder). Instead a single mutable `ApiKeyCredential`
  is shared between the `OpenAIClient` and the policy, and the policy calls
  `credential.Update(token)` each call so the SDK emits the real token.
- Add an internal transport seam (`GitHubCopilotProviderPlugin.TransportOverride`)
  used only by tests to capture the fully-assembled outbound request.
- Add an end-to-end regression test that drives the real OpenAI SDK pipeline and
  asserts the exchanged token (not the placeholder) is on the wire; verified to
  fail against the previous behavior.
- Update the existing `CopilotRequestPolicy` unit tests to the new design (the
  policy updates the credential and sets only the three Copilot custom headers).
- No **BREAKING** changes; no config or public API changes.

## Capabilities

### New Capabilities

<!-- none -->

### Modified Capabilities

- `netclaw-model-providers`: strengthen the "GitHub Copilot provider" requirement
  with a scenario pinning that the outbound Copilot request transmits the
  exchanged token rather than the SDK placeholder credential. (Composes on top of
  the requirement added by the active `add-github-copilot-provider` change; this
  change should be archived after that one is synced.)

## Impact

- Code: `src/Netclaw.Providers/GitHubCopilot/GitHubCopilotProviderPlugin.cs`,
  `src/Netclaw.Providers/GitHubCopilot/CopilotRequestPolicy.cs`.
- Tests: `src/Netclaw.Daemon.Tests/Providers/GitHubCopilot/GitHubCopilotProviderPluginTests.cs`,
  `src/Netclaw.Daemon.Tests/Providers/GitHubCopilot/CopilotRequestPolicyTests.cs`.
- Dependencies: none added. Relies on `System.ClientModel`'s `ApiKeyCredential.Update`
  and `HttpClientPipelineTransport` (already transitively available via the OpenAI SDK).
- Operational: GitHub Copilot becomes usable as a working chat provider. No PRD
  change; this corrects implementation conformance to `netclaw-model-providers`.
