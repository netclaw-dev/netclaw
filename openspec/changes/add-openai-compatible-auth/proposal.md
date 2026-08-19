## Why

The `openai-compatible` provider type covers self-hosted OpenAI-shaped
backends such as llama.cpp, vLLM, Lemonade, and DwarfStar (ds4). The runtime
transport already sends `Authorization: Bearer` when an API key is present —
in the probe, the chat client, the models client, and the capability resolver.
But the descriptor declares `EndpointOnlyAuth`, so the auth method set is
`[None]` only. The init wizard and the provider manager TUI therefore never
offer a key input, and operators cannot configure an authenticated
OpenAI-compatible backend through any interactive surface.

Many deployments need that key: gated intranet gateways, LiteLLM proxies,
and hosted OpenAI-compatible APIs all require Bearer auth.

## What Changes

- Add one `IProviderAuth` shape: `EndpointOrApiKeyAuth`, supporting
  `[AuthMethod.None, AuthMethod.ApiKey]`.
- Change `OpenAiCompatibleDescriptor.Auth` from `EndpointOnlyAuth` to the new
  shape. No runtime transport change — all wire paths already send Bearer
  when a key exists and send no header when it does not.
- Init wizard: show the auth-method picker for this provider with an explicit
  "No auth" choice; show endpoint input plus an optional API-key input; probe
  and persist the key through the existing encrypted secrets path with
  `AuthMethod.ApiKey`.
- Provider manager TUI (`netclaw provider`): the same two additions.
- Update the `netclaw-operations` system skill provider reference and bump its
  version.
- Update smoke tapes that drive the wizard and provider-manager flows.
- No schema change: the `Providers` schema section is open. No new config
  knob. Not breaking — existing `AuthMethod: None` entries keep their behavior.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-model-providers`: the `openai-compatible` provider gains optional
  API-key authentication. The auth method set becomes `[None, ApiKey]`. The
  key is optional; an absent key sends no auth header. Interactive surfaces
  (init wizard, provider manager) offer both choices and persist the key
  through the encrypted secrets path.

## Impact

- **Code:** `src/Netclaw.Providers/IProviderAuth.cs` (new shape),
  `src/Netclaw.Providers/SelfHosted/OpenAiCompatibleDescriptor.cs` (auth
  declaration), `src/Netclaw.Cli/Tui/Wizard/Steps/ProviderStepView.cs` and
  `ProviderStepViewModel.cs`, `src/Netclaw.Cli/Tui/OAuthFlowViews.cs` (auth
  labels include `None`), `src/Netclaw.Cli/Tui/ProviderManagerViewModel.cs`
  and `ProviderManagerPage.cs`.
- **Skill:** `feeds/skills/.system/files/netclaw-operations/references/providers.md`
  with a `metadata.version` bump in the skill frontmatter.
- **Tests:** unit tests for the auth shape and the wizard/manager state
  transitions; a fake-failure test proving a selected `ApiKey` method with an
  empty key blocks save;.
- **No change:** transport code (`OpenAiCompatibleChatClient`,
  `OpenAiCompatibleModelsClient`, `OpenAiCompatibleCapabilityResolver`),
  CLI `provider add` surface (already accepts `--api-key`), config schema,
  persistence, secrets format.
- **Traceability:** multi-provider support requirement in
  `openspec/specs/netclaw-model-providers/spec.md`.
- **Out of scope:** non-Bearer header schemes (`api-key`, `x-api-key`),
  per-instance display names, new provider type keys, OAuth for
  OpenAI-compatible endpoints.
