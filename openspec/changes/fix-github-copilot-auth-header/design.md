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

## References

The fix relies on the `System.ClientModel` pipeline contract plus where the
OpenAI SDK plants its auth policy. Pinned here so future maintainers don't
have to re-derive them from a broken chat turn:

> Note on packaging: `System.ClientModel` is a standalone NuGet package, not
> part of any `Azure.*` SDK. The OpenAI .NET SDK depends on it directly. The
> Microsoft Learn docs for `System.ClientModel.*` are bucketed under the
> "Azure for .NET Developers" doc set for historical/organizational reasons,
> but the package itself is non-Azure.

- **`ApiKeyCredential.Update(string)` is the documented credential-rotation API.**
  Microsoft Learn: "intended to be called when the API key has been regenerated
  and long-lived clients need to be updated to send the new value":
  <https://learn.microsoft.com/en-us/dotnet/api/system.clientmodel.apikeycredential.update>
- **`PipelinePosition` layering.** `PerCall` policies run *before* the
  pipeline's `RetryPolicy`; `PerTry` policies run *after* it. Caller policies
  registered via `options.AddPolicy(..., PerCall)` therefore land upstream of
  any per-try policy the SDK plants:
  <https://learn.microsoft.com/en-us/dotnet/api/system.clientmodel.primitives.pipelineposition>
- **`ApiKeyAuthenticationPolicy` is the policy the SDK uses to write the
  `Authorization` header from an `ApiKeyCredential`.** Microsoft Learn
  documents the factory `CreateBearerAuthorizationPolicy(ApiKeyCredential)` as
  setting the credential value in the `Authorization` header with a `Bearer`
  prefix on each request:
  <https://learn.microsoft.com/en-us/dotnet/api/system.clientmodel.primitives.apikeyauthenticationpolicy>
- **The OpenAI SDK registers that auth policy as a per-try policy.** See
  `OpenAIClientUtilities.CreatePipeline` — `authenticationPolicy` is passed in
  the `perTryPolicies` span of `ClientPipeline.Create`, downstream of any
  caller `PerCall` policy. Pinned to commit `93b09d1`:
  <https://github.com/openai/openai-dotnet/blob/93b09d135e08840cbe5d23bb11b5224fedf0f92f/OpenAI/src/Utility/OpenAIClientUtilities.cs>

Composed: our `CopilotRequestPolicy` runs in the per-call band (before the
retry policy); the SDK's `ApiKeyAuthenticationPolicy` runs in the per-try band
(after the retry policy) and reads the credential on each send. Writing
`Authorization` from our policy is therefore overwritten downstream, but
updating the shared `ApiKeyCredential` from our policy is observed downstream
on the same request — which is exactly the rotation contract
`ApiKeyCredential.Update` is documented to support.

The OpenAI SDK commit pinned above is not necessarily the exact build resolved
by `Microsoft.Extensions.AI.OpenAI` 10.6.0 (the version in
`Directory.Packages.props`); the SDK's pipeline shape has been stable across
recent versions, but if a future bump moves auth out of `perTryPolicies` the
analysis here needs to be re-validated.
