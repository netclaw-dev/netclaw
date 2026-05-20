## Context

The GitHub Copilot provider routes chat completions through the OpenAI SDK
(`OpenAIClient` → `GetChatClient`) pointed at `api.githubcopilot.com`. Copilot
requires a short-lived bearer token obtained by exchanging the long-lived GitHub
OAuth token at `/copilot_internal/v2/token`. Because that token is dynamic and
refreshed, the original implementation constructed the SDK client with a
placeholder `ApiKeyCredential("placeholder")` and registered a
`CopilotRequestPolicy` (at `PipelinePosition.PerCall`) that set the
`Authorization` header to the fresh token on each request.

This never worked. `System.ClientModel`'s pipeline runs the SDK's own
key-credential auth policy after caller-registered policies, and that policy
re-writes `Authorization` from the client credential — the placeholder. Every
request reached Copilot as `Authorization: Bearer placeholder`, which Copilot
rejects with `HTTP 400 "bad request: Authorization header is badly formatted"`,
surfacing to users as a generic `ProviderFailure`. Switching the policy between
`PerCall` and `PerTry` does not help: the SDK's auth policy still runs last.

## Goals / Non-Goals

**Goals:**
- Make the exchanged Copilot token the value actually sent in `Authorization`.
- Guard the behavior with a test that exercises the real SDK pipeline, since the
  defect lives in pipeline ordering and is invisible to isolated policy unit tests.

**Non-Goals:**
- No change to the token-exchange logic, caching, refresh buffer, or the three
  Copilot custom headers (`copilot-integration-id`, `editor-version`,
  `openai-intent`), which were already correct.
- No config, public API, or model-availability changes (which model ids a given
  Copilot account exposes is out of scope).

## Decisions

- **Feed the SDK credential instead of fighting it.** Share one mutable
  `ApiKeyCredential` between the `OpenAIClient` and `CopilotRequestPolicy`. The
  policy calls `credential.Update(token)` before `ProcessNextAsync`, so when the
  SDK's auth policy reads the credential it emits the real token. The policy no
  longer touches the `Authorization` header; it sets only the custom headers.
  - *Alternative considered — re-order policies (PerTry):* rejected; the SDK auth
    policy still runs after caller policies regardless of phase.
  - *Alternative considered — custom `PipelineTransport`/auth policy:* rejected as
    heavier and more fragile than using the SDK's own credential rotation, which
    `ApiKeyCredential.Update` exists for.
- **Internal transport seam for testing.** Add
  `internal PipelineTransport? TransportOverride { get; init; }` on the plugin.
  When set, `CreateChatClient` assigns it to `OpenAIClientOptions.Transport`.
  Tests inject an `HttpClientPipelineTransport` over a capturing
  `HttpMessageHandler` to assert on the fully-assembled outbound request. The
  property is `internal` (visible to `Netclaw.Daemon.Tests` via existing
  `InternalsVisibleTo`) and never set in production wiring.

## Risks / Trade-offs

- [Shared mutable credential across concurrent requests on one chat client] →
  All requests for a given provider entry resolve the same cached token, so the
  per-call `Update` writes the same value; the SDK reads the credential during
  its auth policy on each send. Acceptable for the single-token-per-entry case.
- [Test seam widens the plugin's surface] → Mitigated by keeping it `internal`,
  documenting it as test-only, and never wiring it in DI.

## Migration Plan

No migration. Behavior-only fix; deploys with the daemon. Rollback is reverting
the two source files. Existing configs are unaffected.

## Open Questions

- None. Model availability per Copilot account (e.g. `claude-sonnet-4` returning
  `model_not_supported`) is a separate operator concern, not part of this fix.
